using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Hosting;

/// <summary>Provides a deterministic wire order while retaining SDK tool lookup behavior.</summary>
internal sealed class OrderedToolCollection : McpServerPrimitiveCollection<McpServerTool>
{
    private static readonly IReadOnlyDictionary<string, int> Ranks = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["calendars.list"] = 0,
        ["calendar_entities.query"] = 1,
        ["calendar_occurrences.query"] = 2,
        ["calendar_resources.get"] = 3,
        ["events.create"] = 4,
        ["todos.create"] = 6,
        ["calendar_resources.delete"] = 15,
        ["calendar_resources.exact_get"] = 16,
        ["list_task_lists"] = 17,
        ["show_tasks"] = 18,
        ["add_task"] = 19,
        ["find_tasks"] = 20,
        ["complete_task_by_summary"] = 21,
        ["delete_task_by_summary"] = 22
    };

    public OrderedToolCollection(IEnumerable<McpServerTool> tools)
        : base(StringComparer.Ordinal)
    {
        foreach (var tool in tools)
            Add(tool);
    }

    public override McpServerTool[] ToArray() => base.ToArray()
        .OrderBy(GetRank)
        .ThenBy(tool => tool.ProtocolTool.Name, StringComparer.Ordinal)
        .ToArray();

    public override IEnumerator<McpServerTool> GetEnumerator() => ((IEnumerable<McpServerTool>)ToArray()).GetEnumerator();

    private static int GetRank(McpServerTool tool) => Ranks.GetValueOrDefault(tool.ProtocolTool.Name, int.MaxValue);
}
