using System.Reflection;
using System.Text.Json;
using CalDavTaskStatus = DotnetAgents.CalDav.Core.Models.TaskStatus;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public class ChatTaskToolsTests
{
    private readonly ITaskService _taskService;
    private readonly ChatTaskTools _sut;
    private readonly ITaskListResolver _taskListResolver;

    public ChatTaskToolsTests()
    {
        _taskService = Substitute.For<ITaskService>();
        _taskListResolver = Substitute.For<ITaskListResolver>();
        _sut = new ChatTaskTools(_taskService, _taskListResolver, TimeProvider.System);
    }

    [Fact]
    public async Task AddTaskToListAsync_ResolvesExplicitListByDisplayName()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new TaskList { Href = "/work/", DisplayName = "Work" } });
        _taskListResolver.ResolveAsync(Arg.Any<IReadOnlyList<TaskList>>(), "Work", Arg.Any<CancellationToken>())
            .Returns(new TaskList { Href = "/work/", DisplayName = "Work" });
        _taskService.CreateTaskAsync("/work/", Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(new TaskItem { Href = "/work/new.ics", Summary = "Draft" });

        await _sut.AddTaskToListAsync("Work", "Draft", cancellationToken: CancellationToken.None);

        await _taskService.Received(1).CreateTaskAsync(
            "/work/",
            Arg.Is<TaskItem>(task => task.Summary == "Draft"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddTaskToListAsync_UsesConfiguredDefault_WhenListNameOmitted()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns([
                new TaskList { Href = "/tasks/", DisplayName = "Tasks" },
                new TaskList { Href = "/shopping/", DisplayName = "Shopping" }
            ]);
        _taskListResolver.ResolveAsync(Arg.Any<IReadOnlyList<TaskList>>(), null, Arg.Any<CancellationToken>())
            .Returns(new TaskList { Href = "/tasks/", DisplayName = "Tasks", IsDefault = true });
        _taskService.CreateTaskAsync("/tasks/", Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(new TaskItem { Href = "/tasks/new.ics", Summary = "Buy milk" });

        await _sut.AddTaskToListAsync(null, "Buy milk", cancellationToken: CancellationToken.None);

        await _taskService.Received(1).CreateTaskAsync(
            "/tasks/",
            Arg.Is<TaskItem>(task => task.Summary == "Buy milk"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddTaskToListAsync_UsesAliasTaskList_ForDefaultTasksList()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns([
                new TaskList { Href = "/tasks/", DisplayName = "Tasks" },
                new TaskList { Href = "/shopping/", DisplayName = "Shopping" }
            ]);
        _taskListResolver.ResolveAsync(Arg.Any<IReadOnlyList<TaskList>>(), "task list", Arg.Any<CancellationToken>())
            .Returns(new TaskList { Href = "/tasks/", DisplayName = "Tasks", IsDefault = true });
        _taskService.CreateTaskAsync("/tasks/", Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(new TaskItem { Href = "/tasks/new.ics", Summary = "Fix sink" });

        await _sut.AddTaskToListAsync("task list", "Fix sink", cancellationToken: CancellationToken.None);

        await _taskService.Received(1).CreateTaskAsync(
            "/tasks/",
            Arg.Is<TaskItem>(task => task.Summary == "Fix sink"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FindTaskInListAsync_ReturnsMatchingTasks()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new TaskList { Href = "/work/", DisplayName = "Work" } });
        _taskListResolver.ResolveAsync(Arg.Any<IReadOnlyList<TaskList>>(), "Work", Arg.Any<CancellationToken>())
            .Returns(new TaskList { Href = "/work/", DisplayName = "Work" });
        _taskService.GetTasksAsync("/work/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new TaskItem { Href = "/work/1.ics", Summary = "Review" },
                new TaskItem { Href = "/work/2.ics", Summary = "Review" }
            });

        var json = await _sut.FindTaskInListAsync("Work", "Review", CancellationToken.None);
        var tasks = JsonSerializer.Deserialize<List<TaskItem>>(json);

        tasks.ShouldNotBeNull();
        tasks.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ListTasksInListAsync_UsesConfiguredDefault_WhenListNameOmitted()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns([
                new TaskList { Href = "/tasks/", DisplayName = "Tasks" },
                new TaskList { Href = "/shopping/", DisplayName = "Shopping" }
            ]);
        _taskListResolver.ResolveAsync(Arg.Any<IReadOnlyList<TaskList>>(), null, Arg.Any<CancellationToken>())
            .Returns(new TaskList { Href = "/tasks/", DisplayName = "Tasks", IsDefault = true });
        _taskService.GetTasksAsync("/tasks/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns([new TaskItem { Href = "/tasks/1.ics", Summary = "Pay bills" }]);

        var json = await _sut.ListTasksInListAsync(cancellationToken: CancellationToken.None);
        var tasks = JsonSerializer.Deserialize<List<TaskItem>>(json);

        tasks.ShouldNotBeNull();
        tasks.Count.ShouldBe(1);
        tasks[0].Summary.ShouldBe("Pay bills");
        await _taskService.Received(1).GetTasksAsync("/tasks/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTaskInListAsync_AmbiguousInSameList_ReturnsStructuredJson()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new TaskList { Href = "/work/", DisplayName = "Work" } });
        _taskListResolver.ResolveAsync(Arg.Any<IReadOnlyList<TaskList>>(), "Work", Arg.Any<CancellationToken>())
            .Returns(new TaskList { Href = "/work/", DisplayName = "Work" });
        _taskService.GetTasksAsync("/work/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new TaskItem { Href = "/work/1.ics", Summary = "Review", ETag = "\"1\"" },
                new TaskItem { Href = "/work/2.ics", Summary = "Review", ETag = "\"2\"" }
            });

        var json = await _sut.CompleteTaskInListAsync("Work", "Review", cancellationToken: CancellationToken.None);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().ShouldBe("ambiguous");
        doc.RootElement.GetProperty("summary").GetString().ShouldBe("Review");
        doc.RootElement.GetProperty("message").GetString()!.ShouldContain("Multiple tasks match");
        var candidates = doc.RootElement.GetProperty("candidates");
        candidates.GetArrayLength().ShouldBe(2);
        candidates[0].GetProperty("summary").GetString().ShouldBe("Review");
        candidates[0].GetProperty("taskListName").GetString().ShouldBe("Work");
        candidates[0].GetProperty("href").GetString().ShouldBe("/work/1.ics");
    }

    [Fact]
    public async Task AddTaskToListAsync_ThrowsWhenAliasIsAmbiguous()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns([
                new TaskList { Href = "/personal-tasks/", DisplayName = "Personal Tasks" },
                new TaskList { Href = "/work-tasks/", DisplayName = "Work Tasks" }
            ]);
        _taskListResolver.ResolveAsync(Arg.Any<IReadOnlyList<TaskList>>(), "tasks", Arg.Any<CancellationToken>())
            .Returns<Task<TaskList>>(_ => throw new InvalidOperationException("Task list alias 'tasks' is ambiguous. Matching lists: Personal Tasks, Work Tasks."));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.AddTaskToListAsync("tasks", "Plan sprint", cancellationToken: CancellationToken.None));

        ex.Message.ShouldContain("ambiguous", Case.Insensitive);
        ex.Message.ShouldContain("Personal Tasks");
        ex.Message.ShouldContain("Work Tasks");
    }

    [Fact]
    public async Task DeleteTaskInListAsync_UsesResolvedTaskHref()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new TaskList { Href = "/work/", DisplayName = "Work" } });
        _taskListResolver.ResolveAsync(Arg.Any<IReadOnlyList<TaskList>>(), "Work", Arg.Any<CancellationToken>())
            .Returns(new TaskList { Href = "/work/", DisplayName = "Work" });
        _taskService.GetTasksAsync("/work/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new TaskItem { Href = "/work/1.ics", Summary = "Review", ETag = "\"1\"" } });
        _taskService.DeleteTaskAsync("/work/1.ics", "\"1\"", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var json = await _sut.DeleteTaskInListAsync("Work", "Review", cancellationToken: CancellationToken.None);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("Href").GetString().ShouldBe("/work/1.ics");
        doc.RootElement.GetProperty("Deleted").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task FindTaskInListAsync_WithoutListName_SearchesAcrossVisibleLists()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns([
                new TaskList { Href = "/tasks/", DisplayName = "Tasks" },
                new TaskList { Href = "/shopping/", DisplayName = "Shopping" }
            ]);
        _taskService.GetTasksAsync("/tasks/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns([new TaskItem { Href = "/tasks/1.ics", Summary = "Buy milk" }]);
        _taskService.GetTasksAsync("/shopping/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns([new TaskItem { Href = "/shopping/1.ics", Summary = "Buy milk" }]);

        var json = await _sut.FindTaskInListAsync(null, "Buy milk", CancellationToken.None);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task CompleteTaskInListAsync_AmbiguousAcrossLists_ReturnsStructuredJson()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns([
                new TaskList { Href = "/tasks/", DisplayName = "Tasks" },
                new TaskList { Href = "/shopping/", DisplayName = "Shopping" }
            ]);
        _taskService.GetTasksAsync("/tasks/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns([new TaskItem { Href = "/tasks/1.ics", Summary = "Strawberry", ETag = "\"1\"" }]);
        _taskService.GetTasksAsync("/shopping/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns([new TaskItem { Href = "/shopping/1.ics", Summary = "Strawberry", ETag = "\"2\"" }]);

        var json = await _sut.CompleteTaskInListAsync(null, "Strawberry", cancellationToken: CancellationToken.None);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().ShouldBe("ambiguous");
        doc.RootElement.GetProperty("summary").GetString().ShouldBe("Strawberry");
        var candidates = doc.RootElement.GetProperty("candidates");
        candidates.GetArrayLength().ShouldBe(2);
        candidates[0].GetProperty("summary").GetString().ShouldBe("Strawberry");
        candidates[0].GetProperty("taskListName").GetString().ShouldBe("Tasks");
        candidates[0].GetProperty("href").GetString().ShouldBe("/tasks/1.ics");
    }

    [Fact]
    public void ChatTools_DescriptionsSteerTowardListNames()
    {
        GetDescription(nameof(ChatTaskTools.AddTaskToListAsync)).ShouldBe(
            "Create a task in a user-facing task list name. If the user says 'add a task ...' and does not name a list, omit listName so the configured default task list is used. Explicit list names always win over task content. Never choose a list based on what the task sounds like.");

        GetDescription(nameof(ChatTaskTools.CompleteTaskInListAsync)).ShouldBe(
            "Mark a task as completed by summary. If the user named a list, only that list is searched. If they did not name a list, all visible lists are searched. If zero tasks match, returns not_found. If multiple tasks match, returns ambiguous with candidates instead of guessing. This is a single-target tool: never complete multiple tasks in one call or by repeating this tool for a bulk request without explicit per-target confirmation.");

        GetDescription(nameof(ChatTaskTools.DeleteTaskInListAsync)).ShouldBe(
            "Delete a task by summary. If the user named a list, only that list is searched. If they did not name a list, all visible lists are searched. If zero tasks match, returns not_found. If multiple tasks match, returns ambiguous with candidates instead of guessing. This is a single-target tool: never delete multiple tasks in one call or by repeating this tool for a bulk request without explicit per-target confirmation.");

        GetDescription(nameof(ChatTaskTools.FindTaskInListAsync)).ShouldBe(
            "Find tasks by summary text. If the user named a list, pass listName and search only that list. If they did not name a list, this tool searches all visible lists and returns all matches. Use this for read-only lookup only. Do not use this as the first step of a destructive flow; for mutations, call complete_task_by_summary or delete_task_by_summary directly because they enforce single-target safety.");
    }

    private static string GetDescription(string methodName)
    {
        var method = typeof(ChatTaskTools).GetMethod(methodName);
        method.ShouldNotBeNull();

        var attr = method!.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
        attr.ShouldNotBeNull();
        return attr!.Description;
    }

    [Fact]
    public async Task ListTasksInListAsync_WithStatusFilter_PassesStatusToQuery()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns([new TaskList { Href = "/tasks/", DisplayName = "Tasks" }]);
        _taskListResolver.ResolveAsync(Arg.Any<IReadOnlyList<TaskList>>(), "Tasks", Arg.Any<CancellationToken>())
            .Returns(new TaskList { Href = "/tasks/", DisplayName = "Tasks" });
        _taskService.GetTasksAsync("/tasks/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns([new TaskItem { Href = "/tasks/1.ics", Summary = "Done task", Status = CalDavTaskStatus.Completed }]);

        var json = await _sut.ListTasksInListAsync("Tasks", status: "Completed", cancellationToken: CancellationToken.None);

        json.ShouldContain("Done task");
        await _taskService.Received(1).GetTasksAsync("/tasks/",
            Arg.Is<TaskQuery>(q => q.Status == CalDavTaskStatus.Completed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddTaskToListAsync_WithStatusAndPriority_MapsValues()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns([new TaskList { Href = "/work/", DisplayName = "Work" }]);
        _taskListResolver.ResolveAsync(Arg.Any<IReadOnlyList<TaskList>>(), "Work", Arg.Any<CancellationToken>())
            .Returns(new TaskList { Href = "/work/", DisplayName = "Work" });
        _taskService.CreateTaskAsync("/work/", Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(new TaskItem { Href = "/work/new.ics", Summary = "Urgent" });

        await _sut.AddTaskToListAsync("Work", "Urgent", status: "InProcess", priority: "High",
            cancellationToken: CancellationToken.None);

        await _taskService.Received(1).CreateTaskAsync("/work/",
            Arg.Is<TaskItem>(t => t.Status == CalDavTaskStatus.InProcess && t.Priority == TaskPriority.High),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteTaskInListAsync_NotFound_ReturnsStructuredJsonWithAvailableLists()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns([
                new TaskList { Href = "/tasks/", DisplayName = "Tasks" },
                new TaskList { Href = "/shopping/", DisplayName = "Shopping" }
            ]);
        _taskService.GetTasksAsync("/tasks/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns([new TaskItem { Href = "/tasks/1.ics", Summary = "Other task" }]);
        _taskService.GetTasksAsync("/shopping/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TaskItem>());

        var json = await _sut.DeleteTaskInListAsync(null, "Nonexistent", cancellationToken: CancellationToken.None);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().ShouldBe("not_found");
        doc.RootElement.GetProperty("summary").GetString().ShouldBe("Nonexistent");
        doc.RootElement.GetProperty("message").GetString()!.ShouldContain("not found");
        var availableLists = doc.RootElement.GetProperty("availableLists");
        availableLists.GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task DeleteTaskInListAsync_AmbiguousMatch_ReturnsStructuredJson()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns([
                new TaskList { Href = "/shopping/", DisplayName = "Shopping" },
                new TaskList { Href = "/tasks/", DisplayName = "Tasks" }
            ]);
        _taskService.GetTasksAsync("/shopping/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns([new TaskItem { Href = "/shopping/1.ics", Summary = "buy milk", ETag = "\"1\"" }]);
        _taskService.GetTasksAsync("/tasks/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns([new TaskItem { Href = "/tasks/2.ics", Summary = "buy milk", ETag = "\"2\"" }]);

        var json = await _sut.DeleteTaskInListAsync(null, "buy milk", cancellationToken: CancellationToken.None);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().ShouldBe("ambiguous");
        doc.RootElement.GetProperty("summary").GetString().ShouldBe("buy milk");
        doc.RootElement.GetProperty("message").GetString()!.ShouldContain("Multiple tasks match");
        var candidates = doc.RootElement.GetProperty("candidates");
        candidates.GetArrayLength().ShouldBe(2);
        candidates[0].GetProperty("summary").GetString().ShouldBe("buy milk");
        candidates[0].GetProperty("taskListName").GetString().ShouldBe("Shopping");
        candidates[0].GetProperty("href").GetString().ShouldBe("/shopping/1.ics");
    }

    [Fact]
    public async Task CompleteTaskInListAsync_NotFound_WithExplicitListName_ReturnsStructuredJson()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns([new TaskList { Href = "/work/", DisplayName = "Work" }]);
        _taskListResolver.ResolveAsync(Arg.Any<IReadOnlyList<TaskList>>(), "Work", Arg.Any<CancellationToken>())
            .Returns(new TaskList { Href = "/work/", DisplayName = "Work" });
        _taskService.GetTasksAsync("/work/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TaskItem>());

        var json = await _sut.CompleteTaskInListAsync("Work", "Missing task", cancellationToken: CancellationToken.None);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("status").GetString().ShouldBe("not_found");
        doc.RootElement.GetProperty("summary").GetString().ShouldBe("Missing task");
        doc.RootElement.GetProperty("message").GetString()!.ShouldContain("not found");
        doc.RootElement.GetProperty("message").GetString()!.ShouldContain("Work");
        var availableLists = doc.RootElement.GetProperty("availableLists");
        availableLists.GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task FindTaskInListAsync_ThrowsOnEmptySummary()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns([new TaskList { Href = "/tasks/", DisplayName = "Tasks" }]);
        _taskListResolver.ResolveAsync(Arg.Any<IReadOnlyList<TaskList>>(), "Tasks", Arg.Any<CancellationToken>())
            .Returns(new TaskList { Href = "/tasks/", DisplayName = "Tasks" });

        var ex = await Should.ThrowAsync<ArgumentException>(
            () => _sut.FindTaskInListAsync("Tasks", "  ", CancellationToken.None));

        ex.Message.ShouldContain("summary");
    }

    [Fact]
    public async Task CompleteTaskInListAsync_UniqueMatch_CompletesSuccessfully()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns([new TaskList { Href = "/tasks/", DisplayName = "Tasks" }]);
        _taskListResolver.ResolveAsync(Arg.Any<IReadOnlyList<TaskList>>(), "Tasks", Arg.Any<CancellationToken>())
            .Returns(new TaskList { Href = "/tasks/", DisplayName = "Tasks" });
        _taskService.GetTasksAsync("/tasks/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns([new TaskItem { Href = "/tasks/1.ics", Summary = "Buy milk", ETag = "\"etag1\"" }]);
        _taskService.UpdateTaskAsync(Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<TaskItem>());

        var json = await _sut.CompleteTaskInListAsync("Tasks", "Buy milk", cancellationToken: CancellationToken.None);

        json.ShouldContain("Completed");
        await _taskService.Received(1).UpdateTaskAsync(
            Arg.Is<TaskItem>(t => t.Status == CalDavTaskStatus.Completed && t.ETag == "\"etag1\""),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteTaskInListAsync_WithExplicitEtag_UsesProvidedEtag()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns([new TaskList { Href = "/work/", DisplayName = "Work" }]);
        _taskListResolver.ResolveAsync(Arg.Any<IReadOnlyList<TaskList>>(), "Work", Arg.Any<CancellationToken>())
            .Returns(new TaskList { Href = "/work/", DisplayName = "Work" });
        _taskService.GetTasksAsync("/work/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns([new TaskItem { Href = "/work/1.ics", Summary = "Review", ETag = "\"old\"" }]);
        _taskService.DeleteTaskAsync("/work/1.ics", "\"override\"", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.DeleteTaskInListAsync("Work", "Review", etag: "\"override\"", cancellationToken: CancellationToken.None);

        await _taskService.Received(1).DeleteTaskAsync("/work/1.ics", "\"override\"", Arg.Any<CancellationToken>());
    }
}
