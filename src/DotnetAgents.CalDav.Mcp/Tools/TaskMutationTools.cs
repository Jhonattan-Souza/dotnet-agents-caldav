using CalDavTaskStatus = DotnetAgents.CalDav.Core.Models.TaskStatus;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>
/// MCP mutation tools for creating, updating, completing, and deleting tasks.
/// </summary>
[McpServerToolType]
public sealed class TaskMutationTools
{
    private readonly ITaskService _taskService;
    private readonly TimeProvider _timeProvider;

    public TaskMutationTools(ITaskService taskService, TimeProvider timeProvider)
    {
        _taskService = taskService;
        _timeProvider = timeProvider;
    }

    [McpServerTool(Name = "create_task"), Description("Create a new task in a CalDAV task list by absolute href. Use the chat-oriented add-to-list tool unless you already know the exact href.")]
    public async Task<string> CreateTaskAsync(
        [Description("The absolute href of the task list to create the task in. Use the chat-oriented add-to-list tool unless you already know the exact href.")] string taskListHref,
        [Description("Brief summary / title of the task")] string summary,
        [Description("Detailed description of the task")] string? description = null,
        [Description("Task status: NeedsAction, InProcess, Completed, or Cancelled")] string? status = null,
        [Description("Task priority: None, High, Medium, or Low")] string? priority = null,
        [Description("When the task is due")] DateTimeOffset? due = null,
        CancellationToken cancellationToken = default)
    {
        var task = new TaskItem
        {
            Summary = summary,
            Description = description,
            Status = status is not null ? EnumParsingHelpers.ParseTaskStatus(status) : CalDavTaskStatus.NeedsAction,
            Priority = priority is not null ? EnumParsingHelpers.ParseTaskPriority(priority) : TaskPriority.None,
            Due = due
        };

        var created = await _taskService.CreateTaskAsync(taskListHref, task, cancellationToken);
        return JsonSerializer.Serialize(created);
    }

    [McpServerTool(Name = "update_task"), Description("Partial update of an existing CalDAV task by absolute href. Omitted or null fields preserve the current value (fetch-and-merge semantics). Use chat-oriented list-name tools unless you already know the exact href.")]
    public async Task<string> UpdateTaskAsync(
        [Description("The absolute href of the task to update. Use chat-oriented list-name tools unless you already know the exact href.")] string href,
        [Description("Updated summary / title of the task (null to preserve existing)")] string? summary = null,
        [Description("Updated description of the task (null to preserve existing)")] string? description = null,
        [Description("Updated status: NeedsAction, InProcess, Completed, or Cancelled (null to preserve existing)")] string? status = null,
        [Description("Updated priority: None, High, Medium, or Low (null to preserve existing)")] string? priority = null,
        [Description("Updated due date/time (null to preserve existing)")] DateTimeOffset? due = null,
        [Description("ETag for optimistic concurrency control (null to preserve existing)")] string? etag = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await _taskService.GetTaskAsync(href, cancellationToken)
            ?? throw new InvalidOperationException($"Task not found: {href}");

        var task = existing with
        {
            Summary = summary ?? existing.Summary,
            Description = description ?? existing.Description,
            Status = status is not null ? EnumParsingHelpers.ParseTaskStatus(status) : existing.Status,
            Priority = priority is not null ? EnumParsingHelpers.ParseTaskPriority(priority) : existing.Priority,
            Due = due ?? existing.Due,
            ETag = etag ?? existing.ETag
        };

        var updated = await _taskService.UpdateTaskAsync(task, cancellationToken);
        return JsonSerializer.Serialize(updated);
    }

    [McpServerTool(Name = "complete_task"), Description("Mark a CalDAV task as completed with a deterministic timestamp by absolute href. Use the chat-oriented complete-in-list tool unless you already know the exact href.")]
    public async Task<string> CompleteTaskAsync(
        [Description("The absolute href of the task to complete. Use the chat-oriented complete-in-list tool unless you already know the exact href.")] string href,
        [Description("ETag for optimistic concurrency control")] string? etag = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await _taskService.GetTaskAsync(href, cancellationToken)
            ?? throw new InvalidOperationException($"Task not found: {href}");

        var task = existing with
        {
            Status = CalDavTaskStatus.Completed,
            Completed = _timeProvider.GetUtcNow(),
            ETag = etag ?? existing.ETag
        };

        var completed = await _taskService.UpdateTaskAsync(task, cancellationToken);
        return JsonSerializer.Serialize(completed);
    }

    [McpServerTool(Name = "delete_task"), Description("Delete a CalDAV task by its absolute href. Use the chat-oriented delete-in-list tool unless you already know the exact href.")]
    public async Task<string> DeleteTaskAsync(
        [Description("The absolute href of the task to delete. Use the chat-oriented delete-in-list tool unless you already know the exact href.")] string href,
        [Description("ETag for optimistic concurrency control")] string? etag = null,
        CancellationToken cancellationToken = default)
    {
        await _taskService.DeleteTaskAsync(href, etag, cancellationToken);

        return JsonSerializer.Serialize(new { Href = href, Deleted = true });
    }
}
