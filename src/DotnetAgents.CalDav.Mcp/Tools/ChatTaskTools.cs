using CalDavTaskStatus = DotnetAgents.CalDav.Core.Models.TaskStatus;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace DotnetAgents.CalDav.Mcp.Tools;

[McpServerToolType]
public sealed class ChatTaskTools
{
    private readonly ITaskService _taskService;
    private readonly ITaskListResolver _taskListResolver;
    private readonly TimeProvider _timeProvider;

    public ChatTaskTools(ITaskService taskService, ITaskListResolver taskListResolver, TimeProvider timeProvider)
    {
        _taskService = taskService;
        _taskListResolver = taskListResolver;
        _timeProvider = timeProvider;
    }

    [McpServerTool(Name = "show_tasks"), Description("List tasks in a user-facing task list name. If the user says 'task list' or 'my tasks', omit listName to use the configured default list. Never guess based on task content; this tool resolves list names deterministically and fails with candidates when ambiguous.")]
    public async Task<string> ListTasksInListAsync(
        [Description("User-facing task list name such as 'Shopping', 'Work', or 'task list'. Omit or pass null to use the configured default list.")] string? taskListName = null,
        [Description("Filter by status: NeedsAction, InProcess, Completed, or Cancelled")] string? status = null,
        [Description("Filter for tasks due after this date/time")] DateTimeOffset? dueAfter = null,
        [Description("Filter for tasks due before this date/time")] DateTimeOffset? dueBefore = null,
        [Description("Search for tasks containing this text in summary or description")] string? textSearch = null,
        [Description("Filter by category")] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var taskList = await ResolveTaskListAsync(taskListName, cancellationToken);
        var query = new TaskQuery
        {
            Status = status is not null ? EnumParsingHelpers.ParseTaskStatus(status) : null,
            DueAfter = dueAfter,
            DueBefore = dueBefore,
            TextSearch = textSearch,
            Category = category
        };

        var tasks = await _taskService.GetTasksAsync(taskList.Href, query, cancellationToken);
        return JsonSerializer.Serialize(tasks);
    }

    [McpServerTool(Name = "add_task"), Description("Create a task in a user-facing task list name. If the user says 'add a task ...' and does not name a list, omit listName so the configured default task list is used. Explicit list names always win over task content.")]
    public async Task<string> AddTaskToListAsync(
        [Description("User-facing task list name such as 'Shopping', 'Work', or 'task list'. Omit or pass null to use the configured default list.")] string? taskListName,
        [Description("Brief summary / title of the task (exact summary to store)")] string summary,
        [Description("Detailed description of the task")] string? description = null,
        [Description("Task status: NeedsAction, InProcess, Completed, or Cancelled")] string? status = null,
        [Description("Task priority: None, High, Medium, or Low")] string? priority = null,
        [Description("When the task is due")] DateTimeOffset? due = null,
        CancellationToken cancellationToken = default)
    {
        var taskList = await ResolveTaskListAsync(taskListName, cancellationToken);
        var task = new TaskItem
        {
            Summary = summary,
            Description = description,
            Status = status is not null ? EnumParsingHelpers.ParseTaskStatus(status) : CalDavTaskStatus.NeedsAction,
            Priority = priority is not null ? EnumParsingHelpers.ParseTaskPriority(priority) : TaskPriority.None,
            Due = due
        };

        var created = await _taskService.CreateTaskAsync(taskList.Href, task, cancellationToken);
        return JsonSerializer.Serialize(created);
    }

    [McpServerTool(Name = "find_tasks"), Description("Find tasks by summary text. If the user named a list, pass listName and search only that list. If they did not name a list, this tool searches all visible lists and returns matches without guessing.")]
    public async Task<string> FindTaskInListAsync(
        [Description("User-facing task list name such as 'Shopping', 'Work', or 'task list'. Omit or pass null to search all visible lists.")] string? taskListName,
        [Description("Exact task summary to match. If multiple tasks share this summary, all matches are returned.")] string summary,
        CancellationToken cancellationToken = default)
    {
        var matches = await FindMatchesAsync(taskListName, summary, cancellationToken);
        return JsonSerializer.Serialize(matches);
    }

    [McpServerTool(Name = "complete_task_by_summary"), Description("Mark a task as completed by summary. If the user named a list, only that list is searched. If they did not name a list, all visible lists are searched and the tool only completes when exactly one match exists.")]
    public async Task<string> CompleteTaskInListAsync(
        [Description("User-facing task list name such as 'Shopping', 'Work', or 'task list'. Omit or pass null to search all visible lists.")] string? taskListName,
        [Description("Exact task summary to match. If multiple tasks share this summary, the tool fails with candidates.")] string summary,
        [Description("ETag for optimistic concurrency control (null to preserve the fetched ETag)")] string? etag = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await ResolveUniqueMatchAsync(taskListName, summary, cancellationToken);

        var task = existing.Task with
        {
            Status = CalDavTaskStatus.Completed,
            Completed = _timeProvider.GetUtcNow(),
            ETag = etag ?? existing.Task.ETag
        };

        var completed = await _taskService.UpdateTaskAsync(task, cancellationToken);
        return JsonSerializer.Serialize(completed);
    }

    [McpServerTool(Name = "delete_task_by_summary"), Description("Delete a task by summary. If the user named a list, only that list is searched. If they did not name a list, all visible lists are searched and the tool only deletes when exactly one match exists.")]
    public async Task<string> DeleteTaskInListAsync(
        [Description("User-facing task list name such as 'Shopping', 'Work', or 'task list'. Omit or pass null to search all visible lists.")] string? taskListName,
        [Description("Exact task summary to match. If multiple tasks share this summary, the tool fails with candidates.")] string summary,
        [Description("ETag for optimistic concurrency control (null to skip concurrency check)")] string? etag = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await ResolveUniqueMatchAsync(taskListName, summary, cancellationToken);

        await _taskService.DeleteTaskAsync(existing.Task.Href, etag ?? existing.Task.ETag, cancellationToken);
        return JsonSerializer.Serialize(new { Href = existing.Task.Href, Deleted = true, TaskList = existing.TaskList.DisplayName });
    }

    private async Task<TaskList> ResolveTaskListAsync(string? taskListName, CancellationToken cancellationToken)
    {
        var taskLists = await _taskService.GetTaskListsAsync(cancellationToken);
        return await _taskListResolver.ResolveAsync(taskLists, taskListName, cancellationToken);
    }

    private async Task<IReadOnlyList<TaskItem>> FindTasksBySummaryAsync(
        TaskList taskList,
        string summary,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            throw new ArgumentException("Task summary must be provided.", nameof(summary));
        }

        var requestedSummary = summary.Trim();
        var tasks = await _taskService.GetTasksAsync(taskList.Href, new TaskQuery(), cancellationToken);
        return tasks
            .Where(task => string.Equals(task.Summary?.Trim(), requestedSummary, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private async Task<IReadOnlyList<TaskMatch>> FindMatchesAsync(
        string? taskListName,
        string summary,
        CancellationToken cancellationToken)
    {
        var taskLists = await GetListsToSearchAsync(taskListName, cancellationToken);
        var matches = new List<TaskMatch>();

        foreach (var taskList in taskLists)
        {
            var tasks = await FindTasksBySummaryAsync(taskList, summary, cancellationToken);
            matches.AddRange(tasks.Select(task => new TaskMatch(taskList, task)));
        }

        return matches;
    }

    private async Task<TaskMatch> ResolveUniqueMatchAsync(
        string? taskListName,
        string summary,
        CancellationToken cancellationToken)
    {
        var matches = await FindMatchesAsync(taskListName, summary, cancellationToken);

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException(string.IsNullOrWhiteSpace(taskListName)
                ? $"Task '{summary.Trim()}' was not found in any visible task list."
                : $"Task '{summary.Trim()}' was not found in list '{taskListName.Trim()}'."),
            _ => throw new InvalidOperationException(
                $"Task summary '{summary.Trim()}' is ambiguous. Matching tasks: {string.Join(", ", matches.Select(match => $"{match.Task.Summary} in {match.TaskList.DisplayName} ({match.Task.Href})"))}.")
        };
    }

    private async Task<IReadOnlyList<TaskList>> GetListsToSearchAsync(string? taskListName, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(taskListName))
        {
            return [await ResolveTaskListAsync(taskListName, cancellationToken)];
        }

        return await _taskService.GetTaskListsAsync(cancellationToken);
    }

    private sealed record TaskMatch(TaskList TaskList, TaskItem Task);
}
