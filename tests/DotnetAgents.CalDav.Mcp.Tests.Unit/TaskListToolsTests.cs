using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public class TaskListToolsTests
{
    private readonly ITaskService _taskService;
    private readonly TaskListTools _sut;
    private readonly ITaskListResolver _taskListResolver;

    public TaskListToolsTests()
    {
        _taskService = Substitute.For<ITaskService>();
        _taskListResolver = Substitute.For<ITaskListResolver>();
        _taskListResolver.ResolveAsync(Arg.Any<IReadOnlyList<TaskList>>(), null, Arg.Any<CancellationToken>())
            .Returns(new TaskList { Href = "/calendars/user/tasks/", DisplayName = "My Tasks", IsDefault = true });
        _sut = new TaskListTools(_taskService, _taskListResolver);
    }

    // ─── list_task_lists ───────────────────────────────────────────────────

    [Fact]
    public async Task ListTaskListsAsync_ReturnsJsonWithExpectedFields()
    {
        // Arrange
        var taskLists = new List<TaskList>
        {
            new()
            {
                Href = "/calendars/user/tasks/",
                DisplayName = "My Tasks",
                Description = "Personal task list",
                Color = "#FF0000",
                SupportedComponents = ["VTODO"]
            }
        };

        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TaskList>>(taskLists));

        // Act
        var result = await _sut.ListTaskListsAsync(CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;

        // Should be an array with one element
        root.ValueKind.ShouldBe(JsonValueKind.Array);
        root.GetArrayLength().ShouldBe(1);

        var item = root[0];
        item.GetProperty("Href").GetString().ShouldBe("/calendars/user/tasks/");
        item.GetProperty("DisplayName").GetString().ShouldBe("My Tasks");
        item.GetProperty("Description").GetString().ShouldBe("Personal task list");
        item.GetProperty("Color").GetString().ShouldBe("#FF0000");
        item.GetProperty("IsDefault").GetBoolean().ShouldBeTrue();

        var components = item.GetProperty("SupportedComponents");
        components.ValueKind.ShouldBe(JsonValueKind.Array);
        components[0].GetString().ShouldBe("VTODO");
    }

    [Fact]
    public async Task ListTaskListsAsync_CallsServiceOnce()
    {
        // Arrange
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TaskList>>([]));

        // Act
        await _sut.ListTaskListsAsync(CancellationToken.None);

        // Assert
        await _taskService.Received(1).GetTaskListsAsync(Arg.Any<CancellationToken>());
    }

    // ─── Error path ────────────────────────────────────────────────────────

    [Fact]
    public async Task ListTaskListsAsync_ServiceThrowsException_ExceptionPropagates()
    {
        // Arrange
        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("CalDAV server unavailable"));

        // Act & Assert — the exception should NOT be swallowed
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.ListTaskListsAsync(CancellationToken.None));

        ex.Message.ShouldBe("CalDAV server unavailable");
    }

    [Fact]
    public async Task ListTaskListsAsync_NoDefaultConfigured_SetsAllIsDefaultFalse()
    {
        // Arrange — resolver throws when no default configured
        var resolver = Substitute.For<ITaskListResolver>();
        resolver.ResolveAsync(Arg.Any<IReadOnlyList<TaskList>>(), null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new DotnetAgents.CalDav.Core.TaskListResolutionException(
                "", [], "No default task list configured."));

        var sut = new TaskListTools(_taskService, resolver);

        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TaskList>>(new List<TaskList>
            {
                new() { Href = "/a/", DisplayName = "List A" },
                new() { Href = "/b/", DisplayName = "List B" }
            }));

        // Act
        var result = await sut.ListTaskListsAsync(CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetArrayLength().ShouldBe(2);
        root[0].GetProperty("IsDefault").GetBoolean().ShouldBeFalse();
        root[1].GetProperty("IsDefault").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ListTaskListsAsync_MultipleListsWithDefault_OnlyDefaultIsMarked()
    {
        // Arrange
        var taskLists = new List<TaskList>
        {
            new() { Href = "/tasks/", DisplayName = "Tasks" },
            new() { Href = "/shopping/", DisplayName = "Shopping" }
        };

        _taskService.GetTaskListsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TaskList>>(taskLists));

        // Act
        var result = await _sut.ListTaskListsAsync(CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetArrayLength().ShouldBe(2);

        // Default resolver returns /calendars/user/tasks/ as default
        // Neither /tasks/ nor /shopping/ matches the default href
        // So both should be false
        root[0].GetProperty("IsDefault").GetBoolean().ShouldBeFalse();
        root[1].GetProperty("IsDefault").GetBoolean().ShouldBeFalse();
    }
}
