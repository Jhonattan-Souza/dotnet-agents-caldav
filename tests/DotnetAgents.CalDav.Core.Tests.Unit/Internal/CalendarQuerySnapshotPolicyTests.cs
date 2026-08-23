using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarQuerySnapshotPolicyTests
{
    [Fact]
    public void ItemLimitFailureCarriesTheObservedSnapshotCount()
    {
        var itemCount = CalendarQuerySnapshotPolicy.MaximumItems + 1;

        var failure = CalendarQuerySnapshotPolicy.Validate(itemCount, 0);

        failure.ShouldNotBeNull();
        failure.Code.ShouldBe(QueryFailureCode.LimitExhausted);
        failure.Limits!.ItemCount.ShouldBe(itemCount);
    }

    [Fact]
    public void ExactItemAndByteLimitsAreAdmitted()
    {
        CalendarQuerySnapshotPolicy.Validate(
                CalendarQuerySnapshotPolicy.MaximumItems,
                CalendarQuerySnapshotPolicy.MaximumBytes)
            .ShouldBeNull();
    }

    [Fact]
    public void ByteLimitFailureSaturatesPublicIntegerEvidence()
    {
        var failure = CalendarQuerySnapshotPolicy.Validate(0, (long)int.MaxValue + 1);

        failure.ShouldNotBeNull();
        failure.Code.ShouldBe(QueryFailureCode.LimitExhausted);
        failure.Limits!.ByteCount.ShouldBe(int.MaxValue);
    }

    [Fact]
    public void ByteLimitFailurePreservesRepresentableObservedCount()
    {
        var retainedBytes = CalendarQuerySnapshotPolicy.MaximumBytes + 1;

        var failure = CalendarQuerySnapshotPolicy.Validate(0, retainedBytes);

        failure.ShouldNotBeNull();
        failure.Code.ShouldBe(QueryFailureCode.LimitExhausted);
        failure.Limits!.ByteCount.ShouldBe((int)retainedBytes);
    }
}
