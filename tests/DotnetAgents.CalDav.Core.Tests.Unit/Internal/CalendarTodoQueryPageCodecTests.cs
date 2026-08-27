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
        var admission = Admission();
        var initialPlan = admission.Plan(initial, 0, 1, codec, CancellationToken.None).Value!;
        var padding = CalendarTodoQueryPageCodec.MaximumCallToolResultBytes
            - initialPlan.MeasuredCallToolResultBytes;
        var exact = Snapshot(Json(new string('x', padding + 1)));

        var admitted = admission.Plan(exact, 0, 1, codec, CancellationToken.None);

        admitted.Error.ShouldBeNull();
        admitted.Value!.MeasuredCallToolResultBytes.ShouldBe(
            CalendarTodoQueryPageCodec.MaximumCallToolResultBytes);
        var page = CalendarTodoQueryPageCodec.Materialize(exact, admitted.Value);
        page.StructuredContent.GetProperty("excludedIndeterminateCount").GetInt32().ShouldBe(7);
        page.TemporalEvaluationContext.ShouldBe(new TemporalEvaluationContext(
            "America/Sao_Paulo",
            TemporalEvaluationContextSource.Caller));
        admission.Plan(
                Snapshot(Json(new string('x', padding + 2))),
                0,
                1,
                codec,
                CancellationToken.None)
            .Error!.Code.ShouldBe(QueryFailureCode.PayloadTooLarge);
    }

    private static CalendarQueryPageAdmission Admission()
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
        return new CalendarQueryPageAdmission(new CalendarQueryCursorIssuer(key));
    }

    private static CalendarTodoQueryPageCodec Codec() => new();

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
