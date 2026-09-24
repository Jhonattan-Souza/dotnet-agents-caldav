using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using Json.Schema;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Hosting;

/// <summary>Enforces the exact schema advertised for each Calendar tool on every returned result.</summary>
/// <remarks>
/// This is the outermost call filter, so a mutation may already have committed when its result is found
/// to violate the contract. Throwing would surface only the SDK's generic unstructured tool error and erase
/// that Mutation State, so a mutation violation becomes an indeterminate postWriteTruth outcome that keeps
/// only the handler's reported state. A read cannot lose Mutation State, and its error schemas do not all
/// admit that outcome, so a read violation still throws.
/// </remarks>
internal static class CalendarOutputSchemaGuard
{
    private const string MissingStructuredOutputMessage =
        "A Calendar tool returned no structured output to validate.";
    private const string SchemaViolationMessage =
        "A Calendar tool returned output that violates its advertised schema.";
    private static readonly ConcurrentDictionary<string, JsonSchema> Schemas = new(StringComparer.Ordinal);
    private static readonly CalendarStructuredErrorFacts MutationViolationFacts = new(
        CalendarTelemetryErrorCode.Indeterminate,
        CalendarTelemetryErrorCategory.PostWriteTruth,
        CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation,
        Retryable: false);

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallTool => next =>
        async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken).ConfigureAwait(false);
            return Enforce(request.Params?.Name, result);
        };

    /// <summary>Returns a schema-valid result, replacing a mutation's contract violation with an indeterminate outcome.</summary>
    internal static CallToolResult Enforce(string? toolName, CallToolResult result)
    {
        if (FindViolation(toolName, result) is not { } violation)
            return result;

        if (!CalendarExecutionPolicy.IsMutation(toolName))
        {
            CalendarTelemetry.ObserveOutputContractViolation(toolName, violation);
            throw new InvalidOperationException(Message(violation));
        }

        var mutationState = ReportedMutationState(result);
        CalendarTelemetry.ObserveOutputContractViolation(toolName, violation, MutationViolationFacts, mutationState);
        return CreateMutationViolationResult(mutationState);
    }

    /// <summary>Throws when the result does not satisfy the tool's advertised output schema.</summary>
    internal static void Validate(string? toolName, CallToolResult result)
    {
        if (FindViolation(toolName, result) is { } violation)
            throw new InvalidOperationException(Message(violation));
    }

    internal static CallToolResult CreateMutationViolationResult(CalendarMutationState mutationState)
    {
        var body = new JsonObject
        {
            ["code"] = MutationViolationFacts.CodeName,
            ["category"] = MutationViolationFacts.CategoryName,
            ["phase"] = MutationViolationFacts.PhaseName,
            ["retryable"] = MutationViolationFacts.Retryable,
            ["message"] = MutationViolationMessage(mutationState),
            ["mutationState"] = CalendarTelemetryVocabulary.MutationStateName(mutationState)
        };
        var replacement = new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(body),
            Content = []
        };
        CalendarQueryToolSupport.ApplyCompatibilityText(replacement);
        return replacement;
    }

    // A handler that reported no commit keeps that evidence; any other state needs inspection first.
    private static string MutationViolationMessage(CalendarMutationState mutationState) =>
        mutationState is CalendarMutationState.NotAttempted or CalendarMutationState.NotCommitted
            ? "The mutation result violated its advertised output schema; it reported no committed write."
            : "The mutation result violated its advertised output schema; inspect the target before another write.";

    private static CalendarOutputContractViolation? FindViolation(string? toolName, CallToolResult result)
    {
        if (toolName is null || result.StructuredContent is null)
            return CalendarOutputContractViolation.MissingStructuredContent;

        var schema = Schemas.GetOrAdd(toolName, static name =>
            JsonSchema.FromText(CalendarToolContract.GetOutputSchema(name).GetRawText()));
        var evaluation = schema.Evaluate(
            result.StructuredContent.Value,
            // The guard consumes only validity. Detailed evaluation trees are neither
            // returned nor logged, and retaining them scales with the entire page.
            new EvaluationOptions { OutputFormat = OutputFormat.Flag });
        return evaluation.IsValid ? null : CalendarOutputContractViolation.SchemaViolation;
    }

    // Only one closed mutationState is evidence; a missing, duplicated or unknown value stays unknown.
    private static CalendarMutationState ReportedMutationState(CallToolResult result)
    {
        if (result.StructuredContent is not { ValueKind: JsonValueKind.Object } structured)
            return CalendarMutationState.Unknown;
        var reported = structured.EnumerateObject()
            .Where(property => property.NameEquals("mutationState"))
            .Select(property => property.Value)
            .ToArray();
        return reported is [{ ValueKind: JsonValueKind.String } state]
            ? MutationState(state.GetString())
            : CalendarMutationState.Unknown;
    }

    private static CalendarMutationState MutationState(string? value) => value switch
    {
        "not_attempted" => CalendarMutationState.NotAttempted,
        "not_committed" => CalendarMutationState.NotCommitted,
        "committed" => CalendarMutationState.Committed,
        _ => CalendarMutationState.Unknown
    };

    private static string Message(CalendarOutputContractViolation violation) =>
        violation == CalendarOutputContractViolation.MissingStructuredContent
            ? MissingStructuredOutputMessage
            : SchemaViolationMessage;
}
