using System.Text;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>Bounds embedded timezone text before constructing any iCalendar component paths.</summary>
internal static class CalendarMetadataTimeZoneReader
{
    private const int MaximumCharacters = 256 * 1024;
    private const int MaximumLines = 4096;
    private const int MaximumLineCharacters = 16 * 1024;

    internal static IReadOnlyList<string> Read(string value, CancellationToken cancellationToken)
    {
        if (value.Length > MaximumCharacters)
            throw Limit();
        var unfolded = Unfold(value, cancellationToken);
        ValidateStructureBudget(unfolded, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var document = CalendarContentDocument.Parse(Encoding.UTF8.GetBytes(unfolded));
        ValidateComponents(document);
        var identifiers = document.Properties.Where(property => property.Name.Equals("TZID", StringComparison.OrdinalIgnoreCase)
            && property.ComponentPath.Count == 2 && property.ComponentPath[1].Name == "VTIMEZONE").Take(2).ToArray();
        if (identifiers.Length != 1)
            throw Invalid();
        var identifier = ReadIdentifier(identifiers[0]);
        cancellationToken.ThrowIfCancellationRequested();
        return [identifier];
    }

    private static string ReadIdentifier(CalendarContentProperty property)
    {
        var valueParameters = property.Parameters.Where(parameter => parameter.Name.Equals("VALUE", StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (property.ValueType != CalendarPropertyValueType.Text || valueParameters.Length > 1
            || valueParameters.Any(parameter => parameter.Values.Count != 1))
            throw Invalid();
        ValidateIdentifierText(property.RawEncodedValue);
        var identifier = CalendarContentDocument.DecodeText(property.RawEncodedValue);
        return string.IsNullOrWhiteSpace(identifier) ? throw Invalid() : identifier;
    }

    private static void ValidateIdentifierText(string value)
    {
        // Validate RFC 5545 TEXT before decoding: the shared decoder also serves
        // lossless projection paths and does not reject unknown escape sequences.
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\')
            {
                if (++index >= value.Length || value[index] is not ('\\' or ';' or ',' or 'N' or 'n'))
                    throw Invalid();
            }
            else if (value[index] is ';' or ',' or '\u007f' || value[index] < ' ' && value[index] != '\t')
                throw Invalid();
        }
    }

    private static string Unfold(string value, CancellationToken cancellationToken)
    {
        var output = new char[value.Length];
        var written = 0;
        for (var index = 0; index < value.Length; index++)
        {
            if ((index & 4095) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            var fold = FoldLength(value, index);
            if (fold > 0)
                index += fold - 1;
            else
                output[written++] = value[index];
        }
        return new string(output, 0, written);
    }

    private static int FoldLength(string value, int index)
    {
        if (value[index] == '\r' && index + 2 < value.Length && value[index + 1] == '\n')
            return value[index + 2] is ' ' or '\t' ? 3 : 0;
        return value[index] == '\n' && index + 1 < value.Length && value[index + 1] is ' ' or '\t' ? 2 : 0;
    }

    private static void ValidateStructureBudget(string value, CancellationToken cancellationToken)
    {
        var depth = 0;
        var lines = 0;
        foreach (var line in value.AsSpan().EnumerateLines())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++lines > MaximumLines || line.Length > MaximumLineCharacters)
                throw Limit();
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase) && ++depth > 3)
                throw Limit();
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase) && --depth < 0)
                throw Invalid();
        }
    }

    private static void ValidateComponents(CalendarContentDocument document)
    {
        var roots = document.Components.Where(component => component.Path.Count == 1).ToArray();
        var zones = document.Components.Where(component => component.Path.Count == 2).ToArray();
        if (roots.Length != 1 || roots[0].Path[0].Name != "VCALENDAR"
            || zones.Length != 1 || zones[0].Path[1].Name != "VTIMEZONE")
            throw Invalid();
        if (document.Components.Any(component => component.Path.Count == 3
                && component.Path[2].Name is not ("STANDARD" or "DAYLIGHT")))
            throw Invalid();
    }

    private static CalendarProtocolException Limit() => new("limit_exhausted",
        "Calendar timezone metadata exceeded its character, line, or component-depth limit.");

    private static CalendarProtocolException Invalid() => new("upstream_protocol_error",
        "Calendar timezone metadata did not contain one bounded VTIMEZONE definition.");
}
