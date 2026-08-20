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
    private const string InvalidStringKey = "DotnetAgents.CalDav.Mcp.StrictToolInputGuard.InvalidString";
    private const string ViolationsKey = "DotnetAgents.CalDav.Mcp.StrictToolInputGuard.Violations";

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
            evidence = evidence with
            {
                HasInvalidString = request.Items.TryGetValue(InvalidStringKey, out var invalid) && invalid is true,
                CollectedViolations = request.Items.TryGetValue(ViolationsKey, out var violations)
                    ? violations as IReadOnlyList<CalendarInputViolation>
                    : null
            };
            var rejection = Reject(request.Params?.Name, evidence);
            return rejection is null
                ? next(request, cancellationToken)
                : ValueTask.FromResult(rejection);
        };

    internal static CallToolResult? Reject(string? toolName, StrictToolInputEvidence evidence)
    {
        return toolName switch
        {
            "calendar_entities.query" => RejectEntity(evidence),
            "calendar_occurrences.query" => RejectOccurrence(evidence),
            "todos.query" => RejectTodo(evidence),
            "events.create" or "todos.create" => RejectCreate(evidence),
            "events.patch" or "todos.patch" => RejectPatch(evidence),
            "calendar_occurrences.add" or "calendar_occurrences.exclude"
                or "calendar_occurrences.restore_exclusion" or "calendar_occurrences.cancel"
                or "calendar_occurrences.restore_cancellation" => RejectOccurrenceMutation(evidence),
            "calendar_resources.move" => RejectMove(evidence),
            "calendar_resources.delete" => RejectDelete(evidence),
            "calendar_resources.get" => RejectResourceGet(evidence),
            "calendar_resources.exact_get" => RejectExactGet(evidence),
            "calendar_resources.exact_create" or "calendar_resources.exact_replace" => RejectExactWrite(evidence),
            "calendar_resources.exact_move" => RejectExactMove(evidence),
            _ => null
        };
    }

    private static CallToolResult? RejectEntity(StrictToolInputEvidence evidence) =>
        Reject(evidence, CalendarQueryToolSupport.MaximumArgumentBytes, CalendarEntityTools.CreateInputGuardError);

    private static CallToolResult? RejectOccurrence(StrictToolInputEvidence evidence) =>
        Reject(evidence, CalendarQueryToolSupport.MaximumArgumentBytes, CalendarOccurrenceTools.CreateInputGuardError);

    private static CallToolResult? RejectTodo(StrictToolInputEvidence evidence) =>
        Reject(evidence, CalendarTodoTools.MaximumArgumentBytes, CalendarTodoTools.CreateInputGuardError);

    private static CallToolResult? RejectCreate(StrictToolInputEvidence evidence) =>
        Reject(evidence, CalendarEntityCreateTools.MaximumArgumentBytes, CalendarEntityCreateTools.CreateInputGuardError);

    private static CallToolResult? RejectPatch(StrictToolInputEvidence evidence) =>
        Reject(evidence, CalendarEntityPatchTools.MaximumArgumentBytes, CalendarEntityPatchTools.CreateInputGuardError);

    private static CallToolResult? RejectOccurrenceMutation(StrictToolInputEvidence evidence) => Reject(
        evidence,
        CalendarOccurrenceMutationTools.MaximumArgumentBytes,
        CalendarOccurrenceMutationTools.CreateInputGuardError);

    private static CallToolResult? RejectDelete(StrictToolInputEvidence evidence) =>
        Reject(evidence, CalendarResourceDeleteTools.MaximumArgumentBytes, CalendarResourceDeleteTools.CreateInputGuardError);

    private static CallToolResult? RejectMove(StrictToolInputEvidence evidence) =>
        Reject(evidence, CalendarResourceMoveTools.MaximumArgumentBytes, CalendarResourceMoveTools.CreateInputGuardError);

    private static CallToolResult? RejectExactGet(StrictToolInputEvidence evidence) =>
        Reject(evidence, CalendarQueryToolSupport.MaximumArgumentBytes, CalendarResourceTools.CreateInputGuardError);

    private static CallToolResult? RejectResourceGet(StrictToolInputEvidence evidence) =>
        Reject(evidence, CalendarQueryToolSupport.MaximumArgumentBytes, CalendarResourceTools.CreateInputGuardError);

    private static CallToolResult? RejectExactWrite(StrictToolInputEvidence evidence) => Reject(
        evidence,
        ExactCalendarResourceWriteTools.MaximumArgumentBytes,
        ExactCalendarResourceWriteTools.CreateInputGuardError);

    private static CallToolResult? RejectExactMove(StrictToolInputEvidence evidence) => Reject(
        evidence,
        ExactCalendarResourceWriteTools.MaximumMetadataArgumentBytes,
        ExactCalendarResourceWriteTools.CreateInputGuardError);

    private static CallToolResult? Reject(
        StrictToolInputEvidence evidence,
        int maximumArgumentBytes,
        Func<bool, CallToolResult> createError)
    {
        if (evidence.ArgumentBytes > maximumArgumentBytes)
            return createError(true);
        if (!evidence.HasDuplicateProperty && !evidence.HasInvalidString && evidence.Violations.Count == 0)
            return null;

        var violations = evidence.Violations.Count > 0
            ? evidence.Violations
            : DefaultViolations(evidence);
        return CalendarErrorViolations.Attach(createError(false), violations);
    }

    private static IReadOnlyList<CalendarInputViolation> DefaultViolations(StrictToolInputEvidence evidence)
    {
        var violations = new List<CalendarInputViolation>(2);
        if (evidence.HasDuplicateProperty)
            violations.Add(new("/", "duplicate_member", "An object contains a duplicate member."));
        if (evidence.HasInvalidString)
            violations.Add(new("/", "invalid_unicode", "An object member contains invalid Unicode."));
        return violations;
    }

    internal static StrictToolInputEvidence InspectArguments(JsonNode? arguments)
    {
        if (arguments is null)
            return default;
        try
        {
            var utf8 = Encoding.UTF8.GetBytes(arguments.ToJsonString());
            return new StrictToolInputEvidence(
                utf8.Length,
                ContainsDuplicateProperty(utf8),
                ContainsInvalidString(utf8));
        }
        catch (InvalidOperationException)
        {
            return new StrictToolInputEvidence(null, false, true);
        }
    }

    internal static void Inspect(MessageContext context)
    {
        if (context.JsonRpcMessage is not JsonRpcRequest { Method: "tools/call", Params: JsonObject parameters })
            return;

        var toolName = parameters["name"]?.GetValue<string>();
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
        if (evidence.HasInvalidString)
        {
            context.Items[InvalidStringKey] = true;
            parameters["arguments"] = new JsonObject();
        }
        if (toolName == "calendar_resources.get"
            && !evidence.HasDuplicateProperty
            && !evidence.HasInvalidString)
        {
            var violations = ValidateResourceGetArguments(arguments);
            if (violations.Count > 0)
                context.Items[ViolationsKey] = violations;
        }
    }

    internal static IReadOnlyList<CalendarInputViolation> ValidateResourceGetArguments(JsonNode? arguments)
    {
        try
        {
            if (arguments is not JsonObject value)
                return [new("/", "invalid_type", "The arguments must be an object.")];
            var violations = new List<CalendarInputViolation>();
            foreach (var property in value)
            {
                if (property.Key != "href")
                    violations.Add(new($"/{property.Key}", "unknown_member", "The member is not allowed."));
            }
            if (!value.TryGetPropertyValue("href", out var href)
                || href is not JsonValue hrefValue
                || !hrefValue.TryGetValue<string>(out var text)
                || string.IsNullOrEmpty(text))
            {
                violations.Add(new("/href", "invalid_member", "A non-empty href string is required."));
            }
            return violations;
        }
        catch (InvalidOperationException)
        {
            return [new("/", "invalid_object", "The arguments object is invalid.")];
        }
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

    private static bool ContainsInvalidString(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8);
        while (reader.Read())
        {
            if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.PropertyName))
                continue;
            try
            {
                _ = reader.GetString();
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
        return false;
    }
}

internal readonly record struct StrictToolInputEvidence(
    int? ArgumentBytes,
    bool HasDuplicateProperty,
    bool HasInvalidString = false,
    IReadOnlyList<CalendarInputViolation>? CollectedViolations = null)
{
    internal IReadOnlyList<CalendarInputViolation> Violations => CollectedViolations ?? [];
}
