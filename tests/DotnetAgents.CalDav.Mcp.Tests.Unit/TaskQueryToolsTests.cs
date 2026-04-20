using System.Text.Json;
using CalDavTaskStatus = DotnetAgents.CalDav.Core.Models.TaskStatus;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public class TaskQueryToolsTests
{
    private readonly ITaskService _taskService;
    private readonly TaskQueryTools _sut;

    public TaskQueryToolsTests()
    {
        _taskService = Substitute.For<ITaskService>();
        _sut = new TaskQueryTools(_taskService);
    }

    // ─── list_tasks (via TaskQueryTools) ────────────────────────────────────

    [Fact]
    public async Task ListTasksAsync_ReturnsJsonWithTaskData()
    {
        // Arrange
        var taskListHref = "/calendars/user/tasks/";
        var tasks = new List<TaskItem>
        {
            new()
            {
                Uid = "task-42",
                Summary = "Review PR",
                Description = "Review the CalDAV PR",
                Status = CalDavTaskStatus.InProcess,
                Priority = TaskPriority.Medium,
                Due = new DateTimeOffset(2025, 6, 15, 17, 0, 0, TimeSpan.Zero),
                Href = "/calendars/user/tasks/task-42.ics",
                Categories = ["work", "review"],
                ETag = "\"etag-42\""
            }
        };

        _taskService.GetTasksAsync(taskListHref, Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TaskItem>>(tasks));

        // Act
        var result = await _sut.ListTasksAsync(
            taskListHref,
            status: null,
            dueAfter: null,
            dueBefore: null,
            textSearch: null,
            category: null,
            cancellationToken: CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.ValueKind.ShouldBe(JsonValueKind.Array);
        root.GetArrayLength().ShouldBe(1);

        var item = root[0];
        item.GetProperty("Uid").GetString().ShouldBe("task-42");
        item.GetProperty("Summary").GetString().ShouldBe("Review PR");
        item.GetProperty("Description").GetString().ShouldBe("Review the CalDAV PR");
        item.GetProperty("Href").GetString().ShouldBe("/calendars/user/tasks/task-42.ics");
    }

    [Fact]
    public async Task ListTasksAsync_PassesQueryParametersToService()
    {
        // Arrange
        var taskListHref = "/calendars/user/work/";
        var dueAfter = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var dueBefore = new DateTimeOffset(2025, 6, 30, 23, 59, 59, TimeSpan.Zero);

        _taskService.GetTasksAsync(Arg.Any<string>(), Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TaskItem>>([]));

        // Act
        await _sut.ListTasksAsync(
            taskListHref,
            status: "Completed",
            dueAfter: dueAfter,
            dueBefore: dueBefore,
            textSearch: "quarterly",
            category: "finance",
            cancellationToken: CancellationToken.None);

        // Assert
        await _taskService.Received(1).GetTasksAsync(
            taskListHref,
            Arg.Is<TaskQuery>(q =>
                q.Status == CalDavTaskStatus.Completed &&
                q.DueAfter == dueAfter &&
                q.DueBefore == dueBefore &&
                q.TextSearch == "quarterly" &&
                q.Category == "finance"),
            Arg.Any<CancellationToken>());
    }

    // ─── get_task (via TaskQueryTools) ──────────────────────────────────────

    [Fact]
    public async Task GetTaskAsync_ReturnsJsonContainingFetchedTask()
    {
        // Arrange
        var href = "/calendars/user/tasks/task-99.ics";
        var task = new TaskItem
        {
            Uid = "task-99",
            Summary = "Deploy to prod",
            Status = CalDavTaskStatus.NeedsAction,
            Priority = TaskPriority.High,
            Href = href
        };

        _taskService.GetTaskAsync(href, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TaskItem?>(task));

        // Act
        var result = await _sut.GetTaskAsync(href, CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("Uid").GetString().ShouldBe("task-99");
        root.GetProperty("Summary").GetString().ShouldBe("Deploy to prod");
        root.GetProperty("Href").GetString().ShouldBe(href);
    }

    // ─── Error path shape ──────────────────────────────────────────────────

    [Fact]
    public async Task GetTaskAsync_ServiceThrowsException_ExceptionPropagates()
    {
        // Arrange
        var href = "/calendars/user/tasks/missing.ics";
        _taskService.GetTaskAsync(href, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Resource not found"));

        // Act & Assert — exception should NOT be swallowed
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.GetTaskAsync(href, CancellationToken.None));

        ex.Message.ShouldBe("Resource not found");
    }

    // ─── Status parsing ────────────────────────────────────────────────────

    [Theory]
    [InlineData("needsaction", CalDavTaskStatus.NeedsAction)]
    [InlineData("NEEDSACTION", CalDavTaskStatus.NeedsAction)]
    [InlineData("NeedsAction", CalDavTaskStatus.NeedsAction)]
    [InlineData("completed", CalDavTaskStatus.Completed)]
    [InlineData("COMPLETED", CalDavTaskStatus.Completed)]
    [InlineData("inprocess", CalDavTaskStatus.InProcess)]
    [InlineData("cancelled", CalDavTaskStatus.Cancelled)]
    public async Task ListTasksAsync_AcceptsCaseInsensitiveStatusValues(string statusInput, CalDavTaskStatus expectedStatus)
    {
        // Arrange
        var taskListHref = "/calendars/user/tasks/";

        _taskService.GetTasksAsync(Arg.Any<string>(), Arg.Any<TaskQuery>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TaskItem>>([]));

        // Act
        await _sut.ListTasksAsync(
            taskListHref,
            status: statusInput,
            dueAfter: null,
            dueBefore: null,
            textSearch: null,
            category: null,
            cancellationToken: CancellationToken.None);

        // Assert
        await _taskService.Received(1).GetTasksAsync(
            taskListHref,
            Arg.Is<TaskQuery>(q => q.Status == expectedStatus),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListTasksAsync_InvalidStatus_ThrowsArgumentExceptionWithClearMessage()
    {
        // Arrange
        var taskListHref = "/calendars/user/tasks/";
        var invalidStatus = "InvalidStatus";

        // Act & Assert
        var ex = await Should.ThrowAsync<ArgumentException>(
            () => _sut.ListTasksAsync(
                taskListHref,
                status: invalidStatus,
                dueAfter: null,
                dueBefore: null,
                textSearch: null,
                category: null,
                cancellationToken: CancellationToken.None));

        ex.Message.ShouldContain("Invalid status value");
        ex.Message.ShouldContain("'InvalidStatus'");
        ex.Message.ShouldContain("NeedsAction");
        ex.Message.ShouldContain("InProcess");
        ex.Message.ShouldContain("Completed");
        ex.Message.ShouldContain("Cancelled");
    }

    // ─── Null task guard ───────────────────────────────────────────────────

    [Fact]
    public async Task GetTaskAsync_NullTaskFromService_ThrowsInvalidOperationException()
    {
        // Arrange
        var href = "/calendars/user/tasks/nonexistent.ics";
        _taskService.GetTaskAsync(href, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TaskItem?>(null));

        // Act & Assert
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.GetTaskAsync(href, CancellationToken.None));

        ex.Message.ShouldContain(href);
        ex.Message.ShouldContain("not found");
    }
}