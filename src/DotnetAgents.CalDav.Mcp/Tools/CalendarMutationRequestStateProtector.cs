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
    private const string CollectionDeleteOperation = "calendars.delete";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly byte[] _encryptionKey;
    private readonly byte[] _bindingKey;
    private readonly string _credentialContext;
    private readonly string _configurationContext;
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
            configured.Username,
            configured.Password)));
        _configurationContext = Bind(JsonSerializer.SerializeToUtf8Bytes(new ConfigurationBinding(
            NormalizeEndpoint(configured.BaseUrl),
            NormalizeOrigin(configured.BaseUrl),
            NormalizeScope(configured.CalendarHrefs),
            configured.InteroperabilityProfile ?? string.Empty,
            configured.RequestTimeout.Ticks)));
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
        ReadOnlySpan<byte> intentDigest) => ProtectCore(
            operation,
            BindRevision(revision),
            requestBinding,
            intentDigest);

    public string ProtectCalendarCollectionDelete(CalendarCollectionDeleteReviewBinding binding)
    {
        var href = Encoding.UTF8.GetBytes(binding.Href);
        return ProtectCore(
            CollectionDeleteOperation,
            Bind(href),
            href,
            SerializeCollectionDeleteBinding(binding));
    }

    public bool TryUnprotectCalendarCollectionDelete(
        string state,
        CalendarCollectionDeleteReviewBinding binding,
        out bool expired)
    {
        var href = Encoding.UTF8.GetBytes(binding.Href);
        if (!TryUnprotectCore(
                state,
                CollectionDeleteOperation,
                Bind(href),
                href,
                out var intentBinding,
                out expired))
        {
            return false;
        }

        return HasFixedTimeMatch(intentBinding, Bind(SerializeCollectionDeleteBinding(binding)));
    }

    public string ProtectExactCreate(
        string operation,
        ReadOnlySpan<byte> requestBinding,
        CalendarExactCreateReviewBinding binding) => ProtectCore(
            operation,
            Bind(requestBinding),
            requestBinding,
            SerializeExactCreateBinding(binding));

    public string ProtectExactMove(
        string operation,
        ReadOnlySpan<byte> requestBinding,
        CalendarExactMoveReviewBinding binding) => ProtectCore(
            operation,
            BindRevision(binding.Revision),
            requestBinding,
            SerializeExactMoveBinding(binding),
            ToProtectedExactMoveBinding(binding));

    private string ProtectCore(
        string operation,
        string targetBinding,
        ReadOnlySpan<byte> requestBinding,
        ReadOnlySpan<byte> intentDigest,
        ProtectedExactMoveBinding? exactMoveBinding = null)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new MutationPayload(
            3,
            _timeProvider.GetUtcNow().Add(Lifetime).ToUnixTimeSeconds(),
            operation,
            targetBinding,
            Bind(requestBinding),
            Bind(intentDigest),
            _credentialContext,
            _configurationContext,
            exactMoveBinding));
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
        out bool expired) => TryUnprotectCore(
            state,
            operation,
            BindRevision(revision),
            requestBinding,
            out intentBinding,
            out expired);

    public bool TryUnprotectExactCreate(
        string state,
        string operation,
        ReadOnlySpan<byte> requestBinding,
        out string intentBinding,
        out bool expired) => TryUnprotectCore(
            state,
            operation,
            Bind(requestBinding),
            requestBinding,
            out intentBinding,
            out expired);

    public bool TryUnprotectExactMove(
        string state,
        string operation,
        CalendarExactMoveRequest request,
        ReadOnlySpan<byte> requestBinding,
        out CalendarExactMoveReviewBinding binding,
        out bool expired)
    {
        binding = null!;
        if (!TryUnprotectCore(
                state,
                operation,
                BindRevision(request.Revision),
                requestBinding,
                out var intentBinding,
                out expired,
                out var protectedBinding)
            || !TryRestoreExactMoveBinding(request, protectedBinding, out binding))
        {
            return false;
        }
        return MatchesExactMoveBinding(intentBinding, binding);
    }

    private bool TryUnprotectCore(
        string state,
        string operation,
        string targetBinding,
        ReadOnlySpan<byte> requestBinding,
        out string intentBinding,
        out bool expired) => TryUnprotectCore(
            state,
            operation,
            targetBinding,
            requestBinding,
            out intentBinding,
            out expired,
            out _);

    private bool TryUnprotectCore(
        string state,
        string operation,
        string targetBinding,
        ReadOnlySpan<byte> requestBinding,
        out string intentBinding,
        out bool expired,
        out ProtectedExactMoveBinding? exactMoveBinding)
    {
        intentBinding = string.Empty;
        expired = false;
        exactMoveBinding = null;
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
                || decoded.Version != 3
                || !string.Equals(decoded.Operation, operation, StringComparison.Ordinal)
                || !HasFixedTimeMatch(decoded.RevisionBinding, targetBinding)
                || !HasFixedTimeMatch(decoded.RequestBinding, Bind(requestBinding))
                || string.IsNullOrEmpty(decoded.IntentBinding)
                || !HasFixedTimeMatch(decoded.CredentialContext, _credentialContext)
                || !HasFixedTimeMatch(decoded.ConfigurationContext, _configurationContext))
            {
                return false;
            }

            expired = _timeProvider.GetUtcNow().ToUnixTimeSeconds() >= decoded.ExpiresAtUnixSeconds;
            intentBinding = decoded.IntentBinding;
            exactMoveBinding = decoded.ExactMoveBinding;
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

    public bool MatchesExactCreateBinding(
        string intentBinding,
        CalendarExactCreateReviewBinding binding) =>
        HasFixedTimeMatch(intentBinding, Bind(SerializeExactCreateBinding(binding)));

    public bool MatchesExactMoveBinding(
        string intentBinding,
        CalendarExactMoveReviewBinding binding) =>
        HasFixedTimeMatch(intentBinding, Bind(SerializeExactMoveBinding(binding)));

    private static byte[] SerializeExactCreateBinding(CalendarExactCreateReviewBinding binding) =>
        JsonSerializer.SerializeToUtf8Bytes(new ExactCreateBinding(
            binding.DestinationHref,
            binding.EntityUid,
            binding.EntityKind == CalendarEntityKind.Event ? "event" : "todo",
            Convert.ToHexStringLower(binding.IntentDigest.Span),
            binding.PolicyVersion));

    private static byte[] SerializeCollectionDeleteBinding(CalendarCollectionDeleteReviewBinding binding) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            binding.Href,
            binding.DescriptorDigest
        });

    private static byte[] SerializeExactMoveBinding(CalendarExactMoveReviewBinding binding) =>
        JsonSerializer.SerializeToUtf8Bytes(ToProtectedExactMoveBinding(binding));

    private static ProtectedExactMoveBinding ToProtectedExactMoveBinding(CalendarExactMoveReviewBinding binding) => new(
            Convert.ToHexStringLower(binding.SourceIntentDigest.Span),
            binding.PolicyVersion);

    private static bool TryRestoreExactMoveBinding(
        CalendarExactMoveRequest request,
        ProtectedExactMoveBinding? protectedBinding,
        out CalendarExactMoveReviewBinding binding)
    {
        binding = null!;
        if (protectedBinding is null
            || protectedBinding.SourceIntentDigest.Length != SHA256.HashSizeInBytes * 2)
        {
            return false;
        }
        try
        {
            var digest = Convert.FromHexString(protectedBinding.SourceIntentDigest);
            binding = new CalendarExactMoveReviewBinding(
                request.Revision,
                request.DestinationHref,
                digest,
                protectedBinding.PolicyVersion);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string NormalizeEndpoint(string value)
    {
        var endpoint = value.EndsWith("/", StringComparison.Ordinal) ? value : value + '/';
        return new Uri(endpoint, UriKind.Absolute).AbsoluteUri;
    }

    private static string NormalizeOrigin(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    }

    private static string[] NormalizeScope(string? value) => value is null
        ? []
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

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
        string CredentialContext,
        string ConfigurationContext,
        ProtectedExactMoveBinding? ExactMoveBinding);

    private sealed record CredentialBinding(string Username, string Password);

    private sealed record ConfigurationBinding(
        string Endpoint,
        string Origin,
        IReadOnlyList<string> CalendarScope,
        string InteroperabilityProfile,
        long RequestTimeoutTicks);

    private sealed record RevisionBinding(string Href, string EntityUid, string EntityKind, string EntityTag);

    private sealed record ExactCreateBinding(
        string DestinationHref,
        string EntityUid,
        string EntityKind,
        string IntentDigest,
        string PolicyVersion);

    private sealed record ProtectedExactMoveBinding(
        string SourceIntentDigest,
        string PolicyVersion);
}
