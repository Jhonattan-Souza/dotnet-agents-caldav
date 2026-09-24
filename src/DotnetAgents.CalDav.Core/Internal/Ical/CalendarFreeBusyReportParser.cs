using System.Globalization;
using System.Collections.Frozen;
using System.Text;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>Validates native VFREEBUSY responses before clipping and coalescing periods of the same type.</summary>
internal static class CalendarFreeBusyReportParser
{
    internal const int MaximumPeriods = 5000;
    // One Radicale 3.7.8 busy period component spends six content lines, so the
    // structural budget must admit the period budget in that representation.
    private const int MaximumContentLines = 40000;
    private const int MaximumComponentDepth = 3;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly FrozenSet<string> RecurrenceProperties = new[]
    {
        "RRULE", "RDATE", "EXDATE"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> BusyComponentProperties = new[]
    {
        "FREEBUSY", "DTSTART", "DTEND", "FBTYPE"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses a free/busy REPORT body. RFC 4791 section 7.10 content is always accepted,
    /// including busy periods spread over several VFREEBUSY components. The Radicale
    /// 3.7.8 representation is accepted only for that verified interoperability profile.
    /// </summary>
    internal static IReadOnlyList<CalendarBusyPeriod> Parse(
        byte[] body,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken,
        bool radicaleProfile = false)
    {
        try
        {
            var content = Unfold(body);
            ValidateStructureBudget(content, cancellationToken);
            var document = CalendarContentDocument.Parse(content);
            var busyComponents = ValidateComponents(document, radicaleProfile);
            ValidatePropertyScope(document.Properties);
            var collector = new BusyPeriodCollector(document, from, to, radicaleProfile, cancellationToken);
            var properties = document.Properties
                .Where(IsBusyComponentProperty)
                .ToLookup(property => property.ComponentPath[1].Occurrence);
            foreach (var component in busyComponents)
                collector.ReadComponent(properties[component.Path[1].Occurrence].ToArray());
            return Merge(collector.Periods);
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
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase) && ++depth > MaximumComponentDepth)
                throw InvalidResponse();
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
                depth--;
        }
    }

    private static CalendarContentComponent[] ValidateComponents(CalendarContentDocument document, bool radicaleProfile)
    {
        // A VTIMEZONE may accompany zoned values; any other component could carry
        // busy information that this parser would otherwise silently ignore.
        if (document.Components.Count(component => component.Path.Count == 1) != 1
            || !document.Components.All(IsPermittedComponent))
            throw InvalidResponse();
        var busyComponents = document.Components.Where(component => component.Path is [_, { Name: "VFREEBUSY" }]).ToArray();
        // RFC 4791 section 7.10 requires a VFREEBUSY component. Radicale 3.7.8
        // reports a window without busy time as an otherwise empty VCALENDAR.
        if (busyComponents.Length == 0 && !radicaleProfile)
            throw InvalidResponse();
        var versions = document.Properties.Where(property => property.ComponentPath.Count == 1
            && property.Name.Equals("VERSION", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (versions.Length != 1 || versions[0].RawEncodedValue != "2.0")
            throw InvalidResponse();
        return busyComponents;
    }

    private static bool IsPermittedComponent(CalendarContentComponent component) => component.Path switch
    {
        [{ Name: "VCALENDAR" }] => true,
        [{ Name: "VCALENDAR" }, { Name: "VFREEBUSY" or "VTIMEZONE" }] => true,
        [{ Name: "VCALENDAR" }, { Name: "VTIMEZONE" }, { Name: "STANDARD" or "DAYLIGHT" }] => true,
        _ => false
    };

    private static void ValidatePropertyScope(IReadOnlyList<CalendarContentProperty> properties)
    {
        // RFC 5545 section 3.6 places busy periods, their types and bounds inside
        // VFREEBUSY. Recurrence properties are prohibited there by section 3.6.4:
        // the server must expand them into periods. Ignoring any misplaced
        // representation could turn busy time into an empty report. Time zone
        // observances legitimately carry their own DTSTART and recurrence rules.
        foreach (var property in properties)
        {
            var misplaced = IsTimeZoneProperty(property)
                ? property.Name.Equals("FREEBUSY", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("FBTYPE", StringComparison.OrdinalIgnoreCase)
                : RecurrenceProperties.Contains(property.Name)
                    || BusyComponentProperties.Contains(property.Name) && !IsBusyComponentProperty(property);
            if (misplaced)
                throw InvalidResponse();
        }
    }

    private static bool IsTimeZoneProperty(CalendarContentProperty property) =>
        property.ComponentPath is [_, { Name: "VTIMEZONE" }, ..];

    private static bool IsBusyComponentProperty(CalendarContentProperty property) =>
        property.ComponentPath is [_, { Name: "VFREEBUSY" }];

    private static IEnumerable<CalendarContentProperty> Named(IEnumerable<CalendarContentProperty> properties, string name) =>
        properties.Where(property => property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static DateTimeOffset? ReadOptionalBound(IReadOnlyList<CalendarContentProperty> properties, string name)
    {
        var values = Named(properties, name).Take(2).ToArray();
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
        ValidateSingleValuedParameter(property, "VALUE");
        if (property.ValueType != expected || HasParameter(property, "TZID"))
            throw InvalidResponse();
    }

    private static void ValidateSingleValuedParameter(CalendarContentProperty property, string name)
    {
        var values = property.Parameters.Where(parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (values.Length > 1 || values.Any(parameter => parameter.Values.Count != 1))
            throw InvalidResponse();
    }

    private static bool HasParameter(CalendarContentProperty property, string name) =>
        property.Parameters.Any(parameter => parameter.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string ReadBusyType(CalendarContentProperty property)
    {
        ValidateValueType(property, CalendarPropertyValueType.Period);
        var parameters = property.Parameters.Where(parameter => parameter.Name.Equals("FBTYPE", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (parameters.Length == 0)
            return "BUSY";
        if (parameters.Length != 1 || parameters[0].Values.Count != 1)
            throw InvalidResponse();
        return NormalizeBusyType(parameters[0].Values[0]);
    }

    private static string NormalizeBusyType(string value)
    {
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
        return CreatePeriod(from, to, busyType);
    }

    private static NativeBusyPeriod CreatePeriod(DateTimeOffset from, DateTimeOffset to, string busyType) =>
        to > from ? new(from, to, busyType) : throw InvalidResponse();

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

    /// <summary>Counts every reported period against one budget before clipping it to the requested window.</summary>
    private sealed class BusyPeriodCollector(
        CalendarContentDocument document,
        DateTimeOffset from,
        DateTimeOffset to,
        bool radicaleProfile,
        CancellationToken cancellationToken)
    {
        private int _observed;
        private CalendarTemporalResolver? _resolver;

        public List<NativeBusyPeriod> Periods { get; } = [];

        public void ReadComponent(IReadOnlyList<CalendarContentProperty> properties)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var busyTypes = Named(properties, "FBTYPE").ToArray();
            if (busyTypes.Length == 0)
            {
                ReadPeriodList(properties);
                return;
            }
            // FBTYPE is a FREEBUSY parameter. As a component property it is a stray
            // type unless it is the one type of a Radicale 3.7.8 period component,
            // whose DTSTART and DTEND are the busy period rather than its bounds.
            if (!radicaleProfile || busyTypes.Length != 1 || Named(properties, "FREEBUSY").Any())
                throw InvalidResponse();
            ReadPeriodComponent(properties, busyTypes[0]);
        }

        private void ReadPeriodList(IReadOnlyList<CalendarContentProperty> properties)
        {
            // The successful native REPORT determines the requested window. The
            // component's optional bounds describe its busy information and need
            // only be valid and internally consistent when supplied.
            var start = ReadOptionalBound(properties, "DTSTART");
            var end = ReadOptionalBound(properties, "DTEND");
            if (start is { } actualStart && end is { } actualEnd && actualStart >= actualEnd)
                throw InvalidResponse();
            foreach (var property in Named(properties, "FREEBUSY"))
            {
                var busyType = ReadBusyType(property);
                foreach (var range in property.RawEncodedValue.AsSpan().Split(','))
                    Add(ReadPeriod(property.RawEncodedValue[range], busyType));
            }
        }

        private void ReadPeriodComponent(IReadOnlyList<CalendarContentProperty> properties, CalendarContentProperty busyType)
        {
            var start = Named(properties, "DTSTART").ToArray();
            var end = Named(properties, "DTEND").ToArray();
            if (busyType.Parameters.Count != 0 || start.Length != 1 || end.Length != 1)
                throw InvalidResponse();
            Add(CreatePeriod(ReadInstant(start[0]), ReadInstant(end[0]), NormalizeBusyType(busyType.RawEncodedValue)));
        }

        private DateTimeOffset ReadInstant(CalendarContentProperty property)
        {
            ValidateSingleValuedParameter(property, "VALUE");
            ValidateSingleValuedParameter(property, "TZID");
            if (property.ValueType != CalendarPropertyValueType.DateTime)
                throw InvalidResponse();
            if (!HasParameter(property, "TZID"))
                return ParseUtc(property.RawEncodedValue);
            if (property.RawEncodedValue.EndsWith('Z'))
                throw InvalidResponse();
            // A zoned value resolves through the report's own VTIMEZONE when present.
            // No evaluation zone is supplied, so a floating value never resolves.
            _resolver ??= CreateTimeZoneResolver();
            return _resolver.Resolve(ToCalendarProperty(property)).Value ?? throw InvalidResponse();
        }

        private CalendarTemporalResolver CreateTimeZoneResolver()
        {
            var zones = document.Components.Where(component => component.Path is [_, { Name: "VTIMEZONE" }])
                .Select(component => document.GetComponentOccurrence(component.Path).OriginalSlice.TrimEnd('\r', '\n') + "\r\n");
            var calendar = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//dotnet-agents-caldav//free-busy//EN\r\n"
                + string.Concat(zones) + "END:VCALENDAR\r\n");
            var properties = CalendarContentDocument.Parse(calendar).Properties.Select(ToCalendarProperty).ToArray();
            return new CalendarTemporalResolver(properties, calendar, cancellationToken);
        }

        private static CalendarProperty ToCalendarProperty(CalendarContentProperty property) => new(
            property.ComponentPath, property.Name, property.Parameters,
            property.ValueType, property.RawEncodedValue, property.OriginalSlice);

        private void Add(NativeBusyPeriod period)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_observed > MaximumPeriods)
                throw LimitExceeded();
            var start = period.From < from ? from : period.From;
            var end = period.To > to ? to : period.To;
            if (start < end)
                Periods.Add(period with { From = start, To = end });
        }
    }
}
