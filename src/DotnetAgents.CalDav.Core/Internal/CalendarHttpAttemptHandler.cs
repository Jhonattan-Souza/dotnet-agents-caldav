using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Buffers;

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
        if (request.Options.TryGetValue(CalendarHttpTelemetry.MultigetResourceCountKey, out var multigetResourceCount))
            CalendarQueryTelemetry.ObserveMultigetAttempt(multigetResourceCount);
        request.Options.TryGetValue(CalendarHttpTelemetry.DirectGetMeterKey, out var directGetMeter);
        if (directGetMeter is not null && !directGetMeter.TryBeginAttempt())
            throw new CalendarDirectGetAttemptLimitException();
        using var attempt = StartAttempt(request, resendCount);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            attempt.ObserveResponse(response.StatusCode);
            try
            {
                if (request.Options.TryGetValue(CalendarHttpTelemetry.DirectGetMeterKey, out var meter))
                    await BufferAndChargeAsync(response, meter, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                response.Dispose();
                throw;
            }
            attempt.CompleteResponse();
            return response;
        }
        catch (Exception exception)
        {
            attempt.CompleteFailure(exception, cancellationToken);
            throw;
        }
    }

    private static async Task BufferAndChargeAsync(
        HttpResponseMessage response,
        CalendarDirectGetReadMeter meter,
        CancellationToken cancellationToken)
    {
        const int maximumBufferedBytes = CalendarDirectGetBudget.MaximumResourceBytes + 1;
        var original = response.Content;
        if (original.Headers.ContentLength is > maximumBufferedBytes)
        {
            meter.ChargeBody(maximumBufferedBytes);
            throw new CalendarDirectGetBudgetExceededException();
        }
        await using var stream = await original.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var destination = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            while (destination.Length < maximumBufferedBytes)
            {
                var read = await meter.ReadAndChargeAsync(
                    stream,
                    buffer.AsMemory(0, Math.Min(buffer.Length, maximumBufferedBytes - (int)destination.Length)),
                    cancellationToken).ConfigureAwait(false);
                if (read < 0 || meter.Failure is not null)
                    throw new CalendarDirectGetBudgetExceededException();
                if (read == 0)
                    break;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            var bytes = destination.ToArray();
            response.Content = CopyHeaders(original, bytes);
        }
        catch
        {
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static ByteArrayContent CopyHeaders(HttpContent original, byte[] content)
    {
        var replacement = new ByteArrayContent(content);
        foreach (var header in original.Headers)
            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
        original.Dispose();
        return replacement;
    }

    private static AttemptActivity StartAttempt(HttpRequestMessage request, int resendCount)
    {
        var method = request.Method.Method.ToUpperInvariant();
        var activity = CalendarHttpTelemetry.ActivitySource.StartActivity(method, ActivityKind.Client);
        return new AttemptActivity(activity, method, resendCount, RequestPurpose(request));
    }

    private static CalendarHttpRequestPurpose? RequestPurpose(HttpRequestMessage request)
    {
        if (!request.Options.TryGetValue(CalendarHttpTelemetry.RequestPurposeKey, out var purpose))
            return null;
        return purpose is CalendarHttpRequestPurpose.AbsenceProbe
            or CalendarHttpRequestPurpose.QueryResourceRead
            ? purpose
            : null;
    }

    private sealed class AttemptActivity(
        Activity? activity,
        string method,
        int resendCount,
        CalendarHttpRequestPurpose? purpose) : IDisposable
    {
        private HttpStatusCode? _statusCode;
        private bool _completed;

        internal void ObserveResponse(HttpStatusCode statusCode) => _statusCode = statusCode;

        internal void CompleteResponse()
        {
            if (_completed)
                return;
            _completed = true;
            var statusCode = _statusCode
                ?? throw new InvalidOperationException("An HTTP response must be observed before completion.");
            ApplyFacts(new CalendarHttpAttemptFacts(
                method,
                resendCount,
                purpose,
                new CalendarHttpAttemptResult.Response(
                    statusCode,
                    ResponseClassification(purpose, statusCode))));
        }

        internal void CompleteFailure(Exception exception, CancellationToken cancellationToken)
        {
            if (_completed)
                return;
            _completed = true;
            ApplyFacts(new CalendarHttpAttemptFacts(
                method,
                resendCount,
                purpose,
                new CalendarHttpAttemptResult.Failure(
                    _statusCode,
                    ControlledFailure(exception, cancellationToken))));
        }

        private void ApplyFacts(CalendarHttpAttemptFacts facts)
        {
            if (activity is null)
                return;
            activity.SetTag("http.request.method", facts.Method);
            activity.SetTag("http.request.resend_count", facts.ResendCount);
            activity.SetTag("caldav.http.request_purpose", PurposeName(facts.Purpose));
            switch (facts.Result)
            {
                case CalendarHttpAttemptResult.Response response:
                    ApplyResponse(activity, response);
                    break;
                case CalendarHttpAttemptResult.Failure failure:
                    activity.SetTag(
                        "http.response.status_code",
                        failure.StatusCode is { } status ? (int)status : null);
                    activity.SetTag("caldav.http.observation", null);
                    activity.SetTag("error.type", FailureName(failure.Classification));
                    activity.SetStatus(ActivityStatusCode.Error);
                    break;
            }
        }

        public void Dispose() => activity?.Dispose();
    }

    private static CalendarHttpResponseClassification ResponseClassification(
        CalendarHttpRequestPurpose? purpose,
        HttpStatusCode statusCode) => (purpose, statusCode) switch
        {
            (CalendarHttpRequestPurpose.AbsenceProbe, HttpStatusCode.NotFound) =>
                CalendarHttpResponseClassification.ExpectedAbsence,
            (CalendarHttpRequestPurpose.QueryResourceRead, HttpStatusCode.NotFound) =>
                CalendarHttpResponseClassification.ResourceDisappeared,
            _ when (int)statusCode >= 400 => CalendarHttpResponseClassification.HttpError,
            _ => CalendarHttpResponseClassification.Success
        };

    private static void ApplyResponse(Activity activity, CalendarHttpAttemptResult.Response response)
    {
        activity.SetTag("http.response.status_code", (int)response.StatusCode);
        activity.SetTag("caldav.http.observation", response.Classification switch
        {
            CalendarHttpResponseClassification.ExpectedAbsence =>
                ObservationName(CalendarHttpObservation.ExpectedAbsence),
            CalendarHttpResponseClassification.ResourceDisappeared =>
                ObservationName(CalendarHttpObservation.ResourceDisappeared),
            _ => null
        });
        activity.SetTag(
            "error.type",
            response.Classification == CalendarHttpResponseClassification.HttpError
                ? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
                : null);
        activity.SetStatus(response.Classification switch
        {
            CalendarHttpResponseClassification.ExpectedAbsence
                or CalendarHttpResponseClassification.ResourceDisappeared => ActivityStatusCode.Ok,
            CalendarHttpResponseClassification.HttpError => ActivityStatusCode.Error,
            _ => ActivityStatusCode.Unset
        });
    }

    private static string? PurposeName(CalendarHttpRequestPurpose? purpose) => purpose switch
    {
        CalendarHttpRequestPurpose.AbsenceProbe => "absence_probe",
        CalendarHttpRequestPurpose.QueryResourceRead => "query_resource_read",
        _ => null
    };

    private static string? ObservationName(CalendarHttpObservation? observation) => observation switch
    {
        CalendarHttpObservation.ExpectedAbsence => "expected_absence",
        CalendarHttpObservation.ResourceDisappeared => "resource_disappeared",
        _ => null
    };

    private readonly record struct CalendarHttpAttemptFacts(
        string Method,
        int ResendCount,
        CalendarHttpRequestPurpose? Purpose,
        CalendarHttpAttemptResult Result);

    private abstract record CalendarHttpAttemptResult
    {
        private CalendarHttpAttemptResult() { }

        internal sealed record Response(
            HttpStatusCode StatusCode,
            CalendarHttpResponseClassification Classification) : CalendarHttpAttemptResult;

        internal sealed record Failure(
            HttpStatusCode? StatusCode,
            CalendarHttpFailureClassification Classification) : CalendarHttpAttemptResult;
    }

    private enum CalendarHttpResponseClassification
    {
        Success,
        HttpError,
        ExpectedAbsence,
        ResourceDisappeared
    }

    private enum CalendarHttpFailureClassification
    {
        CallerCancellation,
        Timeout,
        ResponseEnded,
        ConnectionError,
        InternalError
    }

    private static CalendarHttpFailureClassification ControlledFailure(
        Exception exception,
        CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException when cancellationToken.IsCancellationRequested =>
            CalendarHttpFailureClassification.CallerCancellation,
        OperationCanceledException or TimeoutException => CalendarHttpFailureClassification.Timeout,
        HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded } =>
            CalendarHttpFailureClassification.ResponseEnded,
        HttpRequestException => CalendarHttpFailureClassification.ConnectionError,
        IOException => CalendarHttpFailureClassification.ResponseEnded,
        _ => CalendarHttpFailureClassification.InternalError
    };

    private static string? FailureName(CalendarHttpFailureClassification failure) => failure switch
    {
        CalendarHttpFailureClassification.CallerCancellation => null,
        CalendarHttpFailureClassification.Timeout => "timeout",
        CalendarHttpFailureClassification.ResponseEnded => "response_ended",
        CalendarHttpFailureClassification.ConnectionError => "connection_error",
        CalendarHttpFailureClassification.InternalError => "internal_error",
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, null)
    };
}
