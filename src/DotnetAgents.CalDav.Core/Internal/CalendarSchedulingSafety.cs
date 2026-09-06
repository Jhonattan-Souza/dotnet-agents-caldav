using DotnetAgents.CalDav.Core.Abstractions;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Detects participation conservatively without changing caller-authored calendar data.</summary>
internal static class CalendarSchedulingSafety
{
    internal static Task<bool> IsAllowedAsync(
        ICalendarClient client, string href, ReadOnlyMemory<byte> prior, ReadOnlyMemory<byte> proposed,
        CancellationToken cancellationToken) => RequiresCheck(prior, proposed)
            ? client.IsStorageOnlyMutationAllowedAsync(href, prior, proposed, cancellationToken)
            : Task.FromResult(true);

    internal static Task<bool> IsAllowedAsync(
        ICalendarCreateTransport transport, string href, ReadOnlyMemory<byte> prior, ReadOnlyMemory<byte> proposed,
        CancellationToken cancellationToken) => RequiresCheck(prior, proposed)
            ? transport.IsStorageOnlyMutationAllowedAsync(href, prior, proposed, cancellationToken)
            : Task.FromResult(true);

    internal static bool HasParticipation(ReadOnlyMemory<byte> data)
    {
        var bytes = data.Span;
        Span<byte> name = stackalloc byte[9];
        var position = 0;
        while (position < bytes.Length)
        {
            var length = ReadPropertyName(bytes, ref position, name);
            if (length <= name.Length && IsParticipationName(name[..length]))
                return true;
            SkipPropertyValue(bytes, ref position);
        }
        return false;
    }

    private static int ReadPropertyName(ReadOnlySpan<byte> bytes, ref int position, Span<byte> name)
    {
        var length = 0;
        while (position < bytes.Length)
        {
            var value = bytes[position];
            if (value is (byte)':' or (byte)';')
            {
                position++;
                break;
            }
            if (value is (byte)'\r' or (byte)'\n')
            {
                if (SkipNameFold(bytes, ref position))
                    continue;
                break;
            }
            length = AppendPropertyNameByte(value, name, length);
            position++;
        }
        return length;
    }

    private static int AppendPropertyNameByte(byte value, Span<byte> name, int length)
    {
        // CalendarContentDocument accepts an optional group prefix and identifies the
        // property by the suffix after the dot. Keep the same identity when screening writes.
        if (value == (byte)'.')
            return 0;
        if (length < name.Length)
            name[length] = ToUpperAscii(value);
        return length + 1;
    }

    private static bool SkipNameFold(ReadOnlySpan<byte> bytes, ref int position)
    {
        var after = position + 1;
        if (bytes[position] == (byte)'\r' && after < bytes.Length && bytes[after] == (byte)'\n')
            after++;
        if (after >= bytes.Length || bytes[after] is not ((byte)' ' or (byte)'\t'))
            return false;
        position = after + 1;
        return true;
    }

    private static void SkipPropertyValue(ReadOnlySpan<byte> bytes, ref int position)
    {
        while (position < bytes.Length)
        {
            var newline = bytes[position..].IndexOf((byte)'\n');
            if (newline < 0)
            {
                position = bytes.Length;
                return;
            }
            position += newline + 1;
            if (position == bytes.Length || bytes[position] is not ((byte)' ' or (byte)'\t'))
                return;
            position++;
        }
    }

    private static byte ToUpperAscii(byte value) => value is >= (byte)'a' and <= (byte)'z'
        ? (byte)(value - ('a' - 'A')) : value;

    private static bool IsParticipationName(ReadOnlySpan<byte> name) =>
        name.SequenceEqual("ORGANIZER"u8) || name.SequenceEqual("ATTENDEE"u8);

    internal static bool RequiresCheck(ReadOnlyMemory<byte> prior, ReadOnlyMemory<byte> proposed) =>
        HasParticipation(prior) || HasParticipation(proposed);

    internal static bool ProvesSchedulingAbsent(IReadOnlyList<string> headers)
    {
        var values = headers.SelectMany(value => value.Split(',')).Select(value => value.Trim()).ToArray();
        return values.Length > 0 && values.All(IsComplianceValue)
            && !values.Contains("calendar-auto-schedule", StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsComplianceValue(string value) => value.Length > 0
        && (value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
            || value.StartsWith('<') && value.EndsWith('>')
                && Uri.TryCreate(value[1..^1], UriKind.Absolute, out _));
}
