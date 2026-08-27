using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

internal interface ICalendarQueryPageCodec<TItem>
{
    string ToolName { get; }

    CalendarQueryPageConstraints Constraints { get; }

    CalendarQueryFixedBudget MeasureFixedBudget(CalendarQuerySnapshot snapshot);

    QueryPage<TItem> Materialize(CalendarQuerySnapshot snapshot, CalendarQueryPagePlan plan);
}

internal sealed record CalendarQueryPageConstraints(
    int DefaultPageSize,
    int MaximumPageSize,
    int MaximumCallToolResultBytes,
    int MaximumHumanReadableBytes,
    string HumanReadablePayloadTooLargeMessage,
    string ItemPayloadTooLargeMessage);

internal sealed record CalendarQueryPagePlan(
    IReadOnlyList<StoredCalendarEntityQueryItem> Items,
    string? NextCursor,
    int MeasuredCallToolResultBytes);

internal sealed record CalendarQueryPagePlanAdmission(
    CalendarQueryPagePlan? Value,
    QueryFailure? Error)
{
    internal static CalendarQueryPagePlanAdmission Page(CalendarQueryPagePlan value) => new(value, null);

    internal static CalendarQueryPagePlanAdmission Failure(QueryFailure error) => new(null, error);
}

internal sealed record CalendarQueryFixedBudget(int CallToolResultBytes, int HumanReadableBytes);
