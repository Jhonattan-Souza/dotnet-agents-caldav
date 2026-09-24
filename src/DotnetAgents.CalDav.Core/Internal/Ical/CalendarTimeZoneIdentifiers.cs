using NodaTime;
using NodaTime.TimeZones;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>
/// Resolves a TZID that has no resource-local VTIMEZONE through the bundled tzdb data and its
/// CLDR Windows mapping, so every reader, evaluator and authoring path agrees on one zone.
/// </summary>
internal static class CalendarTimeZoneIdentifiers
{
    private static readonly IDictionary<string, string> WindowsMapping =
        TzdbDateTimeZoneSource.Default.WindowsMapping.PrimaryMapping;

    /// <summary>Returns the IANA identifier itself, the mapped IANA identifier for a Windows identifier, or null.</summary>
    public static string? ToIanaIdentifier(string timeZoneId)
    {
        if (DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId) is not null)
            return timeZoneId;
        return WindowsMapping.TryGetValue(timeZoneId, out var mapped) ? mapped : null;
    }

    /// <summary>Whether the identifier is not IANA but resolves through the Windows mapping.</summary>
    public static bool IsMappedWindowsIdentifier(string timeZoneId) =>
        DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId) is null
        && WindowsMapping.ContainsKey(timeZoneId);

    public static DateTimeZone? FindZone(string timeZoneId) =>
        ToIanaIdentifier(timeZoneId) is { } ianaIdentifier
            ? DateTimeZoneProviders.Tzdb[ianaIdentifier]
            : null;

    /// <summary>Returns the distinct TZID parameter values referenced by any property.</summary>
    public static IEnumerable<string> ReferencedIn(IEnumerable<CalendarContentProperty> properties) => properties
        .SelectMany(property => property.Parameters)
        .Where(parameter => parameter.Name.Equals("TZID", StringComparison.OrdinalIgnoreCase))
        .SelectMany(parameter => parameter.Values)
        .Distinct(StringComparer.Ordinal);

    /// <summary>Returns the TZID values defined by resource-local VTIMEZONE components.</summary>
    public static HashSet<string> DefinedIn(IEnumerable<CalendarContentProperty> properties) => properties
        .Where(property => property.Name.Equals("TZID", StringComparison.OrdinalIgnoreCase)
            && property.ComponentPath.Count == 2
            && property.ComponentPath[1].Name.Equals("VTIMEZONE", StringComparison.OrdinalIgnoreCase))
        .Select(property => property.RawEncodedValue)
        .ToHashSet(StringComparer.Ordinal);
}
