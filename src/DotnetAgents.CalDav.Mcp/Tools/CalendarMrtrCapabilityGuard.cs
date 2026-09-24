using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>
/// Decides, per protocol revision, whether a request can carry a confirmation round and refuses one the
/// request did not declare.
/// </summary>
/// <remarks>
/// <para>
/// On the 2026-07-28 revision the client declares capabilities on each request and receives the
/// confirmation as an <c>inputRequests</c> result. That revision forbids sending an input request the
/// client has not declared support for, and requires the undeclared-capability refusal to travel as
/// JSON-RPC error -32021 carrying <c>data.requiredCapabilities</c>. That protocol error is the single
/// documented exception to this server's typed <c>structuredContent</c> results.
/// </para>
/// <para>
/// Revisions negotiated through <c>initialize</c> (2024-11-05 through 2025-11-25) declare capabilities once
/// per session and do not define -32021. On this stateful stdio session the SDK resolves the same
/// confirmation by sending the identical elicitation parameters as a classic server-to-client
/// <c>elicitation/create</c> request and re-entering the tool with the answer, so the protected
/// <c>requestState</c> never leaves the process. Elicitation exists from 2025-06-18, so only a 2025-06-18 or
/// 2025-11-25 session that declared form elicitation takes that path; any other session, including every
/// 2024-11-05 and 2025-03-26 session, keeps the typed <c>unsupported_capability</c> result the tools already
/// return when confirmation is unavailable.
/// </para>
/// <para>
/// Both checks run before the tool issues any CalDAV mutation, so a refused call leaves the mutation not
/// attempted by construction. Capabilities are always read from the request-scoped server, which reports
/// the per-request declaration on 2026-07-28 and the session declaration on earlier revisions.
/// </para>
/// </remarks>
internal static class CalendarMrtrCapabilityGuard
{
    private const string PerRequestMetadataRevision = "2026-07-28";
    private const string ElicitationRevision = "2025-06-18";

    private const string MissingFormElicitationMessage =
        "This Calendar mutation is confirmed through MCP Multi Round-Trip Requests, so the request must "
        + "declare the form elicitation client capability.";

    /// <summary>Guards the first confirmation round of a tool that always confirms before it mutates.</summary>
    internal static void RequireConfirmationCapability(RequestContext<CallToolRequestParams> requestContext) =>
        RequireConfirmationCapability(
            requestContext.Params?.RequestState,
            requestContext.Params?.InputResponses,
            IsConfirmationSupported(requestContext),
            requestContext.Server.ClientCapabilities);

    /// <summary>Guards a confirmation round that a continuation has not already opened.</summary>
    internal static void RequireConfirmationCapability(
        string? requestState,
        IDictionary<string, InputResponse>? inputResponses,
        bool mrtrSupported,
        ClientCapabilities? clientCapabilities)
    {
        if (requestState is not null || inputResponses is not null)
            return;
        if (!mrtrSupported || DeclaresFormElicitation(clientCapabilities))
            return;
        throw new MissingRequiredClientCapabilityException(
            RequiredCapabilities(),
            MissingFormElicitationMessage);
    }

    /// <summary>Reports whether the request's negotiated revision can carry a confirmation round.</summary>
    internal static bool IsConfirmationSupported(RequestContext<CallToolRequestParams> requestContext)
    {
        var server = requestContext.Server;
        return IsConfirmationSupported(
            requestContext.JsonRpcRequest.Context?.ProtocolVersion ?? server.NegotiatedProtocolVersion,
            server.IsMrtrSupported,
            server.ClientCapabilities);
    }

    /// <summary>
    /// Keeps the MRTR answer on 2026-07-28, where an undeclared capability is refused as -32021, and
    /// requires an elicitation-capable revision with declared form elicitation on the initialize-handshake
    /// revisions, where the confirmation becomes a classic elicitation request.
    /// </summary>
    internal static bool IsConfirmationSupported(
        string? protocolVersion,
        bool mrtrSupported,
        ClientCapabilities? clientCapabilities) =>
        mrtrSupported
        && (UsesPerRequestCapabilities(protocolVersion)
            || (DefinesElicitation(protocolVersion) && DeclaresFormElicitation(clientCapabilities)));

    /// <summary>Reports whether a revision declares capabilities per request instead of per session.</summary>
    internal static bool UsesPerRequestCapabilities(string? protocolVersion) =>
        protocolVersion is not null
        && string.CompareOrdinal(protocolVersion, PerRequestMetadataRevision) >= 0;

    /// <summary>Reports whether a revision defines the elicitation request at all.</summary>
    internal static bool DefinesElicitation(string? protocolVersion) =>
        protocolVersion is not null
        && string.CompareOrdinal(protocolVersion, ElicitationRevision) >= 0;

    /// <summary>Reports whether a request declared the in-band elicitation mode the confirmation needs.</summary>
    internal static bool DeclaresFormElicitation(ClientCapabilities? clientCapabilities) =>
        clientCapabilities?.Elicitation?.Form is not null;

    /// <summary>Builds the capability payload the refusal reports and the raw tool entries assume.</summary>
    internal static ClientCapabilities RequiredCapabilities() => new()
    {
        Elicitation = new ElicitationCapability { Form = new FormElicitationCapability() }
    };
}
