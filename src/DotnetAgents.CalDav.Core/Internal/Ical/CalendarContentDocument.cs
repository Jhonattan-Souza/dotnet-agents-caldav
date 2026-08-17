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

internal sealed record CalendarContentOccurrence(int Start, int Length, string OriginalSlice);

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
            ["BINARY"] = CalendarPropertyValueType.Binary,
            ["UID"] = CalendarPropertyValueType.Uid,
            ["XML-REFERENCE"] = CalendarPropertyValueType.XmlReference
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> NoDefaultPropertyNames = new[]
    {
        "CONFERENCE", "IMAGE", "LINK", "REFRESH-INTERVAL", "SOURCE", "STRUCTURED-DATA", "STYLED-DESCRIPTION"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> RegisteredPropertyNames = DefaultValueTypes.Keys
        .Concat(NoDefaultPropertyNames)
        .Concat(["TZOFFSETFROM", "TZOFFSETTO"])
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> ProjectionExtensionsUnsupportedByIcalNet = new[]
    {
        "CALENDAR-ADDRESS", "CONCEPT", "CONFERENCE", "IMAGE", "LINK", "PARTICIPANT-TYPE",
        "STRUCTURED-DATA", "STYLED-DESCRIPTION"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly byte[] _authoritativeUtf8;
    private readonly string _content;
    private readonly int[] _rawOffsets;

    private static FrozenDictionary<string, CalendarPropertyValueType> CreateDefaultValueTypes()
    {
        var mappings = new Dictionary<string, CalendarPropertyValueType>(StringComparer.OrdinalIgnoreCase);
        AddDefaultValueTypes(mappings, CalendarPropertyValueType.Uri,
            "ATTACH", "ATTENDEE", "CALENDAR-ADDRESS", "CONCEPT", "ORGANIZER", "TZURL", "URL");
        AddDefaultValueTypes(mappings, CalendarPropertyValueType.DateTime,
            "ACKNOWLEDGED", "COMPLETED", "CREATED", "DTEND", "DTSTAMP", "DTSTART", "DUE", "EXDATE", "LAST-MODIFIED", "RDATE", "RECURRENCE-ID");
        AddDefaultValueTypes(mappings, CalendarPropertyValueType.Duration, "DURATION", "TRIGGER");
        AddDefaultValueTypes(mappings, CalendarPropertyValueType.Integer, "PERCENT-COMPLETE", "PRIORITY", "REPEAT", "SEQUENCE");
        AddDefaultValueTypes(mappings, CalendarPropertyValueType.Float, "GEO");
        AddDefaultValueTypes(mappings, CalendarPropertyValueType.Period, "FREEBUSY");
        AddDefaultValueTypes(mappings, CalendarPropertyValueType.Recur, "EXRULE", "RRULE");
        AddDefaultValueTypes(mappings, CalendarPropertyValueType.Text,
            "ACTION", "CALSCALE", "CATEGORIES", "CLASS", "COLOR", "COMMENT", "CONTACT", "DESCRIPTION", "LOCATION",
            "LOCATION-TYPE", "METHOD", "NAME", "PARTICIPANT-TYPE", "PRODID", "PROXIMITY", "REFID", "REQUEST-STATUS",
            "RESOURCES", "RESOURCE-TYPE", "STATUS", "SUMMARY", "TRANSP",
            "TZID", "TZNAME", "UID", "VERSION");
        AddDefaultValueTypes(mappings, CalendarPropertyValueType.Uid, "RELATED-TO");
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
        int[] rawOffsets,
        IReadOnlyList<CalendarContentProperty> properties,
        IReadOnlyList<CalendarContentComponent> components)
    {
        _authoritativeUtf8 = authoritativeUtf8;
        _content = content;
        _rawOffsets = rawOffsets;
        Properties = properties;
        Components = components;
    }

    public IReadOnlyList<CalendarContentProperty> Properties { get; }

    public IReadOnlyList<CalendarContentComponent> Components { get; }

    public static CalendarContentDocument Parse(ReadOnlySpan<byte> authoritativeUtf8)
    {
        var bytes = authoritativeUtf8.ToArray();
        var decoded = DecodeContent(bytes);
        var logicalLines = Unfold(ReadPhysicalLines(decoded.Content));
        var (properties, components) = ParseContent(logicalLines);
        return new CalendarContentDocument(bytes, decoded.Content, decoded.RawOffsets, properties, components);
    }

    private static DecodedContent DecodeContent(byte[] authoritativeUtf8)
    {
        try
        {
            var content = StrictUtf8.GetString(authoritativeUtf8);
            return new DecodedContent(
                content,
                MapCharacterOffsets(content, authoritativeUtf8, Enumerable.Range(0, authoritativeUtf8.Length + 1).ToArray()));
        }
        catch (DecoderFallbackException)
        {
            var unfolded = UnfoldRawBytes(authoritativeUtf8);
            var content = StrictUtf8.GetString(unfolded.Bytes);
            return new DecodedContent(content, MapCharacterOffsets(content, unfolded.Bytes, unfolded.RawOffsets));
        }
    }

    private static RawUnfoldedContent UnfoldRawBytes(ReadOnlySpan<byte> authoritativeUtf8)
    {
        var unfolded = new byte[authoritativeUtf8.Length];
        var rawOffsets = new int[authoritativeUtf8.Length + 1];
        var written = 0;
        for (var index = 0; index < authoritativeUtf8.Length; index++)
        {
            if (authoritativeUtf8[index] == (byte)'\r'
                && index + 2 < authoritativeUtf8.Length
                && authoritativeUtf8[index + 1] == (byte)'\n'
                && authoritativeUtf8[index + 2] is (byte)' ' or (byte)'\t')
            {
                index += 2;
                continue;
            }
            if (authoritativeUtf8[index] == (byte)'\n'
                && index + 1 < authoritativeUtf8.Length
                && authoritativeUtf8[index + 1] is (byte)' ' or (byte)'\t')
            {
                index++;
                continue;
            }
            rawOffsets[written] = index;
            unfolded[written++] = authoritativeUtf8[index];
            rawOffsets[written] = index + 1;
        }
        return new RawUnfoldedContent(unfolded[..written], rawOffsets[..(written + 1)]);
    }

    private static int[] MapCharacterOffsets(
        string content,
        ReadOnlySpan<byte> decodedUtf8,
        IReadOnlyList<int> rawByteOffsets)
    {
        var rawOffsets = new int[content.Length + 1];
        var characterIndex = 0;
        var byteIndex = 0;
        foreach (var rune in content.EnumerateRunes())
        {
            rawOffsets[characterIndex] = rawByteOffsets[byteIndex];
            if (rune.Utf16SequenceLength == 2)
                rawOffsets[characterIndex + 1] = rawByteOffsets[byteIndex];
            characterIndex += rune.Utf16SequenceLength;
            byteIndex += rune.Utf8SequenceLength;
            rawOffsets[characterIndex] = rawByteOffsets[byteIndex];
        }
        if (byteIndex != decodedUtf8.Length)
            throw new DecoderFallbackException("The UTF-8 character map is incomplete.");
        return rawOffsets;
    }

    public byte[] Replay() => _authoritativeUtf8.ToArray();

    internal static bool IsRegisteredPropertyName(string name) => RegisteredPropertyNames.Contains(name);

    internal static bool HasNoDefaultValueType(string name) => NoDefaultPropertyNames.Contains(name);

    internal static bool IsProjectionExtensionUnsupportedByIcalNet(string name) =>
        ProjectionExtensionsUnsupportedByIcalNet.Contains(name);

    internal static bool IsValidRegisteredUnknownValue(CalendarContentProperty property) =>
        (property.Name.Equals("TZOFFSETFROM", StringComparison.OrdinalIgnoreCase)
            || property.Name.Equals("TZOFFSETTO", StringComparison.OrdinalIgnoreCase))
        && !property.Parameters.Any(parameter => parameter.Name.Equals("VALUE", StringComparison.OrdinalIgnoreCase))
        && property.RawEncodedValue is not "-0000" and not "-000000"
        && UtcOffsetPattern().IsMatch(property.RawEncodedValue);

    [GeneratedRegex("^[+-](?:[01][0-9]|2[0-3])[0-5][0-9](?:[0-5][0-9]|60)?$", RegexOptions.CultureInvariant)]
    private static partial Regex UtcOffsetPattern();

    public byte[] ReplayForTypedValidation() => ReplayForTypedValidation(static _ => false);

    public byte[] ReplayForProjectionValidation() => ReplayForTypedValidation(property =>
        IsEntityPeriodRecurrenceDate(property)
        || ProjectionExtensionsUnsupportedByIcalNet.Contains(property.Name)
        || IsUnsupportedAttendeeAddressForIcalNet(property));

    public byte[] ReplayForOccurrenceEvaluation() => ReplayForTypedValidation(IsEntityPeriodRecurrenceDate);

    private static bool IsEntityPeriodRecurrenceDate(CalendarContentProperty property) =>
        property.Name.Equals("RDATE", StringComparison.OrdinalIgnoreCase)
        && property.ValueType == CalendarPropertyValueType.Period
        && property.ComponentPath.Count == 2
        && property.ComponentPath[1].Name is "VEVENT" or "VTODO";

    private static bool IsUnsupportedAttendeeAddressForIcalNet(CalendarContentProperty property) =>
        property.Name.Equals("ATTENDEE", StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate(property.RawEncodedValue, UriKind.Absolute, out var address)
        && !address.Scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase);

    private byte[] ReplayForTypedValidation(Func<CalendarContentProperty, bool> removeAdditionalProperty)
    {
        var componentRemovals = Components.Where(component => !IsTypedValidationComponent(component.Path[^1].Name))
            .Select(component => new ContentRange(component.Start, component.Length));
        var propertyRemovals = Properties.Where(property => !IsTypedValidationProperty(property)
                || removeAdditionalProperty(property))
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
        var valueStart = property.Start + colon + 1;
        var valueLength = property.Length - colon - 1 - ending.Length;
        return ReplaceRange(valueStart, valueLength, rawEncodedValue);
    }

    public byte[] SetOrClearSingleProperty(
        IReadOnlyList<CalendarComponentPathSegment> componentPath,
        string propertyName,
        string? rawEncodedValue)
    {
        ValidateReplacement(rawEncodedValue);
        var matches = Properties.Where(property => property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
            && PathsEqual(property.ComponentPath, componentPath)).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException("The addressed singleton property is ambiguous.");

        if (matches.Length == 1)
            return rawEncodedValue is null
                ? ReplaceRange(matches[0].Start, matches[0].Length, string.Empty)
                : ReplaceSinglePropertyValue(componentPath, propertyName, rawEncodedValue);
        if (rawEncodedValue is null)
            return Replay();

        var component = Components.Single(candidate => PathsEqual(candidate.Path, componentPath));
        var insertion = component.Start + component.Length - component.EndSlice.Length;
        var lineEnding = component.EndSlice.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return ReplaceRange(insertion, 0, propertyName + ":" + rawEncodedValue + lineEnding);
    }

    public byte[] EditProperties(
        IReadOnlyList<CalendarComponentPathSegment> componentPath,
        IReadOnlyDictionary<CalendarContentProperty, string?> replacements,
        IReadOnlyList<string> appendedSlices)
    {
        var component = Components.Single(candidate => PathsEqual(candidate.Path, componentPath));
        var edits = replacements.Select(pair => new ContentEdit(pair.Key.Start, pair.Key.Length, pair.Value ?? string.Empty))
            .Append(new ContentEdit(
                component.Start + component.Length - component.EndSlice.Length,
                0,
                string.Concat(appendedSlices)))
            .ToArray();
        return ApplyEdits(edits);
    }

    public byte[] SetOrClearSinglePropertySlice(
        IReadOnlyList<CalendarComponentPathSegment> componentPath,
        string propertyName,
        string? propertySlice)
    {
        var matches = Properties.Where(property => property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
            && PathsEqual(property.ComponentPath, componentPath)).ToArray();
        if (matches.Length > 1)
            throw new InvalidOperationException("The addressed singleton property is ambiguous.");
        var replacements = matches.Length == 0
            ? new Dictionary<CalendarContentProperty, string?>()
            : new Dictionary<CalendarContentProperty, string?> { [matches[0]] = propertySlice };
        var additions = matches.Length == 0 && propertySlice is not null ? new[] { propertySlice } : [];
        return EditProperties(componentPath, replacements, additions);
    }

    public byte[] SetSinglePropertySlicePreservingParameters(
        IReadOnlyList<CalendarComponentPathSegment> componentPath,
        string propertyName,
        string propertySlice,
        IReadOnlySet<string> replacedParameterNames)
    {
        var existing = Properties.Single(property => property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase)
            && PathsEqual(property.ComponentPath, componentPath));
        var desired = ParsePropertySlice(propertySlice);
        if (!desired.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The replacement property name does not match.", nameof(propertySlice));
        var parameters = MergeParameters(existing.Parameters, desired.Parameters, replacedParameterNames);
        var ending = existing.OriginalSlice.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n"
            : existing.OriginalSlice.EndsWith('\n') ? "\n" : string.Empty;
        var replacement = existing.Name
            + string.Concat(parameters.Select(CalendarPatchValueSerializer.Parameter))
            + ":" + desired.RawEncodedValue + ending;
        return EditProperties(
            componentPath,
            new Dictionary<CalendarContentProperty, string?> { [existing] = replacement },
            []);
    }

    public static string RawValueFromPropertySlice(string propertySlice) => ParsePropertySlice(propertySlice).RawEncodedValue;

    private static IReadOnlyList<CalendarParameter> MergeParameters(
        IReadOnlyList<CalendarParameter> existing,
        IReadOnlyList<CalendarParameter> desired,
        IReadOnlySet<string> replacedNames)
    {
        var replacements = desired.Where(parameter => replacedNames.Contains(parameter.Name)).ToArray();
        var merged = new List<CalendarParameter>();
        var inserted = false;
        foreach (var parameter in existing)
        {
            if (!replacedNames.Contains(parameter.Name))
            {
                merged.Add(parameter);
                continue;
            }
            if (inserted)
                continue;
            merged.AddRange(replacements);
            inserted = true;
        }
        if (!inserted)
            merged.AddRange(replacements);
        return merged;
    }

    private static CalendarContentProperty ParsePropertySlice(string propertySlice)
    {
        var logicalLines = Unfold(ReadPhysicalLines(propertySlice));
        if (logicalLines.Count != 1)
            throw new ArgumentException("A replacement property must contain one content line.", nameof(propertySlice));
        var line = logicalLines[0];
        var colon = FindUnquoted(line.Unfolded, ':');
        if (colon <= 0)
            throw new ArgumentException("A replacement property is malformed.", nameof(propertySlice));
        var headerParts = SplitUnquoted(line.Unfolded[..colon], ';');
        var name = GetPropertyName(headerParts[0]);
        var parameters = headerParts.Skip(1).Select(ParseParameter).ToArray();
        return new CalendarContentProperty(
            [],
            name,
            parameters,
            GetValueType(name, parameters),
            line.Unfolded[(colon + 1)..],
            line.OriginalSlice,
            0,
            line.OriginalSlice.Length);
    }

    private static void ValidateReplacement(string? rawEncodedValue)
    {
        if (rawEncodedValue?.IndexOfAny(['\r', '\n']) >= 0)
            throw new ArgumentException("A replacement value cannot contain a physical line break.", nameof(rawEncodedValue));
    }

    public CalendarContentComponent GetMasterComponent(CalendarEntityKind kind)
    {
        var name = kind == CalendarEntityKind.Event ? "VEVENT" : "VTODO";
        return Components.Single(component => component.Path.Count == 2
            && component.Path[^1].Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            && !Properties.Any(property => PathsEqual(property.ComponentPath, component.Path)
                && property.Name.Equals("RECURRENCE-ID", StringComparison.OrdinalIgnoreCase)));
    }

    public CalendarContentComponent GetComponent(IReadOnlyList<CalendarComponentPathSegment> path) =>
        Components.Single(component => PathsEqual(component.Path, path));

    public CalendarContentOccurrence GetComponentOccurrence(IReadOnlyList<CalendarComponentPathSegment> path)
    {
        var component = GetComponent(path);
        return new CalendarContentOccurrence(
            component.Start,
            component.Length,
            _content.Substring(component.Start, component.Length));
    }

    public IReadOnlyList<CalendarContentOccurrence> GetDirectPropertyOccurrences(
        IReadOnlyList<CalendarComponentPathSegment> componentPath,
        string propertyName) => Properties.Where(property =>
            PathsEqual(property.ComponentPath, componentPath)
            && property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
        .Select(property => new CalendarContentOccurrence(property.Start, property.Length, property.OriginalSlice))
        .ToArray();

    public IReadOnlyList<CalendarContentOccurrence> GetDirectComponentOccurrences(
        IReadOnlyList<CalendarComponentPathSegment> componentPath,
        string componentName) => Components.Where(component =>
            component.Path.Count == componentPath.Count + 1
            && component.Path.Take(componentPath.Count).SequenceEqual(componentPath)
            && component.Path[^1].Name.Equals(componentName, StringComparison.OrdinalIgnoreCase))
        .Select(component => new CalendarContentOccurrence(
            component.Start,
            component.Length,
            _content.Substring(component.Start, component.Length)))
        .ToArray();

    public byte[] EditOccurrences(
        IReadOnlyList<CalendarComponentPathSegment> componentPath,
        IReadOnlyList<CalendarContentOccurrence> removals,
        IReadOnlyList<string> appendedSlices)
    {
        var component = Components.Single(candidate => PathsEqual(candidate.Path, componentPath));
        var edits = removals.Select(removal => new ContentEdit(removal.Start, removal.Length, string.Empty))
            .Append(new ContentEdit(
                component.Start + component.Length - component.EndSlice.Length,
                0,
                string.Concat(appendedSlices)));
        return ApplyEdits(edits);
    }

    public static string EncodeText(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\r\n", "\\n", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace(",", "\\,", StringComparison.Ordinal)
        .Replace(";", "\\;", StringComparison.Ordinal);

    public static string DecodeText(string value)
    {
        var decoded = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                decoded.Append(value[index]);
                continue;
            }
            decoded.Append(value[++index] switch
            {
                'n' or 'N' => '\n',
                var escaped => escaped
            });
        }
        return decoded.ToString();
    }

    private byte[] ReplaceRange(int start, int length, string replacement)
        => ApplyEdits([new ContentEdit(start, length, replacement)]);

    private byte[] ApplyEdits(IEnumerable<ContentEdit> contentEdits)
    {
        var edits = contentEdits
            .OrderBy(edit => edit.Start)
            .ThenByDescending(edit => edit.Length)
            .ToArray();
        using var edited = new MemoryStream(_authoritativeUtf8.Length);
        var rawPosition = 0;
        foreach (var edit in edits)
        {
            var rawStart = _rawOffsets[edit.Start];
            var rawEnd = _rawOffsets[edit.Start + edit.Length];
            if (rawStart < rawPosition)
                throw new InvalidOperationException("Calendar content edits cannot overlap.");
            edited.Write(_authoritativeUtf8.AsSpan(rawPosition, rawStart - rawPosition));
            edited.Write(StrictUtf8.GetBytes(edit.Replacement));
            rawPosition = rawEnd;
        }
        edited.Write(_authoritativeUtf8.AsSpan(rawPosition));
        return edited.ToArray();
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

    private sealed record ContentEdit(int Start, int Length, string Replacement);

    private sealed record DecodedContent(string Content, int[] RawOffsets);

    private sealed record RawUnfoldedContent(byte[] Bytes, int[] RawOffsets);
}
