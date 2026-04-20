using DotnetAgents.CalDav.Core.Abstractions;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>
/// MCP tools for task list operations. Each method decorated with <see cref="McpServerToolAttribute"/>
/// is exposed as an MCP tool via the stdio transport.
/// </summary>
[McpServerToolType]
public sealed class TaskListTools
{
    private readonly ITaskService _taskService;
    private readonly ITaskListResolver _taskListResolver;

    public TaskListTools(ITaskService taskService, ITaskListResolver taskListResolver)
    {
        _taskService = taskService;
        _taskListResolver = taskListResolver;
    }

    [McpServerTool(Name = "list_task_lists"), Description("List all CalDAV task lists (calendar collections) for the configured principal. Use this to inspect the exact display names before calling list-aware chat tools.")]
    public async Task<string> ListTaskListsAsync(CancellationToken cancellationToken)
    {
        var taskLists = await _taskService.GetTaskListsAsync(cancellationToken);
        var defaultList = await TryResolveDefaultAsync(taskLists, cancellationToken);
        var listsWithDefault = taskLists.Select(taskList => taskList with
        {
            IsDefault = defaultList is not null && string.Equals(taskList.Href, defaultList.Href, StringComparison.OrdinalIgnoreCase)
        });

        return System.Text.Json.JsonSerializer.Serialize(listsWithDefault);
    }

    private async Task<DotnetAgents.CalDav.Core.Models.TaskList?> TryResolveDefaultAsync(
        IReadOnlyList<DotnetAgents.CalDav.Core.Models.TaskList> taskLists,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _taskListResolver.ResolveAsync(taskLists, null, cancellationToken);
        }
        catch (DotnetAgents.CalDav.Core.TaskListResolutionException)
        {
            return null;
        }
    }
}
