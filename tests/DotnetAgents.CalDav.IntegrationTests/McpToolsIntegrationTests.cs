using System.Text.Json;
using CalDavConflictException = DotnetAgents.CalDav.Core.CalDavConflictException;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.IntegrationTests.Fixtures;
using DotnetAgents.CalDav.Mcp.Tests.Unit;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using TaskItem = DotnetAgents.CalDav.Core.Models.TaskItem;
using TaskPriority = DotnetAgents.CalDav.Core.Models.TaskPriority;
using TaskStatus = DotnetAgents.CalDav.Core.Models.TaskStatus;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

[Collection("RadicaleCollection")]
public sealed class McpToolsIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset FixedNow = new(2025, 6, 15, 12, 30, 0, TimeSpan.Zero);
    private readonly RadicaleFixture _fixture;
    private readonly TaskListTools _taskListTools;
    private readonly TaskQueryTools _taskQueryTools;
    private readonly TaskMutationTools _taskMutationTools;
    private readonly ChatTaskTools _chatTaskTools;
    private readonly List<string> _createdTaskHrefs = [];

    public McpToolsIntegrationTests(RadicaleFixture fixture)
    {
        _fixture = fixture;
        var services = new ServiceCollection();
        services.AddCalDavTasks(options =>
        {
            options.BaseUrl = _fixture.BaseUrl;
            options.Username = "caldavtest";
            options.Password = "caldavtest123";
            options.DefaultTaskList = "Tasks";
        });
        using var serviceProvider = services.BuildServiceProvider();
        var taskListResolver = serviceProvider.GetRequiredService<Core.Abstractions.ITaskListResolver>();
        _taskListTools = new TaskListTools(_fixture.TaskService, taskListResolver);
        _taskQueryTools = new TaskQueryTools(_fixture.TaskService);
        _taskMutationTools = new TaskMutationTools(_fixture.TaskService, new FixedTimeProvider(FixedNow));
        _chatTaskTools = new ChatTaskTools(_fixture.TaskService, taskListResolver, new FixedTimeProvider(FixedNow));
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var href in _createdTaskHrefs.Distinct().ToList())
        {
            try
            {
                await _fixture.TaskService.DeleteTaskAsync(href, null, TestContext.Current.CancellationToken);
            }
            catch (Exception ex)
            {
                _ = ex;
            }
        }
    }

    [Fact]
    public async Task ListTaskLists_ReturnsNonEmptyList()
    {
        var json = await _taskListTools.ListTaskListsAsync(TestContext.Current.CancellationToken);
        var taskLists = Deserialize<List<TaskList>>(json);

        taskLists.ShouldNotBeEmpty();
        taskLists.ShouldContain(taskList => taskList.Href.TrimEnd('/') == _fixture.TaskListHref.TrimEnd('/'));
    }

    [Fact]
    public async Task ListTaskLists_ContainsExpectedTaskListFields()
    {
        var json = await _taskListTools.ListTaskListsAsync(TestContext.Current.CancellationToken);
        var taskLists = Deserialize<List<TaskList>>(json);
        var taskList = taskLists.First(taskList => taskList.Href.TrimEnd('/') == _fixture.TaskListHref.TrimEnd('/'));

        taskList.DisplayName.ShouldBe("Tasks");
        taskList.SupportedComponents.ShouldContain(component => component.Equals("VTODO", StringComparison.OrdinalIgnoreCase));
        taskList.Href.ShouldBe(_fixture.TaskListHref);
        taskList.IsDefault.ShouldBeTrue();
    }

    [Fact]
    public async Task ListTaskLists_ReturnsProvisionedMultiListFixture()
    {
        var json = await _taskListTools.ListTaskListsAsync(TestContext.Current.CancellationToken);
        var taskLists = Deserialize<List<TaskList>>(json);

        taskLists.ShouldContain(taskList => taskList.Href.TrimEnd('/') == _fixture.TaskListHref.TrimEnd('/'));
        taskLists.ShouldContain(taskList => taskList.Href.TrimEnd('/') == _fixture.ShoppingListHref.TrimEnd('/'));
        taskLists.ShouldContain(taskList => taskList.Href.TrimEnd('/') == _fixture.WorkListHref.TrimEnd('/'));
    }

    [Fact]
    public async Task ListTasks_ReturnsCreatedTasks()
    {
        var created = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = UniqueValue("list-created-task"),
            Status = TaskStatus.NeedsAction
        });

        var json = await _taskQueryTools.ListTasksAsync(_fixture.TaskListHref, cancellationToken: TestContext.Current.CancellationToken);
        var tasks = Deserialize<List<TaskItem>>(json);

        tasks.ShouldContain(task => task.Uid == created.Uid);
    }

    [Fact]
    public async Task ListTasks_WithStatusFilter_ReturnsMatchingTasks()
    {
        var needsActionTask = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = UniqueValue("needs-action"),
            Status = TaskStatus.NeedsAction
        });

        var completedTask = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = UniqueValue("completed"),
            Status = TaskStatus.Completed,
            Completed = DateTimeOffset.UtcNow
        });

        var json = await _taskQueryTools.ListTasksAsync(
            _fixture.TaskListHref,
            status: "NeedsAction",
            cancellationToken: TestContext.Current.CancellationToken);
        var tasks = Deserialize<List<TaskItem>>(json);

        tasks.ShouldContain(task => task.Uid == needsActionTask.Uid);
        tasks.ShouldNotContain(task => task.Uid == completedTask.Uid);
    }

    [Fact]
    public async Task ListTasks_WithTextSearch_ReturnsMatchingTasks()
    {
        var uniqueTerm = UniqueValue("unique-term");
        var matchingTask = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = $"Summary {uniqueTerm}",
            Description = "Text search match"
        });

        var otherTask = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = UniqueValue("other-summary"),
            Description = "No matching text"
        });

        var json = await _taskQueryTools.ListTasksAsync(
            _fixture.TaskListHref,
            textSearch: uniqueTerm,
            cancellationToken: TestContext.Current.CancellationToken);
        var tasks = Deserialize<List<TaskItem>>(json);

        tasks.Count.ShouldBe(1);
        tasks.ShouldContain(task => task.Uid == matchingTask.Uid);
        tasks.ShouldNotContain(task => task.Uid == otherTask.Uid);
    }

    [Fact]
    public async Task ListTasks_WithCategoryFilter_ReturnsMatchingTasks()
    {
        var category = UniqueValue("integration-test");
        var taggedTask = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = UniqueValue("category-match"),
            Categories = [category]
        });

        var otherTask = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = UniqueValue("category-other"),
            Categories = [UniqueValue("other-category")]
        });

        var json = await _taskQueryTools.ListTasksAsync(
            _fixture.TaskListHref,
            category: category,
            cancellationToken: TestContext.Current.CancellationToken);
        var tasks = Deserialize<List<TaskItem>>(json);

        tasks.ShouldContain(task => task.Uid == taggedTask.Uid);
        tasks.ShouldNotContain(task => task.Uid == otherTask.Uid);
    }

    [Fact]
    public async Task ListTasks_WithDueDateRange_ReturnsMatchingTasks()
    {
        var matchingTask = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = UniqueValue("march-due"),
            Due = new DateTimeOffset(2026, 3, 15, 12, 0, 0, TimeSpan.Zero)
        });

        var outsideTask = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = UniqueValue("may-due"),
            Due = new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero)
        });

        var json = await _taskQueryTools.ListTasksAsync(
            _fixture.TaskListHref,
            dueAfter: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero),
            dueBefore: new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            cancellationToken: TestContext.Current.CancellationToken);
        var tasks = Deserialize<List<TaskItem>>(json);

        tasks.ShouldContain(task => task.Uid == matchingTask.Uid);
        tasks.ShouldNotContain(task => task.Uid == outsideTask.Uid);
    }

    [Fact]
    public async Task GetTask_ReturnsTaskByHref()
    {
        var created = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = UniqueValue("get-task"),
            Description = "Fetch by href",
            Status = TaskStatus.InProcess,
            Priority = TaskPriority.Medium
        });

        var json = await _taskQueryTools.GetTaskAsync(created.Href, TestContext.Current.CancellationToken);
        var task = Deserialize<TaskItem>(json);

        task.Uid.ShouldBe(created.Uid);
        task.Summary.ShouldBe(created.Summary);
        task.Href.ShouldBe(created.Href);
        task.ETag.ShouldBe(created.ETag);
    }

    [Fact]
    public async Task GetTask_ThrowsWhenNotFound()
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () => _taskQueryTools.GetTaskAsync("/nonexistent/path.ics", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateTask_WithAllParameters_ReturnsCreatedTask()
    {
        var due = new DateTimeOffset(2026, 3, 20, 9, 30, 0, TimeSpan.Zero);
        var summary = UniqueValue("create-full");
        var description = "Created through MCP integration";

        var json = await _taskMutationTools.CreateTaskAsync(
            _fixture.TaskListHref,
            summary,
            description,
            status: "InProcess",
            priority: "High",
            due: due,
            cancellationToken: TestContext.Current.CancellationToken);
        var task = Track(Deserialize<TaskItem>(json));

        task.Uid.ShouldNotBeNullOrWhiteSpace();
        task.Href.ShouldNotBeNullOrWhiteSpace();
        task.ETag.ShouldNotBeNullOrWhiteSpace();
        task.Summary.ShouldBe(summary);
        task.Description.ShouldBe(description);
        task.Status.ShouldBe(TaskStatus.InProcess);
        task.Priority.ShouldBe(TaskPriority.High);
        task.Due.ShouldNotBeNull();
        task.Due!.Value.UtcDateTime.ShouldBe(due.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CreateTask_WithMinimalParameters_ReturnsCreatedTask()
    {
        var summary = UniqueValue("create-minimal");

        var json = await _taskMutationTools.CreateTaskAsync(
            _fixture.TaskListHref,
            summary,
            cancellationToken: TestContext.Current.CancellationToken);
        var task = Track(Deserialize<TaskItem>(json));

        task.Uid.ShouldNotBeNullOrWhiteSpace();
        task.Href.ShouldNotBeNullOrWhiteSpace();
        task.Summary.ShouldBe(summary);
        task.Status.ShouldBe(TaskStatus.NeedsAction);
    }

    [Fact]
    public async Task UpdateTask_ChangesFieldsAndPreservesOthers()
    {
        var created = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = UniqueValue("update-original"),
            Description = "Original description",
            Status = TaskStatus.NeedsAction,
            Priority = TaskPriority.Low
        });

        var json = await _taskMutationTools.UpdateTaskAsync(
            created.Href,
            summary: "Updated",
            cancellationToken: TestContext.Current.CancellationToken);
        var updated = Deserialize<TaskItem>(json);

        updated.Summary.ShouldBe("Updated");
        updated.Description.ShouldBe(created.Description);
        updated.Priority.ShouldBe(created.Priority);
    }

    [Fact]
    public async Task UpdateTask_ThrowsWhenTaskNotFound()
    {
        await Should.ThrowAsync<InvalidOperationException>(
            () => _taskMutationTools.UpdateTaskAsync(
                "/nonexistent.ics",
                summary: "X",
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CompleteTask_SetsStatusAndCompletedTimestamp()
    {
        var created = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = UniqueValue("complete-task"),
            Status = TaskStatus.NeedsAction
        });

        var json = await _taskMutationTools.CompleteTaskAsync(created.Href, cancellationToken: TestContext.Current.CancellationToken);
        var completed = Deserialize<TaskItem>(json);

        completed.Status.ShouldBe(TaskStatus.Completed);
        completed.Completed.ShouldNotBeNull();
        completed.Completed!.Value.UtcDateTime.ShouldBe(FixedNow.UtcDateTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task CompleteTask_WithExplicitEtag_PassesEtag()
    {
        var created = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = UniqueValue("complete-explicit-etag"),
            Status = TaskStatus.NeedsAction
        });

        var staleEtag = created.ETag;
        staleEtag.ShouldNotBeNullOrWhiteSpace();

        var updated = await _fixture.TaskService.UpdateTaskAsync(
            created with { Summary = UniqueValue("etag-updated") },
            TestContext.Current.CancellationToken);

        updated.ETag.ShouldNotBe(staleEtag);

        await Should.ThrowAsync<CalDavConflictException>(
            () => _taskMutationTools.CompleteTaskAsync(
                created.Href,
                etag: staleEtag,
                cancellationToken: TestContext.Current.CancellationToken));

        var refetched = await _fixture.TaskService.GetTaskAsync(created.Href, TestContext.Current.CancellationToken);
        refetched.ShouldNotBeNull();
        refetched!.ETag.ShouldBe(updated.ETag);
        refetched.Status.ShouldBe(updated.Status);
        refetched.Completed.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteTask_ReturnsDeletedFlag()
    {
        var created = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = UniqueValue("delete-task"),
            Status = TaskStatus.NeedsAction
        });

        var json = await _taskMutationTools.DeleteTaskAsync(created.Href, cancellationToken: TestContext.Current.CancellationToken);
        var result = Deserialize<DeleteTaskResult>(json);
        Untrack(created.Href);

        result.Href.ShouldBe(created.Href);
        result.Deleted.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteTask_TaskNoLongerFetchable()
    {
        var created = await CreateTrackedTaskAsync(new TaskItem
        {
            Summary = UniqueValue("delete-fetch"),
            Status = TaskStatus.NeedsAction
        });

        await _taskMutationTools.DeleteTaskAsync(created.Href, cancellationToken: TestContext.Current.CancellationToken);
        Untrack(created.Href);

        await Should.ThrowAsync<InvalidOperationException>(
            () => _taskQueryTools.GetTaskAsync(created.Href, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChatAddTask_WithExplicitShoppingList_CreatesInShoppingList()
    {
        var summary = UniqueValue("shopping-item");

        var json = await _chatTaskTools.AddTaskToListAsync(
            "Shopping",
            summary,
            cancellationToken: TestContext.Current.CancellationToken);
        var task = Track(Deserialize<TaskItem>(json));

        task.Href.ShouldStartWith(_fixture.ShoppingListHref);
        task.Summary.ShouldBe(summary);
    }

    [Fact]
    public async Task ChatAddTask_WithoutListName_UsesDefaultTasksList()
    {
        var summary = UniqueValue("default-task");

        var json = await _chatTaskTools.AddTaskToListAsync(
            null,
            summary,
            cancellationToken: TestContext.Current.CancellationToken);
        var task = Track(Deserialize<TaskItem>(json));

        task.Href.ShouldStartWith(_fixture.TaskListHref);
        task.Summary.ShouldBe(summary);
    }

    [Fact]
    public async Task ChatListTasks_WithAliasTaskList_UsesDefaultTasksListOnly()
    {
        var taskSummary = UniqueValue("tasks-only");
        var shoppingSummary = UniqueValue("shopping-only");
        await CreateTrackedTaskAsync(new TaskItem { Summary = taskSummary });
        var shoppingTask = await _fixture.TaskService.CreateTaskAsync(
            _fixture.ShoppingListHref,
            new TaskItem { Summary = shoppingSummary },
            TestContext.Current.CancellationToken);
        Track(shoppingTask);

        var json = await _chatTaskTools.ListTasksInListAsync(
            "task list",
            cancellationToken: TestContext.Current.CancellationToken);
        var tasks = Deserialize<List<TaskItem>>(json);

        tasks.ShouldContain(task => task.Summary == taskSummary);
        tasks.ShouldNotContain(task => task.Summary == shoppingSummary);
    }

    [Fact]
    public async Task ChatDeleteTaskBySummary_ThrowsWhenMultipleTasksMatchInSameList()
    {
        var summary = UniqueValue("duplicate-delete");
        await CreateTrackedTaskAsync(new TaskItem { Summary = summary });
        await CreateTrackedTaskAsync(new TaskItem { Summary = summary });

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _chatTaskTools.DeleteTaskInListAsync(
                "Tasks",
                summary,
                cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("ambiguous", Case.Insensitive);
        ex.Message.ShouldContain(".ics");
    }

    private async Task<TaskItem> CreateTrackedTaskAsync(TaskItem task)
    {
        var created = await _fixture.TaskService.CreateTaskAsync(_fixture.TaskListHref, task, TestContext.Current.CancellationToken);
        return Track(created);
    }

    private TaskItem Track(TaskItem task)
    {
        if (!string.IsNullOrWhiteSpace(task.Href) && !_createdTaskHrefs.Contains(task.Href))
        {
            _createdTaskHrefs.Add(task.Href);
        }

        return task;
    }

    private void Untrack(string href)
    {
        _createdTaskHrefs.Remove(href);
    }

    private static T Deserialize<T>(string json)
    {
        var value = JsonSerializer.Deserialize<T>(json);
        if (value is null)
        {
            throw new InvalidOperationException($"Failed to deserialize JSON to {typeof(T).Name}.");
        }

        return value;
    }

    private static string UniqueValue(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private sealed record DeleteTaskResult
    {
        public string Href { get; init; } = string.Empty;

        public bool Deleted { get; init; }
    }
}
