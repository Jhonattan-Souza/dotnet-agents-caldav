using System.Collections.Immutable;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarTodoQueryPageCodecTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ActualAccountantAdmitsExactlyFourMiBAndRejectsOneByteMore()
    {
        var codec = Codec();
        var initial = Snapshot(Json("x"));
        var initialPlan = codec.Plan(initial, 0, 1, CancellationToken.None).Value!;
        var padding = CalendarTodoQueryPageCodec.MaximumCallToolResultBytes
            - initialPlan.MeasuredCallToolResultBytes;
        var exact = Snapshot(Json(new string('x', padding + 1)));

        var admitted = codec.Plan(exact, 0, 1, CancellationToken.None);

        admitted.Error.ShouldBeNull();
        admitted.Value!.MeasuredCallToolResultBytes.ShouldBe(
            CalendarTodoQueryPageCodec.MaximumCallToolResultBytes);
        var page = CalendarTodoQueryPageCodec.Materialize(exact, admitted.Value);
        page.StructuredContent.GetProperty("excludedIndeterminateCount").GetInt32().ShouldBe(7);
        page.TemporalEvaluationContext.ShouldBe(new TemporalEvaluationContext(
            "America/Sao_Paulo",
            TemporalEvaluationContextSource.Caller));
        codec.Plan(
                Snapshot(Json(new string('x', padding + 2))),
                0,
                1,
                CancellationToken.None)
            .Error!.Code.ShouldBe(QueryFailureCode.PayloadTooLarge);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(200)]
    public void RepresentativePageSizesAdmitExactlyTheRequestedItems(int pageSize)
    {
        var items = Enumerable.Range(0, pageSize + 1)
            .Select(index => new StoredCalendarEntityQueryItem(Json(index)))
            .ToImmutableArray();
        var snapshot = Snapshot(items);

        var plan = Codec().Plan(snapshot, 0, pageSize, CancellationToken.None);

        plan.Error.ShouldBeNull();
        plan.Value!.Items.Count.ShouldBe(pageSize);
        plan.Value.NextCursor.ShouldNotBeNull();
        CalendarTodoQueryPageCodec.Materialize(snapshot, plan.Value).Items.Count.ShouldBe(pageSize);
    }

    private static CalendarTodoQueryPageCodec Codec()
    {
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = "user",
            Password = "password"
        });
        var key = new CalendarQueryCursorKey(
            options,
            Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        return new CalendarTodoQueryPageCodec(new CalendarQueryCursorIssuer(key));
    }

    private static CalendarQuerySnapshot Snapshot(byte[] json) => Snapshot(
        [new StoredCalendarEntityQueryItem(json)]);

    private static CalendarQuerySnapshot Snapshot(
        ImmutableArray<StoredCalendarEntityQueryItem> items)
    {
        var temporalContext = CalendarTemporalEvaluationContextCodec.Encode(new TemporalEvaluationContext(
            "America/Sao_Paulo",
            TemporalEvaluationContextSource.Caller));
        return new CalendarQuerySnapshot(
            Guid.NewGuid(),
            Now.AddMinutes(10),
            items,
            "[]"u8.ToArray(),
            items.Sum(item => item.JsonByteCount) + 2 + temporalContext.Length + 1,
            temporalContext,
            "7"u8.ToArray());
    }

    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);
}
