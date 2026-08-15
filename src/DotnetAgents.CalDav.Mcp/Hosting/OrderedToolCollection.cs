using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Hosting;

/// <summary>Provides a deterministic wire order while retaining SDK tool lookup behavior.</summary>
internal sealed class OrderedToolCollection : McpServerPrimitiveCollection<McpServerTool>
{
    private static readonly IReadOnlyDictionary<string, int> Ranks = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["calendars.list"] = 0,
        ["calendar_resources.get"] = 1,
        ["calendar_resources.exact_get"] = 2,
        ["list_task_lists"] = 3,
        ["show_tasks"] = 4,
        ["add_task"] = 5,
        ["find_tasks"] = 6,
        ["complete_task_by_summary"] = 7,
        ["delete_task_by_summary"] = 8
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
