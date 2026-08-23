using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarTemporalEvaluationContextCodecTests
{
    [Fact]
    public void EncodedContextIsTheExactRetainedAndWireRepresentation()
    {
        var context = new TemporalEvaluationContext(
            "America/Sao_Paulo",
            TemporalEvaluationContextSource.Caller);

        var encoded = CalendarTemporalEvaluationContextCodec.Encode(context);

        System.Text.Encoding.UTF8.GetString(encoded.Span)
            .ShouldBe("{\"timeZone\":\"America/Sao_Paulo\",\"source\":\"caller\"}");
        CalendarTemporalEvaluationContextCodec.Decode(encoded).ShouldBe(context);
    }

    [Fact]
    public void ContextBytesParticipateInTheExactSnapshotCapacityBoundary()
    {
        var encoded = CalendarTemporalEvaluationContextCodec.Encode(new TemporalEvaluationContext(
            "America/Sao_Paulo",
            TemporalEvaluationContextSource.Configuration));
        var otherRetainedBytes = CalendarQuerySnapshotPolicy.MaximumBytes - encoded.Length;

        CalendarQuerySnapshotPolicy.Validate(1, otherRetainedBytes + encoded.Length).ShouldBeNull();
        CalendarQuerySnapshotPolicy.Validate(1, otherRetainedBytes + encoded.Length + 1)!.Code
            .ShouldBe(QueryFailureCode.LimitExhausted);
    }
}
