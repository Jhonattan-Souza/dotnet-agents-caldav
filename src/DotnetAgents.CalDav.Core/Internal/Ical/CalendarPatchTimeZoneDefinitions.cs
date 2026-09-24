using System.Globalization;
using NodaTime;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>
/// Supplies an IANA VTIMEZONE for each IANA TZID that a semantic edit introduces without a
/// definition. TZIDs already referenced by the original resource keep their original form.
/// </summary>
internal static class CalendarPatchTimeZoneDefinitions
{
    public static byte[] AddIntroduced(ReadOnlySpan<byte> originalUtf8, byte[] editedUtf8)
    {
        var edited = CalendarContentDocument.Parse(editedUtf8);
        var known = CalendarTimeZoneIdentifiers.DefinedIn(edited.Properties);
        known.UnionWith(CalendarTimeZoneIdentifiers.ReferencedIn(CalendarContentDocument.Parse(originalUtf8).Properties));
        var introduced = CalendarTimeZoneIdentifiers.ReferencedIn(edited.Properties)
            .Where(timeZoneId => !known.Contains(timeZoneId)
                && DateTimeZoneProviders.Tzdb.GetZoneOrNull(timeZoneId) is not null)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var definitions = string.Concat(introduced
            .Select(timeZoneId => (TimeZoneId: timeZoneId, Values: LocalValues(edited, timeZoneId)))
            .Where(zone => zone.Values.Length > 0)
            .Select(zone => CalendarCreateTimeZoneSerializer.SerializeForLocalValues(zone.TimeZoneId, zone.Values)));
        if (definitions.Length == 0)
            return editedUtf8;
        var firstEntity = edited.Components.First(component => component.Path.Count == 2
            && component.Path[1].Name is "VEVENT" or "VTODO");
        return edited.InsertBeforeComponent(firstEntity.Path, definitions);
    }

    private static DateTime[] LocalValues(CalendarContentDocument document, string timeZoneId) => document.Properties
        .Where(property => CalendarTimeZoneIdentifiers.ReferencedIn([property]).Contains(timeZoneId, StringComparer.Ordinal))
        .SelectMany(property => property.RawEncodedValue.Split([',', '/'], StringSplitOptions.None))
        .Where(token => token.Length == 15)
        .Select(token => DateTime.ParseExact(token, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture))
        .ToArray();
}
