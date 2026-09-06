namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Reads RFC 4918 DAV compliance classes without interpreting extension identifiers.</summary>
internal static class DavComplianceHeader
{
    private const int MaximumCharacters = 64 * 1024;
    private const int MaximumEmptyElements = 32;
    private const string TokenPunctuation = "!#$%&'*+-.^_`|~";

    internal static bool TryRead(IReadOnlyList<string> headers, out bool automaticScheduling)
    {
        automaticScheduling = false;
        var state = new ReadState();
        var remaining = MaximumCharacters;
        foreach (var header in headers)
        {
            if (header.Length > remaining || !ReadField(header.AsSpan(), ref state))
                return false;
            remaining -= header.Length;
        }
        automaticScheduling = state.AutomaticScheduling;
        return state.HasValue;
    }

    private static bool ReadField(ReadOnlySpan<char> field, ref ReadState state)
    {
        while (true)
        {
            field = field.TrimStart(" \t");
            if (field.IsEmpty || field[0] == ',')
            {
                // HTTP list recipients tolerate a bounded number of empty elements.
                if (++state.EmptyElements > MaximumEmptyElements)
                    return false;
            }
            else if (!ReadValue(ref field, ref state))
                return false;
            field = field.TrimStart(" \t");
            if (field.IsEmpty)
                return true;
            if (field[0] != ',')
                return false;
            field = field[1..];
        }
    }

    private static bool ReadValue(ref ReadOnlySpan<char> field, ref ReadState state)
    {
        var length = field[0] == '<' ? CodedUrlLength(field) : TokenLength(field);
        if (length == 0)
            return false;
        state.HasValue = true;
        state.AutomaticScheduling |= field[..length].Equals("calendar-auto-schedule", StringComparison.OrdinalIgnoreCase);
        field = field[length..];
        return true;
    }

    private static int TokenLength(ReadOnlySpan<char> value)
    {
        var length = 0;
        while (length < value.Length && (char.IsAsciiLetterOrDigit(value[length]) || TokenPunctuation.Contains(value[length])))
            length++;
        return length;
    }

    private static int CodedUrlLength(ReadOnlySpan<char> value)
    {
        var end = value.IndexOf('>');
        return end > 1 && DavComplianceUriSyntax.IsAbsoluteUri(value[1..end]) ? end + 1 : 0;
    }

    private struct ReadState
    {
        internal bool HasValue;
        internal bool AutomaticScheduling;
        internal int EmptyElements;
    }
}
