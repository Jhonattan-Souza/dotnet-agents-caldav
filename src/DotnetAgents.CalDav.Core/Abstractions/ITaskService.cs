using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Abstractions;

/// <summary>
/// High-level VTODO-focused service. The MCP tool layer talks exclusively to this interface.
/// </summary>
public interface ITaskService
{
    /// <summary>List all task lists (calendar collections) for the configured principal.</summary>
    Task<IReadOnlyList<TaskList>> GetTaskListsAsync(CancellationToken cancellationToken);

    /// <summary>Query tasks in a given task list with optional filters.</summary>
    Task<IReadOnlyList<TaskItem>> GetTasksAsync(string taskListHref, TaskQuery query, CancellationToken cancellationToken);

    /// <summary>Fetch a single task by its absolute href. Returns null if not found.</summary>
    Task<TaskItem?> GetTaskAsync(string href, CancellationToken cancellationToken);

    /// <summary>Create a new task. The server assigns UID, href, and ETag.</summary>
    Task<TaskItem> CreateTaskAsync(string taskListHref, TaskItem task, CancellationToken cancellationToken);

    /// <summary>Update an existing task. Uses ETag-based concurrency control when ETag is available.</summary>
    Task<TaskItem> UpdateTaskAsync(TaskItem task, CancellationToken cancellationToken);

    /// <summary>Delete a task by href, optionally checking ETag concurrency.</summary>
    Task DeleteTaskAsync(string href, string? etag, CancellationToken cancellationToken);
}