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
        ["todos.query"] = 3,
        ["calendar_resources.get"] = 4,
        ["events.create"] = 5,
        ["events.patch"] = 6,
        ["todos.create"] = 7,
        ["todos.patch"] = 8,
        ["todos.complete"] = 9,
        ["calendar_occurrences.add"] = 10,
        ["calendar_occurrences.exclude"] = 11,
        ["calendar_occurrences.restore_exclusion"] = 12,
        ["calendar_occurrences.cancel"] = 13,
        ["calendar_occurrences.restore_cancellation"] = 14,
        ["calendar_resources.move"] = 15,
        ["calendar_resources.delete"] = 16,
        ["calendar_resources.exact_get"] = 17,
        ["calendar_resources.exact_create"] = 18,
        ["calendar_resources.exact_replace"] = 19,
        ["calendar_resources.exact_move"] = 20
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
