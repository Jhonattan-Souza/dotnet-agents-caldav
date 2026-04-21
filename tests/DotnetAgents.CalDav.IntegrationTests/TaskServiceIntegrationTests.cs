using CalDavConflictException = DotnetAgents.CalDav.Core.CalDavConflictException;
using TaskItem = DotnetAgents.CalDav.Core.Models.TaskItem;
using TaskPriority = DotnetAgents.CalDav.Core.Models.TaskPriority;
using TaskQuery = DotnetAgents.CalDav.Core.Models.TaskQuery;
using TaskStatus = DotnetAgents.CalDav.Core.Models.TaskStatus;
using DotnetAgents.CalDav.IntegrationTests.Fixtures;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

/// <summary>
/// End-to-end integration tests for <see cref="Core.Abstractions.ITaskService"/>
/// against a real Radicale CalDAV server via Testcontainers.
/// Validates the full supported <see cref="TaskItem"/> field surface.
/// </summary>
[Collection("RadicaleCollection")]
public class TaskServiceIntegrationTests(RadicaleFixture fixture) : IAsyncLifetime
{
    private readonly List<string> _createdTaskHrefs = [];
    private readonly ILogger<TaskServiceIntegrationTests> _logger = LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<TaskServiceIntegrationTests>();

    // ── IAsyncLifetime: clean up created tasks between tests ─────────────────

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        // Clean up any tasks created during the test.
        foreach (var href in _createdTaskHrefs)
        {
            try
            {
                await fixture.TaskService.DeleteTaskAsync(href, null, TestContext.Current.CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Best-effort cleanup failed for task {TaskHref}", href);
            }
        }
    }

    // ── Helper: create a task and track for cleanup ───────────────────────

    private async Task<TaskItem> CreateTrackedTaskAsync(TaskItem task)
    {
        var created = await fixture.TaskService.CreateTaskAsync(fixture.TaskListHref, task, TestContext.Current.CancellationToken);
        _createdTaskHrefs.Add(created.Href);
        return created;
    }

    // ── 1. Task list discovery ─────────────────────────────────────────────

    [Fact]
    public async Task GetTaskLists_ReturnsSeededCollection()
    {
        // Act
        var taskLists = await fixture.TaskService.GetTaskListsAsync(TestContext.Current.CancellationToken);

        // Assert
        taskLists.ShouldNotBeEmpty();
        taskLists.ShouldContain(tl =>
            tl.Href.TrimEnd('/') == fixture.TaskListHref.TrimEnd('/'),
            $"Expected task list '{fixture.TaskListHref}' to be discovered. " +
            $"Actual: [{string.Join(", ", taskLists.Select(t => t.Href))}]");

        var taskList = taskLists.First(tl => tl.Href.TrimEnd('/') == fixture.TaskListHref.TrimEnd('/'));
        taskList.DisplayName.ShouldBe("Tasks");
        taskList.SupportedComponents.ShouldContain(c => c.Equals("VTODO", StringComparison.OrdinalIgnoreCase));
    }

    // ── 2. Create task with full supported field set ───────────────────────

    [Fact]
    public async Task CreateTask_WithFullFieldSet_ReturnsTaskWithServerAssignedFields()
    {
        // Arrange
        var due = new DateTimeOffset(2026, 12, 31, 17, 0, 0, TimeSpan.Zero);
        var start = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
        var completed = new DateTimeOffset(2026, 6, 15, 10, 30, 0, TimeSpan.Zero);

        var task = new TaskItem
        {
            Summary = "Full-field integration task",
            Description = "A task with every supported field populated",
            Due = due,
            Start = start,
            Completed = completed,
            Priority = TaskPriority.High,
            Status = TaskStatus.Completed,
            Categories = ["work", "integration-test"],
            RecurrenceRule = "FREQ=DAILY;COUNT=5"
        };

        // Act
        var created = await CreateTrackedTaskAsync(task);

        // Assert — server-assigned fields
        created.Uid.ShouldNotBeNullOrEmpty();
        created.Href.ShouldNotBeNullOrEmpty();
        created.ETag.ShouldNotBeNullOrEmpty();

        // Assert — client-sent fields round-tripped
        created.Summary.ShouldBe("Full-field integration task");
        created.Description.ShouldBe("A task with every supported field populated");
        created.Priority.ShouldBe(TaskPriority.High);
        created.Status.ShouldBe(TaskStatus.Completed);
        created.Categories.Count.ShouldBe(2);
        created.Categories.ShouldContain("work");
        created.Categories.ShouldContain("integration-test");
        // RecurrenceRule: exact string equality is not stable because iCal.NET/Radicale
        // may canonicalise the RRULE (e.g. adding INTERVAL=1). Assert the key parts
        // that prove the rule survived the round-trip.
        created.RecurrenceRule.ShouldNotBeNullOrEmpty();
        created.RecurrenceRule.ShouldContain("FREQ=DAILY");
        created.RecurrenceRule.ShouldContain("COUNT=5");
    }

    // ── 3. Get task round-trip: assert every supported field ───────────────

    [Fact]
    public async Task GetTask_RoundTrip_AllSupportedFieldsPreserved()
    {
        // Arrange: create a task with specific values for every field, including RecurrenceRule
        var due = new DateTimeOffset(2026, 8, 15, 23, 59, 0, TimeSpan.Zero);
        var start = new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero);

        var task = new TaskItem
        {
            Summary = "Round-trip assertion task",
            Description = "Testing every field survives a round-trip through Radicale",
            Due = due,
            Start = start,
            Priority = TaskPriority.Medium,
            Status = TaskStatus.NeedsAction,
            Categories = ["alpha", "beta", "gamma"],
            RecurrenceRule = "FREQ=WEEKLY;COUNT=10"
        };

        var created = await CreateTrackedTaskAsync(task);

        // Act: re-fetch by href
        var fetched = await fixture.TaskService.GetTaskAsync(created.Href, TestContext.Current.CancellationToken);

        // Assert: every supported field
        fetched.ShouldNotBeNull();
        fetched!.Uid.ShouldBe(created.Uid);
        fetched.Summary.ShouldBe("Round-trip assertion task");
        fetched.Description.ShouldBe("Testing every field survives a round-trip through Radicale");

        // Due/Start: Radicale stores as UTC; we compare with tolerance for timezone rounding
        fetched.Due.ShouldNotBeNull();
        fetched.Due!.Value.UtcDateTime.ShouldBe(due.UtcDateTime, TimeSpan.FromSeconds(1));

        fetched.Start.ShouldNotBeNull();
        fetched.Start!.Value.UtcDateTime.ShouldBe(start.UtcDateTime, TimeSpan.FromSeconds(1));

        fetched.Priority.ShouldBe(TaskPriority.Medium);
        fetched.Status.ShouldBe(TaskStatus.NeedsAction);

        fetched.Categories.Count.ShouldBe(3);
        fetched.Categories.ShouldContain("alpha");
        fetched.Categories.ShouldContain("beta");
        fetched.Categories.ShouldContain("gamma");

        // RecurrenceRule: exact string equality is not stable because iCal.NET/Radicale
        // may canonicalise the RRULE (e.g. adding INTERVAL=1). Assert the key parts
        // that prove the rule survived the round-trip through a live CalDAV server.
        fetched.RecurrenceRule.ShouldNotBeNullOrEmpty();
        fetched.RecurrenceRule.ShouldContain("FREQ=WEEKLY");
        fetched.RecurrenceRule.ShouldContain("COUNT=10");

        fetched.ETag.ShouldNotBeNullOrEmpty();
        fetched.Href.ShouldBe(created.Href);

        // Completed defaults to null for a NeedsAction task
        fetched.Completed.ShouldBeNull();
    }

    // ── 4. List/query tasks with filters ──────────────────────────────────

    [Fact]
    public async Task GetTasks_StatusFilter_ReturnsRelevantTasks()
    {
        // Arrange: create tasks with different statuses
        var needsActionTask = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = "Status filter: needs action",
            Status = TaskStatus.NeedsAction,
            Priority = TaskPriority.Low
        });

        var completedTask = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = "Status filter: completed",
            Status = TaskStatus.Completed,
            Priority = TaskPriority.None,
            Completed = DateTimeOffset.UtcNow
        });

        var allTasksResult = await fixture.TaskService.GetTasksAsync(
            fixture.TaskListHref,
            new TaskQuery { Status = TaskStatus.NeedsAction },
            TestContext.Current.CancellationToken);

        allTasksResult.ShouldContain(t => t.Uid == needsActionTask.Uid,
            "NeedsAction tasks with explicit STATUS should appear in NeedsAction query results");
        allTasksResult.ShouldNotContain(t => t.Uid == completedTask.Uid,
            "Completed tasks should not appear in NeedsAction query results");
    }

    [Fact]
    public async Task GetTasks_StatusFilterNeedsAction_ReturnsNeedsActionTasksWithExplicitStatus()
    {
        var created = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = "NeedsAction explicit status task",
            Status = TaskStatus.NeedsAction
        });

        var result = await fixture.TaskService.GetTasksAsync(
            fixture.TaskListHref,
            new TaskQuery { Status = TaskStatus.NeedsAction },
            TestContext.Current.CancellationToken);

        result.ShouldContain(task => task.Uid == created.Uid);
    }

    [Fact]
    public async Task GetTasks_WithTextFilter_ReturnsMatchingTasks()
    {
        // Arrange: create tasks with different summaries
        var matchingTask = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = "UniqueTextFilter_test_xyz",
            Description = "Contains special content for text search"
        });

        var otherTask = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = "Completely different task"
        });

        // Act
        var result = await fixture.TaskService.GetTasksAsync(
            fixture.TaskListHref,
            new TaskQuery { TextSearch = "UniqueTextFilter_test_xyz" },
            TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain(t => t.Uid == matchingTask.Uid,
            "Text search should find tasks matching the query");
        result.ShouldNotContain(t => t.Uid == otherTask.Uid,
            "Text search should not return non-matching tasks");
    }

    [Fact]
    public async Task GetTasks_WithCategoryFilter_ReturnsMatchingTasks()
    {
        // Arrange: create tasks with different categories
        var taggedTask = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = "Category filter test",
            Categories = ["integration-test", "filter-demo"]
        });

        var untaggedTask = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = "No categories task",
            Categories = []
        });

        // Act
        var result = await fixture.TaskService.GetTasksAsync(
            fixture.TaskListHref,
            new TaskQuery { Category = "integration-test" },
            TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain(t => t.Uid == taggedTask.Uid,
            "Category filter should find tasks with the matching category");
        result.ShouldNotContain(t => t.Uid == untaggedTask.Uid,
            "Category filter should not return tasks without the category");
    }

    [Fact]
    public async Task GetTasks_WithDueDateFIlter_ReturnsMatchingTasks()
    {
        // Arrange: create tasks with specific due dates
        var earlyDueTask = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = "Due date filter: early",
            Due = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)
        });

        var lateDueTask = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = "Due date filter: late",
            Due = new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero)
        });

        var noDueTask = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = "Due date filter: no due date"
        });

        // Act: query for tasks due between Feb and Apr 2026
        var result = await fixture.TaskService.GetTasksAsync(
            fixture.TaskListHref,
            new TaskQuery
            {
                DueAfter = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
                DueBefore = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero)
            },
            TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain(t => t.Uid == earlyDueTask.Uid,
            "Due date filter should include the early-due task");
        result.ShouldNotContain(t => t.Uid == lateDueTask.Uid,
            "Due date filter should not include the late-due task");
        result.ShouldNotContain(t => t.Uid == noDueTask.Uid,
            "Due date filter should not include tasks without a due date");
    }

    // ── 5. Update task preserving/changing fields ─────────────────────────

    [Fact]
    public async Task UpdateTask_ChangesFieldsAndPreservesEtag()
    {
        // Arrange
        var created = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = "Original summary",
            Description = "Original description",
            Priority = TaskPriority.Low,
            Status = TaskStatus.NeedsAction,
            Categories = ["original"]
        });

        // Act: update summary, add categories, change priority
        var updated = await fixture.TaskService.UpdateTaskAsync(
            created with
            {
                Summary = "Updated summary",
                Priority = TaskPriority.High,
                Categories = ["updated-category", "another"],
                Status = TaskStatus.InProcess
            },
            TestContext.Current.CancellationToken);

        // Assert: fields changed
        updated.Summary.ShouldBe("Updated summary");
        updated.Priority.ShouldBe(TaskPriority.High);
        updated.Status.ShouldBe(TaskStatus.InProcess);
        updated.Categories.Count.ShouldBe(2);
        updated.Categories.ShouldContain("updated-category");

        // Assert: ETag changed (Radicale assigns new ETag on update)
        updated.ETag.ShouldNotBeNullOrEmpty();
        updated.ETag.ShouldNotBe(created.ETag,
            "ETag should change after an update when Radicale supports it");

        // Assert: description preserved
        updated.Description.ShouldBe("Original description");

        // Act: re-fetch from server to verify persistence
        var refetched = await fixture.TaskService.GetTaskAsync(updated.Href, TestContext.Current.CancellationToken);
        refetched!.Summary.ShouldBe("Updated summary");
        refetched.Priority.ShouldBe(TaskPriority.High);
    }

    // ── 6. Complete/update semantics through Core surface ──────────────────

    [Fact]
    public async Task UpdateTask_MarkCompleted_ReturnsCompletedStatus()
    {
        // Arrange
        var created = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = "Task to complete",
            Status = TaskStatus.NeedsAction,
            Priority = TaskPriority.Medium
        });

        var completedTime = new DateTimeOffset(2026, 7, 15, 14, 0, 0, TimeSpan.Zero);

        // Act: update task to Completed status with a Completed timestamp
        var completed = await fixture.TaskService.UpdateTaskAsync(
            created with { Status = TaskStatus.Completed, Completed = completedTime },
            TestContext.Current.CancellationToken);

        // Assert
        completed.Status.ShouldBe(TaskStatus.Completed);
        completed.Completed.ShouldNotBeNull();
        completed.Completed!.Value.UtcDateTime.ShouldBe(completedTime.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task UpdateTask_MarkCancelled_PreservesOtherFields()
    {
        // Arrange
        var due = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var created = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = "Task to cancel",
            Due = due,
            Priority = TaskPriority.High,
            Categories = ["important"],
            Status = TaskStatus.NeedsAction
        });

        // Act: cancel the task
        var cancelled = await fixture.TaskService.UpdateTaskAsync(
            created with { Status = TaskStatus.Cancelled },
            TestContext.Current.CancellationToken);

        // Assert: status changed, other fields preserved
        cancelled.Status.ShouldBe(TaskStatus.Cancelled);
        cancelled.Summary.ShouldBe("Task to cancel");
        cancelled.Priority.ShouldBe(TaskPriority.High);
        cancelled.Categories.ShouldContain("important");
        cancelled.Due.ShouldNotBeNull();
        cancelled.Due!.Value.UtcDateTime.ShouldBe(due.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    // ── 7. Delete task ────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteTask_TaskNoLongerFetchable()
    {
        // Arrange
        var created = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = "Task to delete",
            Priority = TaskPriority.Low
        });

        // Act
        await fixture.TaskService.DeleteTaskAsync(created.Href, created.ETag, TestContext.Current.CancellationToken);

        // Prevent cleanup from trying to delete an already-deleted task
        _createdTaskHrefs.Remove(created.Href);

        // Assert: task is gone
        var fetched = await fixture.TaskService.GetTaskAsync(created.Href, TestContext.Current.CancellationToken);
        fetched.ShouldBeNull();
    }

    // ── 8. Optimistic concurrency conflict ─────────────────────────────────

    [Fact]
    public async Task UpdateTask_WithStaleEtag_ThrowsConflictException()
    {
        // Arrange: create a task
        var created = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = "Concurrency test task",
            Priority = TaskPriority.None
        });

        // Act: perform an update to change the ETag on the server
        var firstUpdate = await fixture.TaskService.UpdateTaskAsync(
            created with { Summary = "First update" },
            TestContext.Current.CancellationToken);

        // Now attempt a second update using the ORIGINAL (stale) ETag
        var staleTask = created with { Summary = "Second update from stale etag" };

        // Assert: the conflict exception should be thrown
        var ex = await Should.ThrowAsync<CalDavConflictException>(
            fixture.TaskService.UpdateTaskAsync(staleTask, TestContext.Current.CancellationToken));

        ex.Href.ShouldBe(created.Href);
        // Note: Radicale does not return the current ETag in 412 responses.
        // The CurrentEtag may be null — this is an observed Radicale limitation,
        // not a bug in our code. The exception itself confirms optimistic concurrency works.
        AssertCurrentEtagIsValid(ex.CurrentEtag);
    }

    private static void AssertCurrentEtagIsValid(string? currentEtag)
    {
        if (currentEtag is not null)
        {
            currentEtag.ShouldNotBeEmpty("If provided, the current ETag should not be empty");
        }
    }
}
