using System.Globalization;
using System.Collections.Frozen;
using System.Text;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>Validates native VFREEBUSY responses before clipping and coalescing periods of the same type.</summary>
internal static class CalendarFreeBusyReportParser
{
    internal const int MaximumPeriods = 5000;
    private const int MaximumContentLines = 10000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly FrozenSet<string> ProhibitedReportProperties = new[]
    {
        "FBTYPE", "RRULE", "RDATE", "EXDATE"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> ScopedReportProperties = new[]
    {
        "FREEBUSY", "DTSTART", "DTEND"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlyList<CalendarBusyPeriod> Parse(
        byte[] body,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        try
        {
            var content = Unfold(body);
            ValidateStructureBudget(content, cancellationToken);
            var document = CalendarContentDocument.Parse(content);
            ValidateComponents(document);
            ValidateReportProperties(document.Properties);
            var properties = document.Properties.Where(property => property.ComponentPath.Count == 2).ToArray();
            ValidateBounds(properties);
            return Merge(ReadPeriods(properties, from, to, cancellationToken));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException or ArgumentOutOfRangeException)
        {
            throw InvalidResponse();
        }
    }

    private static byte[] Unfold(byte[] body)
    {
        // Unfold once in bytes, including folds within a UTF-8 code point. This avoids
        // repeated string concatenation for a heavily folded FREEBUSY property.
        var output = new byte[body.Length];
        var written = 0;
        for (var index = 0; index < body.Length; index++)
        {
            var foldLength = FoldLength(body, index);
            if (foldLength > 0)
                index += foldLength - 1;
            else
                output[written++] = body[index];
        }
        return output[..written];
    }

    private static int FoldLength(byte[] body, int index)
    {
        if (body[index] == '\r' && index + 2 < body.Length && body[index + 1] == '\n')
            return body[index + 2] is (byte)' ' or (byte)'\t' ? 3 : 0;
        if (body[index] == '\n' && index + 1 < body.Length)
            return body[index + 1] is (byte)' ' or (byte)'\t' ? 2 : 0;
        return 0;
    }

    private static void ValidateStructureBudget(byte[] content, CancellationToken cancellationToken)
    {
        using var reader = new StringReader(StrictUtf8.GetString(content));
        var depth = 0;
        var count = 0;
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > MaximumContentLines)
                throw LimitExceeded();
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase) && ++depth > 2)
                throw InvalidResponse();
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
                depth--;
        }
    }

    private static void ValidateComponents(CalendarContentDocument document)
    {
        if (document.Components.Count != 2
            || !document.Components.Any(component => component.Path.Count == 1 && component.Path[0].Name == "VCALENDAR")
            || !document.Components.Any(component => component.Path.Count == 2
                && component.Path[0].Name == "VCALENDAR" && component.Path[1].Name == "VFREEBUSY"))
            throw InvalidResponse();
        var versions = document.Properties.Where(property => property.ComponentPath.Count == 1
            && property.Name.Equals("VERSION", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (versions.Length != 1 || versions[0].RawEncodedValue != "2.0")
            throw InvalidResponse();
    }

    private static void ValidateReportProperties(IReadOnlyList<CalendarContentProperty> properties)
    {
        // FBTYPE is a FREEBUSY parameter, not a property describing a whole
        // component. Recurrence properties are prohibited by RFC 5545 section
        // 3.6.4: the server must expand them into FREEBUSY periods. Ignoring any
        // of these representations could turn busy time into an empty report.
        // RFC 5545 section 3.6 places busy periods and their bounds inside
        // VFREEBUSY, so validate their scope before filtering calendar properties.
        if (properties.Any(property => ProhibitedReportProperties.Contains(property.Name)
                || property.ComponentPath.Count != 2 && ScopedReportProperties.Contains(property.Name)))
            throw InvalidResponse();
    }

    private static void ValidateBounds(IReadOnlyList<CalendarContentProperty> properties)
    {
        // The successful native REPORT determines the requested window. The
        // component's optional bounds describe its busy information and need
        // only be valid and internally consistent when supplied.
        var start = ReadOptionalBound(properties, "DTSTART");
        var end = ReadOptionalBound(properties, "DTEND");
        if (start is { } actualStart && end is { } actualEnd && actualStart >= actualEnd)
            throw InvalidResponse();
    }

    private static DateTimeOffset? ReadOptionalBound(IReadOnlyList<CalendarContentProperty> properties, string name)
    {
        var values = properties.Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return values.Length switch
        {
            0 => null,
            1 => ReadDateTimeProperty(values[0]),
            _ => throw InvalidResponse()
        };
    }

    private static DateTimeOffset ReadDateTimeProperty(CalendarContentProperty property)
    {
        ValidateValueType(property, CalendarPropertyValueType.DateTime);
        return ParseUtc(property.RawEncodedValue);
    }

    private static void ValidateValueType(CalendarContentProperty property, CalendarPropertyValueType expected)
    {
        var values = property.Parameters.Where(parameter => parameter.Name.Equals("VALUE", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (property.ValueType != expected || values.Length > 1
            || values.Any(parameter => parameter.Values.Count != 1)
            || property.Parameters.Any(parameter => parameter.Name.Equals("TZID", StringComparison.OrdinalIgnoreCase)))
            throw InvalidResponse();
    }

    private static List<NativeBusyPeriod> ReadPeriods(
        IReadOnlyList<CalendarContentProperty> properties,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var periods = new List<NativeBusyPeriod>();
        var observed = 0;
        foreach (var property in properties.Where(property => property.Name.Equals("FREEBUSY", StringComparison.OrdinalIgnoreCase)))
        {
            var busyType = ReadBusyType(property);
            foreach (var range in property.RawEncodedValue.AsSpan().Split(','))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++observed > MaximumPeriods)
                    throw LimitExceeded();
                var period = ReadPeriod(property.RawEncodedValue[range], busyType);
                var clipped = Clip(period, from, to);
                if (clipped is not null)
                    periods.Add(clipped);
            }
        }
        return periods;
    }

    private static string ReadBusyType(CalendarContentProperty property)
    {
        ValidateValueType(property, CalendarPropertyValueType.Period);
        var parameters = property.Parameters.Where(parameter => parameter.Name.Equals("FBTYPE", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (parameters.Length == 0)
            return "BUSY";
        if (parameters.Length != 1 || parameters[0].Values.Count != 1)
            throw InvalidResponse();
        var value = parameters[0].Values[0];
        if (value.Length is < 1 or > 256 || !value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
            throw InvalidResponse();
        return value.ToUpperInvariant() is "FREE" or "BUSY" or "BUSY-TENTATIVE" or "BUSY-UNAVAILABLE"
            ? value.ToUpperInvariant() : value;
    }

    private static NativeBusyPeriod ReadPeriod(string raw, string busyType)
    {
        var values = raw.Split('/');
        if (values.Length != 2)
            throw InvalidResponse();
        var from = ParseUtc(values[0]);
        var to = CalendarDurationArithmetic.LooksLikeDuration(values[1])
            ? AddDuration(from, values[1]) : ParseUtc(values[1]);
        if (to <= from)
            throw InvalidResponse();
        return new(from, to, busyType);
    }

    private static DateTimeOffset AddDuration(DateTimeOffset from, string raw)
    {
        if (!CalendarDurationArithmetic.TryParse(raw, out var duration) || !duration.IsStrictlyPositive)
            throw InvalidResponse();
        return from + duration.LocalClockDuration;
    }

    private static DateTimeOffset ParseUtc(string raw)
    {
        if (!DateTimeOffset.TryParseExact(raw, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
            throw InvalidResponse();
        return value;
    }

    private static NativeBusyPeriod? Clip(NativeBusyPeriod period, DateTimeOffset from, DateTimeOffset to)
    {
        var start = period.From < from ? from : period.From;
        var end = period.To > to ? to : period.To;
        return start < end ? period with { From = start, To = end } : null;
    }

    private static IReadOnlyList<CalendarBusyPeriod> Merge(List<NativeBusyPeriod> periods)
    {
        var merged = new List<NativeBusyPeriod>();
        foreach (var type in periods.GroupBy(period => period.BusyType, StringComparer.Ordinal))
        {
            foreach (var period in type.OrderBy(period => period.From).ThenBy(period => period.To))
            {
                if (merged.Count > 0 && merged[^1].BusyType == period.BusyType && merged[^1].To >= period.From)
                    merged[^1] = merged[^1] with { To = merged[^1].To > period.To ? merged[^1].To : period.To };
                else
                    merged.Add(period);
            }
        }
        return merged.OrderBy(period => period.From).ThenBy(period => period.To).ThenBy(period => period.BusyType, StringComparer.Ordinal)
            .Select(period => new CalendarBusyPeriod(FormatUtc(period.From), FormatUtc(period.To), period.BusyType)).ToArray();
    }

    internal static string FormatUtc(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static CalendarProtocolException InvalidResponse() => new(
        "upstream_protocol_error", "The server returned invalid or incomplete native free/busy information; availability cannot be inferred.");

    private static CalendarProtocolException LimitExceeded() => new(
        "limit_exhausted", "The native free/busy report exceeded its period or content limits. Retry with a smaller UTC window.");

    private sealed record NativeBusyPeriod(DateTimeOffset From, DateTimeOffset To, string BusyType);
}
