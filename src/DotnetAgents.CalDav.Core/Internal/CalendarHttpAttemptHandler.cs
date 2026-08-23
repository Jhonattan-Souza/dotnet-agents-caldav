using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace DotnetAgents.CalDav.Core.Internal;

internal sealed class CalendarHttpAttemptHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!request.Options.TryGetValue(CalendarHttpTelemetry.AttemptSequenceKey, out var sequence))
        {
            sequence = new CalendarHttpTelemetry.AttemptSequence();
            request.Options.Set(CalendarHttpTelemetry.AttemptSequenceKey, sequence);
        }
        var resendCount = sequence.NextResendCount();
        request.Options.Set(CalendarHttpTelemetry.ResendCountKey, resendCount);
        using var activity = StartAttempt(request, resendCount);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            RecordResponse(activity, response.StatusCode);
            return response;
        }
        catch (Exception exception)
        {
            RecordFailure(activity, exception, cancellationToken);
            throw;
        }
    }

    private static Activity? StartAttempt(HttpRequestMessage request, int resendCount)
    {
        var method = request.Method.Method.ToUpperInvariant();
        var activity = CalendarHttpTelemetry.ActivitySource.StartActivity(method, ActivityKind.Client);
        activity?.SetTag("http.request.method", method);
        activity?.SetTag("http.request.resend_count", resendCount);
        if (request.Options.TryGetValue(CalendarHttpTelemetry.RequestPurposeKey, out var purpose)
            && purpose == CalendarHttpTelemetry.AbsenceProbe)
        {
            activity?.SetTag("caldav.http.request_purpose", CalendarHttpTelemetry.AbsenceProbe);
        }
        return activity;
    }

    private static void RecordResponse(Activity? activity, HttpStatusCode statusCode)
    {
        var numericStatus = (int)statusCode;
        activity?.SetTag("http.response.status_code", numericStatus);
        if (numericStatus < 400)
            return;
        activity?.SetTag("error.type", numericStatus.ToString(CultureInfo.InvariantCulture));
        activity?.SetStatus(ActivityStatusCode.Error);
    }

    private static void RecordFailure(
        Activity? activity,
        Exception exception,
        CancellationToken cancellationToken)
    {
        activity?.SetTag("error.type", ControlledFailure(exception, cancellationToken));
        activity?.SetStatus(ActivityStatusCode.Error);
    }

    private static string? ControlledFailure(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested => null,
        OperationCanceledException or TimeoutException => "timeout",
        HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded } => "response_ended",
        HttpRequestException => "connection_error",
        IOException => "response_ended",
        _ => "internal_error"
    };
}
