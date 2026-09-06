using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using DotnetAgents.CalDav.Mcp.Hosting;
using ModelContextProtocol.Protocol;

namespace DotnetAgents.CalDav.Mcp.Tools;

internal static class CalendarProtocolToolSupport
{
    internal static async Task<CallToolResult> ExecuteReadAsync(
        Func<CancellationToken, Task<object>> action,
        CancellationToken cancellationToken)
    {
        using var progress = AttachProgressIfNeeded();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return Success(await action(linked.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Error(new CalendarProtocolException("limit_exhausted", "The operation exhausted its elapsed-time budget."));
        }
        catch (Exception exception) when (IsProtocolFailure(exception, cancellationToken))
        {
            return Error(MapException(exception));
        }
    }

    internal static CalendarOperationProgress.ProgressScope? AttachProgressIfNeeded() =>
        CalendarOperationProgress.CurrentPhase is null
            ? CalendarOperationProgress.Attach(CalendarOperationProgress.CreateState())
            : null;

    internal static CallToolResult Success(object value, CalendarMutationState? mutationState = null)
    {
        var structured = JsonSerializer.SerializeToNode(value)!.AsObject();
        structured["outcome"] = "success";
        var result = new CallToolResult
        {
            IsError = false,
            StructuredContent = JsonSerializer.SerializeToElement(structured),
            Content = [new TextContentBlock { Text = "Calendar operation completed." }]
        };
        var terminal = mutationState is { } state
            ? CalendarToolResult.Success(result, state)
            : CalendarToolResult.Success(result);
        return terminal.FinalizeBounded((_, _) => BuildError(
            new CalendarProtocolException("payload_too_large", "The operation result exceeded its output byte limit."), mutationState));
    }

    internal static CallToolResult Error(CalendarProtocolException exception, CalendarMutationState? mutationState = null) =>
        BuildError(exception, mutationState).FinalizeResult();

    private static CalendarToolResult BuildError(CalendarProtocolException exception, CalendarMutationState? mutationState)
    {
        var facts = FailureFacts(exception);
        var content = new JsonObject
        {
            ["code"] = facts.CodeName,
            ["message"] = exception.Message,
            ["retryable"] = exception.Retryable,
            ["phase"] = facts.PhaseName,
            ["category"] = facts.CategoryName
        };
        if (mutationState is { } state)
            content["mutationState"] = MutationStateName(state);
        var result = new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(content),
            Content = [new TextContentBlock { Text = exception.Message }]
        };
        return mutationState is { } observed
            ? CalendarToolResult.Error(result, facts, observed)
            : CalendarToolResult.Error(result, facts);
    }

    internal static bool IsProtocolFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is CalendarProtocolException or HttpRequestException or XmlException or IOException
            or TimeoutException or CalendarDiscoveryLimitException or CalendarDiscoveryProtocolException
            or CalendarDiscoveryUnsupportedCapabilityException
            or TimeoutRejectedException or BrokenCircuitException or RateLimiterRejectedException
        || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    internal static CalendarProtocolException MapException(Exception exception) => exception switch
    {
        CalendarProtocolException protocol => protocol,
        HttpRequestException http => MapHttpFailure(http.StatusCode),
        CalendarDiscoveryLimitException limit => new("limit_exhausted", limit.Message),
        CalendarDiscoveryUnsupportedCapabilityException => new("unsupported_capability", "The required discovery operation is unavailable."),
        XmlException or CalendarDiscoveryProtocolException or InvalidDataException =>
            new("upstream_protocol_error", "The Calendar server returned an invalid protocol response."),
        _ => new("upstream_unavailable", "The Calendar server is temporarily unavailable.", true)
    };

    internal static CalendarProtocolException MapHttpFailure(HttpStatusCode? status) => status switch
    {
        HttpStatusCode.Unauthorized => new("upstream_unauthorized", "The Calendar operation was not authorized."),
        HttpStatusCode.Forbidden => new("upstream_forbidden", "The Calendar operation was forbidden."),
        HttpStatusCode.NotFound => new("not_found", "The Calendar is missing or inaccessible."),
        HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented =>
            new("unsupported_capability", "The Calendar server does not support this operation."),
        HttpStatusCode.RequestEntityTooLarge => new("payload_too_large", "The Calendar response exceeded its byte limit."),
        HttpStatusCode.TooManyRequests => new("upstream_rate_limited", "The Calendar server is rate limiting requests.", true),
        null or >= HttpStatusCode.InternalServerError => new("upstream_unavailable", "The Calendar server is temporarily unavailable.", true),
        _ => new("upstream_protocol_error", "The Calendar server returned an unexpected HTTP status.")
    };

    private static CalendarStructuredErrorFacts FailureFacts(CalendarProtocolException exception)
    {
        var code = Enum.GetValues<CalendarTelemetryErrorCode>().FirstOrDefault(
            value => CalendarTelemetryVocabulary.ErrorCodeName(value) == exception.Code,
            CalendarTelemetryErrorCode.UpstreamProtocolError);
        return new(code, Category(code), Phase(code), exception.Retryable);
    }

    private static CalendarTelemetryErrorCategory Category(CalendarTelemetryErrorCode code) => code switch
    {
        CalendarTelemetryErrorCode.InvalidInput => CalendarTelemetryErrorCategory.Input,
        CalendarTelemetryErrorCode.OutsideScope or CalendarTelemetryErrorCode.NotFound => CalendarTelemetryErrorCategory.Selection,
        CalendarTelemetryErrorCode.UnsupportedCapability => CalendarTelemetryErrorCategory.CapabilityAndProjection,
        CalendarTelemetryErrorCode.LimitExhausted or CalendarTelemetryErrorCode.PayloadTooLarge => CalendarTelemetryErrorCategory.LimitsAndAdmission,
        CalendarTelemetryErrorCode.Indeterminate or CalendarTelemetryErrorCode.CommittedButUnverified => CalendarTelemetryErrorCategory.PostWriteTruth,
        CalendarTelemetryErrorCode.SyncResetRequired => CalendarTelemetryErrorCategory.State,
        _ => CalendarTelemetryErrorCategory.Upstream
    };

    private static CalendarTelemetryErrorPhase Phase(CalendarTelemetryErrorCode code) => code switch
    {
        CalendarTelemetryErrorCode.InvalidInput => CalendarTelemetryErrorPhase.SchemaLexicalDiscriminator,
        CalendarTelemetryErrorCode.SyncResetRequired => CalendarTelemetryErrorPhase.Pagination,
        CalendarTelemetryErrorCode.Indeterminate or CalendarTelemetryErrorCode.CommittedButUnverified => CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation,
        CalendarTelemetryErrorCode.PayloadTooLarge => CalendarTelemetryErrorPhase.AdmissionAndPayload,
        _ => CurrentFailurePhase()
    };

    private static CalendarTelemetryErrorPhase CurrentFailurePhase() => CalendarOperationProgress.CurrentPhase switch
    {
        CalendarOperationPhase.Fetch or CalendarOperationPhase.Filter or CalendarOperationPhase.Expand => CalendarTelemetryErrorPhase.Execution,
        CalendarOperationPhase.Reconcile => CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation,
        _ => CalendarTelemetryErrorPhase.SelectionDiscoveryCapability
    };

    internal static string MutationStateName(CalendarMutationState state) => state switch
    {
        CalendarMutationState.NotAttempted => "not_attempted",
        CalendarMutationState.NotCommitted => "not_committed",
        CalendarMutationState.Committed => "committed",
        CalendarMutationState.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}
