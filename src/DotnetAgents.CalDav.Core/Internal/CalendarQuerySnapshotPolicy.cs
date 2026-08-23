using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;

namespace DotnetAgents.CalDav.Core.Internal;

internal static class CalendarQuerySnapshotPolicy
{
    internal const int MaximumItems = 5000;
    internal const long MaximumBytes = 32L * 1024 * 1024;

    internal static QueryFailure? Validate(int itemCount, long retainedBytes)
    {
        if (itemCount > MaximumItems)
        {
            return CalendarQueryFailures.Limit(
                "The Query Result Snapshot exceeds its item limit.",
                new QueryExecutionLimits(ItemCount: itemCount));
        }
        if (retainedBytes > MaximumBytes)
        {
            return CalendarQueryFailures.Limit(
                "The Query Result Snapshot exceeds its byte limit.",
                new QueryExecutionLimits(ByteCount: retainedBytes > int.MaxValue ? int.MaxValue : (int)retainedBytes));
        }
        return null;
    }
}
