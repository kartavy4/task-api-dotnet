using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace TaskApi.Tests;

public class CompleteTaskIntegrationTest(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task Create_then_complete_publishes_event_after_database_commit()
    {
        var created = await CreateTaskAsync("Buy milk", "High");
        Assert.False(created.IsCompleted);
        Assert.Null(created.CompletedAt);

        fixture.Publisher.BeforePublish = async message =>
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TaskDbContext>();
            var stored = await db.Tasks.AsNoTracking().SingleAsync(x => x.Id == message.TaskId);
            Assert.True(stored.IsCompleted);
            Assert.Equal(message.CompletedAt, stored.CompletedAt);
        };

        try
        {
            using var response = await fixture.Client.PutAsync($"/tasks/{created.Id}/complete", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var completed = (await response.Content.ReadFromJsonAsync<TaskResponse>(TaskCompletedEvent.JsonOptions))!;
            Assert.True(completed.IsCompleted);
            Assert.NotNull(completed.CompletedAt);
            Assert.True(completed.CompletedAt >= created.CreatedAt);

            var message = Assert.Single(fixture.Publisher.Messages, x => x.TaskId == created.Id);
            Assert.Equal(created.Title, message.Title);
            Assert.Equal(Priority.High, message.Priority);
            Assert.Equal(completed.CompletedAt, message.CompletedAt);

            using var repeat = await fixture.Client.PutAsync($"/tasks/{created.Id}/complete", null);
            Assert.Equal(HttpStatusCode.Conflict, repeat.StatusCode);
            Assert.Single(fixture.Publisher.Messages, x => x.TaskId == created.Id);
            var tasks = (await fixture.Client.GetFromJsonAsync<TaskResponse[]>("/tasks", TaskCompletedEvent.JsonOptions))!;
            Assert.Equal(completed.CompletedAt, tasks.Single(x => x.Id == created.Id).CompletedAt);
        }
        finally
        {
            fixture.Publisher.BeforePublish = null;
        }
    }

    [Fact]
    public async Task Concurrent_completion_returns_one_success_one_conflict_and_one_event()
    {
        var created = await CreateTaskAsync("Concurrent completion");
        fixture.Barrier.Enable();
        try
        {
            var responses = await Task.WhenAll(
                fixture.Client.PutAsync($"/tasks/{created.Id}/complete", null),
                fixture.Client.PutAsync($"/tasks/{created.Id}/complete", null));

            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
            Assert.Single(fixture.Publisher.Messages, x => x.TaskId == created.Id);
            foreach (var response in responses)
                response.Dispose();
        }
        finally
        {
            fixture.Barrier.Disable();
        }
    }

    [Fact]
    public async Task Publisher_failure_does_not_roll_back_completion()
    {
        var created = await CreateTaskAsync("RabbitMQ is down");
        fixture.Publisher.BeforePublish = _ => throw new IOException("Broker unavailable");
        try
        {
            using var response = await fixture.Client.PutAsync($"/tasks/{created.Id}/complete", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var tasks = (await fixture.Client.GetFromJsonAsync<TaskResponse[]>("/tasks", TaskCompletedEvent.JsonOptions))!;
            var stored = tasks.Single(x => x.Id == created.Id);
            Assert.True(stored.IsCompleted);
            Assert.NotNull(stored.CompletedAt);
        }
        finally
        {
            fixture.Publisher.BeforePublish = null;
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"\\t\\r\\n\"")]
    public async Task Invalid_title_returns_bad_request(string titleJson)
    {
        using var body = new StringContent($"{{\"title\":{titleJson}}}", Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync("/tasks", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Title_length_boundary_and_default_values_are_respected()
    {
        var created = await CreateTaskAsync(new string('x', 200));
        Assert.Equal(Priority.Medium, created.Priority);
        Assert.Null(created.CompletedAt);
        using var response = await fixture.Client.PostAsJsonAsync("/tasks", new { title = new string('x', 201) });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("{\"title\":\"Test\",\"priority\":\"Urgent\"}")]
    [InlineData("{\"title\":\"Test\",\"priority\":123}")]
    [InlineData("{\"title\":\"Test\",\"priority\":null}")]
    [InlineData("{}")]
    public async Task Invalid_request_returns_bad_request(string json)
    {
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync("/tasks", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Delete_removes_task_and_missing_or_invalid_ids_have_expected_status()
    {
        var created = await CreateTaskAsync("Delete me");
        using var deleted = await fixture.Client.DeleteAsync($"/tasks/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var tasks = (await fixture.Client.GetFromJsonAsync<TaskResponse[]>("/tasks", TaskCompletedEvent.JsonOptions))!;
        Assert.DoesNotContain(tasks, x => x.Id == created.Id);
        using var missingDelete = await fixture.Client.DeleteAsync($"/tasks/{created.Id}");
        using var missingComplete = await fixture.Client.PutAsync($"/tasks/{created.Id}/complete", null);
        using var badGuid = await fixture.Client.PutAsync("/tasks/not-a-guid/complete", null);
        Assert.Equal(HttpStatusCode.NotFound, missingDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingComplete.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badGuid.StatusCode);
    }

    private async Task<TaskResponse> CreateTaskAsync(string title, string? priority = null)
    {
        object body = priority is null ? new { title } : new { title, priority };
        using var response = await fixture.Client.PostAsJsonAsync("/tasks", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TaskResponse>(TaskCompletedEvent.JsonOptions))!;
    }
}
