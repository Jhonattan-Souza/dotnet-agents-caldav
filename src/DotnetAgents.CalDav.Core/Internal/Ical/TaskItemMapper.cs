using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using CalDavTaskStatus = DotnetAgents.CalDav.Core.Models.TaskStatus;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>
/// Maps between <see cref="TaskItem"/> domain records and <see cref="Todo"/> / <see cref="Calendar"/> iCal components.
/// </summary>
internal static class TaskItemMapper
{

    /// <summary>Converts an iCalendar text payload into a list of <see cref="TaskItem"/> records.</summary>
    public static IReadOnlyList<TaskItem> FromICalText(string iCalText, string? etag = null)
    {
        var calendar = Calendar.Load(iCalText);
        if (calendar is null)
        {
            return [];
        }

        var tasks = new List<TaskItem>();

        foreach (var todo in calendar.Todos)
        {
            tasks.Add(FromTodo(todo, etag));
        }

        return tasks;
    }

    /// <summary>Converts a single <see cref="Todo"/> to a <see cref="TaskItem"/>.</summary>
    public static TaskItem FromTodo(Todo todo, string? etag = null, string? href = null)
    {
        return new TaskItem
        {
            Uid = todo.Uid ?? string.Empty,
            Summary = todo.Summary ?? string.Empty,
            Description = todo.Description,
            Due = MapCalDateTimeToDateTimeOffset(todo.Due),
            Start = MapCalDateTimeToDateTimeOffset(todo.DtStart),
            Completed = MapCalDateTimeToDateTimeOffset(todo.Completed),
            Priority = MapPriorityFromIcal(todo.Priority),
            Status = MapStatusFromIcal(todo.Status),
            Categories = todo.Categories?
                .SelectMany(c => c?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) ?? [])
                .ToList() as IReadOnlyList<string> ?? [],
            RecurrenceRule = todo.RecurrenceRules?.FirstOrDefault()?.ToString(),
            ETag = etag,
            Href = href ?? string.Empty
        };
    }

    /// <summary>Converts a <see cref="TaskItem"/> to an iCalendar text payload.</summary>
    public static string ToICalText(TaskItem task)
    {
        var calendar = new Calendar();
        var todo = ToTodo(task);
        calendar.Todos.Add(todo);
        var serializer = new CalendarSerializer();
        return serializer.SerializeToString(calendar) ?? string.Empty;
    }

    /// <summary>Converts a <see cref="TaskItem"/> to a <see cref="Todo"/> component.</summary>
    public static Todo ToTodo(TaskItem task)
    {
        var todo = new Todo
        {
            Uid = string.IsNullOrEmpty(task.Uid) ? Guid.NewGuid().ToString() : task.Uid,
            Summary = task.Summary,
            Description = task.Description,
            Priority = MapPriorityToIcal(task.Priority),
            Status = MapStatusToIcal(task.Status),
        };

        if (task.Due is not null)
            todo.Due = new CalDateTime(task.Due.Value.UtcDateTime);

        if (task.Start is not null)
            todo.DtStart = new CalDateTime(task.Start.Value.UtcDateTime);

        if (task.Completed is not null)
            todo.Completed = new CalDateTime(task.Completed.Value.UtcDateTime);

        if (task.Categories is { Count: > 0 })
        {
            todo.Categories = new List<string>(task.Categories);
        }

        if (task.RecurrenceRule is not null)
        {
            var recurrence = RecurrenceMapper.FromString(task.RecurrenceRule);
            if (recurrence is not null)
                todo.RecurrenceRules = [recurrence];
        }

        return todo;
    }

    /// <summary>Converts a nullable <see cref="CalDateTime"/> to a nullable <see cref="DateTimeOffset"/> (UTC).</summary>
    private static DateTimeOffset? MapCalDateTimeToDateTimeOffset(CalDateTime? calDt)
    {
        if (calDt is null)
            return null;

        // Use AsUtc to normalize to UTC, then wrap in DateTimeOffset
        var utc = calDt.AsUtc;
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    /// <summary>
    /// Maps RFC 5545 PRIORITY integer (0–9) to <see cref="TaskPriority"/>.
    /// Per RFC 5545: 0 = undefined, 1 = highest, 9 = lowest.
    /// </summary>
    private static TaskPriority MapPriorityFromIcal(int priority) => priority switch
    {
        0 => TaskPriority.None,
        >= 1 and <= 4 => TaskPriority.High,
        5 => TaskPriority.Medium,
        >= 6 and <= 9 => TaskPriority.Low,
        _ => TaskPriority.None
    };

    /// <summary>
    /// Maps <see cref="TaskPriority"/> to RFC 5545 PRIORITY integer.
    /// Per RFC 5545: 0 = undefined, 1 = highest, 9 = lowest.
    /// </summary>
    private static int MapPriorityToIcal(TaskPriority priority) => priority switch
    {
        TaskPriority.None => 0,
        TaskPriority.High => 1,
        TaskPriority.Medium => 5,
        TaskPriority.Low => 9,
        _ => 0
    };

    private static CalDavTaskStatus MapStatusFromIcal(string? status) => status?.ToUpperInvariant() switch
    {
        "NEEDS-ACTION" => CalDavTaskStatus.NeedsAction,
        "IN-PROCESS" => CalDavTaskStatus.InProcess,
        "COMPLETED" => CalDavTaskStatus.Completed,
        "CANCELLED" => CalDavTaskStatus.Cancelled,
        _ => CalDavTaskStatus.NeedsAction
    };

    private static string? MapStatusToIcal(CalDavTaskStatus status) => status switch
    {
        CalDavTaskStatus.NeedsAction => "NEEDS-ACTION",
        CalDavTaskStatus.InProcess => "IN-PROCESS",
        CalDavTaskStatus.Completed => "COMPLETED",
        CalDavTaskStatus.Cancelled => "CANCELLED",
        _ => "NEEDS-ACTION"
    };
}
