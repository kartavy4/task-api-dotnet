using Microsoft.EntityFrameworkCore;

namespace TaskApi;

public class TaskDbContext(DbContextOptions<TaskDbContext> options) : DbContext(options)
{
    public DbSet<TaskItem> Tasks => Set<TaskItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var task = modelBuilder.Entity<TaskItem>();
        task.HasKey(x => x.Id);
        task.Property(x => x.Title).IsRequired().HasMaxLength(200);
        task.Property(x => x.Priority).HasConversion<string>().HasMaxLength(10);
        // PostgreSQL uses the system column xmin, not SQL Server's byte[] rowversion.
        task.Property(x => x.RowVersion).IsRowVersion();
    }
}
