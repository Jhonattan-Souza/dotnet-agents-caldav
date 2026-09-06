using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Checks RFC 3986 absolute-URI syntax for opaque DAV identifiers, without scheme-specific URL rules.</summary>
internal static class DavComplianceUriSyntax
{
    private const string UnreservedPunctuation = "-._~";
    private const string SubDelimiters = "!$&'()*+,;=";

    internal static bool IsAbsoluteUri(ReadOnlySpan<char> value)
    {
        if (value.ContainsAnyExceptInRange('!', '~'))
            return false;
        var colon = value.IndexOf(':');
        if (colon < 1 || !IsScheme(value[..colon]))
            return false;
        var suffix = value[(colon + 1)..];
        var query = suffix.IndexOf('?');
        return query < 0 ? IsHierarchy(suffix)
            : IsHierarchy(suffix[..query]) && IsComponent(suffix[(query + 1)..], "/?:@");
    }

    private static bool IsScheme(ReadOnlySpan<char> value)
    {
        if (!char.IsAsciiLetter(value[0]))
            return false;
        foreach (var character in value[1..])
            if (!char.IsAsciiLetterOrDigit(character) && !"+-.".Contains(character))
                return false;
        return true;
    }

    private static bool IsHierarchy(ReadOnlySpan<char> value)
    {
        if (!value.StartsWith("//"))
            return IsComponent(value, "/:@");
        var authorityAndPath = value[2..];
        var slash = authorityAndPath.IndexOf('/');
        return slash < 0 ? IsAuthority(authorityAndPath)
            : IsAuthority(authorityAndPath[..slash]) && IsComponent(authorityAndPath[slash..], "/:@");
    }

    private static bool IsAuthority(ReadOnlySpan<char> value)
    {
        var at = value.LastIndexOf('@');
        if (at >= 0)
        {
            if (!IsComponent(value[..at], ":"))
                return false;
            value = value[(at + 1)..];
        }
        if (value.StartsWith("["))
            return IsLiteralAuthority(value);
        var colon = value.LastIndexOf(':');
        return colon < 0 ? IsComponent(value, string.Empty)
            : IsComponent(value[..colon], string.Empty) && IsPort(value[(colon + 1)..]);
    }

    private static bool IsLiteralAuthority(ReadOnlySpan<char> value)
    {
        var end = value.IndexOf(']');
        if (end < 2 || !IsIpLiteral(value[1..end]))
            return false;
        var remainder = value[(end + 1)..];
        return remainder.IsEmpty || remainder[0] == ':' && IsPort(remainder[1..]);
    }

    private static bool IsIpLiteral(ReadOnlySpan<char> value)
    {
        if (value[0] is 'v' or 'V')
            return IsFutureAddress(value);
        return !value.Contains('%') && (!value.Contains('.') || IsIpv4Tail(value[(value.LastIndexOf(':') + 1)..]))
            && IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6;
    }

    private static bool IsFutureAddress(ReadOnlySpan<char> value)
    {
        var dot = value.IndexOf('.');
        if (dot < 2 || dot == value.Length - 1 || value.Contains('%'))
            return false;
        foreach (var character in value[1..dot])
            if (!char.IsAsciiHexDigit(character))
                return false;
        return IsComponent(value[(dot + 1)..], ":");
    }

    private static bool IsIpv4Tail(ReadOnlySpan<char> value)
    {
        var octets = 0;
        while (true)
        {
            var dot = value.IndexOf('.');
            if (!IsDecimalOctet(dot < 0 ? value : value[..dot]))
                return false;
            octets++;
            if (dot < 0)
                return octets == 4;
            if (octets == 4)
                return false;
            value = value[(dot + 1)..];
        }
    }

    private static bool IsDecimalOctet(ReadOnlySpan<char> value) => value.Length is >= 1 and <= 3
        && (value.Length == 1 || value[0] != '0')
        && byte.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static bool IsPort(ReadOnlySpan<char> value) => !value.ContainsAnyExceptInRange('0', '9');

    private static bool IsComponent(ReadOnlySpan<char> value, string extraCharacters)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '%')
            {
                if (index + 2 >= value.Length || !char.IsAsciiHexDigit(value[index + 1]) || !char.IsAsciiHexDigit(value[index + 2]))
                    return false;
                index += 2;
            }
            else if (!IsUnreserved(character) && !SubDelimiters.Contains(character) && !extraCharacters.Contains(character))
                return false;
        }
        return true;
    }

    private static bool IsUnreserved(char value) => char.IsAsciiLetterOrDigit(value) || UnreservedPunctuation.Contains(value);
}
