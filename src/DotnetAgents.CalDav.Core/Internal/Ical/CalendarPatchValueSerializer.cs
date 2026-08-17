using System.Globalization;
using System.Text;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal static class CalendarPatchValueSerializer
{
    public static string Text(string name, string value) => Line(name, CalendarContentDocument.EncodeText(value));

    public static string Token(string name, string value) => Line(name, value.ToUpperInvariant());

    public static string Integer(string name, int value) => Line(name, value.ToString(CultureInfo.InvariantCulture));

    public static string Uri(string name, string value) => Line(name, value);

    public static string Duration(string value) => Line("DURATION", value);

    public static string Geo(CalendarGeo value) => Line(
        "GEO",
        value.Latitude.ToString("R", CultureInfo.InvariantCulture) + ";"
            + value.Longitude.ToString("R", CultureInfo.InvariantCulture));

    public static string Temporal(string name, CalendarTemporalValue value)
    {
        var header = new StringBuilder(name);
        var encoded = value.Kind switch
        {
            CalendarTemporalKind.Date => value.Value.Replace("-", string.Empty, StringComparison.Ordinal),
            CalendarTemporalKind.UtcDateTime => Compact(value.Value[..^1]) + "Z",
            CalendarTemporalKind.FloatingDateTime => Compact(value.Value),
            CalendarTemporalKind.ZonedDateTime => Compact(value.Value),
            _ => throw new ArgumentException("Unsupported temporal kind.", nameof(value))
        };
        if (value.Kind == CalendarTemporalKind.Date)
            header.Append(";VALUE=DATE");
        else if (value.Kind == CalendarTemporalKind.ZonedDateTime)
            header.Append(";TZID=").Append(EncodeParameter(value.TimeZoneId!));
        return Line(header.ToString(), encoded);
    }

    public static CalendarTemporalValue ParseTemporal(CalendarContentProperty property)
    {
        var kind = property.ValueType == CalendarPropertyValueType.Date
            ? CalendarTemporalKind.Date
            : property.RawEncodedValue.EndsWith('Z')
                ? CalendarTemporalKind.UtcDateTime
                : property.Parameters.Any(parameter => parameter.Name.Equals("TZID", StringComparison.OrdinalIgnoreCase))
                    ? CalendarTemporalKind.ZonedDateTime
                    : CalendarTemporalKind.FloatingDateTime;
        var format = kind == CalendarTemporalKind.Date ? "yyyyMMdd" : "yyyyMMdd'T'HHmmss";
        var raw = kind == CalendarTemporalKind.UtcDateTime ? property.RawEncodedValue[..^1] : property.RawEncodedValue;
        var parsed = DateTime.ParseExact(raw, format, CultureInfo.InvariantCulture, DateTimeStyles.None);
        var value = kind == CalendarTemporalKind.Date
            ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : parsed.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
                + (kind == CalendarTemporalKind.UtcDateTime ? "Z" : string.Empty);
        var timeZoneId = kind == CalendarTemporalKind.ZonedDateTime
            ? property.Parameters.Where(parameter => parameter.Name.Equals("TZID", StringComparison.OrdinalIgnoreCase))
                .SelectMany(parameter => parameter.Values).Single()
            : null;
        return new(kind, value, timeZoneId);
    }

    public static string NamedUri(string name, CalendarNamedUri value, string labelName)
    {
        var header = new StringBuilder(name);
        if (value.Label is not null)
            header.Append(';').Append(labelName).Append('=').Append(EncodeParameter(value.Label));
        foreach (var parameter in value.Parameters)
        {
            header.Append(';').Append(parameter.Name.ToUpperInvariant()).Append('=');
            header.AppendJoin(',', parameter.Values.Select(EncodeParameter));
        }
        return Line(header.ToString(), value.Uri);
    }

    public static string Parameter(CalendarParameter parameter)
    {
        var encoded = new StringBuilder(";" + parameter.Name + "=");
        encoded.AppendJoin(',', parameter.Values.Select(EncodeParameter));
        return encoded.ToString();
    }

    private static string Line(string name, string value) => name + ":" + value + "\r\n";

    private static string Compact(string value) => value
        .Replace("-", string.Empty, StringComparison.Ordinal)
        .Replace(":", string.Empty, StringComparison.Ordinal);

    private static string EncodeParameter(string value)
    {
        var encoded = value.Replace("^", "^^", StringComparison.Ordinal)
            .Replace("\r\n", "^n", StringComparison.Ordinal)
            .Replace("\n", "^n", StringComparison.Ordinal)
            .Replace("\"", "^'", StringComparison.Ordinal);
        return encoded.IndexOfAny([':', ';', ',']) >= 0 ? "\"" + encoded + "\"" : encoded;
    }
}
