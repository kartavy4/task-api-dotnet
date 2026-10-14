using System.Text.Json;
using RabbitMQ.Client;

namespace TaskApi;

public sealed class RabbitMqService(
    IConnectionFactory factory, ILogger<RabbitMqService> logger)
    : ITaskEventPublisher, IHostedService, IAsyncDisposable
{
    public const string Exchange = "task.events";
    public const string RoutingKey = "task.completed";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _abort = new();
    private IConnection? _connection;
    private IChannel? _channel;
    private volatile bool _stopping;
    private int _disposed;

    // Connect lazily: RabbitMQ being down must not prevent the API from starting.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task PublishAsync(TaskCompletedEvent message)
    {
        if (_stopping)
            throw new InvalidOperationException("RabbitMQ publisher is stopping.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));

        // A RabbitMQ channel must not be used by simultaneous publishers.
        await _gate.WaitAsync(timeout.Token);
        try
        {
            if (_stopping)
                throw new InvalidOperationException("RabbitMQ publisher is stopping.");

            await EnsureConnectedAsync(timeout.Token);
            var body = JsonSerializer.SerializeToUtf8Bytes(message, TaskCompletedEvent.JsonOptions);
            var properties = new BasicProperties
            {
                ContentType = "application/json",
                Persistent = true,
                Type = RoutingKey,
                MessageId = message.TaskId.ToString()
            };

            await _channel!.BasicPublishAsync(
                Exchange, RoutingKey, mandatory: false, properties, body, timeout.Token);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_channel?.IsOpen == true)
            return;

        // The next request recreates a broken connection. The failed event is only logged.
        await CloseAsync(ct);
        _connection = await factory.CreateConnectionAsync(ct);
        _channel = await _connection.CreateChannelAsync(cancellationToken: ct);
        await _channel.ExchangeDeclareAsync(
            Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: ct);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        _abort.CancelAfter(TimeSpan.FromSeconds(3));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            // Let the active publication finish before closing its channel.
            await _gate.WaitAsync(timeout.Token);
            try
            {
                await CloseAsync(timeout.Token);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "RabbitMQ shutdown did not finish normally.");
        }
        finally
        {
            await _abort.CancelAsync();
        }
    }

    private async Task CloseAsync(CancellationToken ct)
    {
        var channel = _channel;
        var connection = _connection;
        _channel = null;
        _connection = null;

        try
        {
            if (channel is not null)
            {
                try
                {
                    if (channel.IsOpen)
                        await channel.CloseAsync(200, "Application shutdown", abort: true, ct);
                }
                finally
                {
                    await channel.DisposeAsync().AsTask().WaitAsync(ct);
                }
            }
        }
        finally
        {
            if (connection is not null)
            {
                try
                {
                    if (connection.IsOpen)
                        await connection.CloseAsync(200, "Application shutdown",
                            TimeSpan.FromSeconds(1), abort: true, ct);
                }
                finally
                {
                    await connection.DisposeAsync().AsTask().WaitAsync(ct);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await StopAsync(CancellationToken.None);
        _abort.Dispose();
        _gate.Dispose();
    }
}
