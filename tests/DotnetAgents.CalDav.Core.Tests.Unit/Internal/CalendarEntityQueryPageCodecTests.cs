using System.Collections.Immutable;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarEntityQueryPageCodecTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ActualAccountantAdmitsExactlyFourMiBAndRejectsOneByteMore()
    {
        var codec = Codec();
        var temporalContext = CalendarTemporalEvaluationContextCodec.Encode(new TemporalEvaluationContext(
            "America/Sao_Paulo",
            TemporalEvaluationContextSource.Caller));
        var initial = Snapshot(Json("x"), temporalContext);
        var initialPlan = codec.Plan(initial, 0, 1, CancellationToken.None).Value!;
        var padding = CalendarEntityQueryPageCodec.MaximumCallToolResultBytes
            - initialPlan.MeasuredCallToolResultBytes;
        var exact = Snapshot(Json(new string('x', padding + 1)), temporalContext);

        var admitted = codec.Plan(exact, 0, 1, CancellationToken.None);
        admitted.Error.ShouldBeNull();
        admitted.Value!.MeasuredCallToolResultBytes.ShouldBe(
            CalendarEntityQueryPageCodec.MaximumCallToolResultBytes);
        var page = codec.Materialize(exact, admitted.Value);
        page.TemporalEvaluationContext.ShouldBe(new TemporalEvaluationContext(
            "America/Sao_Paulo",
            TemporalEvaluationContextSource.Caller));
        var above = Snapshot(Json(new string('x', padding + 2)), temporalContext);
        var refused = codec.Plan(above, 0, 1, CancellationToken.None);
        refused.Error!.Code.ShouldBe(QueryFailureCode.PayloadTooLarge);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(200)]
    public void PagePlanningSerializesTheFixedEnvelopeOnce(int pageSize)
    {
        var work = new CalendarQueryPageWorkCounter();
        var items = Enumerable.Range(0, pageSize + 1)
            .Select(index => new StoredCalendarEntityQueryItem(Json(index)))
            .ToImmutableArray();
        var snapshot = new CalendarQuerySnapshot(
            Guid.NewGuid(),
            Now.AddMinutes(10),
            items,
            "[]"u8.ToArray(),
            items.Sum(item => item.JsonByteCount) + 2);
        var codec = Codec(work);
        var planned = codec.Plan(snapshot, 0, pageSize, CancellationToken.None);

        planned.Error.ShouldBeNull();
        work.AdmissionEnvelopeSerializationCount.ShouldBe(1);
        planned.Value!.Items.Count.ShouldBe(pageSize);
        codec.Materialize(snapshot, planned.Value).Items.Count.ShouldBe(pageSize);
        work.FinalMaterializationCount.ShouldBe(1);
    }

    [Fact]
    public void QueryPhaseIsNeverEmittedWithoutAnOperationAncestor()
    {
        var stopped = new List<System.Diagnostics.Activity>();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarQueryTelemetry.InstrumentationName,
            Sample = static (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        CalendarQueryTelemetry.StartPhase("discovery").ShouldBeNull();
        stopped.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0, -1, 1)]
    [InlineData(0, 1, 1)]
    [InlineData(1, -1, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(1, 0, 0)]
    [InlineData(1, 0, 201)]
    public void PlanRejectsEveryInvalidPositionAndPageSize(int itemCount, int position, int pageSize)
    {
        var items = Enumerable.Range(0, itemCount)
            .Select(index => new StoredCalendarEntityQueryItem(Json(index)))
            .ToImmutableArray();
        var snapshot = new CalendarQuerySnapshot(Guid.NewGuid(), Now.AddMinutes(10), items, "[]"u8.ToArray(), 0);

        var planned = Codec().Plan(snapshot, position, pageSize, CancellationToken.None);

        planned.Error!.Code.ShouldBe(QueryFailureCode.InvalidInput);
    }

    [Fact]
    public void AdmitReturnsBothPageAndFailureBranches()
    {
        var codec = Codec();
        codec.Admit(Snapshot(Json(1)), 0, 1, CancellationToken.None).Value.ShouldNotBeNull();
        codec.Admit(Snapshot(Json(1)), 1, 1, CancellationToken.None).Error!.Code
            .ShouldBe(QueryFailureCode.InvalidInput);
    }

    [Fact]
    public void HumanPresentationBudgetRejectsOversizedDiagnosticsBeforeItemAdmission()
    {
        var diagnostics = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new QueryDiagnostic("safe", new string('x', CalendarEntityQueryPageCodec.MaximumHumanReadableBytes), "warning")
        });
        var snapshot = new CalendarQuerySnapshot(
            Guid.NewGuid(),
            Now.AddMinutes(10),
            ImmutableArray<StoredCalendarEntityQueryItem>.Empty,
            diagnostics,
            diagnostics.Length);

        var planned = Codec().Plan(snapshot, 0, 1, CancellationToken.None);

        planned.Error!.Code.ShouldBe(QueryFailureCode.PayloadTooLarge);
    }

    [Fact]
    public void MaterializeHandlesNullDiagnosticsAndNonNullCursorMechanically()
    {
        var item = new StoredCalendarEntityQueryItem(Json(1));
        var snapshot = new CalendarQuerySnapshot(
            Guid.NewGuid(),
            Now.AddMinutes(10),
            [item, item],
            "null"u8.ToArray(),
            item.JsonByteCount * 2 + 4);
        var codec = Codec();
        var plan = codec.Plan(snapshot, 0, 1, CancellationToken.None).Value!;

        var page = codec.Materialize(snapshot, plan);

        page.Diagnostics.ShouldBeEmpty();
        page.NextCursor.ShouldNotBeNull();
        page.StructuredContent.GetProperty("pagination").GetProperty("nextCursor").GetString()
            .ShouldBe(page.NextCursor);
    }

    private static CalendarEntityQueryPageCodec Codec(CalendarQueryPageWorkCounter? workCounter = null)
    {
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = "user",
            Password = "password"
        });
        var key = new CalendarQueryCursorKey(options, Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        return new CalendarEntityQueryPageCodec(new CalendarQueryCursorIssuer(key), workCounter);
    }

    private static CalendarQuerySnapshot Snapshot(byte[] json) => new(
        Guid.NewGuid(),
        Now.AddMinutes(10),
        [new StoredCalendarEntityQueryItem(json)],
        "[]"u8.ToArray(),
        json.Length + 2);

    private static CalendarQuerySnapshot Snapshot(byte[] json, ReadOnlyMemory<byte> temporalContext) => new(
        Guid.NewGuid(),
        Now.AddMinutes(10),
        [new StoredCalendarEntityQueryItem(json)],
        "[]"u8.ToArray(),
        json.Length + 2 + temporalContext.Length,
        temporalContext);

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);
}
