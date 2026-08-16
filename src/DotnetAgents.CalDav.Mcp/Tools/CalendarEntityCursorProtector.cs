using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Configuration;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Process-local authenticated cursor codec for non-snapshot Calendar Entity pagination.</summary>
internal sealed class CalendarEntityCursorProtector
{
    internal const int MaximumCursorCharacters = 2048;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly byte[] _encryptionKey;
    private readonly string _credentialContext;
    private readonly TimeProvider _timeProvider;

    public CalendarEntityCursorProtector(TimeProvider timeProvider, IOptions<CalDavOptions> options)
        : this(timeProvider, options, RandomNumberGenerator.GetBytes(64))
    {
    }

    internal CalendarEntityCursorProtector(
        TimeProvider timeProvider,
        IOptions<CalDavOptions> options,
        byte[] keyMaterial)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        if (keyMaterial.Length != 64)
            throw new ArgumentException("Cursor key material must contain 64 bytes.", nameof(keyMaterial));

        _timeProvider = timeProvider;
        _encryptionKey = keyMaterial[..32];
        var configured = options.Value;
        _credentialContext = Hash(JsonSerializer.SerializeToUtf8Bytes(new CredentialBinding(
            configured.BaseUrl,
            configured.Username,
            configured.Password)));
    }

    public string Protect(string queryContext, string calendarHref, string resourceHref)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new CursorPayload(
            1,
            _timeProvider.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds(),
            Hash(queryContext),
            _credentialContext,
            calendarHref,
            resourceHref));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipherText = new byte[payload.Length];
        var tag = new byte[16];
        using (var cipher = new AesGcm(_encryptionKey, tag.Length))
            cipher.Encrypt(nonce, payload, cipherText, tag);

        var encoded = Base64UrlEncode([.. nonce, .. tag, .. cipherText]);
        if (encoded.Length > MaximumCursorCharacters)
            throw new InvalidOperationException("The protected cursor exceeds the safe size limit.");
        return encoded;
    }

    public bool TryProtect(
        string queryContext,
        string calendarHref,
        string resourceHref,
        out string? cursor)
    {
        try
        {
            cursor = Protect(queryContext, calendarHref, resourceHref);
            return true;
        }
        catch (InvalidOperationException)
        {
            cursor = null;
            return false;
        }
    }

    public bool TryUnprotect(
        string cursor,
        string queryContext,
        out CalendarEntityContinuation continuation,
        out bool expired)
    {
        continuation = default;
        expired = false;
        if (!TryDecodeProtectedCursor(cursor, out var protectedBytes))
            return false;

        var nonce = protectedBytes.AsSpan(0, 12);
        var tag = protectedBytes.AsSpan(12, 16);
        var cipherText = protectedBytes.AsSpan(28);
        var payload = new byte[cipherText.Length];
        try
        {
            using var cipher = new AesGcm(_encryptionKey, tag.Length);
            cipher.Decrypt(nonce, cipherText, tag, payload);
            var decoded = JsonSerializer.Deserialize<CursorPayload>(payload);
            if (!IsValidPayload(decoded, queryContext))
                return false;

            var validPayload = decoded!;
            expired = _timeProvider.GetUtcNow().ToUnixTimeSeconds() >= validPayload.ExpiresAtUnixSeconds;
            if (expired)
                return false;
            continuation = new CalendarEntityContinuation(validPayload.CalendarHref, validPayload.ResourceHref);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool IsValidPayload(CursorPayload? payload, string queryContext) => payload is not null
        && payload.Version == 1
        && !string.IsNullOrEmpty(payload.CalendarHref)
        && !string.IsNullOrEmpty(payload.ResourceHref)
        && HasFixedTimeMatch(payload.QueryHash, Hash(queryContext))
        && HasFixedTimeMatch(payload.CredentialContext, _credentialContext);

    private static bool HasFixedTimeMatch(string left, string right) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(left),
        Encoding.UTF8.GetBytes(right));

    private static bool TryDecodeProtectedCursor(string cursor, out byte[] protectedBytes)
    {
        protectedBytes = [];
        return cursor.Length is > 0 and <= MaximumCursorCharacters
            && TryBase64UrlDecode(cursor, out protectedBytes)
            && protectedBytes.Length > 28;
    }

    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));

    private static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            return false;
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        try
        {
            bytes = Convert.FromBase64String(padded);
            return string.Equals(Base64UrlEncode(bytes), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private sealed record CursorPayload(
        int Version,
        long ExpiresAtUnixSeconds,
        string QueryHash,
        string CredentialContext,
        string CalendarHref,
        string ResourceHref);

    private sealed record CredentialBinding(
        string BaseUrl,
        string Username,
        string Password);
}

internal readonly record struct CalendarEntityContinuation(string CalendarHref, string ResourceHref);
