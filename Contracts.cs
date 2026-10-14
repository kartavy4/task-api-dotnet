using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskApi;

public record CreateTaskRequest(string? Title, Priority Priority = Priority.Medium);

public record TaskResponse(
    Guid Id, string Title, bool IsCompleted, DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt, Priority Priority)
{
    public static TaskResponse From(TaskItem item) => new(
        item.Id, item.Title, item.IsCompleted, item.CreatedAt, item.CompletedAt, item.Priority);
}

public record TaskCompletedEvent(Guid TaskId, string Title, DateTimeOffset CompletedAt, Priority Priority)
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter<Priority>(allowIntegerValues: false) }
    };
}

public interface ITaskEventPublisher
{
    Task PublishAsync(TaskCompletedEvent message);
}
