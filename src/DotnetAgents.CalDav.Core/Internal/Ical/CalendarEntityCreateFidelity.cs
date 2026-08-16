using System.Globalization;
using System.Text;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal static class CalendarEntityCreateFidelity
{
    private static readonly HashSet<string> DerivedEntityProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "CREATED", "DTSTAMP", "LAST-MODIFIED", "SEQUENCE"
    };
    private static readonly HashSet<string> TokenProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "ACTION", "CLASS", "PARTICIPANT-TYPE", "STATUS", "TRANSP"
    };
    private static readonly HashSet<string> TokenParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "CUTYPE", "ENCODING", "PARTSTAT", "RELTYPE", "ROLE", "RSVP", "VALUE"
    };

    public static bool IsEquivalent(ReadOnlySpan<byte> submittedUtf8, ReadOnlySpan<byte> observedUtf8)
    {
        try
        {
            var submitted = Canonicalize(CalendarContentDocument.Parse(submittedUtf8));
            var observed = Canonicalize(CalendarContentDocument.Parse(observedUtf8));
            return submitted.SequenceEqual(observed, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> Canonicalize(CalendarContentDocument document)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var canonical = new List<string>();
        foreach (var property in document.Properties.Where(IsRequestedSemanticProperty))
        {
            var key = $"{string.Join('/', property.ComponentPath.Select(PathPart))}|{property.Name}";
            var occurrence = occurrences.GetValueOrDefault(key);
            occurrences[key] = occurrence + 1;
            canonical.Add(CanonicalizeProperty(property, occurrence));
        }
        canonical.Sort(StringComparer.Ordinal);
        return canonical;
    }

    private static string PathPart(CalendarComponentPathSegment segment) => $"{segment.Name}#{segment.Occurrence}";

    private static bool IsRequestedSemanticProperty(CalendarContentProperty property)
    {
        if (property.ComponentPath.Count == 1)
            return false;
        return property.ComponentPath.Count != 2
            || property.ComponentPath[1].Name is not ("VEVENT" or "VTODO")
            || !DerivedEntityProperties.Contains(property.Name);
    }

    private static string CanonicalizeProperty(CalendarContentProperty property, int occurrence)
    {
        var canonical = new StringBuilder();
        foreach (var segment in property.ComponentPath)
            AppendPart(canonical, $"{segment.Name.ToUpperInvariant()}#{segment.Occurrence}");
        AppendPart(canonical, property.Name.ToUpperInvariant());
        AppendPart(canonical, occurrence.ToString(CultureInfo.InvariantCulture));
        AppendPart(canonical, property.ValueType.ToString());
        foreach (var parameter in property.Parameters
                     .Where(parameter => !IsDefaultValueParameter(property, parameter))
                     .Select(parameter => CanonicalizeParameter(property, parameter))
                     .Order(StringComparer.Ordinal))
        {
            AppendPart(canonical, parameter);
        }
        AppendPart(canonical, CanonicalizeValue(property));
        return canonical.ToString();
    }

    private static string CanonicalizeParameter(
        CalendarContentProperty property,
        CalendarParameter parameter)
    {
        var canonical = new StringBuilder(parameter.Name.Length + 16);
        AppendPart(canonical, parameter.Name.ToUpperInvariant());
        foreach (var value in parameter.Values)
        {
            AppendPart(canonical, IsTokenParameter(property, parameter)
                ? value.ToUpperInvariant()
                : value);
        }
        return canonical.ToString();
    }

    private static bool IsTokenParameter(
        CalendarContentProperty property,
        CalendarParameter parameter) => TokenParameters.Contains(parameter.Name)
        || property.Name.Equals("TRIGGER", StringComparison.OrdinalIgnoreCase)
            && parameter.Name.Equals("RELATED", StringComparison.OrdinalIgnoreCase);

    private static bool IsDefaultValueParameter(CalendarContentProperty property, CalendarParameter parameter) =>
        parameter.Name.Equals("VALUE", StringComparison.OrdinalIgnoreCase)
        && parameter.Values.Count == 1
        && property.ValueType == CalendarContentDocument.GetDefaultValueType(property.Name);

    private static string CanonicalizeValue(CalendarContentProperty property) => property.ValueType switch
    {
        CalendarPropertyValueType.Text when TokenProperties.Contains(property.Name) =>
            DecodeText(property.RawEncodedValue).ToUpperInvariant(),
        CalendarPropertyValueType.Text => DecodeText(property.RawEncodedValue),
        CalendarPropertyValueType.Integer when long.TryParse(
            property.RawEncodedValue,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var integer) => integer.ToString(CultureInfo.InvariantCulture),
        CalendarPropertyValueType.Float => CanonicalizeFloat(property.RawEncodedValue),
        CalendarPropertyValueType.DateTime or CalendarPropertyValueType.Date or CalendarPropertyValueType.Duration =>
            property.RawEncodedValue.ToUpperInvariant(),
        _ => property.RawEncodedValue
    };

    private static string CanonicalizeFloat(string rawValue)
    {
        var values = rawValue.Split(';');
        if (values.All(value => double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out _)))
        {
            return string.Join(';', values.Select(value => double.Parse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)));
        }
        return rawValue;
    }

    private static string DecodeText(string rawValue)
    {
        var decoded = new StringBuilder(rawValue.Length);
        for (var index = 0; index < rawValue.Length; index++)
        {
            if (rawValue[index] != '\\' || index + 1 >= rawValue.Length)
            {
                decoded.Append(rawValue[index]);
                continue;
            }
            decoded.Append(rawValue[++index] switch
            {
                'n' or 'N' => '\n',
                '\\' => '\\',
                ',' => ',',
                ';' => ';',
                var escaped => escaped
            });
        }
        return decoded.ToString();
    }

    private static void AppendPart(StringBuilder destination, string value) =>
        destination.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
}
