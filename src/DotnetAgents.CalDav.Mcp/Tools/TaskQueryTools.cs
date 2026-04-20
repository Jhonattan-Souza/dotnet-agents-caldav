using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>
/// MCP tools for querying tasks within task lists.
/// Each method decorated with <see cref="McpServerToolAttribute"/>
/// is exposed as an MCP tool via the stdio transport.
/// </summary>
[McpServerToolType]
public sealed class TaskQueryTools
{
    private readonly ITaskService _taskService;

    public TaskQueryTools(ITaskService taskService)
    {
        _taskService = taskService;
    }

    [McpServerTool(Name = "list_tasks"), Description("List tasks in a CalDAV task list by absolute href with optional filters. Only use this when the user explicitly provides or confirms the exact href. Prefer the chat-oriented list-name tools for normal agent workflows.")]
    public async Task<string> ListTasksAsync(
        [Description("The absolute href of the task list to query. Only use this when the user explicitly provides or confirms the exact href. Prefer chat-oriented list-name tools otherwise.")] string taskListHref,
        [Description("Filter by status: NeedsAction, InProcess, Completed, or Cancelled")] string? status = null,
        [Description("Filter for tasks due after this date/time")] DateTimeOffset? dueAfter = null,
        [Description("Filter for tasks due before this date/time")] DateTimeOffset? dueBefore = null,
        [Description("Search for tasks containing this text in summary or description")] string? textSearch = null,
        [Description("Filter by category")] string? category = null,
        CancellationToken cancellationToken = default)
    {
        var query = new TaskQuery
        {
            Status = status is not null ? EnumParsingHelpers.ParseTaskStatus(status) : null,
            DueAfter = dueAfter,
            DueBefore = dueBefore,
            TextSearch = textSearch,
            Category = category
        };

        var tasks = await _taskService.GetTasksAsync(taskListHref, query, cancellationToken);
        return System.Text.Json.JsonSerializer.Serialize(tasks);
    }

    [McpServerTool(Name = "get_task"), Description("Get a single CalDAV task by its absolute href. Only use this when the user explicitly provides or confirms the exact href. Prefer chat-oriented list-name tools otherwise.")]
    public async Task<string> GetTaskAsync(
        [Description("The absolute href of the task to retrieve. Only use this when the user explicitly provides or confirms the exact href. Prefer chat-oriented list-name tools otherwise.")] string href,
        CancellationToken cancellationToken)
    {
        var task = await _taskService.GetTaskAsync(href, cancellationToken);

        if (task is null)
        {
            throw new InvalidOperationException($"Task not found at href: {href}");
        }

        return System.Text.Json.JsonSerializer.Serialize(task);
    }
}
