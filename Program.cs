using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using TaskApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter<Priority>(allowIntegerValues: false)));
builder.Services.AddProblemDetails();
builder.Services.AddDbContext<TaskDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

builder.Services.AddSingleton<IConnectionFactory>(_ => new ConnectionFactory
{
    Uri = new Uri(builder.Configuration["RabbitMq:Uri"]
        ?? throw new InvalidOperationException("RabbitMq:Uri is required.")),
    AutomaticRecoveryEnabled = false,
    RequestedConnectionTimeout = TimeSpan.FromSeconds(2),
    ContinuationTimeout = TimeSpan.FromSeconds(2)
});
builder.Services.AddSingleton<RabbitMqService>();
builder.Services.AddSingleton<ITaskEventPublisher>(sp => sp.GetRequiredService<RabbitMqService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<RabbitMqService>());

var app = builder.Build();
app.UseExceptionHandler();
app.MapTaskEndpoints();

if (app.Configuration.GetValue<bool>("Database:ApplyMigrations"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TaskDbContext>();
    await db.Database.MigrateAsync();
}

app.Run();

public partial class Program;
