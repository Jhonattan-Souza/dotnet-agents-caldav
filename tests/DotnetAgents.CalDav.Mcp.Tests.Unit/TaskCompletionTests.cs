using CalDavTaskStatus = DotnetAgents.CalDav.Core.Models.TaskStatus;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public class TaskCompletionTests
{
    private static readonly DateTimeOffset FixedNow = new(2025, 6, 15, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task CompleteAsync_UsesCurrentTimeAndReturnsPersistedTask()
    {
        var taskService = Substitute.For<ITaskService>();
        var fetched = new TaskItem
        {
            Href = "/tasks/1.ics",
            Uid = "task-1",
            Summary = "Review",
            Status = CalDavTaskStatus.InProcess,
            ETag = "\"etag-old\""
        };
        var persisted = fetched with
        {
            Status = CalDavTaskStatus.Completed,
            Completed = FixedNow,
            ETag = "\"etag-new\""
        };
        taskService.UpdateTaskAsync(Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(persisted);
        var sut = new TaskCompletion(taskService, new FixedTimeProvider(FixedNow));

        var result = await sut.CompleteAsync(fetched, null, CancellationToken.None);

        result.ShouldBeSameAs(persisted);
        await taskService.Received(1).UpdateTaskAsync(
            Arg.Is<TaskItem>(task =>
                task.Status == CalDavTaskStatus.Completed &&
                task.Completed == FixedNow &&
                task.ETag == fetched.ETag),
            CancellationToken.None);
    }

    [Fact]
    public async Task CompleteAsync_AlreadyCompletedTask_ReplacesCompletionTimeAndPreservesAllOtherData()
    {
        var taskService = Substitute.For<ITaskService>();
        var fetched = new TaskItem
        {
            Href = "/tasks/recurring.ics",
            Uid = "task-recurring",
            Summary = "Weekly review",
            Description = "Review every active project",
            Due = new DateTimeOffset(2025, 6, 20, 17, 0, 0, TimeSpan.Zero),
            Start = new DateTimeOffset(2025, 6, 20, 9, 0, 0, TimeSpan.Zero),
            Completed = new DateTimeOffset(2025, 6, 1, 8, 0, 0, TimeSpan.Zero),
            Priority = TaskPriority.High,
            Status = CalDavTaskStatus.Completed,
            Categories = ["work", "review"],
            RecurrenceRule = "FREQ=WEEKLY;COUNT=4",
            ETag = "\"etag-fetched\""
        };
        TaskItem? submitted = null;
        taskService.UpdateTaskAsync(Arg.Do<TaskItem>(task => submitted = task), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<TaskItem>());
        var sut = new TaskCompletion(taskService, new FixedTimeProvider(FixedNow));

        await sut.CompleteAsync(fetched, null, CancellationToken.None);

        submitted.ShouldBe(fetched with { Completed = FixedNow });
    }

    [Fact]
    public async Task CompleteAsync_ExplicitEtagOverridesFetchedEtag()
    {
        var taskService = Substitute.For<ITaskService>();
        var fetched = new TaskItem { Href = "/tasks/1.ics", ETag = "\"etag-fetched\"" };
        TaskItem? submitted = null;
        taskService.UpdateTaskAsync(Arg.Do<TaskItem>(task => submitted = task), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<TaskItem>());
        var sut = new TaskCompletion(taskService, new FixedTimeProvider(FixedNow));

        await sut.CompleteAsync(fetched, "\"etag-caller\"", CancellationToken.None);

        submitted!.ETag.ShouldBe("\"etag-caller\"");
    }

    [Fact]
    public async Task CompleteAsync_ForwardsCancellationToken()
    {
        var taskService = Substitute.For<ITaskService>();
        var cancellationToken = new CancellationTokenSource().Token;
        taskService.UpdateTaskAsync(Arg.Any<TaskItem>(), cancellationToken)
            .Returns(call => call.Arg<TaskItem>());
        var sut = new TaskCompletion(taskService, new FixedTimeProvider(FixedNow));

        await sut.CompleteAsync(new TaskItem(), null, cancellationToken);

        await taskService.Received(1).UpdateTaskAsync(Arg.Any<TaskItem>(), cancellationToken);
    }

    [Fact]
    public async Task CompleteAsync_UpdateFailurePropagatesUnchanged()
    {
        var taskService = Substitute.For<ITaskService>();
        var failure = new InvalidOperationException("Concurrency conflict");
        taskService.UpdateTaskAsync(Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(failure);
        var sut = new TaskCompletion(taskService, new FixedTimeProvider(FixedNow));

        var thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => sut.CompleteAsync(new TaskItem(), null, CancellationToken.None));

        thrown.ShouldBeSameAs(failure);
    }
}
