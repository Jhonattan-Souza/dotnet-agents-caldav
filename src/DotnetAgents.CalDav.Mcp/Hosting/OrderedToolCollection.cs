using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Hosting;

/// <summary>Provides a deterministic wire order while retaining SDK tool lookup behavior.</summary>
internal sealed class OrderedToolCollection : McpServerPrimitiveCollection<McpServerTool>
{
    private static readonly IReadOnlyDictionary<string, int> Ranks = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["calendars.list"] = 0,
        ["list_task_lists"] = 1,
        ["show_tasks"] = 2,
        ["add_task"] = 3,
        ["find_tasks"] = 4,
        ["complete_task_by_summary"] = 5,
        ["delete_task_by_summary"] = 6
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
