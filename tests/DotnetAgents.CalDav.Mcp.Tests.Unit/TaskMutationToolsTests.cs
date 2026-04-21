using System.ComponentModel;
using System.Reflection;
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

public class TaskMutationToolsTests
{
    private readonly ITaskService _taskService;
    private readonly TaskMutationTools _sut;
    private readonly TimeProvider _timeProvider;

    private static readonly DateTimeOffset FixedNow = new(2025, 6, 15, 12, 30, 0, TimeSpan.Zero);

    public TaskMutationToolsTests()
    {
        _taskService = Substitute.For<ITaskService>();
        _timeProvider = new FixedTimeProvider(FixedNow);
        _sut = new TaskMutationTools(_taskService, _timeProvider);
    }

    // ─── create_task ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateTaskAsync_ReturnsJsonWithCreatedTask()
    {
        // Arrange
        var taskListHref = "/calendars/user/tasks/";
        var createdTask = new TaskItem
        {
            Uid = "new-uid-1",
            Summary = "Buy groceries",
            Description = "Get milk and eggs",
            Status = CalDavTaskStatus.NeedsAction,
            Priority = TaskPriority.Medium,
            Due = new DateTimeOffset(2025, 7, 1, 17, 0, 0, TimeSpan.Zero),
            Href = "/calendars/user/tasks/new-uid-1.ics",
            ETag = "\"etag-new\""
        };

        _taskService.CreateTaskAsync(taskListHref, Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(createdTask));

        // Act
        var result = await _sut.CreateTaskAsync(
            taskListHref,
            summary: "Buy groceries",
            description: "Get milk and eggs",
            status: "NeedsAction",
            priority: "Medium",
            due: new DateTimeOffset(2025, 7, 1, 17, 0, 0, TimeSpan.Zero),
            cancellationToken: CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("Uid").GetString().ShouldBe("new-uid-1");
        root.GetProperty("Summary").GetString().ShouldBe("Buy groceries");
        root.GetProperty("Href").GetString().ShouldBe("/calendars/user/tasks/new-uid-1.ics");
        root.GetProperty("ETag").GetString().ShouldBe("\"etag-new\"");
    }

    [Fact]
    public async Task CreateTaskAsync_PassesTaskItemToService()
    {
        // Arrange
        var taskListHref = "/calendars/user/work/";
        var due = new DateTimeOffset(2025, 8, 1, 9, 0, 0, TimeSpan.Zero);

        _taskService.CreateTaskAsync(taskListHref, Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TaskItem { Uid = "uid-x", Summary = "X", Href = "/x" }));

        // Act
        await _sut.CreateTaskAsync(
            taskListHref,
            summary: "Write report",
            description: "Quarterly report",
            status: "InProcess",
            priority: "High",
            due: due,
            cancellationToken: CancellationToken.None);

        // Assert
        await _taskService.Received(1).CreateTaskAsync(
            taskListHref,
            Arg.Is<TaskItem>(t =>
                t.Summary == "Write report" &&
                t.Description == "Quarterly report" &&
                t.Status == CalDavTaskStatus.InProcess &&
                t.Priority == TaskPriority.High &&
                t.Due == due),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTaskAsync_WithMinimalFields_PassesDefaultsToService()
    {
        // Arrange
        var taskListHref = "/calendars/user/tasks/";

        _taskService.CreateTaskAsync(taskListHref, Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TaskItem { Uid = "uid-min", Summary = "Minimal", Href = "/min" }));

        // Act — only required fields
        var result = await _sut.CreateTaskAsync(
            taskListHref,
            summary: "Minimal",
            description: null,
            status: null,
            priority: null,
            due: null,
            cancellationToken: CancellationToken.None);

        // Assert
        await _taskService.Received(1).CreateTaskAsync(
            taskListHref,
            Arg.Is<TaskItem>(t =>
                t.Summary == "Minimal" &&
                t.Description == null &&
                t.Status == CalDavTaskStatus.NeedsAction &&
                t.Priority == TaskPriority.None &&
                t.Due == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateTaskAsync_ServiceThrowsException_ExceptionPropagates()
    {
        // Arrange
        var taskListHref = "/calendars/user/tasks/";
        _taskService.CreateTaskAsync(taskListHref, Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Server error"));

        // Act & Assert
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.CreateTaskAsync(taskListHref, summary: "X", description: null,
                status: null, priority: null, due: null, cancellationToken: CancellationToken.None));

        ex.Message.ShouldBe("Server error");
    }

    // ─── update_task ─────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateTaskAsync_ReturnsJsonWithUpdatedTask()
    {
        // Arrange
        var existingTask = new TaskItem
        {
            Uid = "task-42",
            Summary = "Old summary",
            Status = CalDavTaskStatus.NeedsAction,
            Priority = TaskPriority.None,
            Href = "/calendars/user/tasks/task-42.ics",
            ETag = "\"etag-v1\""
        };
        var updatedTask = new TaskItem
        {
            Uid = "task-42",
            Summary = "Updated summary",
            Status = CalDavTaskStatus.InProcess,
            Priority = TaskPriority.High,
            Href = "/calendars/user/tasks/task-42.ics",
            ETag = "\"etag-v2\""
        };

        _taskService.GetTaskAsync("/calendars/user/tasks/task-42.ics", Arg.Any<CancellationToken>())
            .Returns(existingTask);
        _taskService.UpdateTaskAsync(Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(updatedTask));

        // Act
        var result = await _sut.UpdateTaskAsync(
            href: "/calendars/user/tasks/task-42.ics",
            summary: "Updated summary",
            description: null,
            status: "InProcess",
            priority: "High",
            due: null,
            etag: "\"etag-v1\"",
            cancellationToken: CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("Uid").GetString().ShouldBe("task-42");
        root.GetProperty("Summary").GetString().ShouldBe("Updated summary");
        root.GetProperty("ETag").GetString().ShouldBe("\"etag-v2\"");
    }

    [Fact]
    public async Task UpdateTaskAsync_PassesTaskItemWithProvidedValues()
    {
        // Arrange
        var due = new DateTimeOffset(2025, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var existingTask = new TaskItem
        {
            Href = "/calendars/user/tasks/task-42.ics",
            Uid = "task-42",
            Summary = "Old summary",
            Description = "Old description",
            Status = CalDavTaskStatus.NeedsAction,
            Priority = TaskPriority.Low,
            Due = new DateTimeOffset(2025, 6, 1, 9, 0, 0, TimeSpan.Zero),
            ETag = "\"etag-old\""
        };

        _taskService.GetTaskAsync("/calendars/user/tasks/task-42.ics", Arg.Any<CancellationToken>())
            .Returns(existingTask);
        _taskService.UpdateTaskAsync(Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TaskItem { Uid = "u1", Summary = "s", Href = "/h" }));

        // Act
        await _sut.UpdateTaskAsync(
            href: "/calendars/user/tasks/task-42.ics",
            summary: "New summary",
            description: "New description",
            status: "Completed",
            priority: "Low",
            due: due,
            etag: "\"etag-42\"",
            cancellationToken: CancellationToken.None);

        // Assert — provided values override existing; others are preserved via fetch-and-merge
        await _taskService.Received(1).UpdateTaskAsync(
            Arg.Is<TaskItem>(t =>
                t.Href == "/calendars/user/tasks/task-42.ics" &&
                t.Summary == "New summary" &&
                t.Description == "New description" &&
                t.Status == CalDavTaskStatus.Completed &&
                t.Priority == TaskPriority.Low &&
                t.Due == due &&
                t.ETag == "\"etag-42\""),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTaskAsync_WithNullOptionalFields_PreservesExistingValues()
    {
        // Arrange — existing task has real data; null optional fields should preserve it
        var existingTask = new TaskItem
        {
            Href = "/calendars/user/tasks/task-99.ics",
            Uid = "task-99",
            Summary = "Original summary",
            Description = "Original description",
            Status = CalDavTaskStatus.InProcess,
            Priority = TaskPriority.High,
            Due = new DateTimeOffset(2025, 8, 1, 9, 0, 0, TimeSpan.Zero),
            ETag = "\"etag-existing\""
        };

        _taskService.GetTaskAsync("/calendars/user/tasks/task-99.ics", Arg.Any<CancellationToken>())
            .Returns(existingTask);
        _taskService.UpdateTaskAsync(Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TaskItem { Uid = "u1", Summary = "s", Href = "/h" }));

        // Act — only summary is provided; all other optionals are null (preserve existing)
        await _sut.UpdateTaskAsync(
            href: "/calendars/user/tasks/task-99.ics",
            summary: "Updated summary",
            description: null,
            status: null,
            priority: null,
            due: null,
            etag: null,
            cancellationToken: CancellationToken.None);

        // Assert — null optional fields preserve existing task values
        await _taskService.Received(1).UpdateTaskAsync(
            Arg.Is<TaskItem>(t =>
                t.Href == "/calendars/user/tasks/task-99.ics" &&
                t.Summary == "Updated summary" &&
                t.Description == existingTask.Description &&
                t.Status == existingTask.Status &&
                t.Priority == existingTask.Priority &&
                t.Due == existingTask.Due &&
                t.ETag == existingTask.ETag),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTaskAsync_ServiceThrowsException_ExceptionPropagates()
    {
        // Arrange
        var existingTask = new TaskItem
        {
            Href = "/calendars/user/tasks/task-99.ics",
            Summary = "Existing",
            Status = CalDavTaskStatus.NeedsAction,
            ETag = "\"etag-v1\""
        };
        _taskService.GetTaskAsync("/calendars/user/tasks/task-99.ics", Arg.Any<CancellationToken>())
            .Returns(existingTask);
        _taskService.UpdateTaskAsync(Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Conflict"));

        // Act & Assert
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.UpdateTaskAsync(
                href: "/calendars/user/tasks/task-99.ics",
                summary: "X", description: null, status: null,
                priority: null, due: null, etag: null,
                cancellationToken: CancellationToken.None));

        ex.Message.ShouldBe("Conflict");
    }

    // ─── complete_task ────────────────────────────────────────────────────────

    [Fact]
    public async Task CompleteTaskAsync_ReturnsJsonWithCompletedTask()
    {
        // Arrange
        var existingTask = new TaskItem
        {
            Uid = "task-7",
            Summary = "Finish review",
            Status = CalDavTaskStatus.InProcess,
            Href = "/calendars/user/tasks/task-7.ics",
            ETag = "\"etag-v1\""
        };
        var completedTask = new TaskItem
        {
            Uid = "task-7",
            Summary = "Finish review",
            Status = CalDavTaskStatus.Completed,
            Completed = FixedNow,
            Href = "/calendars/user/tasks/task-7.ics",
            ETag = "\"etag-done\""
        };

        _taskService.GetTaskAsync("/calendars/user/tasks/task-7.ics", Arg.Any<CancellationToken>())
            .Returns(existingTask);
        _taskService.UpdateTaskAsync(Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(completedTask));

        // Act
        var result = await _sut.CompleteTaskAsync(
            href: "/calendars/user/tasks/task-7.ics",
            etag: "\"etag-v1\"",
            cancellationToken: CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("Uid").GetString().ShouldBe("task-7");
        root.GetProperty("Status").GetInt32().ShouldBe((int)CalDavTaskStatus.Completed);
        root.GetProperty("ETag").GetString().ShouldBe("\"etag-done\"");
    }

    [Fact]
    public async Task CompleteTaskAsync_SetsStatusToCompletedAndCompletedTimestamp()
    {
        // Arrange
        var existingTask = new TaskItem
        {
            Href = "/calendars/user/tasks/task-7.ics",
            Uid = "task-7",
            Summary = "Some task",
            Status = CalDavTaskStatus.InProcess,
            ETag = "\"etag-v0\""
        };

        _taskService.GetTaskAsync("/calendars/user/tasks/task-7.ics", Arg.Any<CancellationToken>())
            .Returns(existingTask);
        _taskService.UpdateTaskAsync(Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TaskItem { Uid = "c1", Summary = "C", Href = "/c" }));

        // Act
        await _sut.CompleteTaskAsync(
            href: "/calendars/user/tasks/task-7.ics",
            etag: "\"etag-v1\"",
            cancellationToken: CancellationToken.None);

        // Assert — Status must be Completed, Completed timestamp must be the fixed time,
        // and the explicit etag overrides the fetched task's etag
        await _taskService.Received(1).UpdateTaskAsync(
            Arg.Is<TaskItem>(t =>
                t.Href == "/calendars/user/tasks/task-7.ics" &&
                t.Uid == existingTask.Uid &&
                t.Summary == existingTask.Summary &&
                t.Status == CalDavTaskStatus.Completed &&
                t.Completed == FixedNow &&
                t.ETag == "\"etag-v1\""),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTaskAsync_WithNoEtag_PreservesFetchedEtag()
    {
        // Arrange
        var existingTask = new TaskItem
        {
            Href = "/calendars/user/tasks/task-8.ics",
            Uid = "task-8",
            Summary = "Another task",
            Status = CalDavTaskStatus.InProcess,
            ETag = "\"etag-existing\""
        };

        _taskService.GetTaskAsync("/calendars/user/tasks/task-8.ics", Arg.Any<CancellationToken>())
            .Returns(existingTask);
        _taskService.UpdateTaskAsync(Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new TaskItem { Uid = "c2", Summary = "C", Href = "/c" }));

        // Act — no explicit etag, so fetched task's etag should be preserved
        await _sut.CompleteTaskAsync(
            href: "/calendars/user/tasks/task-8.ics",
            etag: null,
            cancellationToken: CancellationToken.None);

        // Assert
        await _taskService.Received(1).UpdateTaskAsync(
            Arg.Is<TaskItem>(t =>
                t.Href == "/calendars/user/tasks/task-8.ics" &&
                t.Status == CalDavTaskStatus.Completed &&
                t.Completed == FixedNow &&
                t.ETag == existingTask.ETag),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTaskAsync_ServiceThrowsException_ExceptionPropagates()
    {
        // Arrange
        var existingTask = new TaskItem
        {
            Href = "/calendars/user/tasks/missing.ics",
            Summary = "Missing",
            Status = CalDavTaskStatus.NeedsAction,
            ETag = "\"etag-x\""
        };
        _taskService.GetTaskAsync("/calendars/user/tasks/missing.ics", Arg.Any<CancellationToken>())
            .Returns(existingTask);
        _taskService.UpdateTaskAsync(Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Not found"));

        // Act & Assert
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.CompleteTaskAsync(
                href: "/calendars/user/tasks/missing.ics",
                etag: null,
                cancellationToken: CancellationToken.None));

        ex.Message.ShouldBe("Not found");
    }

    // ─── delete_task ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteTaskAsync_ReturnsJsonWithHref()
    {
        // Arrange
        var href = "/calendars/user/tasks/task-42.ics";
        _taskService.DeleteTaskAsync(href, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.DeleteTaskAsync(
            href: href,
            etag: "\"etag-42\"",
            cancellationToken: CancellationToken.None);

        // Assert
        var doc = JsonDocument.Parse(result);
        var root = doc.RootElement;
        root.GetProperty("Href").GetString().ShouldBe(href);
        root.GetProperty("Deleted").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteTaskAsync_CallsServiceWithProvidedHrefAndEtag()
    {
        // Arrange
        var href = "/calendars/user/tasks/task-99.ics";
        var etag = "\"etag-99\"";
        _taskService.DeleteTaskAsync(href, etag, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Act
        await _sut.DeleteTaskAsync(
            href: href,
            etag: etag,
            cancellationToken: CancellationToken.None);

        // Assert
        await _taskService.Received(1).DeleteTaskAsync(href, etag, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteTaskAsync_WithNoEtag_PassesNullEtag()
    {
        // Arrange
        var href = "/calendars/user/tasks/task-50.ics";
        _taskService.DeleteTaskAsync(href, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Act
        await _sut.DeleteTaskAsync(
            href: href,
            etag: null,
            cancellationToken: CancellationToken.None);

        // Assert
        await _taskService.Received(1).DeleteTaskAsync(href, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteTaskAsync_ServiceThrowsException_ExceptionPropagates()
    {
        // Arrange
        var href = "/calendars/user/tasks/missing.ics";
        _taskService.DeleteTaskAsync(href, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Server error"));

        // Act & Assert
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.DeleteTaskAsync(
                href: href,
                etag: null,
                cancellationToken: CancellationToken.None));

        ex.Message.ShouldBe("Server error");
    }

    // ─── complete_task data-preservation tests ──────────────────────────────

    [Fact]
    public async Task CompleteTaskAsync_PreservesExistingFieldsFromFetchedTask()
    {
        // Arrange — simulate an existing task with meaningful data
        var existingTask = new TaskItem
        {
            Href = "/calendars/user/tasks/task-7.ics",
            Uid = "task-7",
            Summary = "Important task",
            Description = "Detailed description",
            Status = CalDavTaskStatus.InProcess,
            Priority = TaskPriority.High,
            Due = new DateTimeOffset(2025, 12, 31, 17, 0, 0, TimeSpan.Zero),
            Categories = ["work", "urgent"],
            Start = new DateTimeOffset(2025, 6, 1, 9, 0, 0, TimeSpan.Zero),
            RecurrenceRule = "FREQ=WEEKLY;COUNT=4",
            ETag = "\"etag-original\""
        };

        _taskService.GetTaskAsync(existingTask.Href, Arg.Any<CancellationToken>())
            .Returns(existingTask);
        _taskService.UpdateTaskAsync(Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<TaskItem>(0) with { ETag = "\"etag-done\"" });

        // Act
        await _sut.CompleteTaskAsync(
            href: existingTask.Href,
            etag: null,
            cancellationToken: CancellationToken.None);

        // Assert — existing fields must be preserved; only Status and Completed change
        await _taskService.Received(1).UpdateTaskAsync(
            Arg.Is<TaskItem>(t =>
                t.Href == existingTask.Href &&
                t.Uid == existingTask.Uid &&
                t.Summary == existingTask.Summary &&
                t.Description == existingTask.Description &&
                t.Priority == existingTask.Priority &&
                t.Due == existingTask.Due &&
                t.Start == existingTask.Start &&
                t.Categories.SequenceEqual(existingTask.Categories) &&
                t.RecurrenceRule == existingTask.RecurrenceRule &&
                t.Status == CalDavTaskStatus.Completed &&
                t.Completed == FixedNow &&
                t.ETag == existingTask.ETag),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTaskAsync_ExplicitEtagOverridesFetchedEtag()
    {
        // Arrange
        var existingTask = new TaskItem
        {
            Href = "/calendars/user/tasks/task-7.ics",
            Uid = "task-7",
            Summary = "Some task",
            Status = CalDavTaskStatus.InProcess,
            ETag = "\"etag-original\""
        };

        _taskService.GetTaskAsync(existingTask.Href, Arg.Any<CancellationToken>())
            .Returns(existingTask);
        _taskService.UpdateTaskAsync(Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<TaskItem>(0));

        // Act — caller provides an explicit etag
        await _sut.CompleteTaskAsync(
            href: existingTask.Href,
            etag: "\"etag-caller\"",
            cancellationToken: CancellationToken.None);

        // Assert — the explicit etag overrides the fetched task's etag
        await _taskService.Received(1).UpdateTaskAsync(
            Arg.Is<TaskItem>(t =>
                t.ETag == "\"etag-caller\"" &&
                t.Status == CalDavTaskStatus.Completed),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTaskAsync_ThrowsWhenTaskNotFound()
    {
        // Arrange — GetTaskAsync returns null
        _taskService.GetTaskAsync("/calendars/user/tasks/missing.ics", Arg.Any<CancellationToken>())
            .Returns((TaskItem?)null);

        // Act & Assert
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.CompleteTaskAsync(
                href: "/calendars/user/tasks/missing.ics",
                etag: null,
                cancellationToken: CancellationToken.None));

        ex.Message.ShouldContain("not found");
    }

    // ─── update_task Description attribute tests ─────────────────────────────

    [Fact]
    public void UpdateTaskAsync_DescriptionAttributeDocumentsPartialUpdateSemantics()
    {
        var method = typeof(TaskMutationTools).GetMethod(nameof(TaskMutationTools.UpdateTaskAsync));
        method.ShouldNotBeNull();

        var descAttr = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
        descAttr.ShouldNotBeNull("update_task must have a [Description] attribute");
        descAttr.Description.ShouldContain("partial update", Case.Insensitive,
            "update_task description must document partial-update semantics so MCP clients understand null = preserve");
        descAttr.Description.ShouldContain("explicitly provides or confirms the exact href", Case.Insensitive);
    }

    [Fact]
    public void UpdateTaskAsync_ParameterDescriptionsDocumentNullPreservationSemantics()
    {
        var method = typeof(TaskMutationTools).GetMethod(nameof(TaskMutationTools.UpdateTaskAsync));
        method.ShouldNotBeNull();

        var parameters = method.GetParameters();
        var relevantParams = parameters
            .Where(p => p.Name is not "href" and not "cancellationToken")
            .ToList();

        var missingDescription = relevantParams
            .Where(p => p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>() is null)
            .Select(p => p.Name)
            .ToList();
        missingDescription.ShouldBeEmpty($"Parameters missing [Description]: {string.Join(", ", missingDescription)}");

        var missingNull = relevantParams
            .Where(p =>
            {
                var attr = p.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>();
                return attr is not null && !attr.Description.Contains("null", StringComparison.OrdinalIgnoreCase);
            })
            .Select(p => p.Name)
            .ToList();
        missingNull.ShouldBeEmpty($"Parameter descriptions missing 'null' mention: {string.Join(", ", missingNull)}");
    }

    [Fact]
    public void RawHrefMutationToolDescriptions_RequireExplicitHrefAndDiscourageBulkUse()
    {
        GetDescription(nameof(TaskMutationTools.CreateTaskAsync)).ShouldContain("explicitly provides or confirms the exact href", Case.Insensitive);
        GetDescription(nameof(TaskMutationTools.CompleteTaskAsync)).ShouldContain("explicitly provides or confirms the exact href", Case.Insensitive);
        GetDescription(nameof(TaskMutationTools.DeleteTaskAsync)).ShouldContain("Do not use this for search-then-delete flows or bulk deletion", Case.Insensitive);

        GetParameterDescription(nameof(TaskMutationTools.DeleteTaskAsync), "href")
            .ShouldContain("Do not use this for search-then-delete flows or bulk deletion", Case.Insensitive);
    }

    private static string GetDescription(string methodName)
    {
        var method = typeof(TaskMutationTools).GetMethod(methodName);
        method.ShouldNotBeNull();

        var attr = method!.GetCustomAttribute<DescriptionAttribute>();
        attr.ShouldNotBeNull();
        return attr!.Description;
    }

    private static string GetParameterDescription(string methodName, string parameterName)
    {
        var method = typeof(TaskMutationTools).GetMethod(methodName);
        method.ShouldNotBeNull();

        var parameter = method!.GetParameters().Single(p => p.Name == parameterName);
        var attr = parameter.GetCustomAttribute<DescriptionAttribute>();
        attr.ShouldNotBeNull();
        return attr!.Description;
    }

    // ─── update_task data-preservation tests ────────────────────────────────

    [Fact]
    public async Task UpdateTaskAsync_PreservesExistingFieldsWhenOnlySomeUpdated()
    {
        // Arrange — existing task with data we want to preserve
        var existingTask = new TaskItem
        {
            Href = "/calendars/user/tasks/task-42.ics",
            Uid = "task-42",
            Summary = "Original summary",
            Description = "Original description",
            Status = CalDavTaskStatus.InProcess,
            Priority = TaskPriority.High,
            Due = new DateTimeOffset(2025, 12, 31, 17, 0, 0, TimeSpan.Zero),
            Categories = ["work"],
            Start = new DateTimeOffset(2025, 6, 1, 9, 0, 0, TimeSpan.Zero),
            RecurrenceRule = "FREQ=DAILY;COUNT=5",
            ETag = "\"etag-v1\""
        };

        _taskService.GetTaskAsync(existingTask.Href, Arg.Any<CancellationToken>())
            .Returns(existingTask);
        _taskService.UpdateTaskAsync(Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<TaskItem>(0));

        // Act — only update status; other fields are null (omitted)
        await _sut.UpdateTaskAsync(
            href: existingTask.Href,
            summary: null,
            description: null,
            status: "Completed",
            priority: null,
            due: null,
            etag: null,
            cancellationToken: CancellationToken.None);

        // Assert — existing fields must be preserved; only Status changed
        await _taskService.Received(1).UpdateTaskAsync(
            Arg.Is<TaskItem>(t =>
                t.Href == existingTask.Href &&
                t.Uid == existingTask.Uid &&
                t.Summary == existingTask.Summary &&        // preserved
                t.Description == existingTask.Description && // preserved
                t.Priority == existingTask.Priority &&       // preserved
                t.Due == existingTask.Due &&                 // preserved
                t.Start == existingTask.Start &&             // preserved
                t.Categories.SequenceEqual(existingTask.Categories) && // preserved
                t.RecurrenceRule == existingTask.RecurrenceRule &&     // preserved
                t.Status == CalDavTaskStatus.Completed &&              // updated
                t.ETag == existingTask.ETag),                           // preserved (no explicit etag)
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTaskAsync_SetsProvidedFieldsAndPreservesOthers()
    {
        // Arrange
        var existingTask = new TaskItem
        {
            Href = "/calendars/user/tasks/task-55.ics",
            Uid = "task-55",
            Summary = "Old summary",
            Description = "Old description",
            Status = CalDavTaskStatus.NeedsAction,
            Priority = TaskPriority.None,
            Due = null,
            ETag = "\"etag-old\""
        };

        var newDue = new DateTimeOffset(2025, 10, 1, 9, 0, 0, TimeSpan.Zero);

        _taskService.GetTaskAsync(existingTask.Href, Arg.Any<CancellationToken>())
            .Returns(existingTask);
        _taskService.UpdateTaskAsync(Arg.Any<TaskItem>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<TaskItem>(0));

        // Act — explicitly set summary, priority, and due; leave others null
        await _sut.UpdateTaskAsync(
            href: existingTask.Href,
            summary: "New summary",
            description: null,
            status: null,
            priority: "High",
            due: newDue,
            etag: "\"etag-caller\"",
            cancellationToken: CancellationToken.None);

        // Assert
        await _taskService.Received(1).UpdateTaskAsync(
            Arg.Is<TaskItem>(t =>
                t.Summary == "New summary" &&          // updated
                t.Description == existingTask.Description && // preserved (null means don't change)
                t.Status == existingTask.Status &&     // preserved
                t.Priority == TaskPriority.High &&    // updated
                t.Due == newDue &&                      // updated
                t.ETag == "\"etag-caller\""),           // explicit etag overrides
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTaskAsync_ThrowsWhenTaskNotFound()
    {
        // Arrange — GetTaskAsync returns null
        _taskService.GetTaskAsync("/calendars/user/tasks/missing.ics", Arg.Any<CancellationToken>())
            .Returns((TaskItem?)null);

        // Act & Assert
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.UpdateTaskAsync(
                href: "/calendars/user/tasks/missing.ics",
                summary: "Doesn't matter",
                description: null,
                status: null,
                priority: null,
                due: null,
                etag: null,
                cancellationToken: CancellationToken.None));

        ex.Message.ShouldContain("not found");
    }
}
