namespace DotnetAgents.CalDav.Core.Internal;

/// <summary>Shared bounded-query transport policy.</summary>
internal static class CalendarQueryPolicy
{
    public const int MaximumMultigetBatchSize = 50;
    public const int MaximumDirectGetResources = 200;
    public const int MaximumDirectGetConcurrency = 4;
}
