namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Shared bounded-query transport policy.</summary>
internal static class CalendarQueryPolicy
{
    public const int MaximumMultigetBatchSize = 50;
}
