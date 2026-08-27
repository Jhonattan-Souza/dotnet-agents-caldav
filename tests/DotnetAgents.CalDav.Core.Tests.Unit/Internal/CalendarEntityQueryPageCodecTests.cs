using System.Collections.Immutable;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

[Collection("ActivityListener")]
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
        var admission = Admission();
        var initialPlan = admission.Plan(initial, 0, 1, codec, CancellationToken.None).Value!;
        var padding = CalendarEntityQueryPageCodec.MaximumCallToolResultBytes
            - initialPlan.MeasuredCallToolResultBytes;
        var exact = Snapshot(Json(new string('x', padding + 1)), temporalContext);

        var admitted = admission.Plan(exact, 0, 1, codec, CancellationToken.None);
        admitted.Error.ShouldBeNull();
        admitted.Value!.MeasuredCallToolResultBytes.ShouldBe(
            CalendarEntityQueryPageCodec.MaximumCallToolResultBytes);
        var page = codec.Materialize(exact, admitted.Value);
        page.TemporalEvaluationContext.ShouldBe(new TemporalEvaluationContext(
            "America/Sao_Paulo",
            TemporalEvaluationContextSource.Caller));
        var above = Snapshot(Json(new string('x', padding + 2)), temporalContext);
        var refused = admission.Plan(above, 0, 1, codec, CancellationToken.None);
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
        var planned = Admission().Plan(snapshot, 0, pageSize, codec, CancellationToken.None);

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

        CalendarQueryTelemetry.StartPhase(CalendarQueryPhase.Discovery).ShouldBeNull();
        stopped.ShouldBeEmpty();
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

        var codec = Codec();
        var planned = Admission().Plan(snapshot, 0, 1, codec, CancellationToken.None);

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
        var plan = Admission().Plan(snapshot, 0, 1, codec, CancellationToken.None).Value!;

        var page = codec.Materialize(snapshot, plan);

        page.Diagnostics.ShouldBeEmpty();
        page.NextCursor.ShouldNotBeNull();
        page.StructuredContent.GetProperty("pagination").GetProperty("nextCursor").GetString()
            .ShouldBe(page.NextCursor);
    }

    private static CalendarQueryPageAdmission Admission()
    {
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = "user",
            Password = "password"
        });
        var key = new CalendarQueryCursorKey(options, Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        return new CalendarQueryPageAdmission(new CalendarQueryCursorIssuer(key));
    }

    private static CalendarEntityQueryPageCodec Codec(CalendarQueryPageWorkCounter? workCounter = null) =>
        new(workCounter);

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
