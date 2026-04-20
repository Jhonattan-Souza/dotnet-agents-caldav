using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Abstractions;

/// <summary>
/// Low-level CalDAV HTTP client focused on VTODO operations.
/// Handles PROPFIND, REPORT, GET, PUT, DELETE verbs and XML/iCalendar encoding.
/// </summary>
public interface ICalDavClient
{
    /// <summary>Discover task lists (calendar collections supporting VTODO) for the configured principal.</summary>
    Task<IReadOnlyList<TaskList>> GetTaskListsAsync(CancellationToken cancellationToken);

    /// <summary>Query tasks in a specific calendar collection.</summary>
    Task<IReadOnlyList<TaskItem>> GetTasksAsync(string taskListHref, TaskQuery query, CancellationToken cancellationToken);

    /// <summary>Fetch a single task by its absolute href. Returns null if not found.</summary>
    Task<TaskItem?> GetTaskAsync(string href, CancellationToken cancellationToken);

    /// <summary>Create a new task in the specified calendar collection. Returns the created task with server-assigned href and ETag.</summary>
    Task<TaskItem> CreateTaskAsync(string taskListHref, TaskItem task, CancellationToken cancellationToken);

    /// <summary>
    /// Update an existing task. If <see cref="TaskItem.ETag"/> is non-null, an <c>If-Match</c> header
    /// is sent for optimistic concurrency; a mismatch results in a <see cref="CalDavConflictException"/>.
    /// </summary>
    Task<TaskItem> UpdateTaskAsync(TaskItem task, CancellationToken cancellationToken);

    /// <summary>Delete a task. If <paramref name="etag"/> is non-null, an <c>If-Match</c> header is sent.</summary>
    Task DeleteTaskAsync(string href, string? etag, CancellationToken cancellationToken);
}