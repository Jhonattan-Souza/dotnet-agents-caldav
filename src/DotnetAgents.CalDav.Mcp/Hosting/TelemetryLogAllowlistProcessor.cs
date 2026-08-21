using OpenTelemetry;
using OpenTelemetry.Logs;

namespace DotnetAgents.CalDav.Mcp.Hosting;

internal sealed class TelemetryLogAllowlistProcessor : BaseProcessor<LogRecord>
{
    private static readonly HashSet<string> AllowedAttributeNames = new(StringComparer.Ordinal)
    {
        "Category",
        "Code",
        "MutationState",
        "Outcome",
        "Phase"
    };

    public override void OnEnd(LogRecord data)
    {
        data.Body = "CalDAV diagnostic";
        data.FormattedMessage = null;
        data.Exception = null;
        data.Attributes = data.Attributes?
            .Where(attribute => IsSafeAttribute(attribute))
            .ToArray();
    }

    private static bool IsSafeAttribute(KeyValuePair<string, object?> attribute) =>
        AllowedAttributeNames.Contains(attribute.Key)
        && attribute.Value is string value
        && value is { Length: > 0 and <= 64 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
