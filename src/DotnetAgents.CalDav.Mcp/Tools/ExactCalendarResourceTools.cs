using System.ComponentModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Opt-in exact Calendar Object Resource access through protected MCP resource links.</summary>
[McpServerToolType]
public sealed class ExactCalendarResourceTools
{
    private readonly ICalendarService _calendarService;

    public ExactCalendarResourceTools(ICalendarService calendarService)
    {
        _calendarService = calendarService;
    }

    [McpServerTool(
        Name = "calendar_resources.exact_get",
        ReadOnly = true,
        Destructive = false,
        Idempotent = true,
        OpenWorld = true,
        UseStructuredContent = true,
        OutputSchemaType = typeof(CalendarExactGetSuccessResult)),
     Description("Read one confirmed absolute href through a protected MCP resource link.")]
    public Task<CallToolResult> GetToolAsync(
        RequestContext<CallToolRequestParams> requestContext,
        CancellationToken cancellationToken) => GetRawAsync(
            requestContext.Params?.Arguments,
            cancellationToken);

    internal async Task<CallToolResult> GetRawAsync(
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        if (CalendarQueryToolSupport.MeasureArguments(
                arguments,
                arguments ?? new Dictionary<string, JsonElement>()) > CalendarQueryToolSupport.MaximumArgumentBytes)
        {
            return CalendarResourceTools.CreateInputGuardError(payloadTooLarge: true);
        }
        if (!ExactCalendarResourceArgumentParser.TryParseGet(arguments, out var href))
            return CalendarResourceTools.CreateInputGuardError(payloadTooLarge: false);
        return await GetAsync(href, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CallToolResult> GetAsync(string href, CancellationToken cancellationToken)
    {
        return await CalendarResourceTools.ExecuteReadAsync(
            _calendarService,
            href,
            CreateSuccess,
            cancellationToken);
    }

    private static CallToolResult CreateSuccess(CalendarResourceSnapshot snapshot)
    {
        var link = ExactCalendarResourceLink.Create(snapshot);
        return CalendarResourceTools.CreateSuccess(
            new CalendarExactGetSuccessResult("success", new CalendarExactResourceLinkResult("resource_link", link.Uri, link.Name)),
            link);
    }
}

public sealed record CalendarExactGetSuccessResult(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("resourceLink")] CalendarExactResourceLinkResult ResourceLink);

public sealed record CalendarExactResourceLinkResult(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("name")] string Name);

internal static class ExactCalendarResourceLink
{
    private const string Scheme = "caldav-exact";

    public static ResourceLinkBlock Create(CalendarResourceSnapshot snapshot)
    {
        var uri = $"{Scheme}://snapshot/{Encode(snapshot.ResourceHref)}?etag={Encode(snapshot.EntityTag)}";
        return new ResourceLinkBlock
        {
            Uri = uri,
            Name = "Calendar Object Resource exact snapshot",
            Description = "Exact UTF-8 content from the bound strong Entity Tag revision.",
            MimeType = "text/calendar; charset=utf-8",
            Size = snapshot.AuthoritativeUtf8.Length
        };
    }

    public static bool TryParse(string uri, out string href, out string entityTag)
    {
        href = string.Empty;
        entityTag = string.Empty;
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !HasProtectedShape(parsed, uri))
            return false;

        var etagPrefix = "?etag=";
        if (!parsed.Query.StartsWith(etagPrefix, StringComparison.Ordinal))
            return false;
        var encodedEntityTag = parsed.Query[etagPrefix.Length..];
        if (encodedEntityTag.Length == 0 || encodedEntityTag.Contains('&'))
            return false;
        return TryDecodeBinding(parsed.AbsolutePath[1..], encodedEntityTag, out href, out entityTag);
    }

    private static bool HasProtectedShape(Uri parsed, string original) =>
        parsed.Scheme.Equals(Scheme, StringComparison.Ordinal)
        && parsed.Host.Equals("snapshot", StringComparison.Ordinal)
        && string.IsNullOrEmpty(parsed.UserInfo)
        && parsed.IsDefaultPort
        && string.IsNullOrEmpty(parsed.Fragment)
        && parsed.AbsolutePath.Length > 1
        && !parsed.AbsolutePath[1..].Contains('/')
        && string.Equals(parsed.AbsoluteUri, original, StringComparison.Ordinal);

    private static bool TryDecodeBinding(
        string encodedHref,
        string encodedEntityTag,
        out string href,
        out string entityTag)
    {
        href = string.Empty;
        entityTag = string.Empty;
        if (!TryDecode(encodedHref, out href) || !TryDecode(encodedEntityTag, out entityTag))
            return false;
        return IsCanonicalResourceHref(href)
            && EntityTagHeaderValue.TryParse(entityTag, out var parsedEntityTag)
            && parsedEntityTag is { IsWeak: false };
    }

    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static bool TryDecode(string value, out string decoded)
    {
        decoded = string.Empty;
        try
        {
            var base64 = value.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            decoded = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(base64));
            return string.Equals(Encode(decoded), value, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsCanonicalResourceHref(string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var parsed)
        && (parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        && string.IsNullOrEmpty(parsed.UserInfo)
        && string.IsNullOrEmpty(parsed.Query)
        && string.IsNullOrEmpty(parsed.Fragment)
        && string.Equals(parsed.AbsoluteUri, href, StringComparison.Ordinal);
}
