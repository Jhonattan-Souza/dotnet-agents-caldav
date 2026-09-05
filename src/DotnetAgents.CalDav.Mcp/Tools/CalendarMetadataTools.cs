using System.ComponentModel;
using System.Text.Json.Serialization;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>Standard collection property inspection and explicit metadata updates.</summary>
[McpServerToolType]
public sealed class CalendarMetadataTools(ICalendarMetadataModule module)
{
    [McpServerTool(Name = "calendars.inspect", ReadOnly = true, Destructive = false,
        Idempotent = true, OpenWorld = true, UseStructuredContent = true),
     Description("Inspect one exact Calendar href for standard metadata, advertised reports, privileges, limits and scheduling evidence. Advertisement is evidence, not verified operation support or permission.")]
    public Task<CallToolResult> InspectAsync(string calendarHref, CancellationToken cancellationToken) =>
        CalendarProtocolToolSupport.ExecuteReadAsync(async token =>
            await module.InspectAsync(calendarHref, token).ConfigureAwait(false), cancellationToken);

    [McpServerTool(Name = "calendars.patch", ReadOnly = false, Destructive = false,
        Idempotent = true, OpenWorld = true, UseStructuredContent = true),
     Description("Set or remove display name or description on one exact Calendar href. Omitted properties are preserved. Metadata writes are unconditional: concurrent edits to the same addressed properties can be overwritten. A committed or uncertain failure requires inspection before another write.")]
    public async Task<CallToolResult> PatchAsync(
        string calendarHref,
        CalendarMetadataPatch patch,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var result = await module.PatchAsync(calendarHref, patch, linked.Token).ConfigureAwait(false);
            return result.Error is not null
                ? CalendarProtocolToolSupport.Error(result.Error, result.MutationState)
                : CalendarProtocolToolSupport.Success(new CalendarMetadataPatchSuccess(
                    result.Calendar!, "unconditional", CalendarProtocolToolSupport.MutationStateName(result.MutationState)), result.MutationState);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return CalendarProtocolToolSupport.Error(new CalendarProtocolException("limit_exhausted",
                "The property update exhausted its execution budget before dispatch."), CalendarMutationState.NotAttempted);
        }
        catch (Exception exception) when (CalendarProtocolToolSupport.IsProtocolFailure(exception, cancellationToken))
        {
            return CalendarProtocolToolSupport.Error(CalendarProtocolToolSupport.MapException(exception), CalendarMutationState.NotAttempted);
        }
    }
}

public sealed record CalendarMetadataPatchSuccess(
    [property: JsonPropertyName("calendar")] CalendarMetadataSnapshot Calendar,
    [property: JsonPropertyName("concurrency")] string Concurrency,
    [property: JsonPropertyName("mutationState")] string MutationState);
