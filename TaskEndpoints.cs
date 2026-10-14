using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace TaskApi;

public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this WebApplication app)
    {
        app.MapPost("/tasks", Create);
        app.MapGet("/tasks", GetAll);
        app.MapPut("/tasks/{id}/complete", Complete);
        app.MapDelete("/tasks/{id}", Delete);
    }

    private static async Task<IResult> Create(
        CreateTaskRequest request, TaskDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > 200)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["title"] = ["Title must contain 1 to 200 characters and cannot be whitespace."]
            });

        if (!Enum.IsDefined(request.Priority))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["priority"] = ["Priority must be Low, Medium or High."]
            });

        var item = new TaskItem { Title = request.Title.Trim(), Priority = request.Priority };
        db.Tasks.Add(item);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/tasks/{item.Id}", TaskResponse.From(item));
    }

    private static async Task<IResult> GetAll(TaskDbContext db, CancellationToken ct)
    {
        var items = await db.Tasks.AsNoTracking().OrderBy(x => x.CreatedAt).ToListAsync(ct);
        return Results.Ok(items.Select(TaskResponse.From));
    }

    private static async Task<IResult> Complete(
        Guid id, TaskDbContext db, ITaskEventPublisher publisher,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var item = await db.Tasks.FindAsync([id], ct);
        if (item is null)
            return Results.NotFound();
        if (item.IsCompleted)
            return Results.Conflict(new { message = "Task is already completed." });

        item.IsCompleted = true;
        item.CompletedAt = UtcTimestamp.Now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { message = "Task was changed by another request." });
        }

        var message = new TaskCompletedEvent(item.Id, item.Title, item.CompletedAt.Value, item.Priority);
        try
        {
            // The DB commit has succeeded. A disconnected HTTP client must not cancel publishing.
            await publisher.PublishAsync(message);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("TaskEvents").LogError(ex,
                "Could not publish task.completed. Event: {Event}",
                JsonSerializer.Serialize(message, TaskCompletedEvent.JsonOptions));
        }

        return Results.Ok(TaskResponse.From(item));
    }

    private static async Task<IResult> Delete(Guid id, TaskDbContext db, CancellationToken ct)
    {
        var deleted = await db.Tasks.Where(x => x.Id == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? Results.NotFound() : Results.NoContent();
    }
}
