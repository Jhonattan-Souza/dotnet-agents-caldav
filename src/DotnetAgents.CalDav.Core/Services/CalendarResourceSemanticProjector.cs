using System.Text.Json;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Services;

/// <summary>Projects authoritative iCalendar bytes through a stable, content-free public boundary.</summary>
public static class CalendarResourceSemanticProjector
{
    public static JsonElement Event(CalendarResourceSnapshot snapshot) =>
        CalendarResourceSemanticProjectionMapper.Event(snapshot);

    public static JsonElement Todo(CalendarResourceSnapshot snapshot) =>
        CalendarResourceSemanticProjectionMapper.Todo(snapshot);

    public static JsonElement TodoForOccurrence(
        CalendarResourceSnapshot snapshot,
        CalendarTemporalValue recurrenceIdentity) =>
        CalendarResourceSemanticProjectionMapper.TodoForOccurrence(snapshot, recurrenceIdentity);

    public static JsonElement? TodoCompletedAt(CalendarResourceSnapshot snapshot) =>
        CalendarResourceSemanticProjectionMapper.TodoCompletedAt(snapshot);

    public static JsonElement? TodoCompletedAtForOccurrence(
        CalendarResourceSnapshot snapshot,
        CalendarTemporalValue recurrenceIdentity) =>
        CalendarResourceSemanticProjectionMapper.TodoCompletedAtForOccurrence(snapshot, recurrenceIdentity);
}
