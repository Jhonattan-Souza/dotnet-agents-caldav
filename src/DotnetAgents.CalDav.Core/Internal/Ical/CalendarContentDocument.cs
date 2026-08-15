using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Frozen;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal sealed record CalendarContentProperty(
    IReadOnlyList<CalendarComponentPathSegment> ComponentPath,
    string Name,
    IReadOnlyList<CalendarParameter> Parameters,
    CalendarPropertyValueType ValueType,
    string RawEncodedValue,
    string OriginalSlice,
    int Start,
    int Length);

internal sealed record CalendarContentComponent(
    IReadOnlyList<CalendarComponentPathSegment> Path,
    string BeginSlice,
    string EndSlice,
    int Start,
    int Length);

internal sealed partial class CalendarContentDocument
{
    private static readonly FrozenDictionary<string, CalendarPropertyValueType> DefaultValueTypes = CreateDefaultValueTypes();
    private static readonly FrozenDictionary<string, CalendarPropertyValueType> ExplicitValueTypes =
        new Dictionary<string, CalendarPropertyValueType>(StringComparer.OrdinalIgnoreCase)
        {
            ["TEXT"] = CalendarPropertyValueType.Text,
            ["URI"] = CalendarPropertyValueType.Uri,
            ["CAL-ADDRESS"] = CalendarPropertyValueType.Uri,
            ["DATE"] = CalendarPropertyValueType.Date,
            ["DATE-TIME"] = CalendarPropertyValueType.DateTime,
            ["DURATION"] = CalendarPropertyValueType.Duration,
            ["INTEGER"] = CalendarPropertyValueType.Integer,
            ["FLOAT"] = CalendarPropertyValueType.Float,
            ["PERIOD"] = CalendarPropertyValueType.Period,
            ["RECUR"] = CalendarPropertyValueType.Recur,
            ["BINARY"] = CalendarPropertyValueType.Binary
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> RegisteredPropertyNames = DefaultValueTypes.Keys
        .Concat(["TZOFFSETFROM", "TZOFFSETTO"])
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] _authoritativeUtf8;
    private readonly string _content;

    private static FrozenDictionary<string, CalendarPropertyValueType> CreateDefaultValueTypes()
    {
        var mappings = new Dictionary<string, CalendarPropertyValueType>(StringComparer.OrdinalIgnoreCase);
        AddDefaultValueTypes(mappings, CalendarPropertyValueType.Uri,
            "ATTACH", "ATTENDEE", "CALENDAR-ADDRESS", "CONCEPT", "CONFERENCE", "IMAGE", "LINK", "ORGANIZER", "SOURCE", "TZURL", "URL");
        AddDefaultValueTypes(mappings, CalendarPropertyValueType.DateTime,
            "ACKNOWLEDGED", "COMPLETED", "CREATED", "DTEND", "DTSTAMP", "DTSTART", "DUE", "EXDATE", "LAST-MODIFIED", "RDATE", "RECURRENCE-ID");
        AddDefaultValueTypes(mappings, CalendarPropertyValueType.Duration, "DURATION", "REFRESH-INTERVAL", "TRIGGER");
        AddDefaultValueTypes(mappings, CalendarPropertyValueType.Integer, "PERCENT-COMPLETE", "PRIORITY", "REPEAT", "SEQUENCE");
        AddDefaultValueTypes(mappings, CalendarPropertyValueType.Float, "GEO");
        AddDefaultValueTypes(mappings, CalendarPropertyValueType.Period, "FREEBUSY");
        AddDefaultValueTypes(mappings, CalendarPropertyValueType.Recur, "EXRULE", "RRULE");
        AddDefaultValueTypes(mappings, CalendarPropertyValueType.Text,
            "ACTION", "CALSCALE", "CATEGORIES", "CLASS", "COLOR", "COMMENT", "CONTACT", "DESCRIPTION", "LOCATION",
            "METHOD", "NAME", "PRODID", "PROXIMITY", "REFID", "RELATED-TO", "REQUEST-STATUS", "RESOURCES",
            "RESOURCE-TYPE", "STATUS", "STRUCTURED-DATA", "SUMMARY", "TRANSP", "TZID", "TZNAME", "UID", "VERSION");
        return mappings.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddDefaultValueTypes(
        IDictionary<string, CalendarPropertyValueType> mappings,
        CalendarPropertyValueType valueType,
        params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
            mappings.Add(propertyName, valueType);
    }

    private CalendarContentDocument(
        byte[] authoritativeUtf8,
        string content,
        IReadOnlyList<CalendarContentProperty> properties,
        IReadOnlyList<CalendarContentComponent> components)
    {
        _authoritativeUtf8 = authoritativeUtf8;
        _content = content;
        Properties = properties;
        Components = components;
    }

    public IReadOnlyList<CalendarContentProperty> Properties { get; }

    public IReadOnlyList<CalendarContentComponent> Components { get; }

    public static CalendarContentDocument Parse(ReadOnlySpan<byte> authoritativeUtf8)
    {
        var bytes = authoritativeUtf8.ToArray();
        var content = StrictUtf8.GetString(bytes);
        var logicalLines = Unfold(ReadPhysicalLines(content));
        var (properties, components) = ParseContent(logicalLines);
        return new CalendarContentDocument(bytes, content, properties, components);
    }

    public byte[] Replay() => _authoritativeUtf8.ToArray();

    internal static bool IsRegisteredPropertyName(string name) => RegisteredPropertyNames.Contains(name);

    internal static bool IsValidRegisteredUnknownValue(CalendarContentProperty property) =>
        property.Name is "TZOFFSETFROM" or "TZOFFSETTO"
        && !property.Parameters.Any(parameter => parameter.Name.Equals("VALUE", StringComparison.OrdinalIgnoreCase))
        && property.RawEncodedValue is not "-0000" and not "-000000"
        && UtcOffsetPattern().IsMatch(property.RawEncodedValue);

    [GeneratedRegex("^[+-](?:[01][0-9]|2[0-3])[0-5][0-9](?:[0-5][0-9]|60)?$", RegexOptions.CultureInvariant)]
    private static partial Regex UtcOffsetPattern();

    public byte[] ReplayForTypedValidation()
    {
        var componentRemovals = Components.Where(component => !IsTypedValidationComponent(component.Path[^1].Name))
            .Select(component => new ContentRange(component.Start, component.Length));
        var propertyRemovals = Properties.Where(property => !IsTypedValidationProperty(property))
            .Select(property => new ContentRange(property.Start, property.Length));
        var removals = componentRemovals.Concat(propertyRemovals)
            .OrderBy(range => range.Start)
            .ThenByDescending(range => range.Length)
            .ToArray();
        var validation = new StringBuilder(_content.Length);
        var position = 0;
        foreach (var removal in removals)
        {
            if (removal.Start < position)
                continue;
            validation.Append(_content, position, removal.Start - position);
            position = removal.Start + removal.Length;
        }
        validation.Append(_content, position, _content.Length - position);
        return StrictUtf8.GetBytes(validation.ToString());
    }

    public byte[] ReplaceSinglePropertyValue(
        IReadOnlyList<CalendarComponentPathSegment> componentPath,
        string propertyName,
        string rawEncodedValue)
    {
        if (rawEncodedValue.Contains('\r') || rawEncodedValue.Contains('\n'))
            throw new ArgumentException("A replacement value cannot contain a physical line break.", nameof(rawEncodedValue));
        var matches = Properties.Where(property => property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
            && PathsEqual(property.ComponentPath, componentPath)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("The addressed property must occur exactly once.");

        var property = matches[0];
        var colon = FindPhysicalUnquoted(property.OriginalSlice, ':');
        var ending = property.OriginalSlice.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n"
            : property.OriginalSlice.EndsWith('\n') ? "\n" : string.Empty;
        var edited = new StringBuilder(_content.Length - property.Length + colon + rawEncodedValue.Length + ending.Length + 1);
        edited.Append(_content, 0, property.Start);
        edited.Append(property.OriginalSlice, 0, colon + 1);
        edited.Append(rawEncodedValue);
        edited.Append(ending);
        edited.Append(_content, property.Start + property.Length, _content.Length - property.Start - property.Length);
        return StrictUtf8.GetBytes(edited.ToString());
    }

    private static (IReadOnlyList<CalendarContentProperty> Properties, IReadOnlyList<CalendarContentComponent> Components)
        ParseContent(IReadOnlyList<LogicalLine> lines)
    {
        var properties = new List<CalendarContentProperty>();
        var components = new List<CalendarContentComponent>();
        var stack = new Stack<ComponentFrame>();
        var rootOccurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var colon = FindUnquoted(line.Unfolded, ':');
            if (colon <= 0)
                throw new FormatException("An iCalendar content line is malformed.");

            var header = line.Unfolded[..colon];
            var value = line.Unfolded[(colon + 1)..];
            if (header.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
            {
                PushComponent(stack, rootOccurrences, value, line.OriginalSlice, line.Start);
                continue;
            }

            if (header.Equals("END", StringComparison.OrdinalIgnoreCase))
            {
                PopComponent(stack, components, value, line.OriginalSlice, line.Start);
                continue;
            }

            if (stack.Count == 0)
                throw new FormatException("An iCalendar property appears outside a component.");

            var headerParts = SplitUnquoted(header, ';');
            var name = GetPropertyName(headerParts[0]);

            var parameters = headerParts.Skip(1).Select(ParseParameter).ToArray();
            properties.Add(new CalendarContentProperty(
                stack.Reverse().Select(frame => new CalendarComponentPathSegment(frame.Name, frame.Occurrence)).ToArray(),
                name,
                parameters,
                GetValueType(name, parameters),
                value,
                line.OriginalSlice,
                line.Start,
                line.OriginalSlice.Length));
        }

        if (stack.Count != 0)
            throw new FormatException("An iCalendar component is not closed.");

        return (properties, components);
    }

    private static void PushComponent(
        Stack<ComponentFrame> stack,
        Dictionary<string, int> rootOccurrences,
        string rawName,
        string beginSlice,
        int beginStart)
    {
        var name = rawName.Trim().ToUpperInvariant();
        if (!IsName(name))
            throw new FormatException("An iCalendar component name is malformed.");

        var occurrences = stack.TryPeek(out var parent) ? parent.ChildOccurrences : rootOccurrences;
        occurrences.TryGetValue(name, out var occurrence);
        occurrences[name] = occurrence + 1;
        var path = stack.Reverse()
            .Select(frame => new CalendarComponentPathSegment(frame.Name, frame.Occurrence))
            .Append(new CalendarComponentPathSegment(name, occurrence))
            .ToArray();
        stack.Push(new ComponentFrame(name, occurrence, path, beginSlice, beginStart));
    }

    private static void PopComponent(
        Stack<ComponentFrame> stack,
        List<CalendarContentComponent> components,
        string rawName,
        string endSlice,
        int endStart)
    {
        if (!stack.TryPop(out var component)
            || !string.Equals(component.Name, rawName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("An iCalendar component boundary is mismatched.");
        }

        components.Add(new CalendarContentComponent(
            component.Path,
            component.BeginSlice,
            endSlice,
            component.BeginStart,
            endStart + endSlice.Length - component.BeginStart));
    }

    private static CalendarParameter ParseParameter(string text)
    {
        var equals = FindUnquoted(text, '=');
        if (equals <= 0)
            throw new FormatException("An iCalendar parameter is malformed.");

        var name = text[..equals];
        if (!IsName(name))
            throw new FormatException("An iCalendar parameter name is malformed.");

        var values = SplitUnquoted(text[(equals + 1)..], ',')
            .Select(DecodeParameterValue)
            .ToArray();
        if (values.Length == 0)
            throw new FormatException("An iCalendar parameter has no value.");
        return new CalendarParameter(name, values);
    }

    private static string DecodeParameterValue(string value)
    {
        var unquoted = value.Length >= 2 && value[0] == '"' && value[^1] == '"'
            ? value[1..^1]
            : value;
        var decoded = new StringBuilder(unquoted.Length);
        for (var index = 0; index < unquoted.Length; index++)
        {
            if (unquoted[index] != '^' || index + 1 >= unquoted.Length)
            {
                decoded.Append(unquoted[index]);
                continue;
            }

            var escaped = unquoted[index + 1];
            if (escaped == '^')
                decoded.Append('^');
            else if (escaped is 'n' or 'N')
                decoded.Append('\n');
            else if (escaped == '\'')
                decoded.Append('"');
            else
            {
                decoded.Append('^');
                continue;
            }

            index++;
        }

        return decoded.ToString();
    }

    private static CalendarPropertyValueType GetValueType(
        string propertyName,
        IReadOnlyList<CalendarParameter> parameters)
    {
        var explicitType = parameters
            .Where(parameter => parameter.Name.Equals("VALUE", StringComparison.OrdinalIgnoreCase))
            .SelectMany(parameter => parameter.Values)
            .LastOrDefault();
        return explicitType is null
            ? GetDefaultValueType(propertyName)
            : ParseValueType(explicitType);
    }

    internal static CalendarPropertyValueType GetDefaultValueType(string propertyName) =>
        DefaultValueTypes.TryGetValue(propertyName, out var valueType) ? valueType : CalendarPropertyValueType.Unknown;

    private static CalendarPropertyValueType ParseValueType(string value) =>
        ExplicitValueTypes.TryGetValue(value, out var valueType) ? valueType : CalendarPropertyValueType.Unknown;

    private static IReadOnlyList<PhysicalLine> ReadPhysicalLines(string content)
    {
        var lines = new List<PhysicalLine>();
        var start = 0;
        for (var index = 0; index < content.Length; index++)
        {
            if (content[index] == '\r')
            {
                if (index + 1 >= content.Length || content[index + 1] != '\n')
                    throw new FormatException("An iCalendar line ending is malformed.");
                lines.Add(new PhysicalLine(content[start..index], content[start..(index + 2)], start));
                start = ++index + 1;
            }
            else if (content[index] == '\n')
            {
                lines.Add(new PhysicalLine(content[start..index], content[start..(index + 1)], start));
                start = index + 1;
            }
        }

        if (start < content.Length)
            lines.Add(new PhysicalLine(content[start..], content[start..], start));
        return lines;
    }

    private static IReadOnlyList<LogicalLine> Unfold(IReadOnlyList<PhysicalLine> physicalLines)
    {
        var logicalLines = new List<LogicalLine>();
        foreach (var line in physicalLines)
        {
            if (line.Content.Length > 0 && line.Content[0] is ' ' or '\t')
            {
                if (logicalLines.Count == 0)
                    throw new FormatException("An iCalendar fold has no preceding line.");
                var previous = logicalLines[^1];
                logicalLines[^1] = previous with
                {
                    Unfolded = previous.Unfolded + line.Content[1..],
                    OriginalSlice = previous.OriginalSlice + line.OriginalSlice
                };
            }
            else if (line.Content.Length > 0)
            {
                logicalLines.Add(new LogicalLine(line.Content, line.OriginalSlice, line.Start));
            }
        }

        return logicalLines;
    }

    private static string[] SplitUnquoted(string text, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '"')
                quoted = !quoted;
            else if (text[index] == separator && !quoted)
            {
                parts.Add(text[start..index]);
                start = index + 1;
            }
        }

        if (quoted)
            throw new FormatException("An iCalendar quoted parameter is not closed.");
        parts.Add(text[start..]);
        return parts.ToArray();
    }

    private static int FindUnquoted(string text, char value)
    {
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '"')
                quoted = !quoted;
            else if (text[index] == value && !quoted)
                return index;
        }

        return -1;
    }

    private static bool IsName(string value) =>
        value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static string GetPropertyName(string value)
    {
        var parts = value.Split('.');
        if (parts.Length is < 1 or > 2 || parts.Any(part => !IsName(part)))
            throw new FormatException("An iCalendar property name is malformed.");
        return parts[^1];
    }

    private static bool IsTypedValidationComponent(string name) => name is
        "VCALENDAR" or "VEVENT" or "VTODO" or "VTIMEZONE" or "STANDARD" or "DAYLIGHT" or "VALARM";

    private static bool IsTypedValidationProperty(CalendarContentProperty property) =>
        IsRegisteredPropertyName(property.Name);

    private static int FindPhysicalUnquoted(string slice, char value)
    {
        var quoted = false;
        for (var index = 0; index < slice.Length; index++)
        {
            var foldLength = GetFoldMarkerLength(slice, index);
            if (foldLength > 0)
            {
                index += foldLength - 1;
                continue;
            }
            if (slice[index] == '"')
                quoted = !quoted;
            else if (slice[index] == value && !quoted)
                return index;
        }

        throw new FormatException("An iCalendar content line is malformed.");
    }

    private static int GetFoldMarkerLength(string slice, int index)
    {
        if (slice[index] == '\r' && index + 2 < slice.Length && slice[index + 1] == '\n')
            return slice[index + 2] is ' ' or '\t' ? 3 : 0;
        if (slice[index] == '\n' && index + 1 < slice.Length)
            return slice[index + 1] is ' ' or '\t' ? 2 : 0;
        return 0;
    }

    private static bool PathsEqual(
        IReadOnlyList<CalendarComponentPathSegment> left,
        IReadOnlyList<CalendarComponentPathSegment> right) => left.Count == right.Count
        && left.Zip(right).All(pair => pair.First == pair.Second);

    private sealed record ComponentFrame(
        string Name,
        int Occurrence,
        IReadOnlyList<CalendarComponentPathSegment> Path,
        string BeginSlice,
        int BeginStart)
    {
        public Dictionary<string, int> ChildOccurrences { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record PhysicalLine(string Content, string OriginalSlice, int Start);

    private sealed record LogicalLine(string Unfolded, string OriginalSlice, int Start);

    private sealed record ContentRange(int Start, int Length);
}
