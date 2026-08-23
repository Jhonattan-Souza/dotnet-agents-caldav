using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarQuerySnapshotPolicyTests
{
    [Theory]
    [InlineData(4999, 32 * 1024 * 1024 - 1)]
    [InlineData(5000, 32 * 1024 * 1024)]
    public void BelowAndExactItemAndByteLimitsAreAccepted(int items, long bytes) =>
        CalendarQuerySnapshotPolicy.Validate(items, bytes).ShouldBeNull();

    [Theory]
    [InlineData(5001, 32 * 1024 * 1024, 5001, null)]
    [InlineData(5000, 32 * 1024 * 1024 + 1L, null, 32 * 1024 * 1024 + 1)]
    public void OneBeyondEitherAtomicLimitIsRejected(
        int items,
        long bytes,
        int? expectedItems,
        int? expectedBytes)
    {
        var failure = CalendarQuerySnapshotPolicy.Validate(items, bytes).ShouldNotBeNull();

        failure.Code.ShouldBe(QueryFailureCode.LimitExhausted);
        failure.Limits!.ItemCount.ShouldBe(expectedItems);
        failure.Limits.ByteCount.ShouldBe(expectedBytes);
    }
}
