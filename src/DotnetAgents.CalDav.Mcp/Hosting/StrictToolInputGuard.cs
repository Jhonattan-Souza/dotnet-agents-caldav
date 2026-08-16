using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetAgents.CalDav.Mcp.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Hosting;

/// <summary>Preserves strict JSON argument evidence before the SDK materializes tool dictionaries.</summary>
internal static class StrictToolInputGuard
{
    private const string ArgumentBytesKey = "DotnetAgents.CalDav.Mcp.StrictToolInputGuard.ArgumentBytes";
    private const string DuplicateKey = "DotnetAgents.CalDav.Mcp.StrictToolInputGuard.Duplicate";

    public static McpMessageFilter Incoming => next => async (context, cancellationToken) =>
    {
        Inspect(context);
        await next(context, cancellationToken).ConfigureAwait(false);
    };

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallTool => next =>
        (request, cancellationToken) =>
        {
            var evidence = new StrictToolInputEvidence(
                request.Items.TryGetValue(ArgumentBytesKey, out var size) && size is int argumentBytes
                    ? argumentBytes
                    : null,
                request.Items.TryGetValue(DuplicateKey, out var duplicate) && duplicate is true);
            var rejection = Reject(request.Params?.Name, evidence);
            return rejection is null
                ? next(request, cancellationToken)
                : ValueTask.FromResult(rejection);
        };

    internal static CallToolResult? Reject(string? toolName, StrictToolInputEvidence evidence)
    {
        if (toolName != "calendar_entities.query")
            return null;
        if (evidence.ArgumentBytes > CalendarEntityTools.MaximumArgumentBytes)
            return CalendarEntityTools.CreateInputGuardError(payloadTooLarge: true);
        return evidence.HasDuplicateProperty
            ? CalendarEntityTools.CreateInputGuardError(payloadTooLarge: false)
            : null;
    }

    internal static StrictToolInputEvidence InspectArguments(JsonNode? arguments)
    {
        if (arguments is null)
            return default;
        try
        {
            var utf8 = Encoding.UTF8.GetBytes(arguments.ToJsonString());
            return new StrictToolInputEvidence(utf8.Length, ContainsDuplicateProperty(utf8));
        }
        catch (InvalidOperationException)
        {
            return new StrictToolInputEvidence(null, true);
        }
    }

    internal static void Inspect(MessageContext context)
    {
        if (context.JsonRpcMessage is not JsonRpcRequest { Method: "tools/call", Params: JsonObject parameters })
            return;

        JsonNode? arguments;
        try
        {
            arguments = parameters["arguments"];
        }
        catch (InvalidOperationException)
        {
            context.Items[DuplicateKey] = true;
            return;
        }
        var evidence = InspectArguments(arguments);
        if (evidence.ArgumentBytes is { } argumentBytes)
            context.Items[ArgumentBytesKey] = argumentBytes;
        if (evidence.HasDuplicateProperty)
            context.Items[DuplicateKey] = true;
    }

    private static bool ContainsDuplicateProperty(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);
        var scopes = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.StartArray:
                    scopes.Push(null);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    scopes.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (scopes.Peek() is { } properties && !properties.Add(reader.GetString()!))
                        return true;
                    break;
            }
        }
        return false;
    }
}

internal readonly record struct StrictToolInputEvidence(
    int? ArgumentBytes,
    bool HasDuplicateProperty);
