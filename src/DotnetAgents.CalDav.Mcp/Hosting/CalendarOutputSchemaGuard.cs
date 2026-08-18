using System.Collections.Concurrent;
using Json.Schema;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Hosting;

/// <summary>Rejects tool output that does not satisfy the exact schema advertised for that tool.</summary>
internal static class CalendarOutputSchemaGuard
{
    private static readonly ConcurrentDictionary<string, JsonSchema> Schemas = new(StringComparer.Ordinal);

    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallTool => next =>
        async (request, cancellationToken) =>
        {
            var result = await next(request, cancellationToken).ConfigureAwait(false);
            Validate(request.Params?.Name, result);
            return result;
        };

    internal static void Validate(string? toolName, CallToolResult result)
    {
        if (toolName is null || result.StructuredContent is null)
            throw new InvalidOperationException("A Calendar tool returned no structured output to validate.");

        var schema = Schemas.GetOrAdd(toolName, static name =>
            JsonSchema.FromText(CalendarToolContract.GetOutputSchema(name).GetRawText()));
        var evaluation = schema.Evaluate(
            result.StructuredContent.Value,
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!evaluation.IsValid)
            throw new InvalidOperationException("A Calendar tool returned output that violates its advertised schema.");
    }
}
