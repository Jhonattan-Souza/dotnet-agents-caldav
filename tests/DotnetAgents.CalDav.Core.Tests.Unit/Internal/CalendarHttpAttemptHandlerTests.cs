using System.Diagnostics;
using System.Net;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

[Collection("ActivityListener")]
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
    public async Task QueryReadNotFoundIsClassifiedAtThePhysicalAttemptBeforeExport()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);
        using var parent = new Activity("test-parent").Start();
        var meter = new CalendarDirectGetBudget().StartResource();
        using var request = QueryReadRequest(meter);
        using var invoker = CreateInvoker(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new ByteArrayContent([])
            }));

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        var attempt = stopped.Single(activity => activity.ParentId == parent.Id);
        attempt.GetTagItem("http.response.status_code").ShouldBe(404);
        attempt.GetTagItem("caldav.http.request_purpose").ShouldBe("query_resource_read");
        attempt.GetTagItem("caldav.http.observation").ShouldBe("resource_disappeared");
        attempt.GetTagItem("error.type").ShouldBeNull();
        attempt.Status.ShouldBe(ActivityStatusCode.Ok);
        meter.Attempts.ShouldBe(1);
    }

    [Fact]
    public async Task UnknownPurposeOnNotFoundIsNeitherPreservedNorClassifiedAsDisappearance()
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);
        using var parent = new Activity("test-parent").Start();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://private.example/secret.ics");
        request.Options.Set(CalendarHttpTelemetry.RequestPurposeKey, "unknown");
        using var invoker = CreateInvoker(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        var attempt = stopped.Single(activity => activity.ParentId == parent.Id);
        attempt.GetTagItem("caldav.http.request_purpose").ShouldBeNull();
        attempt.GetTagItem("caldav.http.observation").ShouldBeNull();
        attempt.GetTagItem("error.type").ShouldBe("404");
        attempt.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, ActivityStatusCode.Unset, null)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ActivityStatusCode.Error, "503")]
    public async Task QueryReadPurposeSurvivesSuccessfulAndFailedPhysicalResponses(
        HttpStatusCode statusCode,
        ActivityStatusCode expectedStatus,
        string? expectedErrorType)
    {
        var stopped = new List<Activity>();
        using var listener = Listen(stopped);
        using var parent = new Activity("test-parent").Start();
        var meter = new CalendarDirectGetBudget().StartResource();
        using var request = QueryReadRequest(meter);
        using var invoker = CreateInvoker(_ => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent([])
        }));

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        var attempt = stopped.Single(activity => activity.ParentId == parent.Id);
        attempt.GetTagItem("caldav.http.request_purpose").ShouldBe(CalendarHttpTelemetry.QueryResourceRead);
        attempt.GetTagItem("caldav.http.observation").ShouldBeNull();
        attempt.GetTagItem("error.type").ShouldBe(expectedErrorType);
        attempt.Status.ShouldBe(expectedStatus);
        meter.Attempts.ShouldBe(1);
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

    [Fact]
    public async Task QueryReadCountsTwoHeaderlessFailuresAndTheRecoveredAttemptExactlyOnceEach()
    {
        var wireAttempts = 0;
        var meter = new CalendarDirectGetBudget().StartResource();
        using var invoker = CreateInvoker(_ => ++wireAttempts < 3
            ? Task.FromException<HttpResponseMessage>(new HttpRequestException("private"))
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("ok"u8.ToArray())
            }));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var failed = QueryReadRequest(meter);
            await Should.ThrowAsync<HttpRequestException>(() =>
                invoker.SendAsync(failed, TestContext.Current.CancellationToken));
        }
        using var recovered = QueryReadRequest(meter);
        using var response = await invoker.SendAsync(recovered, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        meter.Attempts.ShouldBe(3);
        meter.Failure.ShouldBeNull();
        wireAttempts.ShouldBe(3);
    }

    [Fact]
    public async Task FourthQueryReadAttemptIsRejectedBeforeTheWire()
    {
        var wireAttempts = 0;
        var meter = new CalendarDirectGetBudget().StartResource();
        using var invoker = CreateInvoker(_ =>
        {
            wireAttempts++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("private"));
        });
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var failed = QueryReadRequest(meter);
            await Should.ThrowAsync<HttpRequestException>(() =>
                invoker.SendAsync(failed, TestContext.Current.CancellationToken));
        }
        using var rejected = QueryReadRequest(meter);

        await Should.ThrowAsync<CalendarDirectGetAttemptLimitException>(() =>
            invoker.SendAsync(rejected, TestContext.Current.CancellationToken));

        meter.Attempts.ShouldBe(3);
        wireAttempts.ShouldBe(3);
    }

    [Fact]
    public async Task PartialFailedBodyIsChargedBeforeARecoveredAttempt()
    {
        var wireAttempts = 0;
        var budget = new CalendarDirectGetBudget();
        var meter = budget.StartResource();
        using var invoker = CreateInvoker(_ => Task.FromResult(++wireAttempts == 1
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new PartialFailureStream(1024))
            }
            : new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([42])
            }));
        using var failed = QueryReadRequest(meter);

        await Should.ThrowAsync<IOException>(() =>
            invoker.SendAsync(failed, TestContext.Current.CancellationToken));

        using var recovered = QueryReadRequest(meter);
        using var response = await invoker.SendAsync(recovered, TestContext.Current.CancellationToken);
        meter.Attempts.ShouldBe(2);
        budget.AggregateBytes.ShouldBe(1025);
    }

    [Fact]
    public async Task ResourceOverflowIsTypedAndPreventsASecondWireAttempt()
    {
        var wireAttempts = 0;
        var meter = new CalendarDirectGetBudget().StartResource();
        using var invoker = CreateInvoker(_ =>
        {
            wireAttempts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[(4 * 1024 * 1024) + 1])
            });
        });
        using var first = QueryReadRequest(meter);

        await Should.ThrowAsync<CalendarDirectGetBudgetExceededException>(() =>
            invoker.SendAsync(first, TestContext.Current.CancellationToken));
        using var rejected = QueryReadRequest(meter);
        await Should.ThrowAsync<CalendarDirectGetAttemptLimitException>(() =>
            invoker.SendAsync(rejected, TestContext.Current.CancellationToken));

        meter.Failure!.Code.ShouldBe(QueryFailureCode.PayloadTooLarge);
        wireAttempts.ShouldBe(1);
    }

    [Fact]
    public async Task ConcurrentBodiesStopAtAggregateLimitPlusOneAfterAllFourRequestsDispatch()
    {
        var budget = new CalendarDirectGetBudget();
        for (var index = 0; index < 7; index++)
        {
            var charged = budget.StartResource();
            charged.TryBeginAttempt().ShouldBeTrue();
            charged.ChargeBody(4 * 1024 * 1024);
        }
        var almostFull = budget.StartResource();
        almostFull.TryBeginAttempt().ShouldBeTrue();
        almostFull.ChargeBody((4 * 1024 * 1024) - 3);
        var started = 0;
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var streams = new List<CountingInfiniteStream>();
        using var invoker = CreateInvoker(async _ =>
        {
            if (Interlocked.Increment(ref started) == 4)
                allStarted.TrySetResult();
            await allStarted.Task;
            var stream = new CountingInfiniteStream();
            lock (streams)
                streams.Add(stream);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        });
        var meters = Enumerable.Range(0, 4).Select(_ => budget.StartResource()).ToArray();
        var requests = meters.Select(QueryReadRequest).ToArray();

        var sends = requests.Select(request => Should.ThrowAsync<Exception>(() =>
            invoker.SendAsync(request, TestContext.Current.CancellationToken))).ToArray();
        await Task.WhenAll(sends);

        started.ShouldBe(4);
        streams.Sum(stream => stream.BytesRead).ShouldBe(4);
        budget.AggregateBytes.ShouldBe((32L * 1024 * 1024) + 1);
        foreach (var meter in meters)
            meter.Failure!.Limits!.Dimension.ShouldBe(QueryLimitDimension.ByteCount);
        var laterRetry = budget.StartResource();
        laterRetry.TryBeginAttempt().ShouldBeFalse();
        laterRetry.Attempts.ShouldBe(0);
        laterRetry.Failure!.Limits!.Dimension.ShouldBe(QueryLimitDimension.ByteCount);
        foreach (var request in requests)
            request.Dispose();
    }

    private static HttpRequestMessage QueryReadRequest(CalendarDirectGetReadMeter meter)
    {
        using var scope = CalendarHttpTelemetry.BeginQueryResourceRead(meter);
        var request = new HttpRequestMessage(HttpMethod.Get, "https://private.example/resource.ics");
        CalendarHttpTelemetry.MarkQueryResourceRead(request);
        return request;
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

    private sealed class PartialFailureStream(int successfulBytes) : Stream
    {
        private bool _returned;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_returned)
                return ValueTask.FromException<int>(new IOException("private partial response"));
            _returned = true;
            var count = Math.Min(successfulBytes, buffer.Length);
            buffer.Span[..count].Fill(1);
            return ValueTask.FromResult(count);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class CountingInfiniteStream : Stream
    {
        private long _bytesRead;
        internal long BytesRead => Volatile.Read(ref _bytesRead);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span.Fill(1);
            Interlocked.Add(ref _bytesRead, buffer.Length);
            return ValueTask.FromResult(buffer.Length);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
