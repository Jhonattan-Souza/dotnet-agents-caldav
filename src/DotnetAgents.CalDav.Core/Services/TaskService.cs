using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Logging;

namespace DotnetAgents.CalDav.Core.Services;

/// <summary>
/// VTODO-focused service implementation. Delegates CalDAV protocol details to <see cref="ICalDavClient"/>.
/// </summary>
internal sealed class TaskService : ITaskService
{
    private readonly ICalDavClient _calDavClient;
    private readonly ILogger<TaskService> _logger;

    public TaskService(ICalDavClient calDavClient, ILogger<TaskService> logger)
    {
        _calDavClient = calDavClient;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<TaskList>> GetTaskListsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Listing task lists");
        return _calDavClient.GetTaskListsAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<TaskItem>> GetTasksAsync(string taskListHref, TaskQuery query, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Querying tasks from {TaskListHref}", taskListHref);
        return _calDavClient.GetTasksAsync(taskListHref, query, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<TaskItem?> GetTaskAsync(string href, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Fetching task {Href}", href);
        return _calDavClient.GetTaskAsync(href, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<TaskItem> CreateTaskAsync(string taskListHref, TaskItem task, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Creating task in {TaskListHref}", taskListHref);
        return _calDavClient.CreateTaskAsync(taskListHref, task, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<TaskItem> UpdateTaskAsync(TaskItem task, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Updating task {Uid}", task.Uid);
        return _calDavClient.UpdateTaskAsync(task, cancellationToken);
    }

    /// <inheritdoc/>
    public Task DeleteTaskAsync(string href, string? etag, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Deleting task at {Href}", href);
        return _calDavClient.DeleteTaskAsync(href, etag, cancellationToken);
    }
}