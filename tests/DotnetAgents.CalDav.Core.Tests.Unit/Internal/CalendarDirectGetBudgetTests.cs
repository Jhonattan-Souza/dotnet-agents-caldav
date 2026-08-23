using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Internal;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarDirectGetBudgetTests
{
    [Theory]
    [InlineData((4 * 1024 * 1024) - 1, null)]
    [InlineData(4 * 1024 * 1024, null)]
    [InlineData((4 * 1024 * 1024) + 1, QueryFailureCode.PayloadTooLarge)]
    public void FinalResponseHonorsTheExactPerResourceBoundary(int bodyBytes, QueryFailureCode? expected)
    {
        var meter = new CalendarDirectGetBudget().StartResource();
        meter.TryBeginAttempt().ShouldBeTrue();

        meter.ChargeBody(bodyBytes);

        if (expected is null)
            meter.Failure.ShouldBeNull();
        else
            meter.Failure!.Code.ShouldBe(expected.Value);
    }

    [Theory]
    [InlineData((4 * 1024 * 1024) - 1, null)]
    [InlineData(4 * 1024 * 1024, null)]
    [InlineData((4 * 1024 * 1024) + 1, QueryFailureCode.PayloadTooLarge)]
    public void RetryResponseHonorsTheExactPerResourceBoundary(int bodyBytes, QueryFailureCode? expected)
    {
        var meter = new CalendarDirectGetBudget().StartResource();
        meter.TryBeginAttempt().ShouldBeTrue();
        meter.ChargeBody(0);
        meter.TryBeginAttempt().ShouldBeTrue();

        meter.ChargeBody(bodyBytes);

        meter.Attempts.ShouldBe(2);
        if (expected is null)
            meter.Failure.ShouldBeNull();
        else
            meter.Failure!.Code.ShouldBe(expected.Value);
    }

    [Fact]
    public void PayloadOverflowRejectsTheRetryBeforeAnotherWireAttempt()
    {
        var meter = new CalendarDirectGetBudget().StartResource();
        meter.TryBeginAttempt().ShouldBeTrue();
        meter.ChargeBody((4 * 1024 * 1024) + 1);
        meter.TryBeginAttempt().ShouldBeFalse();

        meter.Failure!.Code.ShouldBe(QueryFailureCode.PayloadTooLarge);
        meter.Attempts.ShouldBe(1);
    }

    [Fact]
    public void FailedResourceExposesZeroRemainingBodyCapacity()
    {
        var meter = new CalendarDirectGetBudget().StartResource();
        meter.TryBeginAttempt().ShouldBeTrue();
        meter.ChargeBody((4 * 1024 * 1024) + 1);

        meter.RemainingBodyCapacityPlusOne.ShouldBe(0);
    }

    [Fact]
    public void AggregateBodyBudgetAcceptsExactlyThirtyTwoMebibytesAndRejectsOneMoreByte()
    {
        var budget = new CalendarDirectGetBudget();
        for (var index = 0; index < 8; index++)
        {
            var meter = budget.StartResource();
            meter.TryBeginAttempt().ShouldBeTrue();
            meter.ChargeBody(4 * 1024 * 1024);
            meter.Failure.ShouldBeNull();
        }
        var overflow = budget.StartResource();
        overflow.TryBeginAttempt().ShouldBeTrue();
        overflow.ChargeBody(1);

        overflow.Failure!.Code.ShouldBe(QueryFailureCode.LimitExhausted);
        overflow.Failure.Limits!.Dimension.ShouldBe(QueryLimitDimension.ByteCount);
        overflow.Failure.Limits.Observed.ShouldBe((32L * 1024 * 1024) + 1);
        overflow.Failure.Limits.Limit.ShouldBe(32L * 1024 * 1024);
    }

    [Fact]
    public void FourthWireAttemptIsRejectedBeforeDispatchAtThreeOfThree()
    {
        var meter = new CalendarDirectGetBudget().StartResource();
        meter.TryBeginAttempt().ShouldBeTrue();
        meter.TryBeginAttempt().ShouldBeTrue();
        meter.TryBeginAttempt().ShouldBeTrue();

        meter.TryBeginAttempt().ShouldBeFalse();

        meter.Attempts.ShouldBe(3);
        meter.Failure!.Limits!.Dimension.ShouldBe(QueryLimitDimension.AttemptCount);
        meter.Failure.Limits.Observed.ShouldBe(3);
        meter.Failure.Limits.Limit.ShouldBe(3);
    }

    [Fact]
    public async Task StreamingReadAfterAggregateExhaustionStopsBeforeConsumingAnotherByte()
    {
        var budget = new CalendarDirectGetBudget();
        for (var index = 0; index < 8; index++)
        {
            var charged = budget.StartResource();
            charged.TryBeginAttempt().ShouldBeTrue();
            charged.ChargeBody(4 * 1024 * 1024);
        }
        var overflowing = budget.StartResource();
        overflowing.TryBeginAttempt().ShouldBeTrue();
        overflowing.ChargeBody(1);
        var refused = budget.StartResource();
        await using var source = new MemoryStream([42]);

        var read = await refused.ReadAndChargeAsync(
            source,
            new byte[1],
            TestContext.Current.CancellationToken);

        read.ShouldBe(-1);
        source.Position.ShouldBe(0);
        refused.Failure!.Code.ShouldBe(QueryFailureCode.LimitExhausted);
        refused.Failure.Limits!.Dimension.ShouldBe(QueryLimitDimension.ByteCount);
    }
}
