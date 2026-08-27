using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal;

internal interface ICalendarQueryPageCodec<TItem>
{
    string ToolName { get; }

    int DefaultPageSize { get; }

    int MaximumPageSize { get; }

    CalendarQueryPagePlanAdmission Plan(
        CalendarQuerySnapshot snapshot,
        int position,
        int pageSize,
        CancellationToken cancellationToken);

    QueryPage<TItem> Materialize(CalendarQuerySnapshot snapshot, CalendarQueryPagePlan plan);
}

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
