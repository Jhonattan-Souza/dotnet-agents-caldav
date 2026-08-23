using System.Diagnostics;
using DotnetAgents.CalDav.Core.Internal;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

[Collection("ActivityListener")]
public sealed class CalendarQueryTelemetryTests
{
    [Theory]
    [InlineData("discovery")]
    [InlineData("candidate")]
    [InlineData("fetch")]
    [InlineData("evaluation")]
    [InlineData("serialization")]
    [InlineData("reservation")]
    [InlineData("snapshot_lookup")]
    [InlineData("page_admission")]
    public void StartPhaseCreatesOnlyClosedDirectOperationChildren(string phase)
    {
        using var listener = Listen();
        using var source = new ActivitySource(CalendarQueryTelemetry.InstrumentationName, "0.1.0");
        using var operation = source.StartActivity("caldav.operation");
        operation.ShouldNotBeNull();

        using var activity = CalendarQueryTelemetry.StartPhase(phase);

        activity.ShouldNotBeNull();
        activity.ParentId.ShouldBe(operation.Id);
        activity.GetTagItem("caldav.query.phase").ShouldBe(phase);
    }

    [Fact]
    public void StartPhaseRejectsUnknownValueAndDoesNotEmitWithoutOperation()
    {
        using var listener = Listen();

        CalendarQueryTelemetry.StartPhase("discovery").ShouldBeNull();
        Should.Throw<ArgumentOutOfRangeException>(() => CalendarQueryTelemetry.StartPhase("private"));
    }

    [Fact]
    public void BeginAndCountersRequireRecordedOperationAndIgnoreNonPositiveWork()
    {
        CalendarQueryTelemetry.Begin(false);
        CalendarQueryTelemetry.Add("caldav.query.candidate_count", 1);
        CalendarQueryTelemetry.ObserveMultigetAttempt(1);
        CalendarQueryTelemetry.ObserveMultigetSuccess();
        using var listener = Listen();
        using var source = new ActivitySource(CalendarQueryTelemetry.InstrumentationName, "0.1.0");
        using var operation = source.StartActivity("caldav.operation");
        operation.ShouldNotBeNull();

        CalendarQueryTelemetry.Begin(false);
        CalendarQueryTelemetry.Add("caldav.query.candidate_count", 0);
        CalendarQueryTelemetry.Add("caldav.query.candidate_count", -1);
        CalendarQueryTelemetry.Add("caldav.query.candidate_count", 2);
        CalendarQueryTelemetry.Add("caldav.query.candidate_count", 3);
        CalendarQueryTelemetry.ObserveMultigetAttempt(4);
        CalendarQueryTelemetry.ObserveMultigetSuccess();

        operation.GetTagItem("caldav.query.mode").ShouldBe("start");
        operation.GetTagItem("caldav.query.candidate_count").ShouldBe(5L);
        operation.GetTagItem("caldav.query.fetch_mode").ShouldBe("multiget");
        operation.GetTagItem("caldav.query.multiget_resource_count").ShouldBe(4L);
    }

    [Fact]
    public void ContinueInitializesOnlyLookupAndAdmissionCounters()
    {
        using var listener = Listen();
        using var source = new ActivitySource(CalendarQueryTelemetry.InstrumentationName, "0.1.0");
        using var operation = source.StartActivity("caldav.operation");
        operation.ShouldNotBeNull();

        CalendarQueryTelemetry.Begin(true);

        operation.GetTagItem("caldav.query.mode").ShouldBe("continue");
        operation.GetTagItem("caldav.query.snapshot_lookup_count").ShouldBe(0L);
        operation.GetTagItem("caldav.query.page_admission_count").ShouldBe(0L);
        operation.GetTagItem("caldav.query.candidate_count").ShouldBeNull();
        operation.GetTagItem("caldav.query.fetch_mode").ShouldBeNull();
    }

    [Fact]
    public async Task ConcurrentFallbackWorkPreservesMixedModeAndExactClosedCounters()
    {
        const int workerCount = 128;
        using var listener = Listen();
        using var source = new ActivitySource(CalendarQueryTelemetry.InstrumentationName, "0.1.0");
        using var operation = source.StartActivity("caldav.operation");
        operation.ShouldNotBeNull();
        CalendarQueryTelemetry.Begin(false);
        CalendarQueryTelemetry.ObserveMultigetSuccess();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var workers = Enumerable.Range(0, workerCount).Select(async _ =>
        {
            await release.Task;
            CalendarQueryTelemetry.ObserveDirectGetFallback();
            CalendarQueryTelemetry.Add("caldav.query.direct_get_resource_count");
            CalendarQueryTelemetry.Add("caldav.query.direct_get_attempt_count");
            CalendarQueryTelemetry.Add("caldav.query.disappeared_resource_count");
        }).ToArray();
        release.SetResult();
        await Task.WhenAll(workers);

        operation.GetTagItem("caldav.query.fetch_mode").ShouldBe("mixed");
        operation.GetTagItem("caldav.query.fallback_reason").ShouldBe("multiget_unavailable");
        operation.GetTagItem("caldav.query.direct_get_resource_count").ShouldBe((long)workerCount);
        operation.GetTagItem("caldav.query.direct_get_attempt_count").ShouldBe((long)workerCount);
        operation.GetTagItem("caldav.query.disappeared_resource_count").ShouldBe((long)workerCount);
    }

    [Fact]
    public void FetchModeTransitionRemainsMixedWhenDirectWorkPrecedesMultigetObservation()
    {
        using var listener = Listen();
        using var source = new ActivitySource(CalendarQueryTelemetry.InstrumentationName, "0.1.0");
        using var operation = source.StartActivity("caldav.operation");
        operation.ShouldNotBeNull();
        CalendarQueryTelemetry.Begin(false);

        CalendarQueryTelemetry.ObserveDirectGetFallback();
        CalendarQueryTelemetry.ObserveMultigetSuccess();
        CalendarQueryTelemetry.ObserveMultigetSuccess();

        operation.GetTagItem("caldav.query.fetch_mode").ShouldBe("mixed");
        operation.GetTagItem("caldav.query.fallback_reason").ShouldBe("multiget_unavailable");
    }

    [Fact]
    public void NonRecordingOperationDoesNotReceiveQueryTags()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarQueryTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.PropagationData
        };
        ActivitySource.AddActivityListener(listener);
        using var source = new ActivitySource(CalendarQueryTelemetry.InstrumentationName, "0.1.0");
        using var operation = source.StartActivity("caldav.operation");
        operation.ShouldNotBeNull();

        CalendarQueryTelemetry.Begin(false);
        CalendarQueryTelemetry.Add("caldav.query.candidate_count", 1);
        CalendarQueryTelemetry.ObserveMultigetAttempt(2);
        CalendarQueryTelemetry.ObserveMultigetSuccess();
        using var phase = CalendarQueryTelemetry.StartPhase("candidate");

        phase.ShouldNotBeNull();
        phase.IsAllDataRequested.ShouldBeFalse();
        phase.GetTagItem("caldav.query.phase").ShouldBeNull();
        operation.GetTagItem("caldav.query.mode").ShouldBeNull();
        operation.GetTagItem("caldav.query.candidate_count").ShouldBeNull();
        operation.GetTagItem("caldav.query.fetch_mode").ShouldBeNull();
    }

    private static ActivityListener Listen()
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarQueryTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
