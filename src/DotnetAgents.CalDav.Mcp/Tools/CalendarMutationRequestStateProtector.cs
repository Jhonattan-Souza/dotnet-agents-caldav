using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Confidential, authenticated, revision-bound state for stateless mutation confirmation.</summary>
internal sealed class CalendarMutationRequestStateProtector
{
    internal const int MaximumRequestStateCharacters = 2048;
    private const string DeleteOperation = "calendar_resources.delete";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly byte[] _encryptionKey;
    private readonly byte[] _bindingKey;
    private readonly string _credentialContext;
    private readonly TimeProvider _timeProvider;

    public CalendarMutationRequestStateProtector(TimeProvider timeProvider, IOptions<CalDavOptions> options)
        : this(timeProvider, options, RandomNumberGenerator.GetBytes(64))
    {
    }

    internal CalendarMutationRequestStateProtector(
        TimeProvider timeProvider,
        IOptions<CalDavOptions> options,
        byte[] keyMaterial)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        if (keyMaterial.Length != 64)
            throw new ArgumentException("Mutation state key material must contain 64 bytes.", nameof(keyMaterial));

        _timeProvider = timeProvider;
        _encryptionKey = keyMaterial[..32];
        _bindingKey = keyMaterial[32..];
        var configured = options.Value;
        _credentialContext = Bind(JsonSerializer.SerializeToUtf8Bytes(new CredentialBinding(
            configured.BaseUrl,
            configured.Username,
            configured.Password)));
    }

    public string Protect(CalendarResourceRevisionReference revision)
        => Protect(DeleteOperation, revision, []);

    public string Protect(
        string operation,
        CalendarResourceRevisionReference revision,
        ReadOnlySpan<byte> requestBinding) => Protect(operation, revision, requestBinding, []);

    public string Protect(
        string operation,
        CalendarResourceRevisionReference revision,
        ReadOnlySpan<byte> requestBinding,
        ReadOnlySpan<byte> intentDigest)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new MutationPayload(
            2,
            _timeProvider.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds(),
            operation,
            BindRevision(revision),
            Bind(requestBinding),
            Bind(intentDigest),
            _credentialContext));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipherText = new byte[payload.Length];
        var tag = new byte[16];
        using (var cipher = new AesGcm(_encryptionKey, tag.Length))
            cipher.Encrypt(nonce, payload, cipherText, tag);

        var encoded = Base64UrlEncode([.. nonce, .. tag, .. cipherText]);
        if (encoded.Length > MaximumRequestStateCharacters)
            throw new InvalidOperationException("The protected mutation state exceeds the safe size limit.");
        return encoded;
    }

    public bool TryUnprotect(
        string state,
        CalendarResourceRevisionReference revision,
        out bool expired)
        => TryUnprotect(state, DeleteOperation, revision, [], out expired);

    public bool TryUnprotect(
        string state,
        string operation,
        CalendarResourceRevisionReference revision,
        ReadOnlySpan<byte> requestBinding,
        out bool expired)
    {
        if (!TryUnprotect(
                state,
                operation,
                revision,
                requestBinding,
                out var intentBinding,
                out expired))
            return false;
        return HasFixedTimeMatch(intentBinding, Bind([]));
    }

    public bool TryUnprotect(
        string state,
        string operation,
        CalendarResourceRevisionReference revision,
        ReadOnlySpan<byte> requestBinding,
        out string intentBinding,
        out bool expired)
    {
        intentBinding = string.Empty;
        expired = false;
        if (!TryDecode(state, out var protectedBytes))
            return false;

        var nonce = protectedBytes.AsSpan(0, 12);
        var tag = protectedBytes.AsSpan(12, 16);
        var cipherText = protectedBytes.AsSpan(28);
        var payload = new byte[cipherText.Length];
        try
        {
            using var cipher = new AesGcm(_encryptionKey, tag.Length);
            cipher.Decrypt(nonce, cipherText, tag, payload);
            var decoded = JsonSerializer.Deserialize<MutationPayload>(payload);
            if (decoded is null
                || decoded.Version != 2
                || !string.Equals(decoded.Operation, operation, StringComparison.Ordinal)
                || !HasFixedTimeMatch(decoded.RevisionBinding, BindRevision(revision))
                || !HasFixedTimeMatch(decoded.RequestBinding, Bind(requestBinding))
                || string.IsNullOrEmpty(decoded.IntentBinding)
                || !HasFixedTimeMatch(decoded.CredentialContext, _credentialContext))
            {
                return false;
            }

            expired = _timeProvider.GetUtcNow().ToUnixTimeSeconds() >= decoded.ExpiresAtUnixSeconds;
            intentBinding = decoded.IntentBinding;
            return !expired;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return false;
        }
    }

    private string BindRevision(CalendarResourceRevisionReference revision) => Bind(
        JsonSerializer.SerializeToUtf8Bytes(new RevisionBinding(
            revision.Href,
            revision.EntityUid,
            revision.EntityKind == CalendarEntityKind.Event ? "event" : "todo",
            revision.EntityTag)));

    private string Bind(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(HMACSHA256.HashData(_bindingKey, value));

    public bool MatchesIntent(string intentBinding, ReadOnlySpan<byte> intentDigest) =>
        HasFixedTimeMatch(intentBinding, Bind(intentDigest));

    private static bool HasFixedTimeMatch(string left, string right) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(left),
        Encoding.UTF8.GetBytes(right));

    private static bool TryDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Length is <= 0 or > MaximumRequestStateCharacters
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            return false;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - (padded.Length % 4)) % 4);
        try
        {
            bytes = Convert.FromBase64String(padded);
            return bytes.Length > 28 && string.Equals(Base64UrlEncode(bytes), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) => Convert.ToBase64String(value)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private sealed record MutationPayload(
        int Version,
        long ExpiresAtUnixSeconds,
        string Operation,
        string RevisionBinding,
        string RequestBinding,
        string IntentBinding,
        string CredentialContext);

    private sealed record CredentialBinding(string BaseUrl, string Username, string Password);

    private sealed record RevisionBinding(string Href, string EntityUid, string EntityKind, string EntityTag);
}
