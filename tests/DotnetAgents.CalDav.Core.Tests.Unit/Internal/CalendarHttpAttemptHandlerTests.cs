using System.Diagnostics;
using System.Net;
using DotnetAgents.CalDav.Core.Internal;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarHttpAttemptHandlerTests
{
    [Fact]
    public async Task SendAsync_RecordsOnlySafeAttemptDimensions()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);
        using var parent = new Activity("test-parent").Start();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://private.example/secret.ics");
        CalendarHttpTelemetry.MarkAbsenceProbe(request);
        using var invoker = CreateInvoker(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.NotFound)));

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        var attempt = stopped.Single(activity => activity.ParentId == parent.Id);
        attempt.Source.Name.ShouldBe(CalendarHttpTelemetry.InstrumentationName);
        attempt.DisplayName.ShouldBe("GET");
        attempt.GetTagItem("http.request.method").ShouldBe("GET");
        attempt.GetTagItem("http.request.resend_count").ShouldBe(0);
        attempt.GetTagItem("http.response.status_code").ShouldBe(404);
        attempt.GetTagItem("caldav.http.request_purpose").ShouldBe("absence_probe");
        attempt.GetTagItem("error.type").ShouldBe("404");
        attempt.Status.ShouldBe(ActivityStatusCode.Error);
        attempt.Events.ShouldBeEmpty();
        attempt.TagObjects.Any(tag =>
            tag.Value?.ToString()?.Contains("private", StringComparison.Ordinal) == true).ShouldBeFalse();
    }

    [Fact]
    public async Task SendAsync_UsesOnlyControlledFailureClassificationsWithoutEvents()
    {
        using var requestedCancellation = new CancellationTokenSource();
        requestedCancellation.Cancel();
        await AssertFailureAsync(
            new OperationCanceledException(requestedCancellation.Token),
            requestedCancellation.Token,
            expectedErrorType: null);
        await AssertFailureAsync(new OperationCanceledException(), CancellationToken.None, "timeout");
        await AssertFailureAsync(new TimeoutException("private timeout"), CancellationToken.None, "timeout");
        await AssertFailureAsync(
            new HttpRequestException(HttpRequestError.ResponseEnded, "private response ended"),
            CancellationToken.None,
            "response_ended");
        await AssertFailureAsync(new HttpRequestException("private connection"), CancellationToken.None, "connection_error");
        await AssertFailureAsync(new IOException("private response"), CancellationToken.None, "response_ended");
        await AssertFailureAsync(new InvalidOperationException("private failure"), CancellationToken.None, "internal_error");
    }

    private static async Task AssertFailureAsync(
        Exception exception,
        CancellationToken cancellationToken,
        string? expectedErrorType)
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);
        using var parent = new Activity("test-parent").Start();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://private.example/secret.ics");
        using var invoker = CreateInvoker(_ => Task.FromException<HttpResponseMessage>(exception));

        _ = await Should.ThrowAsync<Exception>(async () =>
            await invoker.SendAsync(request, cancellationToken));

        var attempt = stopped.Single(activity => activity.ParentId == parent.Id);
        attempt.GetTagItem("error.type").ShouldBe(expectedErrorType);
        attempt.Status.ShouldBe(ActivityStatusCode.Error);
        attempt.Events.ShouldBeEmpty();
    }

    private static ActivityListener Listen(ICollection<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarHttpTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static HttpMessageInvoker CreateInvoker(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> send) => new(
        new CalendarHttpAttemptHandler
        {
            InnerHandler = new StubHandler(send)
        });

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request);
    }
}
