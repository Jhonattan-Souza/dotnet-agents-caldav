using CalDavTaskStatus = DotnetAgents.CalDav.Core.Models.TaskStatus;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Mcp;

internal sealed class TaskCompletion
{
    private readonly ITaskService _taskService;
    private readonly TimeProvider _timeProvider;

    public TaskCompletion(ITaskService taskService, TimeProvider timeProvider)
    {
        _taskService = taskService;
        _timeProvider = timeProvider;
    }

    public Task<TaskItem> CompleteAsync(
        TaskItem task,
        string? etag,
        CancellationToken cancellationToken)
    {
        var completed = task with
        {
            Status = CalDavTaskStatus.Completed,
            Completed = _timeProvider.GetUtcNow(),
            ETag = etag ?? task.ETag
        };

        return _taskService.UpdateTaskAsync(completed, cancellationToken);
    }
}
