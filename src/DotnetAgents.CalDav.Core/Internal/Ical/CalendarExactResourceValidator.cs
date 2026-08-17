using System.Text;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal sealed record CalendarExactResourceIdentity(string EntityUid, CalendarEntityKind EntityKind);

internal static class CalendarExactResourceValidator
{
    private const int MaximumComponentDepth = 64;

    public static bool TryValidate(
        ReadOnlySpan<byte> authoritativeUtf8,
        out CalendarExactResourceIdentity identity)
    {
        identity = default!;
        try
        {
            if (!HasExactWireFormat(authoritativeUtf8)
                || !HasExactContentLineSyntax(authoritativeUtf8))
                return false;
            var document = CalendarContentDocument.Parse(authoritativeUtf8);
            return TryValidateDocument(document, out identity);
        }
        catch (Exception exception) when (exception is FormatException
            or DecoderFallbackException
            or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasExactWireFormat(ReadOnlySpan<byte> authoritativeUtf8)
    {
        return authoritativeUtf8.Length >= 2
            && authoritativeUtf8[^2] == (byte)'\r'
            && authoritativeUtf8[^1] == (byte)'\n'
            && HasOnlyCrlfLineEndings(authoritativeUtf8);
    }

    private static bool HasOnlyCrlfLineEndings(ReadOnlySpan<byte> authoritativeUtf8)
    {
        for (var index = 0; index < authoritativeUtf8.Length; index++)
        {
            if (authoritativeUtf8[index] == (byte)'\r'
                && authoritativeUtf8[index + 1] != (byte)'\n')
                return false;
            if (authoritativeUtf8[index] == (byte)'\n'
                && (index == 0 || authoritativeUtf8[index - 1] != (byte)'\r'))
                return false;
        }
        return true;
    }

    private static bool HasExactContentLineSyntax(ReadOnlySpan<byte> authoritativeUtf8)
    {
        var physicalLines = Encoding.Latin1.GetString(authoritativeUtf8)
            .Split("\r\n", StringSplitOptions.None);
        var logicalLine = new StringBuilder();
        var hasLogicalLine = false;
        var componentDepth = 0;
        foreach (var physicalLine in physicalLines[..^1])
        {
            if (physicalLine.Length == 0)
                return false;
            if (physicalLine[0] is ' ' or '\t')
            {
                if (!hasLogicalLine)
                    return false;
                logicalLine.Append(physicalLine.AsSpan(1));
                continue;
            }
            if (hasLogicalLine && !HasValidLogicalLine(logicalLine.ToString(), ref componentDepth))
                return false;
            logicalLine.Clear();
            logicalLine.Append(physicalLine);
            hasLogicalLine = true;
        }
        return hasLogicalLine
            && HasValidLogicalLine(logicalLine.ToString(), ref componentDepth)
            && componentDepth == 0;
    }

    private static bool HasValidLogicalLine(string logicalLine, ref int componentDepth)
    {
        if (!HasValidRawContentLine(logicalLine)
            || !TryFindUnquotedSeparator(logicalLine, ':', out var colon))
        {
            return false;
        }
        var name = logicalLine.AsSpan(0, colon);
        if (name.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
            return ++componentDepth <= MaximumComponentDepth;
        if (!name.Equals("END", StringComparison.OrdinalIgnoreCase))
            return true;
        return --componentDepth >= 0;
    }

    private static bool HasValidRawContentLine(string logicalLine)
    {
        if (!TryFindUnquotedSeparator(logicalLine, ':', out var colon))
            return false;
        if (!TrySplitUnquoted(logicalLine.AsSpan(0, colon), ';', out var headerParts)
            || !IsIcalendarNameText(headerParts[0])
            || !HasOnlyValueCharacters(logicalLine.AsSpan(colon + 1)))
            return false;
        var propertyName = headerParts[0];
        return propertyName.Equals("BEGIN", StringComparison.OrdinalIgnoreCase)
            || propertyName.Equals("END", StringComparison.OrdinalIgnoreCase)
                ? headerParts.Length == 1 && IsIcalendarNameText(logicalLine.AsSpan(colon + 1))
                : headerParts.Skip(1).All(HasValidRawParameter);
    }

    private static bool HasOnlyValueCharacters(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not ('\t' or ' ')
                && character is not (>= '!' and <= '~')
                && character < '\u0080')
                return false;
        }
        return true;
    }

    private static bool HasValidRawParameter(string parameter)
    {
        var equals = parameter.IndexOf('=', StringComparison.Ordinal);
        if (equals <= 0 || !IsIcalendarNameText(parameter.AsSpan(0, equals)))
            return false;
        return TrySplitUnquoted(parameter.AsSpan(equals + 1), ',', out var values)
            && values.All(HasValidRawParameterValue);
    }

    private static bool HasValidRawParameterValue(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value.AsSpan(1, value.Length - 2).IndexOf('"') < 0
                && HasOnlySafeCharacters(value.AsSpan(1, value.Length - 2), quoted: true);
        return HasOnlySafeCharacters(value, quoted: false);
    }

    private static bool HasOnlySafeCharacters(ReadOnlySpan<char> value, bool quoted)
    {
        foreach (var character in value)
        {
            if (quoted ? !IsQSafeCharacter(character) : !IsSafeCharacter(character))
                return false;
        }
        return true;
    }

    private static bool IsSafeCharacter(char character) => character is '\t' or ' '
        || character == '!'
        || character is >= '#' and <= '9'
        || character is >= '<' and <= '~'
        || character >= '\u0080';

    private static bool IsQSafeCharacter(char character) => character is '\t' or ' '
        || character == '!'
        || character is >= '#' and <= '~'
        || character >= '\u0080';

    private static bool TryFindUnquotedSeparator(string value, char separator, out int position)
    {
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '"')
                quoted = !quoted;
            else if (value[index] == separator && !quoted)
            {
                position = index;
                return true;
            }
        }
        position = -1;
        return false;
    }

    private static bool TrySplitUnquoted(
        ReadOnlySpan<char> value,
        char separator,
        out string[] parts)
    {
        var result = new List<string>();
        var start = 0;
        var quoted = false;
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '"')
                quoted = !quoted;
            else if (value[index] == separator && !quoted)
            {
                result.Add(value[start..index].ToString());
                start = index + 1;
            }
        }
        result.Add(value[start..].ToString());
        parts = result.ToArray();
        return !quoted;
    }

    private static bool IsIcalendarNameText(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return false;
        foreach (var character in value)
        {
            if (character is not (>= 'A' and <= 'Z')
                and not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                and not '-')
                return false;
        }
        return true;
    }

    public static bool TryReadIdentity(
        ReadOnlySpan<byte> authoritativeUtf8,
        out CalendarExactResourceIdentity identity)
    {
        identity = default!;
        try
        {
            if (!HasBoundedComponentDepth(authoritativeUtf8))
                return false;
            return TryReadDocumentIdentity(CalendarContentDocument.Parse(authoritativeUtf8), out identity);
        }
        catch (Exception exception) when (exception is FormatException
            or DecoderFallbackException
            or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasBoundedComponentDepth(ReadOnlySpan<byte> authoritativeUtf8)
    {
        var physicalLines = Encoding.Latin1.GetString(authoritativeUtf8)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        var logicalLine = new StringBuilder();
        var depth = 0;
        foreach (var physicalLine in physicalLines)
        {
            if (physicalLine.StartsWith(' ') || physicalLine.StartsWith('\t'))
            {
                logicalLine.Append(physicalLine.AsSpan(1));
                continue;
            }
            if (!UpdateComponentDepth(logicalLine, ref depth))
                return false;
            logicalLine.Clear();
            logicalLine.Append(physicalLine);
        }
        return UpdateComponentDepth(logicalLine, ref depth);
    }

    private static bool UpdateComponentDepth(StringBuilder logicalLine, ref int depth)
    {
        if (logicalLine.Length == 0)
            return true;
        var line = logicalLine.ToString();
        var colon = line.IndexOf(':');
        if (colon < 0)
            return true;
        var name = line.AsSpan(0, colon);
        if (name.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
            return ++depth <= MaximumComponentDepth;
        if (name.Equals("END", StringComparison.OrdinalIgnoreCase))
            depth--;
        return true;
    }

    private static bool TryValidateDocument(
        CalendarContentDocument document,
        out CalendarExactResourceIdentity identity)
    {
        identity = default!;
        if (!CalendarResourceProjector.IsValidExactResource(document))
            return false;
        return TryReadDocumentIdentity(document, out identity);
    }

    private static bool TryReadDocumentIdentity(
        CalendarContentDocument document,
        out CalendarExactResourceIdentity identity)
    {
        identity = default!;
        var entities = GetEntityComponents(document);
        var kindNames = entities.Select(component => component.Path[1].Name).Distinct(StringComparer.Ordinal).ToArray();
        if (entities.Length == 0 || kindNames.Length != 1)
            return false;
        var values = entities.Select(component => ReadIdentity(document, component)).ToArray();
        if (!HasConsistentIdentity(values))
            return false;
        identity = new CalendarExactResourceIdentity(
            values[0]!.Value.Uid,
            kindNames[0] == "VEVENT" ? CalendarEntityKind.Event : CalendarEntityKind.Todo);
        return true;
    }

    private static CalendarContentComponent[] GetEntityComponents(CalendarContentDocument document) => document.Components
        .Where(component => component.Path.Count == 2
            && component.Path[0].Name == "VCALENDAR"
            && component.Path[1].Name is "VEVENT" or "VTODO")
        .ToArray();

    private static bool HasConsistentIdentity((string Uid, bool HasRecurrenceIdentity)?[] values) =>
        values.All(value => value is not null)
        && values.Select(value => value!.Value.Uid).Distinct(StringComparer.Ordinal).Count() == 1
        && values.Count(value => !value!.Value.HasRecurrenceIdentity) == 1;

    private static (string Uid, bool HasRecurrenceIdentity)? ReadIdentity(
        CalendarContentDocument document,
        CalendarContentComponent component)
    {
        var properties = document.Properties.Where(property =>
            property.ComponentPath.SequenceEqual(component.Path)).ToArray();
        var uids = properties.Where(property => property.Name.Equals("UID", StringComparison.OrdinalIgnoreCase))
            .Select(property => DecodeText(property.RawEncodedValue)).ToArray();
        var recurrenceCount = properties.Count(property =>
            property.Name.Equals("RECURRENCE-ID", StringComparison.OrdinalIgnoreCase));
        return uids.Length == 1 && uids[0].Length > 0 && recurrenceCount <= 1
            ? (uids[0], recurrenceCount == 1)
            : null;
    }

    private static string DecodeText(string value)
    {
        var decoded = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\\' && index + 1 < value.Length)
                decoded.Append(value[++index] is 'n' or 'N' ? '\n' : value[index]);
            else
                decoded.Append(value[index]);
        }
        return decoded.ToString();
    }
}
