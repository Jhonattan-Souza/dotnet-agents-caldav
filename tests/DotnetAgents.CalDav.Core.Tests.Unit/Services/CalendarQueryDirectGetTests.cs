using System.Text;
using System.Net;
using System.Diagnostics;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using NSubstitute;
using Polly;
using Polly.CircuitBreaker;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarQueryDirectGetTests
{
    private const string CalendarHref = "https://cal.example/calendars/work/";
    private const string ResourceHref = CalendarHref + "event.ics";

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(51)]
    [InlineData(200)]
    public async Task SuccessfulMultigetUsesFiftyResourceBatchesAndZeroGets(int resourceCount)
    {
        var hrefs = Enumerable.Range(0, resourceCount)
            .Select(index => $"{CalendarHref}{index:D3}.ics")
            .ToArray();
        var transport = new FallbackTransport(
            hrefs,
            requested => new CalendarMultigetResult.Resources(requested.Select(Success).ToArray()));
        await using var provider = CreateProvider(transport);

        (await QueryAsync(provider, TestContext.Current.CancellationToken))
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();

        transport.MultigetCount.ShouldBe((resourceCount + 49) / 50);
        transport.GetCount.ShouldBe(0);
    }

    [Fact]
    public async Task VerifiedUnavailableMultigetReadsTheCompleteQueryThroughDirectGet()
    {
        var transport = new FallbackTransport([ResourceHref]);
        await using var provider = CreateProvider(transport);

        var reply = await provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
                CalendarEntityScope.All,
                [CalendarEntityKind.Event])),
            CancellationToken.None);

        reply.ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>().Value.Items.Count.ShouldBe(1);
        transport.MultigetCount.ShouldBe(1);
        transport.GetCount.ShouldBe(1);
    }

    [Fact]
    public async Task KnownUnavailableWithTwoHundredOneCandidatesFailsBeforeTheFirstGet()
    {
        var hrefs = Enumerable.Range(0, 201).Select(index => $"{CalendarHref}{index:D3}.ics").ToArray();
        var transport = new FallbackTransport(hrefs);
        await using var provider = CreateProvider(transport);

        var failure = (await QueryAsync(provider, TestContext.Current.CancellationToken))
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.LimitExhausted);
        failure.Error.Limits!.Dimension.ShouldBe(QueryLimitDimension.ResourceCount);
        failure.Error.Limits.Observed.ShouldBe(201);
        failure.Error.Limits.Limit.ShouldBe(200);
        transport.GetCount.ShouldBe(0);
    }

    [Fact]
    public async Task FiveFallbackResourcesRunAsOneWaveOfFourThenOne()
    {
        var hrefs = Enumerable.Range(0, 5).Select(index => $"{CalendarHref}{index}.ics").ToArray();
        var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FallbackTransport? transport = null;
        transport = new FallbackTransport(hrefs, get: async (href, cancellationToken) =>
        {
            if (href != hrefs[4])
            {
                if (transport!.GetCount == 4)
                    fourStarted.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            return Success(href);
        });
        await using var provider = CreateProvider(transport);

        var query = QueryAsync(provider, TestContext.Current.CancellationToken);
        await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        transport.GetCount.ShouldBe(4);
        transport.MaximumInFlight.ShouldBe(4);
        release.TrySetResult();

        (await query).ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>().Value.Items.Count.ShouldBe(5);
        transport.GetCount.ShouldBe(5);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(200)]
    public async Task FallbackAcceptsTheClosedResourceCountMatrix(int resourceCount)
    {
        var hrefs = Enumerable.Range(0, resourceCount)
            .Select(index => $"{CalendarHref}{index:D3}.ics")
            .ToArray();
        var transport = new FallbackTransport(hrefs);
        await using var provider = CreateProvider(transport);

        (await QueryAsync(provider, TestContext.Current.CancellationToken))
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();

        transport.GetCount.ShouldBe(resourceCount);
        transport.MaximumInFlight.ShouldBeLessThanOrEqualTo(4);
    }

    [Fact]
    public async Task ConcurrentQueriesShareTheFourPerOriginPermit()
    {
        var hrefs = Enumerable.Range(0, 4).Select(index => $"{CalendarHref}{index}.ics").ToArray();
        var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FallbackTransport? transport = null;
        transport = new FallbackTransport(hrefs, get: async (href, cancellationToken) =>
        {
            if (transport!.GetCount == 4)
                fourStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Success(href);
        });
        await using var provider = CreateProvider(transport);

        var first = QueryAsync(provider, TestContext.Current.CancellationToken);
        await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var second = QueryAsync(provider, TestContext.Current.CancellationToken);

        transport.GetCount.ShouldBe(4);
        transport.MaximumInFlight.ShouldBe(4);
        release.TrySetResult();
        await Task.WhenAll(first, second);
        transport.GetCount.ShouldBe(8);
        transport.MaximumInFlight.ShouldBe(4);
    }

    [Fact]
    public async Task DifferentOriginsReceiveIndependentFourWidePermits()
    {
        const string secondCalendarHref = "https://other.example/calendars/work/";
        var firstHrefs = Enumerable.Range(0, 4).Select(index => $"{CalendarHref}{index}.ics").ToArray();
        var secondHrefs = Enumerable.Range(0, 4).Select(index => $"{secondCalendarHref}{index}.ics").ToArray();
        var eightStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FallbackTransport? transport = null;
        transport = new FallbackTransport(firstHrefs.Concat(secondHrefs).ToArray(), get: async (href, cancellationToken) =>
        {
            if (transport!.GetCount == 8)
                eightStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Success(href);
        });
        var firstCalendar = Calendar(CalendarHref);
        var secondCalendar = Calendar(secondCalendarHref);
        var retriever = new CalendarQueryResourceRetriever();

        var first = retriever.RetrieveAsync(
            transport,
            firstHrefs.ToDictionary(href => href, _ => firstCalendar),
            TestContext.Current.CancellationToken);
        var second = retriever.RetrieveAsync(
            transport,
            secondHrefs.ToDictionary(href => href, _ => secondCalendar),
            TestContext.Current.CancellationToken);
        await eightStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        transport.MaximumInFlight.ShouldBe(8);
        release.TrySetResult();

        (await first).Error.ShouldBeNull();
        (await second).Error.ShouldBeNull();
    }

    [Fact]
    public async Task ExternalCancellationAwaitsCurrentWaveCleanupAndReleasesEveryPermit()
    {
        var hrefs = Enumerable.Range(0, 5).Select(index => $"{CalendarHref}{index}.ics").ToArray();
        var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = 0;
        var blockReads = 1;
        FallbackTransport? transport = null;
        transport = new FallbackTransport(hrefs, get: async (href, cancellationToken) =>
        {
            if (transport!.GetCount == 4)
                fourStarted.TrySetResult();
            if (Volatile.Read(ref blockReads) == 0)
                return Success(href);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancellationObserved);
                throw;
            }
            return Success(href);
        });
        await using var provider = CreateProvider(transport);
        using var cancellation = new CancellationTokenSource();
        var query = QueryAsync(provider, cancellation.Token);
        await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => query);

        cancellationObserved.ShouldBe(4);
        transport.GetCount.ShouldBe(4);
        transport.InFlight.ShouldBe(0);
        Volatile.Write(ref blockReads, 0);
        (await QueryAsync(provider, TestContext.Current.CancellationToken))
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();
        transport.GetCount.ShouldBe(9);
        transport.MaximumInFlight.ShouldBe(4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameWaveFailuresChooseCanonicalHrefAndNeverScheduleLaterWave(bool canonicalCompletesFirst)
    {
        var hrefs = Enumerable.Range(0, 5).Select(index => $"{CalendarHref}{index}.ics").ToArray();
        var releaseCanonical = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLater = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FallbackTransport(hrefs, get: async (href, cancellationToken) =>
        {
            if (href == hrefs[0])
            {
                if (!canonicalCompletesFirst)
                    await releaseCanonical.Task.WaitAsync(cancellationToken);
                throw new HttpRequestException("private", null, HttpStatusCode.Forbidden);
            }
            if (href == hrefs[1])
            {
                if (canonicalCompletesFirst)
                    await releaseLater.Task.WaitAsync(cancellationToken);
                else
                    releaseCanonical.TrySetResult();
                throw new HttpRequestException("private", null, HttpStatusCode.TooManyRequests);
            }
            if (canonicalCompletesFirst)
                releaseLater.TrySetResult();
            return Success(href);
        });
        await using var provider = CreateProvider(transport);

        var failure = (await QueryAsync(provider, TestContext.Current.CancellationToken))
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.UpstreamForbidden);
        transport.RequestedHrefs.ShouldNotContain(hrefs[4]);
        transport.GetCount.ShouldBe(4);
    }

    [Fact]
    public async Task ReversedMultigetResponseOrderIsRestoredToCanonicalRequestedOrder()
    {
        var hrefs = new[] { CalendarHref + "a.ics", CalendarHref + "b.ics" };
        var transport = new FallbackTransport(
            hrefs,
            requested => new CalendarMultigetResult.Resources(requested.Reverse().Select(Success).ToArray()));
        await using var provider = CreateProvider(transport);

        var page = (await QueryAsync(provider, TestContext.Current.CancellationToken))
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();

        page.Value.Items.Select(item => item.Value.GetProperty("resourceRevision").GetProperty("href").GetString())
            .ShouldBe(hrefs);
        transport.GetCount.ShouldBe(0);
    }

    [Fact]
    public async Task DiscardedPartialMultigetAbsenceIsCountedOnceFromFinalFallbackTruth()
    {
        var hrefs = Enumerable.Range(0, 51).Select(index => $"{CalendarHref}{index:D3}.ics").ToArray();
        var batch = 0;
        var transport = new FallbackTransport(
            hrefs,
            requested => Interlocked.Increment(ref batch) == 1
                ? new CalendarMultigetResult.Resources(requested.Select(href => href == hrefs[0]
                    ? new CalendarResourceRead(CalendarResourceReadCode.NotFound, href)
                    : Success(href)).ToArray())
                : new CalendarMultigetResult.VerifiedUnavailable(),
            (href, _) => Task.FromResult(href == hrefs[0]
                ? new CalendarResourceRead(CalendarResourceReadCode.NotFound, href)
                : Success(href)));
        await using var provider = CreateProvider(transport);
        using var listener = ListenToQueries();
        using var source = new ActivitySource(CalendarQueryTelemetry.InstrumentationName, "0.1.0");
        using var operation = source.StartActivity("caldav.operation", ActivityKind.Internal);
        operation.ShouldNotBeNull();

        (await QueryAsync(provider, TestContext.Current.CancellationToken))
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Page>();

        operation.GetTagItem("caldav.query.fetch_mode").ShouldBe("mixed");
        operation.GetTagItem("caldav.query.disappeared_resource_count").ShouldBe(1L);
        transport.MultigetCount.ShouldBe(2);
        transport.GetCount.ShouldBe(51);
    }

    [Fact]
    public async Task ThreeRealAttemptTimeoutsBecomeTheClosedAttemptCountLimit()
    {
        var wire = new CancelledAttemptHandler();
        var services = new ServiceCollection();
        services.AddTransient<CalendarHttpAttemptHandler>();
        var builder = services.AddHttpClient("query-timeout")
            .ConfigurePrimaryHttpMessageHandler(() => wire);
        builder.AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 2;
            options.Retry.Delay = TimeSpan.Zero;
            options.Retry.UseJitter = false;
            options.Retry.BackoffType = DelayBackoffType.Constant;
            options.AttemptTimeout.Timeout = TimeSpan.FromMilliseconds(10);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(1);
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromMilliseconds(500);
        });
        builder.AddHttpMessageHandler<CalendarHttpAttemptHandler>();
        await using var provider = services.BuildServiceProvider();
        var transport = new TimeoutFallbackTransport(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("query-timeout"));
        var calendar = new CalendarDescriptor
        {
            Href = CalendarHref,
            DisplayName = "Work",
            DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
            EventSupport = EntityKindSupport.Advertised,
            TodoSupport = EntityKindSupport.NotAdvertised
        };

        var result = await new CalendarQueryResourceRetriever().RetrieveAsync(
            transport,
            new Dictionary<string, CalendarDescriptor> { [ResourceHref] = calendar },
            TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe(QueryFailureCode.LimitExhausted);
        result.Error.Limits!.Dimension.ShouldBe(QueryLimitDimension.AttemptCount);
        result.Error.Limits.Observed.ShouldBe(3);
        result.Error.Limits.Limit.ShouldBe(3);
        wire.Attempts.ShouldBe(3);
    }

    [Fact]
    public async Task ErrorBodyThatCrossesTheAggregateBudgetWinsOverItsHttpStatus()
    {
        var hrefs = Enumerable.Range(0, 9).Select(index => $"{CalendarHref}{index}.ics").ToArray();
        using var transport = new AggregateCrossingTransport(hrefs);
        var calendar = Calendar(CalendarHref);

        var result = await new CalendarQueryResourceRetriever().RetrieveAsync(
            transport,
            hrefs.ToDictionary(href => href, _ => calendar),
            TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe(QueryFailureCode.LimitExhausted);
        result.Error.Limits!.Dimension.ShouldBe(QueryLimitDimension.ByteCount);
        result.Error.Limits.Observed.ShouldBe((32L * 1024 * 1024) + 1);
        result.Error.Limits.Limit.ShouldBe(32L * 1024 * 1024);
        transport.WireAttempts.ShouldBe(9);
        result.Resources.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameWaveAggregateExhaustionWinsRegardlessOfCanonicalFailureOrder(
        bool canonicalFailureCompletesFirst)
    {
        using var transport = new AggregatePrecedenceTransport(canonicalFailureCompletesFirst);
        var calendar = Calendar(CalendarHref);

        var result = await new CalendarQueryResourceRetriever().RetrieveAsync(
            transport,
            transport.Hrefs.ToDictionary(href => href, _ => calendar),
            TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe(QueryFailureCode.LimitExhausted);
        result.Error.Limits!.Dimension.ShouldBe(QueryLimitDimension.ByteCount);
        result.Error.Limits.Observed.ShouldBe((32L * 1024 * 1024) + 1);
        result.Error.Limits.Limit.ShouldBe(32L * 1024 * 1024);
        result.Resources.ShouldBeEmpty();
        transport.WireAttempts.ShouldBe(canonicalFailureCompletesFirst ? 10 : 9);
    }

    [Fact]
    public async Task PreWireBrokenCircuitDoesNotInventAnHttpAttempt()
    {
        var transport = new FallbackTransport(
            [ResourceHref],
            get: (_, _) => Task.FromException<CalendarResourceRead>(new BrokenCircuitException()));
        await using var provider = CreateProvider(transport);
        using var listener = ListenToQueries();
        using var source = new ActivitySource(CalendarQueryTelemetry.InstrumentationName, "0.1.0");
        using var operation = source.StartActivity("caldav.operation", ActivityKind.Internal);

        var failure = (await QueryAsync(provider, TestContext.Current.CancellationToken))
            .ShouldBeOfType<QueryReply<CalendarEntityQueryItem>.Failure>();

        failure.Error.Code.ShouldBe(QueryFailureCode.UpstreamUnavailable);
        operation.ShouldNotBeNull().GetTagItem("caldav.query.direct_get_attempt_count").ShouldBe(0L);
        transport.GetCount.ShouldBe(1);
    }

    [Fact]
    public async Task ThreePhysicalFailuresReportedAsHttpFailureExhaustTheClosedAttemptBudget()
    {
        var transport = new FallbackTransport(
            [ResourceHref],
            get: (_, _) =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ResourceHref);
                CalendarHttpTelemetry.MarkQueryResourceRead(request);
                request.Options.TryGetValue(CalendarHttpTelemetry.DirectGetMeterKey, out var meter).ShouldBeTrue();
                meter.ShouldNotBeNull().TryBeginAttempt().ShouldBeTrue();
                meter.TryBeginAttempt().ShouldBeTrue();
                meter.TryBeginAttempt().ShouldBeTrue();
                return Task.FromException<CalendarResourceRead>(
                    new HttpRequestException("private", null, HttpStatusCode.ServiceUnavailable));
            });

        var result = await new CalendarQueryResourceRetriever().RetrieveAsync(
            transport,
            new Dictionary<string, CalendarDescriptor> { [ResourceHref] = Calendar(CalendarHref) },
            TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe(QueryFailureCode.LimitExhausted);
        result.Error.Limits!.Dimension.ShouldBe(QueryLimitDimension.AttemptCount);
        result.Error.Limits.Observed.ShouldBe(3);
        result.Error.Limits.Limit.ShouldBe(3);
        transport.GetCount.ShouldBe(1);
    }

    [Fact]
    public async Task PhysicalAttemptLimitSignaledByTheHandlerRetainsItsTypedDimension()
    {
        var transport = new FallbackTransport(
            [ResourceHref],
            get: (_, _) =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ResourceHref);
                CalendarHttpTelemetry.MarkQueryResourceRead(request);
                request.Options.TryGetValue(CalendarHttpTelemetry.DirectGetMeterKey, out var meter).ShouldBeTrue();
                meter.ShouldNotBeNull().TryBeginAttempt().ShouldBeTrue();
                meter.TryBeginAttempt().ShouldBeTrue();
                meter.TryBeginAttempt().ShouldBeTrue();
                meter.TryBeginAttempt().ShouldBeFalse();
                return Task.FromException<CalendarResourceRead>(new CalendarDirectGetAttemptLimitException());
            });

        var result = await new CalendarQueryResourceRetriever().RetrieveAsync(
            transport,
            new Dictionary<string, CalendarDescriptor> { [ResourceHref] = Calendar(CalendarHref) },
            TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe(QueryFailureCode.LimitExhausted);
        result.Error.Limits!.Dimension.ShouldBe(QueryLimitDimension.AttemptCount);
        result.Error.Limits.Observed.ShouldBe(3);
        result.Error.Limits.Limit.ShouldBe(3);
    }

    [Fact]
    public async Task ConsumedPerResourceByteBudgetWinsOverAConcurrentIoFailure()
    {
        var transport = new FallbackTransport(
            [ResourceHref],
            get: (_, _) =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ResourceHref);
                CalendarHttpTelemetry.MarkQueryResourceRead(request);
                request.Options.TryGetValue(CalendarHttpTelemetry.DirectGetMeterKey, out var meter).ShouldBeTrue();
                meter.ShouldNotBeNull().TryBeginAttempt().ShouldBeTrue();
                meter.ChargeBody((4 * 1024 * 1024) + 1);
                return Task.FromException<CalendarResourceRead>(new IOException("private"));
            });

        var result = await new CalendarQueryResourceRetriever().RetrieveAsync(
            transport,
            new Dictionary<string, CalendarDescriptor> { [ResourceHref] = Calendar(CalendarHref) },
            TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe(QueryFailureCode.PayloadTooLarge);
        result.Error.Limits!.ByteCount.ShouldBe((4 * 1024 * 1024) + 1);
    }

    [Theory]
    [InlineData(DirectFailureKind.UnsignaledCancellation, QueryFailureCode.UpstreamUnavailable)]
    [InlineData(DirectFailureKind.Forbidden, QueryFailureCode.UpstreamForbidden)]
    [InlineData(DirectFailureKind.Timeout, QueryFailureCode.UpstreamUnavailable)]
    [InlineData(DirectFailureKind.UnsupportedCapability, QueryFailureCode.UnsupportedCapability)]
    [InlineData(DirectFailureKind.Io, QueryFailureCode.UpstreamUnavailable)]
    [InlineData(DirectFailureKind.Xml, QueryFailureCode.UpstreamProtocolError)]
    [InlineData(DirectFailureKind.Protocol, QueryFailureCode.UpstreamProtocolError)]
    [InlineData(DirectFailureKind.UnmeteredBudgetException, QueryFailureCode.UpstreamProtocolError)]
    public async Task DirectFallbackMapsEveryControlledTransportFailure(
        DirectFailureKind failureKind,
        QueryFailureCode expectedCode)
    {
        var transport = new FallbackTransport(
            [ResourceHref],
            get: (_, _) => Task.FromException<CalendarResourceRead>(DirectFailure(failureKind)));

        var result = await new CalendarQueryResourceRetriever().RetrieveAsync(
            transport,
            new Dictionary<string, CalendarDescriptor> { [ResourceHref] = Calendar(CalendarHref) },
            TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe(expectedCode);
        result.Resources.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(MultigetFailureKind.Forbidden, QueryFailureCode.UpstreamForbidden)]
    [InlineData(MultigetFailureKind.Timeout, QueryFailureCode.UpstreamUnavailable)]
    [InlineData(MultigetFailureKind.AttemptTimeout, QueryFailureCode.UpstreamUnavailable)]
    [InlineData(MultigetFailureKind.BrokenCircuit, QueryFailureCode.UpstreamUnavailable)]
    [InlineData(MultigetFailureKind.Io, QueryFailureCode.UpstreamUnavailable)]
    [InlineData(MultigetFailureKind.UnsupportedCapability, QueryFailureCode.UnsupportedCapability)]
    [InlineData(MultigetFailureKind.Xml, QueryFailureCode.UpstreamProtocolError)]
    [InlineData(MultigetFailureKind.Protocol, QueryFailureCode.UpstreamProtocolError)]
    public async Task MultigetPlanningMapsEveryControlledTransportFailure(
        MultigetFailureKind failureKind,
        QueryFailureCode expectedCode)
    {
        var transport = new FallbackTransport(
            [ResourceHref],
            _ => throw MultigetFailure(failureKind));

        var result = await new CalendarQueryResourceRetriever().RetrieveAsync(
            transport,
            new Dictionary<string, CalendarDescriptor> { [ResourceHref] = Calendar(CalendarHref) },
            TestContext.Current.CancellationToken);

        result.Error!.Code.ShouldBe(expectedCode);
        result.Resources.ShouldBeEmpty();
        transport.GetCount.ShouldBe(0);
    }

    private static Task<QueryReply<CalendarEntityQueryItem>> QueryAsync(
        ServiceProvider provider,
        CancellationToken cancellationToken) =>
        provider.GetRequiredService<ICalendarQueryModule>().QueryEntitiesAsync(
            new CalendarEntityQueryRequest.Start(new CalendarEntityQuery(
                CalendarEntityScope.All,
                [CalendarEntityKind.Event])),
            cancellationToken);

    private static CalendarResourceRead Success(string resourceHref) => CalendarResourceRead.Success(
        resourceHref,
        "\"r1\"",
        Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Fallback tests//EN\r\n"
            + $"BEGIN:VEVENT\r\nUID:{Uri.EscapeDataString(resourceHref)}\r\nDTSTAMP:20260823T120000Z\r\n"
            + "DTSTART:20260824T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"));

    private static CalendarDescriptor Calendar(string href) => new()
    {
        Href = href,
        DisplayName = "Work",
        DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
        EventSupport = EntityKindSupport.Advertised,
        TodoSupport = EntityKindSupport.NotAdvertised
    };

    private static Exception DirectFailure(DirectFailureKind kind) => kind switch
    {
        DirectFailureKind.UnsignaledCancellation => new OperationCanceledException(),
        DirectFailureKind.Forbidden => new HttpRequestException("private", null, HttpStatusCode.Forbidden),
        DirectFailureKind.Timeout => new TimeoutException("private"),
        DirectFailureKind.UnsupportedCapability => new CalendarDiscoveryUnsupportedCapabilityException("private"),
        DirectFailureKind.Io => new IOException("private"),
        DirectFailureKind.Xml => new System.Xml.XmlException("private"),
        DirectFailureKind.Protocol => new CalendarDiscoveryProtocolException("private"),
        DirectFailureKind.UnmeteredBudgetException => new CalendarDirectGetBudgetExceededException(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static Exception MultigetFailure(MultigetFailureKind kind) => kind switch
    {
        MultigetFailureKind.Forbidden => new HttpRequestException("private", null, HttpStatusCode.Forbidden),
        MultigetFailureKind.Timeout => new TimeoutException("private"),
        MultigetFailureKind.AttemptTimeout => new Polly.Timeout.TimeoutRejectedException("private"),
        MultigetFailureKind.BrokenCircuit => new BrokenCircuitException("private"),
        MultigetFailureKind.Io => new IOException("private"),
        MultigetFailureKind.UnsupportedCapability => new CalendarDiscoveryUnsupportedCapabilityException("private"),
        MultigetFailureKind.Xml => new System.Xml.XmlException("private"),
        MultigetFailureKind.Protocol => new CalendarDiscoveryProtocolException("private"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static ServiceProvider CreateProvider(ICalendarQueryTransport transport)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavCalendars(options =>
        {
            options.BaseUrl = "https://cal.example";
            options.Username = "user";
            options.Password = "password";
        });
        services.AddSingleton(Substitute.For<ICalendarClient>());
        services.AddSingleton(transport);
        return services.BuildServiceProvider();
    }

    private static ActivityListener ListenToQueries()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarQueryTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class FallbackTransport(
        IReadOnlyList<string> hrefs,
        Func<IReadOnlyList<string>, CalendarMultigetResult>? multiget = null,
        Func<string, CancellationToken, Task<CalendarResourceRead>>? get = null) : ICalendarQueryTransport
    {
        private int _getCount;
        private int _inFlight;
        private int _maximumInFlight;
        internal int MultigetCount { get; private set; }
        internal int GetCount => Volatile.Read(ref _getCount);
        internal int MaximumInFlight => Volatile.Read(ref _maximumInFlight);
        internal int InFlight => Volatile.Read(ref _inFlight);
        internal List<string> RequestedHrefs { get; } = [];

        public Task<CalendarQueryDiscovery> DiscoverAsync(CancellationToken cancellationToken) => Task.FromResult(
            new CalendarQueryDiscovery(
                new CalendarDiscoveryResult([new CalendarDescriptor
                {
                    Href = CalendarHref,
                    DisplayName = "Work",
                    DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                    EventSupport = EntityKindSupport.Advertised,
                    TodoSupport = EntityKindSupport.NotAdvertised
                }], []),
                CalendarSelectionResult.Success(new CalendarDescriptor
                {
                    Href = CalendarHref,
                    DisplayName = "Work",
                    DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                    EventSupport = EntityKindSupport.Advertised,
                    TodoSupport = EntityKindSupport.NotAdvertised
                }),
                CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound)));

        public Task<IReadOnlyList<string>> QueryCandidateHrefsAsync(
            string calendarHref,
            CalendarEntityKind entityKind,
            DateTimeOffset? from,
            DateTimeOffset? to,
            CancellationToken cancellationToken) => Task.FromResult(hrefs);

        public Task<CalendarMultigetResult> MultigetAsync(
            string calendarHref,
            IReadOnlyList<string> resourceHrefs,
            CancellationToken cancellationToken)
        {
            MultigetCount++;
            CalendarQueryTelemetry.ObserveMultigetAttempt(resourceHrefs.Count);
            return Task.FromResult(multiget?.Invoke(resourceHrefs) ?? new CalendarMultigetResult.VerifiedUnavailable());
        }

        public async Task<CalendarResourceRead> GetAsync(
            string calendarHref,
            string resourceHref,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _getCount);
            lock (RequestedHrefs)
                RequestedHrefs.Add(resourceHref);
            var inFlight = Interlocked.Increment(ref _inFlight);
            InterlockedExtensions.Max(ref _maximumInFlight, inFlight);
            try
            {
                return get is null
                    ? Success(resourceHref)
                    : await get(resourceHref, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    private sealed class TimeoutFallbackTransport(HttpClient client) : ICalendarQueryTransport
    {
        public Task<CalendarQueryDiscovery> DiscoverAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> QueryCandidateHrefsAsync(
            string calendarHref,
            CalendarEntityKind entityKind,
            DateTimeOffset? from,
            DateTimeOffset? to,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CalendarMultigetResult> MultigetAsync(
            string calendarHref,
            IReadOnlyList<string> resourceHrefs,
            CancellationToken cancellationToken)
        {
            CalendarQueryTelemetry.ObserveMultigetAttempt(resourceHrefs.Count);
            return Task.FromResult<CalendarMultigetResult>(new CalendarMultigetResult.VerifiedUnavailable());
        }

        public async Task<CalendarResourceRead> GetAsync(
            string calendarHref,
            string resourceHref,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, resourceHref);
            CalendarHttpTelemetry.MarkQueryResourceRead(request);
            using var response = await client.SendAsync(request, cancellationToken);
            throw new InvalidOperationException("The timeout pipeline unexpectedly returned a response.");
        }
    }

    private sealed class CancelledAttemptHandler : HttpMessageHandler
    {
        private int _attempts;
        internal int Attempts => Volatile.Read(ref _attempts);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The attempt timeout did not cancel the wire operation.");
        }
    }

    private sealed class AggregateCrossingTransport : ICalendarQueryTransport, IDisposable
    {
        private readonly HttpMessageInvoker _invoker;
        private int _wireAttempts;

        internal AggregateCrossingTransport(IReadOnlyList<string> hrefs)
        {
            Hrefs = hrefs;
            _invoker = new HttpMessageInvoker(new CalendarHttpAttemptHandler
            {
                InnerHandler = new AggregateCrossingHandler(this)
            });
        }

        private IReadOnlyList<string> Hrefs { get; }
        internal int WireAttempts => Volatile.Read(ref _wireAttempts);

        public Task<CalendarQueryDiscovery> DiscoverAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> QueryCandidateHrefsAsync(
            string calendarHref,
            CalendarEntityKind entityKind,
            DateTimeOffset? from,
            DateTimeOffset? to,
            CancellationToken cancellationToken) => Task.FromResult(Hrefs);

        public Task<CalendarMultigetResult> MultigetAsync(
            string calendarHref,
            IReadOnlyList<string> resourceHrefs,
            CancellationToken cancellationToken) => Task.FromResult<CalendarMultigetResult>(
                new CalendarMultigetResult.VerifiedUnavailable());

        public async Task<CalendarResourceRead> GetAsync(
            string calendarHref,
            string resourceHref,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, resourceHref);
            CalendarHttpTelemetry.MarkQueryResourceRead(request);
            using var response = await _invoker.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return Success(resourceHref);
        }

        public void Dispose() => _invoker.Dispose();

        private sealed class AggregateCrossingHandler(AggregateCrossingTransport owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var attempt = Interlocked.Increment(ref owner._wireAttempts);
                return Task.FromResult(new HttpResponseMessage(
                    attempt <= 8 ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
                {
                    Content = new ByteArrayContent(new byte[attempt <= 8 ? 4 * 1024 * 1024 : 1]),
                    RequestMessage = request
                });
            }
        }
    }

    private sealed class AggregatePrecedenceTransport : ICalendarQueryTransport, IDisposable
    {
        private readonly TaskCompletionSource _canonicalCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _canonicalFailureCompletesFirst;
        private readonly HttpMessageInvoker _invoker;
        private readonly TaskCompletionSource _overflowCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _wireAttempts;

        internal AggregatePrecedenceTransport(bool canonicalFailureCompletesFirst)
        {
            _canonicalFailureCompletesFirst = canonicalFailureCompletesFirst;
            Hrefs = Enumerable.Range(0, 8)
                .Select(index => $"{CalendarHref}{index:D2}.ics")
                .Append(CalendarHref + "a.ics")
                .Append(CalendarHref + "b.ics")
                .ToArray();
            _invoker = new HttpMessageInvoker(new CalendarHttpAttemptHandler
            {
                InnerHandler = new AggregatePrecedenceHandler(this)
            });
        }

        internal IReadOnlyList<string> Hrefs { get; }

        internal int WireAttempts => Volatile.Read(ref _wireAttempts);

        public Task<CalendarQueryDiscovery> DiscoverAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> QueryCandidateHrefsAsync(
            string calendarHref,
            CalendarEntityKind entityKind,
            DateTimeOffset? from,
            DateTimeOffset? to,
            CancellationToken cancellationToken) => Task.FromResult(Hrefs);

        public Task<CalendarMultigetResult> MultigetAsync(
            string calendarHref,
            IReadOnlyList<string> resourceHrefs,
            CancellationToken cancellationToken) => Task.FromResult<CalendarMultigetResult>(
                new CalendarMultigetResult.VerifiedUnavailable());

        public async Task<CalendarResourceRead> GetAsync(
            string calendarHref,
            string resourceHref,
            CancellationToken cancellationToken)
        {
            if (resourceHref.EndsWith("a.ics", StringComparison.Ordinal)
                && !_canonicalFailureCompletesFirst)
            {
                await _overflowCompleted.Task.WaitAsync(cancellationToken);
            }
            else if (resourceHref.EndsWith("b.ics", StringComparison.Ordinal)
                     && _canonicalFailureCompletesFirst)
            {
                await _canonicalCompleted.Task.WaitAsync(cancellationToken);
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, resourceHref);
                CalendarHttpTelemetry.MarkQueryResourceRead(request);
                using var response = await _invoker.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();
                return Success(resourceHref);
            }
            finally
            {
                if (resourceHref.EndsWith("a.ics", StringComparison.Ordinal))
                    _canonicalCompleted.TrySetResult();
                else if (resourceHref.EndsWith("b.ics", StringComparison.Ordinal))
                    _overflowCompleted.TrySetResult();
            }
        }

        public void Dispose() => _invoker.Dispose();

        private sealed class AggregatePrecedenceHandler(AggregatePrecedenceTransport owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._wireAttempts);
                var href = request.RequestUri!.AbsoluteUri;
                var status = href.EndsWith("a.ics", StringComparison.Ordinal)
                    ? HttpStatusCode.Forbidden
                    : HttpStatusCode.OK;
                var bytes = href.EndsWith("00.ics", StringComparison.Ordinal)
                    ? (4 * 1024 * 1024) - 3
                    : href.EndsWith("b.ics", StringComparison.Ordinal)
                        ? 4
                        : href.EndsWith("a.ics", StringComparison.Ordinal) ? 0 : 4 * 1024 * 1024;
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new ByteArrayContent(new byte[bytes]),
                    RequestMessage = request
                });
            }
        }
    }

    private static class InterlockedExtensions
    {
        internal static void Max(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }

    public enum DirectFailureKind
    {
        UnsignaledCancellation,
        Forbidden,
        Timeout,
        UnsupportedCapability,
        Io,
        Xml,
        Protocol,
        UnmeteredBudgetException
    }

    public enum MultigetFailureKind
    {
        Forbidden,
        Timeout,
        AttemptTimeout,
        BrokenCircuit,
        Io,
        UnsupportedCapability,
        Xml,
        Protocol
    }
}
