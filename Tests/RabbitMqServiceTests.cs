using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using RabbitMQ.Client;

namespace TaskApi.Tests;

public class RabbitMqServiceTests
{
    [Fact]
    public async Task Publishes_expected_json_and_reuses_connection()
    {
        var (factory, connection, channel) = CreateTransport();
        var payloads = new List<byte[]>();
        channel.BasicPublishAsync(
                RabbitMqService.Exchange, RabbitMqService.RoutingKey, false,
                Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                payloads.Add(call.Arg<ReadOnlyMemory<byte>>().ToArray());
                Assert.True(call.Arg<BasicProperties>().Persistent);
                Assert.Equal("application/json", call.Arg<BasicProperties>().ContentType);
                return ValueTask.CompletedTask;
            });

        await using var service = new RabbitMqService(factory, NullLogger<RabbitMqService>.Instance);
        var message = new TaskCompletedEvent(Guid.NewGuid(), "Buy milk", DateTimeOffset.UtcNow, Priority.High);
        await service.PublishAsync(message);
        await service.PublishAsync(message with { TaskId = Guid.NewGuid() });

        await factory.Received(1).CreateConnectionAsync(Arg.Any<CancellationToken>());
        Assert.Equal(2, payloads.Count);
        using var json = JsonDocument.Parse(payloads[0]);
        Assert.Equal(message.TaskId, json.RootElement.GetProperty("taskId").GetGuid());
        Assert.Equal(message.Title, json.RootElement.GetProperty("title").GetString());
        Assert.Equal("High", json.RootElement.GetProperty("priority").GetString());
        Assert.Equal(message.CompletedAt, json.RootElement.GetProperty("completedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Shutdown_waits_for_active_publication_then_closes_channel_and_connection()
    {
        var (factory, connection, channel) = CreateTransport();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        channel.BasicPublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask(WaitForReleaseAsync(call.Arg<CancellationToken>())));

        async Task WaitForReleaseAsync(CancellationToken ct)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
        }

        await using var service = new RabbitMqService(factory, NullLogger<RabbitMqService>.Instance);
        var message = new TaskCompletedEvent(Guid.NewGuid(), "Shutdown", DateTimeOffset.UtcNow, Priority.Medium);
        var publishing = service.PublishAsync(message);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = service.StopAsync(CancellationToken.None);
        Assert.False(stopping.IsCompleted);
        await channel.DidNotReceive().CloseAsync(Arg.Any<ushort>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());

        release.TrySetResult();
        await Task.WhenAll(publishing, stopping).WaitAsync(TimeSpan.FromSeconds(5));
        await channel.Received(1).CloseAsync(200, "Application shutdown", true, Arg.Any<CancellationToken>());
        await connection.Received(1).CloseAsync(200, "Application shutdown", TimeSpan.FromSeconds(1), true, Arg.Any<CancellationToken>());
        await channel.Received(1).DisposeAsync();
        await connection.Received(1).DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PublishAsync(message));
    }

    private static (IConnectionFactory, IConnection, IChannel) CreateTransport()
    {
        var factory = Substitute.For<IConnectionFactory>();
        var connection = Substitute.For<IConnection>();
        var channel = Substitute.For<IChannel>();
        channel.IsOpen.Returns(true);
        connection.IsOpen.Returns(true);
        factory.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(connection));
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(channel));
        return (factory, connection, channel);
    }
}
