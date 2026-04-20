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
    public async Task CompleteTaskInListAsync_ThrowsWhenSummaryAmbiguous()
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

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.CompleteTaskInListAsync("Work", "Review", cancellationToken: CancellationToken.None));

        ex.Message.ShouldContain("ambiguous");
        ex.Message.ShouldContain("/work/1.ics");
        ex.Message.ShouldContain("/work/2.ics");
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
    public async Task CompleteTaskInListAsync_WithoutListName_ThrowsWhenMatchesExistAcrossLists()
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

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.CompleteTaskInListAsync(null, "Strawberry", cancellationToken: CancellationToken.None));

        ex.Message.ShouldContain("ambiguous", Case.Insensitive);
        ex.Message.ShouldContain("Shopping");
        ex.Message.ShouldContain("Tasks");
    }

    [Fact]
    public void ChatTools_DescriptionsSteerTowardListNames()
    {
        var method = typeof(ChatTaskTools).GetMethod(nameof(ChatTaskTools.AddTaskToListAsync));
        method.ShouldNotBeNull();

        var attr = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
        attr.ShouldNotBeNull();
        attr!.Description.ShouldContain("default", Case.Insensitive);
        attr.Description.ShouldContain("Explicit list names always win", Case.Insensitive);
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
    public async Task DeleteTaskInListAsync_ThrowsNotFound_WithoutListName()
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

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.DeleteTaskInListAsync(null, "Nonexistent", cancellationToken: CancellationToken.None));

        ex.Message.ShouldContain("not found");
        ex.Message.ShouldContain("any visible task list");
    }

    [Fact]
    public async Task CompleteTaskInListAsync_ThrowsNotFound_WithExplicitListName()
    {
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns([new TaskList { Href = "/work/", DisplayName = "Work" }]);
        _taskListResolver.ResolveAsync(Arg.Any<IReadOnlyList<TaskList>>(), "Work", Arg.Any<CancellationToken>())
            .Returns(new TaskList { Href = "/work/", DisplayName = "Work" });
        _taskService.GetTasksAsync("/work/", Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TaskItem>());

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.CompleteTaskInListAsync("Work", "Missing task", cancellationToken: CancellationToken.None));

        ex.Message.ShouldContain("not found");
        ex.Message.ShouldContain("Work");
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
