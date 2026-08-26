using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Hosting;

/// <summary>Provides a deterministic wire order while retaining SDK tool lookup behavior.</summary>
internal sealed class OrderedToolCollection : McpServerPrimitiveCollection<McpServerTool>
{
    private static readonly IReadOnlyDictionary<string, int> Ranks = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["calendars.list"] = 0,
        ["calendars.create"] = 1,
        ["calendars.delete"] = 2,
        ["calendar_entities.query"] = 3,
        ["calendar_occurrences.query"] = 4,
        ["todos.query"] = 5,
        ["calendar_resources.get"] = 6,
        ["events.create"] = 7,
        ["events.patch"] = 8,
        ["todos.create"] = 9,
        ["todos.patch"] = 10,
        ["todos.complete"] = 11,
        ["calendar_occurrences.add"] = 12,
        ["calendar_occurrences.exclude"] = 13,
        ["calendar_occurrences.restore_exclusion"] = 14,
        ["calendar_occurrences.cancel"] = 15,
        ["calendar_occurrences.restore_cancellation"] = 16,
        ["calendar_resources.move"] = 17,
        ["calendar_resources.delete"] = 18,
        ["calendar_resources.exact_get"] = 19,
        ["calendar_resources.exact_create"] = 20,
        ["calendar_resources.exact_replace"] = 21,
        ["calendar_resources.exact_move"] = 22
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
