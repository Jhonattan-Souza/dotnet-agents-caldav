using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal.Ical;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

/// <summary>Reads and validates Calendar Color, Calendar Order and Calendar Time Zone property values.</summary>
internal static partial class CalendarCollectionPropertyValues
{
    internal static readonly XNamespace AppleIcal = "http://apple.com/ns/ical/";
    internal static readonly XName ColorName = AppleIcal + "calendar-color";
    internal static readonly XName OrderName = AppleIcal + "calendar-order";
    internal static readonly XName TimeZoneName = CalendarMetadataProtocol.CalDav + "calendar-timezone";
    private const int MaximumTimeZoneIdLength = 255;

    /// <summary>Returns <c>#RRGGBB</c> for a 6- or 8-digit value; an Apple alpha channel is discarded.</summary>
    internal static string? ReadColor(string? value)
    {
        var trimmed = value?.Trim();
        return trimmed is not null && ReadableColor().IsMatch(trimmed) ? trimmed[..7] : null;
    }

    internal static bool IsWritableColor(string? value) => value is not null && WritableColor().IsMatch(value);

    /// <summary>Returns a non-negative 32-bit order; any other stored value is not reported.</summary>
    internal static int? ReadOrder(string? value) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var order) ? order : null;

    internal static bool IsWritableOrder(int? value) => value is >= 0;

    internal static bool IsTimeZoneId(string? value) => value is { Length: <= MaximumTimeZoneIdLength }
        && TimeZoneIdCharacters().IsMatch(value)
        && IanaTimeZoneIds.IsValid(value);

    internal static string SerializeTimeZone(string timeZoneId) =>
        CalendarCreateTimeZoneSerializer.SerializeCollectionTimeZone(timeZoneId);

    [GeneratedRegex(@"\A#[0-9A-Fa-f]{6}(?:[0-9A-Fa-f]{2})?\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex ReadableColor();

    [GeneratedRegex(@"\A#[0-9A-Fa-f]{6}\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex WritableColor();

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_+/-]*\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex TimeZoneIdCharacters();
}
