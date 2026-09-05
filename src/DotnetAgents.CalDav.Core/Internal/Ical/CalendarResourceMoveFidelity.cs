using System.Globalization;
using System.Collections.Frozen;
using System.Text;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal static class CalendarResourceMoveFidelity
{
    private static readonly FrozenDictionary<string, string> DefaultValueTokens = CreateDefaultValueTokens();
    private const int MaximumComponentDepth = 64;
    private static readonly FrozenSet<string> TextListProperties = new[]
    {
        "CATEGORIES", "LOCATION-TYPE", "RESOURCES"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> TokenValuedProperties = new[]
    {
        "ACTION", "CALSCALE", "CLASS", "COLOR", "LOCATION-TYPE", "METHOD", "PARTICIPANT-TYPE",
        "PROXIMITY", "RESOURCE-TYPE", "STATUS", "TRANSP"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private static readonly FrozenSet<string> ExtensibleTokenParameters = new[]
    {
        "CUTYPE", "DISPLAY", "FBTYPE", "FEATURE", "LINKREL", "PARTSTAT", "RELTYPE", "ROLE", "VALUE"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsCompleteMatch(
        CalendarResourceSnapshot source,
        CalendarResourceSnapshot destination) =>
        source.SemanticMutationAvailable
        && destination.SemanticMutationAvailable
        && source.Projection.Kind == destination.Projection.Kind
        && string.Equals(source.Projection.EntityUid, destination.Projection.EntityUid, StringComparison.Ordinal)
        && IsCompleteLosslessSemanticMatch(source.AuthoritativeUtf8.Span, destination.AuthoritativeUtf8.Span);

    private static bool IsCompleteLosslessSemanticMatch(
        ReadOnlySpan<byte> sourceUtf8,
        ReadOnlySpan<byte> destinationUtf8)
    {
        try
        {
            var source = Canonicalize(CalendarContentDocument.Parse(sourceUtf8));
            var destination = Canonicalize(CalendarContentDocument.Parse(destinationUtf8));
            return source.SequenceEqual(destination, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> Canonicalize(CalendarContentDocument document)
    {
        if (document.Components.Any(component => component.Path.Count > MaximumComponentDepth))
            throw new FormatException("An iCalendar component tree exceeds the semantic Move depth limit.");
        var properties = document.Properties.ToLookup(
            property => GetPathKey(property.ComponentPath),
            StringComparer.Ordinal);
        var children = document.Components
            .Where(component => component.Path.Count > 1)
            .ToLookup(
                component => GetPathKey(component.Path.Take(component.Path.Count - 1)),
                StringComparer.Ordinal);
        return document.Components
            .Where(component => component.Path.Count == 1)
            .Select(component => CanonicalizeComponent(properties, children, component))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string CanonicalizeComponent(
        ILookup<string, CalendarContentProperty> properties,
        ILookup<string, CalendarContentComponent> children,
        CalendarContentComponent component)
    {
        var canonical = new StringBuilder();
        AppendPart(canonical, component.Path[^1].Name.ToUpperInvariant());
        var pathKey = GetPathKey(component.Path);
        foreach (var property in properties[pathKey]
                     .Select(CanonicalizeProperty)
                     .Order(StringComparer.Ordinal))
        {
            AppendPart(canonical, property);
        }
        foreach (var child in children[pathKey]
                     .Select(candidate => CanonicalizeComponent(properties, children, candidate))
                     .Order(StringComparer.Ordinal))
        {
            AppendPart(canonical, child);
        }
        return canonical.ToString();
    }

    private static string GetPathKey(IEnumerable<CalendarComponentPathSegment> path)
    {
        var key = new StringBuilder();
        foreach (var segment in path)
        {
            AppendPart(key, segment.Name.ToUpperInvariant());
            AppendPart(key, segment.Occurrence.ToString(CultureInfo.InvariantCulture));
        }
        return key.ToString();
    }

    private static string CanonicalizeProperty(CalendarContentProperty property)
    {
        if (CalendarContentDocument.IsRegisteredPropertyName(property.Name)
            && !CalendarResourceProjector.HasValidRegisteredPropertyGrammar(property))
        {
            throw new FormatException("A registered iCalendar property has invalid semantic grammar.");
        }
        var canonical = new StringBuilder();
        AppendPart(canonical, GetLosslessPropertyIdentity(property));
        AppendPart(canonical, property.ValueType.ToString());
        foreach (var parameter in property.Parameters
                     .Where(parameter => !IsSingleDefaultValueParameter(property, parameter))
                     .Select(parameter => CanonicalizeParameter(property, parameter))
                     .Order(StringComparer.Ordinal))
        {
            AppendPart(canonical, parameter);
        }
        AppendPart(canonical, CanonicalizeValue(property));
        return canonical.ToString();
    }

    private static string GetLosslessPropertyIdentity(CalendarContentProperty property)
    {
        var unfolded = property.OriginalSlice
            .Replace("\r\n ", string.Empty, StringComparison.Ordinal)
            .Replace("\r\n\t", string.Empty, StringComparison.Ordinal)
            .Replace("\n ", string.Empty, StringComparison.Ordinal)
            .Replace("\n\t", string.Empty, StringComparison.Ordinal);
        var delimiter = unfolded.IndexOfAny([';', ':']);
        if (delimiter <= 0)
            throw new FormatException("An iCalendar property header is malformed.");
        return unfolded[..delimiter].ToUpperInvariant();
    }

    private static bool IsSingleDefaultValueParameter(
        CalendarContentProperty property,
        CalendarParameter parameter)
    {
        if (!parameter.Name.Equals("VALUE", StringComparison.OrdinalIgnoreCase)
            || parameter.Values.Count != 1
            || !DefaultValueTokens.TryGetValue(property.Name, out var defaultToken))
        {
            return false;
        }
        var valueParameters = property.Parameters.Where(candidate =>
            candidate.Name.Equals("VALUE", StringComparison.OrdinalIgnoreCase)).ToArray();
        return valueParameters.Length == 1
            && parameter.Values[0].Equals(defaultToken, StringComparison.OrdinalIgnoreCase);
    }

    private static string CanonicalizeParameter(
        CalendarContentProperty property,
        CalendarParameter parameter)
    {
        var canonical = new StringBuilder();
        AppendPart(canonical, parameter.Name.ToUpperInvariant());
        foreach (var value in parameter.Values)
        {
            AppendPart(canonical, CanonicalizeParameterValue(property, parameter, value));
        }
        return canonical.ToString();
    }

    private static string CanonicalizeParameterValue(
        CalendarContentProperty property,
        CalendarParameter parameter,
        string value)
    {
        if (!CalendarResourceProjector.IsKnownParameterApplicable(property, parameter.Name))
            return value;
        var special = CanonicalizeSpecialParameterValue(parameter.Name, value);
        if (special is not null)
            return special;
        if (ExtensibleTokenParameters.Contains(parameter.Name)
            && CalendarResourceProjector.IsToken(value))
        {
            return value.ToUpperInvariant();
        }
        return IsKnownTokenParameterValue(property, parameter, value) ? value.ToUpperInvariant() : value;
    }

    private static string? CanonicalizeSpecialParameterValue(string parameterName, string value)
    {
        if (parameterName.Equals("ORDER", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var order)
            && order >= 1)
        {
            return order.ToString(CultureInfo.InvariantCulture);
        }
        if (parameterName.Equals("GAP", StringComparison.OrdinalIgnoreCase)
            && CalendarDurationArithmetic.TryParse(value, out _))
        {
            return value.ToUpperInvariant();
        }
        if (parameterName.Equals("FMTTYPE", StringComparison.OrdinalIgnoreCase)
            && IsValidFormatType(value))
        {
            return value.ToUpperInvariant();
        }
        return null;
    }

    private static bool IsKnownTokenParameterValue(
        CalendarContentProperty property,
        CalendarParameter parameter,
        string value) => parameter.Name.ToUpperInvariant() switch
        {
            "DERIVED" or "RSVP" => IsOneOf(value, "TRUE", "FALSE"),
            "ENCODING" => IsOneOf(value, "8BIT", "BASE64"),
            "LANGUAGE" => CalendarResourceProjector.HasValidLanguageTag(value),
            "RANGE" when property.Name.Equals("RECURRENCE-ID", StringComparison.OrdinalIgnoreCase) =>
                value.Equals("THISANDFUTURE", StringComparison.OrdinalIgnoreCase),
            "RELATED" when property.Name.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase) =>
                IsOneOf(value, "START", "END"),
            _ => false
        };

    private static bool IsOneOf(string value, params string[] known) =>
        known.Contains(value, StringComparer.OrdinalIgnoreCase);

    private static bool IsValidFormatType(string value)
    {
        var parts = value.Split('/');
        return parts.Length == 2 && parts.All(IsValidFormatTypePart);
    }

    private static bool IsValidFormatTypePart(string value) => value.Length is >= 1 and <= 127
        && char.IsAsciiLetterOrDigit(value[0])
        && char.IsAsciiLetterOrDigit(value[^1])
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '!' or '#' or '$' or '&' or '-' or '^' or '_' or '.' or '+');

    private static string CanonicalizeValue(CalendarContentProperty property)
    {
        if (!CalendarContentDocument.IsRegisteredPropertyName(property.Name))
            return property.RawEncodedValue;
        return property.ValueType switch
        {
        CalendarPropertyValueType.Text when TextListProperties.Contains(property.Name) =>
            CanonicalizeTextList(property),
        CalendarPropertyValueType.Text when property.Name.Equals("REQUEST-STATUS", StringComparison.OrdinalIgnoreCase) =>
            CanonicalizeStructuredText(property.RawEncodedValue, ';', sort: false),
        CalendarPropertyValueType.Text => CanonicalizeScalarText(property),
        CalendarPropertyValueType.Integer when TryCanonicalizeInteger(property.RawEncodedValue, out var integer) => integer,
        CalendarPropertyValueType.Float => CanonicalizeFloat(property.RawEncodedValue),
        CalendarPropertyValueType.DateTime or CalendarPropertyValueType.Date or CalendarPropertyValueType.Duration =>
            property.RawEncodedValue,
        CalendarPropertyValueType.Recur => CanonicalizeRecurrence(property.RawEncodedValue),
            _ => property.RawEncodedValue
        };
    }

    private static string CanonicalizeScalarText(CalendarContentProperty property)
    {
        var decoded = DecodeText(property.RawEncodedValue);
        return IsTokenPropertyValue(property.Name, decoded) ? decoded.ToUpperInvariant() : decoded;
    }

    private static bool IsTokenPropertyValue(string propertyName, string value) =>
        TokenValuedProperties.Contains(propertyName)
        && CalendarResourceProjector.IsToken(value);

    private static string CanonicalizeTextList(CalendarContentProperty property)
    {
        var items = SplitEscaped(property.RawEncodedValue, ',');
        var canonical = new StringBuilder();
        foreach (var item in items
                     .Select(DecodeText)
                     .Select(item => IsTokenPropertyValue(property.Name, item) ? item.ToUpperInvariant() : item)
                     .Order(StringComparer.Ordinal))
        {
            AppendPart(canonical, item);
        }
        return canonical.ToString();
    }

    private static string CanonicalizeStructuredText(string rawValue, char separator, bool sort)
    {
        var items = SplitEscaped(rawValue, separator).Select(DecodeText);
        if (sort)
            items = items.Order(StringComparer.Ordinal);
        var canonical = new StringBuilder();
        foreach (var item in items)
            AppendPart(canonical, item);
        return canonical.ToString();
    }

    private static IReadOnlyList<string> SplitEscaped(string rawValue, char separator)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        var escaped = false;
        foreach (var character in rawValue)
        {
            if (character == separator && !escaped)
            {
                items.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(character);
            escaped = character == '\\' && !escaped;
            if (character != '\\')
                escaped = false;
        }
        items.Add(current.ToString());
        return items;
    }

    private static string CanonicalizeFloat(string rawValue)
    {
        var values = rawValue.Split(';');
        return values.All(value => TryCanonicalizeExactFloat(value, out _))
            ? string.Join(';', values.Select(value =>
                TryCanonicalizeExactFloat(value, out var canonical) ? canonical : value))
            : rawValue;
    }

    private static bool TryCanonicalizeInteger(string value, out string canonical)
    {
        canonical = string.Empty;
        var digits = value;
        var negative = false;
        if (digits.StartsWith('+') || digits.StartsWith('-'))
        {
            negative = digits[0] == '-';
            digits = digits[1..];
        }
        if (digits.Length == 0 || digits.Any(character => !char.IsAsciiDigit(character)))
            return false;
        digits = digits.TrimStart('0');
        if (digits.Length == 0)
        {
            canonical = "0";
            return true;
        }
        canonical = (negative ? "-" : string.Empty) + digits;
        return true;
    }

    private static bool TryCanonicalizeExactFloat(string value, out string canonical)
    {
        canonical = string.Empty;
        if (!TryReadFloatLexeme(value, out var digitsAndPoint, out var sign, out var decimalPoint))
            return false;
        var fractionalDigits = decimalPoint < 0 ? 0 : digitsAndPoint.Length - decimalPoint - 1;
        var digits = digitsAndPoint.Replace(".", string.Empty, StringComparison.Ordinal);
        digits = digits.TrimStart('0');
        if (digits.Length == 0)
        {
            canonical = "0";
            return true;
        }
        var trailingZeros = digits.Length - digits.TrimEnd('0').Length;
        digits = digits.TrimEnd('0');
        var exponent = -fractionalDigits + trailingZeros;
        canonical = sign + digits + 'E' + exponent.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryReadFloatLexeme(
        string value,
        out string digitsAndPoint,
        out string sign,
        out int decimalPoint)
    {
        sign = string.Empty;
        if (value.StartsWith('+') || value.StartsWith('-'))
        {
            sign = value[0] == '-' ? "-" : string.Empty;
            value = value[1..];
        }
        digitsAndPoint = value;
        decimalPoint = value.IndexOf('.');
        return value.Length > 0
            && value.IndexOfAny(['e', 'E']) < 0
            && decimalPoint == value.LastIndexOf('.')
            && decimalPoint != 0
            && decimalPoint != value.Length - 1
            && value.Where(character => character != '.').All(char.IsAsciiDigit);
    }

    private static string CanonicalizeRecurrence(string rawValue) => string.Join(
        ';',
        rawValue.Split(';').Select(CanonicalizeRecurrencePart).Order(StringComparer.Ordinal));

    private static string CanonicalizeRecurrencePart(string part)
    {
        var delimiter = part.IndexOf('=');
        if (delimiter <= 0)
            return part;
        var name = part[..delimiter].ToUpperInvariant();
        var value = part[(delimiter + 1)..];
        var canonicalValue = name switch
        {
            "FREQ" when IsOneOf(
                value,
                "SECONDLY", "MINUTELY", "HOURLY", "DAILY", "WEEKLY", "MONTHLY", "YEARLY") =>
                value.ToUpperInvariant(),
            "WKST" when IsOneOf(value, "MO", "TU", "WE", "TH", "FR", "SA", "SU") => value.ToUpperInvariant(),
            "UNTIL" when IsCalendarDateOrDateTime(value) => value.ToUpperInvariant(),
            "BYDAY" => CanonicalizeByDayList(value),
            "BYSECOND" or "BYMINUTE" or "BYHOUR" or "BYMONTHDAY" or "BYYEARDAY"
                or "BYWEEKNO" or "BYMONTH" or "BYSETPOS" =>
                CanonicalizeIntegerList(value),
            "COUNT" or "INTERVAL" when TryCanonicalizeInteger(value, out var integer) => integer,
            _ => value
        };
        return name + '=' + canonicalValue;
    }

    private static string CanonicalizeByDayList(string value)
    {
        var canonical = new List<string>();
        foreach (var item in value.Split(','))
        {
            if (!TryCanonicalizeByDay(item, out var normalized))
                return value;
            canonical.Add(normalized);
        }
        return string.Join(',', canonical.Order(StringComparer.Ordinal));
    }

    private static bool TryCanonicalizeByDay(string value, out string canonical)
    {
        canonical = string.Empty;
        if (value.Length < 2)
            return false;
        var weekday = value[^2..].ToUpperInvariant();
        if (!IsOneOf(weekday, "MO", "TU", "WE", "TH", "FR", "SA", "SU"))
            return false;
        var ordinal = value[..^2];
        if (ordinal.Length == 0)
        {
            canonical = weekday;
            return true;
        }
        if (!TryCanonicalizeInteger(ordinal, out var normalized)
            || !int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            || number is 0 or < -53 or > 53)
        {
            return false;
        }
        canonical = normalized + weekday;
        return true;
    }

    private static string CanonicalizeIntegerList(string value)
    {
        var canonical = new List<string>();
        foreach (var item in value.Split(','))
        {
            if (!TryCanonicalizeInteger(item, out var normalized))
                return value;
            canonical.Add(normalized);
        }
        return string.Join(',', canonical.Order(StringComparer.Ordinal));
    }

    private static bool IsCalendarDateOrDateTime(string value)
    {
        var normalized = value.ToUpperInvariant();
        if (normalized.Length == 8)
            return DateOnly.TryParseExact(
                normalized,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _);
        if (normalized.Length == 15)
            return DateTime.TryParseExact(
                normalized,
                "yyyyMMdd'T'HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _);
        return normalized.Length == 16
            && DateTimeOffset.TryParseExact(
                normalized,
                "yyyyMMdd'T'HHmmss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _);
    }

    private static string DecodeText(string rawValue)
    {
        var decoded = new StringBuilder(rawValue.Length);
        for (var index = 0; index < rawValue.Length; index++)
        {
            if (rawValue[index] != '\\')
            {
                decoded.Append(rawValue[index]);
                continue;
            }
            if (index + 1 >= rawValue.Length)
                throw new FormatException("An iCalendar TEXT value ends with an incomplete escape.");
            var escaped = rawValue[++index];
            switch (escaped)
            {
                case 'n' or 'N':
                    decoded.Append('\n');
                    break;
                case '\\' or ',' or ';':
                    decoded.Append(escaped);
                    break;
                default:
                    throw new FormatException("An iCalendar TEXT value contains an invalid escape.");
            }
        }
        return decoded.ToString();
    }

    private static FrozenDictionary<string, string> CreateDefaultValueTokens()
    {
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Add(tokens, "URI", "ATTACH", "CONCEPT", "TZURL", "URL");
        Add(tokens, "CAL-ADDRESS", "ATTENDEE", "CALENDAR-ADDRESS", "ORGANIZER");
        Add(tokens, "DATE-TIME", "ACKNOWLEDGED", "COMPLETED", "CREATED", "DTEND", "DTSTAMP",
            "DTSTART", "DUE", "EXDATE", "LAST-MODIFIED", "RDATE", "RECURRENCE-ID");
        Add(tokens, "DURATION", "DURATION", "TRIGGER");
        Add(tokens, "INTEGER", "PERCENT-COMPLETE", "PRIORITY", "REPEAT", "SEQUENCE");
        Add(tokens, "FLOAT", "GEO");
        Add(tokens, "PERIOD", "FREEBUSY");
        Add(tokens, "RECUR", "EXRULE", "RRULE");
        Add(tokens, "UID", "RELATED-TO");
        Add(tokens, "TEXT", "ACTION", "CALSCALE", "CATEGORIES", "CLASS", "COLOR", "COMMENT",
            "CONTACT", "DESCRIPTION", "LOCATION", "LOCATION-TYPE", "METHOD", "NAME", "PARTICIPANT-TYPE",
            "PRODID", "PROXIMITY", "REFID", "REQUEST-STATUS", "RESOURCES", "RESOURCE-TYPE",
            "STATUS", "SUMMARY", "TRANSP", "TZID", "TZNAME", "UID", "VERSION");
        return tokens.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static void Add(IDictionary<string, string> destination, string token, params string[] properties)
    {
        foreach (var property in properties)
            destination.Add(property, token);
    }

    private static void AppendPart(StringBuilder destination, string value) => destination
        .Append(value.Length.ToString(CultureInfo.InvariantCulture))
        .Append(':')
        .Append(value);
}
