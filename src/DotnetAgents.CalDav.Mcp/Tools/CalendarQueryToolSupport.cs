using System.Globalization;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Models;
using ModelContextProtocol.Protocol;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Shared admission, lexical parsing, and result-budget policy for bounded Calendar queries.</summary>
internal static class CalendarQueryToolSupport
{
    internal const int MaximumArgumentBytes = 256 * 1024;
    internal const int MaximumHumanReadableBytes = 64 * 1024;
    internal const int MaximumStructuredResultBytes = 4 * 1024 * 1024;

    internal static int MeasureArguments(
        IDictionary<string, JsonElement>? rawArguments,
        object materializedArguments) => rawArguments is { } raw
            ? JsonSerializer.SerializeToUtf8Bytes(raw).Length
            : JsonSerializer.SerializeToUtf8Bytes(materializedArguments).Length;

    internal static bool TryCreateScope(
        CalendarEntityScopeArgument scope,
        out CalendarEntityScope domainScope)
    {
        domainScope = null!;
        if (scope.Mode == "default" && scope.Calendar is null)
        {
            domainScope = CalendarEntityScope.Default;
            return true;
        }
        if (scope.Mode == "all" && scope.Calendar is null)
        {
            domainScope = CalendarEntityScope.All;
            return true;
        }
        if (scope.Mode != "selected" || scope.Calendar is null)
            return false;
        return scope.Calendar.By switch
        {
            "name" => TryCreateNameScope(scope.Calendar, out domainScope),
            "href" => TryCreateHrefScope(scope.Calendar, out domainScope),
            _ => false
        };
    }

    internal static bool TryParseUtc(CalendarEntityUtcArgument argument, out DateTimeOffset? value)
    {
        value = null;
        if (argument.Kind != "utcDateTime")
            return false;
        var lexical = argument.Value;
        var hasFraction = lexical.Length > 20 && lexical.Length >= 22 && lexical[19] == '.';
        var secondLexical = hasFraction ? lexical[..19] + "Z" : lexical;
        if (!DateTimeOffset.TryParseExact(
                secondLexical,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
            return false;
        if (!hasFraction)
        {
            value = parsed;
            return true;
        }
        var fraction = lexical.AsSpan(20, lexical.Length - 21);
        if (lexical[^1] != 'Z' || fraction.IsEmpty || !fraction.ToString().All(char.IsAsciiDigit))
            return false;
        return TryApplyFraction(parsed, fraction, out value);
    }

    internal static bool HasScopeShape(JsonElement scope)
    {
        if (!TryGetUniqueProperties(scope, out var properties)
            || !properties.TryGetValue("mode", out var modeElement)
            || modeElement.ValueKind != JsonValueKind.String)
            return false;
        var mode = modeElement.GetString();
        if (mode is "default" or "all")
            return properties.Count == 1;
        return mode == "selected"
            && properties.Count == 2
            && properties.TryGetValue("calendar", out var calendar)
            && HasCalendarReferenceShape(calendar);
    }

    internal static bool HasTemporalShape(JsonElement temporal) =>
        TryGetUniqueProperties(temporal, out var properties)
        && properties.Count == 2
        && properties.ContainsKey("kind")
        && properties.ContainsKey("value");

    internal static int MeasureResult(CallToolResult result) =>
        JsonSerializer.SerializeToUtf8Bytes(result).Length;

    internal static CallToolResult EnsureBoundedResult(
        CallToolResult result,
        Func<int, bool, CallToolResult> createPayloadError)
    {
        var humanReadableBytes = MeasureHumanReadableResult(result);
        if (humanReadableBytes > MaximumHumanReadableBytes)
            return createPayloadError(humanReadableBytes, true);
        var observedBytes = MeasureResult(result);
        return observedBytes <= MaximumStructuredResultBytes
            ? result
            : createPayloadError(observedBytes, false);
    }

    private static bool TryCreateNameScope(
        CalendarEntityReferenceArgument reference,
        out CalendarEntityScope domainScope)
    {
        domainScope = null!;
        if (reference.Name is not { Length: > 0 } name || name != name.Trim() || reference.Href is not null)
            return false;
        domainScope = CalendarEntityScope.Selected(new CalendarReference(Name: name));
        return true;
    }

    private static bool TryCreateHrefScope(
        CalendarEntityReferenceArgument reference,
        out CalendarEntityScope domainScope)
    {
        domainScope = null!;
        if (reference.Href is not { Length: > 0 } href || reference.Name is not null)
            return false;
        domainScope = CalendarEntityScope.Selected(new CalendarReference(Href: href));
        return true;
    }

    private static bool TryApplyFraction(
        DateTimeOffset parsed,
        ReadOnlySpan<char> fraction,
        out DateTimeOffset? value)
    {
        value = null;
        var representedDigits = Math.Min(7, fraction.Length);
        var ticksText = fraction[..representedDigits].ToString().PadRight(7, '0');
        var ticks = int.Parse(ticksText, CultureInfo.InvariantCulture);
        if (fraction[representedDigits..].ContainsAnyExcept('0'))
            ticks++;
        if (ticks == TimeSpan.TicksPerSecond)
        {
            if (parsed.Ticks > DateTimeOffset.MaxValue.Ticks - TimeSpan.TicksPerSecond)
                return false;
            parsed = parsed.AddSeconds(1);
            ticks = 0;
        }
        value = parsed.AddTicks(ticks);
        return true;
    }

    private static bool HasCalendarReferenceShape(JsonElement calendar)
    {
        if (!TryGetUniqueProperties(calendar, out var properties)
            || !properties.TryGetValue("by", out var byElement)
            || byElement.ValueKind != JsonValueKind.String
            || properties.Count != 2)
            return false;
        var by = byElement.GetString();
        return by == "name" && properties.ContainsKey("name")
            || by == "href" && properties.ContainsKey("href");
    }

    private static bool TryGetUniqueProperties(
        JsonElement element,
        out Dictionary<string, JsonElement> properties)
    {
        properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        foreach (var property in element.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
                return false;
        }
        return true;
    }

    internal static int MeasureHumanReadableResult(CallToolResult result)
    {
        var diagnostics = result.StructuredContent is { ValueKind: JsonValueKind.Object } structured
            && structured.TryGetProperty("diagnostics", out var value)
            ? value
            : JsonSerializer.SerializeToElement(Array.Empty<object>());
        return JsonSerializer.SerializeToUtf8Bytes(new CalendarHumanReadableBudget(
            result.Content.OfType<TextContentBlock>().Select(block => block.Text).ToArray(),
            diagnostics)).Length;
    }

    private sealed record CalendarHumanReadableBudget(
        IReadOnlyList<string> Text,
        JsonElement Diagnostics);
}
