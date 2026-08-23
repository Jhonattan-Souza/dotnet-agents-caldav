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
        using var activity = StartAttempt(request, resendCount);
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
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
            RecordResponse(activity, response.StatusCode, request);
            return response;
        }
        catch (Exception exception)
        {
            RecordFailure(activity, exception, cancellationToken);
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

    private static Activity? StartAttempt(HttpRequestMessage request, int resendCount)
    {
        var method = request.Method.Method.ToUpperInvariant();
        var activity = CalendarHttpTelemetry.ActivitySource.StartActivity(method, ActivityKind.Client);
        activity?.SetTag("http.request.method", method);
        activity?.SetTag("http.request.resend_count", resendCount);
        if (request.Options.TryGetValue(CalendarHttpTelemetry.RequestPurposeKey, out var purpose)
            && purpose is CalendarHttpTelemetry.AbsenceProbe or CalendarHttpTelemetry.QueryResourceRead)
        {
            activity?.SetTag("caldav.http.request_purpose", purpose);
        }
        return activity;
    }

    private static void RecordResponse(
        Activity? activity,
        HttpStatusCode statusCode,
        HttpRequestMessage request)
    {
        var numericStatus = (int)statusCode;
        activity?.SetTag("http.response.status_code", numericStatus);
        if (statusCode == HttpStatusCode.NotFound
            && request.Options.TryGetValue(CalendarHttpTelemetry.RequestPurposeKey, out var purpose)
            && purpose == CalendarHttpTelemetry.QueryResourceRead)
        {
            activity?.SetTag("caldav.http.observation", "resource_disappeared");
            activity?.SetStatus(ActivityStatusCode.Ok);
            return;
        }
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
