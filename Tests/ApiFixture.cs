using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace TaskApi.Tests;

public sealed class ApiFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private string _adminConnectionString = null!;
    private readonly string _database = $"taskapi_test_{Guid.NewGuid():N}";

    public TaskApiFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public RecordingPublisher Publisher { get; } = new();
    public CompletionBarrier Barrier { get; } = new();

    public async Task InitializeAsync()
    {
        _adminConnectionString = Environment.GetEnvironmentVariable("TASKAPI_TEST_POSTGRES")!;
        if (string.IsNullOrWhiteSpace(_adminConnectionString))
        {
            _postgres = new PostgreSqlBuilder().WithImage("postgres:17-alpine").Build();
            await _postgres.StartAsync();
            _adminConnectionString = _postgres.GetConnectionString();
        }

        await using var admin = new NpgsqlConnection(_adminConnectionString);
        await admin.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{_database}\"", admin);
        await create.ExecuteNonQueryAsync();

        var connectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = _database
        }.ConnectionString;
        Factory = new TaskApiFactory(connectionString, Publisher, Barrier);
        Client = Factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();
        if (Factory is not null)
            await Factory.DisposeAsync();

        if (!string.IsNullOrWhiteSpace(_adminConnectionString))
        {
            await using var admin = new NpgsqlConnection(_adminConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }

        if (_postgres is not null)
            await _postgres.DisposeAsync();
    }
}

public sealed class TaskApiFactory(
    string connectionString, RecordingPublisher publisher, CompletionBarrier barrier)
    : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<TaskDbContext>>();
            services.RemoveAll<TaskDbContext>();
            services.AddDbContext<TaskDbContext>(options =>
                options.UseNpgsql(connectionString).AddInterceptors(barrier));
            services.RemoveAll<ITaskEventPublisher>();
            services.AddSingleton<ITaskEventPublisher>(publisher);
        });
    }
}

public sealed class RecordingPublisher : ITaskEventPublisher
{
    public ConcurrentQueue<TaskCompletedEvent> Messages { get; } = new();
    public Func<TaskCompletedEvent, Task>? BeforePublish { get; set; }

    public async Task PublishAsync(TaskCompletedEvent message)
    {
        if (BeforePublish is not null)
            await BeforePublish(message);
        Messages.Enqueue(message);
    }
}

// Both requests must reach SaveChanges with the old xmin. A simple WhenAll may miss the race.
public sealed class CompletionBarrier : SaveChangesInterceptor
{
    private TaskCompletionSource? _bothArrived;
    private int _arrived;

    public void Enable()
    {
        _arrived = 0;
        _bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Disable() => _bothArrived = null;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var barrier = _bothArrived;
        var completing = eventData.Context!.ChangeTracker.Entries<TaskItem>()
            .Any(x => x.State == EntityState.Modified && x.Entity.IsCompleted);

        if (barrier is not null && completing)
        {
            if (Interlocked.Increment(ref _arrived) == 2)
                barrier.TrySetResult();
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        return result;
    }
}
