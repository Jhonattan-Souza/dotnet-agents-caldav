using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>
/// Refuses to open an MRTR confirmation round when the request did not declare form elicitation.
/// </summary>
/// <remarks>
/// The 2026-07-28 revision forbids sending an <c>inputRequests</c> the client has not declared support
/// for, and requires the undeclared-capability refusal to travel as JSON-RPC error -32021 carrying
/// <c>data.requiredCapabilities</c>. That protocol error is the single documented exception to this
/// server's typed <c>structuredContent</c> results. The guard runs before the tool issues any CalDAV
/// request, so a refused call leaves the mutation not attempted by construction.
/// </remarks>
internal static class CalendarMrtrCapabilityGuard
{
    private const string MissingFormElicitationMessage =
        "This Calendar mutation is confirmed through MCP Multi Round-Trip Requests, so the request must "
        + "declare the form elicitation client capability.";

    /// <summary>Guards the first confirmation round of a tool that always confirms before it mutates.</summary>
    internal static void RequireConfirmationCapability(
        RequestContext<CallToolRequestParams> requestContext,
        McpServer server) => RequireConfirmationCapability(
            requestContext.Params?.RequestState,
            requestContext.Params?.InputResponses,
            server.IsMrtrSupported,
            server.ClientCapabilities);

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

    /// <summary>Reports whether a request declared the in-band elicitation mode the confirmation needs.</summary>
    internal static bool DeclaresFormElicitation(ClientCapabilities? clientCapabilities) =>
        clientCapabilities?.Elicitation?.Form is not null;

    /// <summary>Builds the capability payload the refusal reports and the raw tool entries assume.</summary>
    internal static ClientCapabilities RequiredCapabilities() => new()
    {
        Elicitation = new ElicitationCapability { Form = new FormElicitationCapability() }
    };
}
