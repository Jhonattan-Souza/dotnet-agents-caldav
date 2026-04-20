using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;
using CalDavTaskStatus = DotnetAgents.CalDav.Core.Models.TaskStatus;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public class TaskServiceTests
{
    private readonly ICalDavClient _calDavClient;
    private readonly ILogger<TaskService> _logger;
    private readonly TaskService _sut;

    public TaskServiceTests()
    {
        _calDavClient = Substitute.For<ICalDavClient>();
        _logger = Substitute.For<ILogger<TaskService>>();
        _sut = new TaskService(_calDavClient, _logger);
    }

    [Fact]
    public async Task GetTaskListsAsync_DelegatesToClient()
    {
        // Arrange
        var expected = new List<TaskList>
        {
            new() { Href = "/calendars/user/tasks/", DisplayName = "Tasks" }
        }.AsReadOnly();
        _calDavClient.GetTaskListsAsync(Arg.Any<CancellationToken>()).Returns(expected);

        // Act
        var result = await _sut.GetTaskListsAsync(CancellationToken.None);

        // Assert
        result.ShouldBeSameAs(expected);
        await _calDavClient.Received(1).GetTaskListsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTasksAsync_DelegatesToClientWithCorrectArguments()
    {
        // Arrange
        var expected = new List<TaskItem>
        {
            new() { Uid = "task-1", Summary = "Task 1" }
        }.AsReadOnly();
        var taskListHref = "/calendars/user/tasks/";
        var query = new TaskQuery { Status = CalDavTaskStatus.NeedsAction };
        _calDavClient.GetTasksAsync(taskListHref, query, Arg.Any<CancellationToken>()).Returns(expected);

        // Act
        var result = await _sut.GetTasksAsync(taskListHref, query, CancellationToken.None);

        // Assert
        result.ShouldBeSameAs(expected);
        await _calDavClient.Received(1).GetTasksAsync(taskListHref, query, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTaskAsync_DelegatesToClient()
    {
        // Arrange
        var href = "/calendars/user/tasks/abc.ics";
        var expected = new TaskItem { Uid = "abc", Summary = "Task abc" };
        _calDavClient.GetTaskAsync(href, Arg.Any<CancellationToken>()).Returns(expected);

        // Act
        var result = await _sut.GetTaskAsync(href, CancellationToken.None);

        // Assert
        result.ShouldBe(expected);
        await _calDavClient.Received(1).GetTaskAsync(href, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTaskAsync_DelegatesToClient()
    {
        // Arrange
        var taskListHref = "/calendars/user/tasks/";
        var task = new TaskItem { Summary = "New task" };
        var created = new TaskItem { Uid = "new-uid", Summary = "New task", Href = "/calendars/user/tasks/new.ics" };
        _calDavClient.CreateTaskAsync(taskListHref, task, Arg.Any<CancellationToken>()).Returns(created);

        // Act
        var result = await _sut.CreateTaskAsync(taskListHref, task, CancellationToken.None);

        // Assert
        result.ShouldBeSameAs(created);
        await _calDavClient.Received(1).CreateTaskAsync(taskListHref, task, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTaskAsync_DelegatesToClient()
    {
        // Arrange
        var task = new TaskItem { Uid = "uid-1", Summary = "Updated task", ETag = "etag-1" };
        var updated = new TaskItem { Uid = "uid-1", Summary = "Updated task", ETag = "etag-2" };
        _calDavClient.UpdateTaskAsync(task, Arg.Any<CancellationToken>()).Returns(updated);

        // Act
        var result = await _sut.UpdateTaskAsync(task, CancellationToken.None);

        // Assert
        result.ShouldBeSameAs(updated);
        await _calDavClient.Received(1).UpdateTaskAsync(task, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteTaskAsync_DelegatesToClient()
    {
        // Arrange
        var href = "/calendars/user/tasks/abc.ics";
        var etag = "etag-abc";
        _calDavClient.DeleteTaskAsync(href, etag, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        // Act
        await _sut.DeleteTaskAsync(href, etag, CancellationToken.None);

        // Assert
        await _calDavClient.Received(1).DeleteTaskAsync(href, etag, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteTaskAsync_NullEtag_DelegatesToClient()
    {
        // Arrange
        var href = "/calendars/user/tasks/abc.ics";
        _calDavClient.DeleteTaskAsync(href, null, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        // Act
        await _sut.DeleteTaskAsync(href, null, CancellationToken.None);

        // Assert
        await _calDavClient.Received(1).DeleteTaskAsync(href, null, Arg.Any<CancellationToken>());
    }
}