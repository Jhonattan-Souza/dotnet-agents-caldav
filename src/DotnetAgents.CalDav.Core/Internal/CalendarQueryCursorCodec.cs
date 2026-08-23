using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Configuration;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Internal;

internal sealed class CalendarQueryCursorKey
{
    internal CalendarQueryCursorKey(IOptions<CalDavOptions> options)
        : this(options, RandomNumberGenerator.GetBytes(64))
    {
    }

    internal CalendarQueryCursorKey(IOptions<CalDavOptions> options, byte[] keyMaterial)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (keyMaterial.Length != 64)
            throw new ArgumentException("Cursor key material must contain 64 bytes.", nameof(keyMaterial));

        EncryptionKey = keyMaterial[..32];
        NonceKey = keyMaterial[32..];
        var serializedContext = JsonSerializer.SerializeToUtf8Bytes(CursorContext.From(options.Value));
        try
        {
            Context = Convert.ToHexStringLower(HMACSHA256.HashData(NonceKey, serializedContext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serializedContext);
        }
    }

    internal byte[] EncryptionKey { get; }

    internal byte[] NonceKey { get; }

    internal string Context { get; }

    private sealed record CursorContext(
        string BaseUrl,
        string Username,
        string Password,
        string CalendarScope,
        string DefaultEventCalendarName,
        string DefaultTodoCalendarName,
        string EvaluationTimeZone,
        long RequestTimeoutTicks)
    {
        internal static CursorContext From(CalDavOptions options) => new(
            options.BaseUrl,
            options.Username,
            options.Password,
            string.Join(',', (options.CalendarHrefs ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)),
            options.DefaultEventCalendarName?.Trim() ?? string.Empty,
            options.DefaultTodoCalendarName?.Trim() ?? string.Empty,
            options.EvaluationTimeZone ?? string.Empty,
            options.RequestTimeout.Ticks);
    }
}

internal sealed class CalendarQueryCursorIssuer(CalendarQueryCursorKey key)
{
    internal const int MaximumCursorCharacters = 2048;

    internal string Issue(
        string tool,
        Guid snapshotId,
        int position,
        DateTimeOffset expiresAt,
        ReadOnlyMemory<byte> temporalEvaluationContextUtf8 = default) => Protect(new CalendarQueryCursor(
        Version: 2,
        Tool: tool,
        SnapshotId: snapshotId,
        Position: position,
        ExpiresAtUnixMilliseconds: expiresAt.ToUnixTimeMilliseconds(),
        Context: key.Context,
        TemporalContextBinding: BindTemporalContext(temporalEvaluationContextUtf8.Span)));

    private string BindTemporalContext(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key.NonceKey, value));

    private string Protect(CalendarQueryCursor cursor)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(cursor);
        var nonce = HMACSHA256.HashData(key.NonceKey, payload)[..12];
        var cipherText = new byte[payload.Length];
        var tag = new byte[16];
        using (var cipher = new AesGcm(key.EncryptionKey, tag.Length))
            cipher.Encrypt(nonce, payload, cipherText, tag);
        var encoded = Base64UrlEncode([.. nonce, .. tag, .. cipherText]);
        if (encoded.Length > MaximumCursorCharacters)
            throw new InvalidOperationException("The protected cursor exceeds the safe size limit.");
        return encoded;
    }

    internal static string Base64UrlEncode(ReadOnlySpan<byte> value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}

internal sealed class CalendarQueryCursorAuthenticator(
    CalendarQueryCursorKey key,
    TimeProvider timeProvider)
{
    internal CalendarQueryCursorAuthentication Authenticate(string cursor, string expectedTool)
    {
        if (!TryDecodeProtectedCursor(cursor, out var protectedBytes))
            return CalendarQueryCursorAuthentication.Invalid;
        try
        {
            var decoded = Decrypt(protectedBytes);
            if (!IsValid(decoded, expectedTool))
                return CalendarQueryCursorAuthentication.Invalid;
            return timeProvider.GetUtcNow().ToUnixTimeMilliseconds() >= decoded!.ExpiresAtUnixMilliseconds
                ? CalendarQueryCursorAuthentication.Expired
                : CalendarQueryCursorAuthentication.Valid(decoded);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return CalendarQueryCursorAuthentication.Invalid;
        }
    }

    internal bool MatchesTemporalContext(
        CalendarQueryCursor cursor,
        ReadOnlySpan<byte> temporalEvaluationContextUtf8) => FixedTimeEquals(
        cursor.TemporalContextBinding,
        Convert.ToHexStringLower(HMACSHA256.HashData(key.NonceKey, temporalEvaluationContextUtf8)));

    private CalendarQueryCursor? Decrypt(byte[] protectedBytes)
    {
        var nonce = protectedBytes.AsSpan(0, 12);
        var tag = protectedBytes.AsSpan(12, 16);
        var cipherText = protectedBytes.AsSpan(28);
        var payload = new byte[cipherText.Length];
        using var cipher = new AesGcm(key.EncryptionKey, tag.Length);
        cipher.Decrypt(nonce, cipherText, tag, payload);
        return JsonSerializer.Deserialize<CalendarQueryCursor>(payload);
    }

    private bool IsValid(CalendarQueryCursor? cursor, string expectedTool) => cursor is
        {
            Version: 2,
            SnapshotId: var snapshotId,
            Position: >= 1,
            ExpiresAtUnixMilliseconds: > 0,
            TemporalContextBinding.Length: 64
        }
        && string.Equals(cursor.Tool, expectedTool, StringComparison.Ordinal)
        && snapshotId != Guid.Empty
        && FixedTimeEquals(cursor.Context, key.Context);

    private static bool FixedTimeEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(left),
        Encoding.UTF8.GetBytes(right));

    private static bool TryDecodeProtectedCursor(string cursor, out byte[] protectedBytes)
    {
        protectedBytes = [];
        if (cursor.Length is <= 0 or > CalendarQueryCursorIssuer.MaximumCursorCharacters
            || cursor.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            return false;
        var padded = cursor.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try
        {
            protectedBytes = Convert.FromBase64String(padded);
            return protectedBytes.Length > 28
                && string.Equals(CalendarQueryCursorIssuer.Base64UrlEncode(protectedBytes), cursor, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

internal sealed record CalendarQueryCursor(
    int Version,
    string Tool,
    Guid SnapshotId,
    int Position,
    long ExpiresAtUnixMilliseconds,
    string Context,
    string TemporalContextBinding);

internal sealed record CalendarQueryCursorAuthentication(
    CalendarQueryCursorAuthenticationCode Code,
    CalendarQueryCursor? Cursor)
{
    internal static CalendarQueryCursorAuthentication Invalid { get; } = new(
        CalendarQueryCursorAuthenticationCode.Invalid,
        null);

    internal static CalendarQueryCursorAuthentication Expired { get; } = new(
        CalendarQueryCursorAuthenticationCode.Expired,
        null);

    internal static CalendarQueryCursorAuthentication Valid(CalendarQueryCursor cursor) => new(
        CalendarQueryCursorAuthenticationCode.Valid,
        cursor);
}

internal enum CalendarQueryCursorAuthenticationCode
{
    Valid,
    Invalid,
    Expired
}
