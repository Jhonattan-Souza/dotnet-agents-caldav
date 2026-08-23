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
        var initial = Snapshot(Json("x"));
        var initialPlan = codec.Plan(initial, 0, 1, CancellationToken.None).Value!;
        var padding = CalendarEntityQueryPageCodec.MaximumCallToolResultBytes
            - initialPlan.MeasuredCallToolResultBytes;
        var exact = Snapshot(Json(new string('x', padding + 1)));

        var admitted = codec.Plan(exact, 0, 1, CancellationToken.None);
        admitted.Error.ShouldBeNull();
        admitted.Value!.MeasuredCallToolResultBytes.ShouldBe(
            CalendarEntityQueryPageCodec.MaximumCallToolResultBytes);
        var above = Snapshot(Json(new string('x', padding + 2)));
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

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);
}
