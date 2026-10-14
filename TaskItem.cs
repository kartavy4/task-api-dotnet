namespace TaskApi;

public enum Priority
{
    Low,
    Medium,
    High
}

public class TaskItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Title { get; set; }
    public bool IsCompleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = UtcTimestamp.Now;
    public DateTimeOffset? CompletedAt { get; set; }
    public Priority Priority { get; set; } = Priority.Medium;
    public uint RowVersion { get; set; }
}

internal static class UtcTimestamp
{
    public static DateTimeOffset Now
    {
        get
        {
            var now = DateTimeOffset.UtcNow;
            // PostgreSQL stores microseconds; keep API, database and event values identical.
            return now.AddTicks(-(now.Ticks % 10));
        }
    }
}
