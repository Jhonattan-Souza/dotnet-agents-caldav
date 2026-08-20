using System.Text;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Ical;

public sealed class CalendarTodoCompletionClassifierTests
{
    [Theory]
    [InlineData(null, null, null, CalendarTodoCompletionState.Open)]
    [InlineData("NEEDS-ACTION", null, null, CalendarTodoCompletionState.Open)]
    [InlineData("IN-PROCESS", null, 50, CalendarTodoCompletionState.Open)]
    [InlineData(null, "20260819T100000Z", null, CalendarTodoCompletionState.Completed)]
    [InlineData(null, null, 100, CalendarTodoCompletionState.Completed)]
    [InlineData("COMPLETED", null, null, CalendarTodoCompletionState.Completed)]
    [InlineData("CANCELLED", null, 50, CalendarTodoCompletionState.Cancelled)]
    public void Classify_UsesConservativeCompletionEvidence(
        string? status,
        string? completed,
        int? percentComplete,
        CalendarTodoCompletionState expected)
    {
        var snapshot = Snapshot(status, completed, percentComplete);

        var result = CalendarTodoCompletionClassifier.Classify(snapshot);

        result.State.ShouldBe(expected);
    }

    [Theory]
    [InlineData("NEEDS-ACTION", "20260819T100000Z", null)]
    [InlineData("COMPLETED", null, 50)]
    [InlineData("CANCELLED", "20260819T100000Z", null)]
    [InlineData("CANCELLED", null, 100)]
    [InlineData("COMPLETED", null, 0)]
    [InlineData("NEEDS-ACTION", null, 100)]
    [InlineData("X-UNKNOWN", null, null)]
    public void Classify_ReportsContradictoryOrUnknownEvidence(
        string status,
        string? completed,
        int? percentComplete)
    {
        var result = CalendarTodoCompletionClassifier.Classify(Snapshot(status, completed, percentComplete));

        result.State.ShouldBe(CalendarTodoCompletionState.Indeterminate);
        result.Diagnostics.ShouldNotBeEmpty();
    }

    [Fact]
    public void Classify_UsesThisAndFutureOverrideForLaterOccurrence()
    {
        var snapshot = SnapshotWithOverrides();

        var result = CalendarTodoCompletionClassifier.Classify(
            snapshot,
            new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z"));

        result.State.ShouldBe(CalendarTodoCompletionState.Cancelled);
        var fields = CalendarResourceSemanticProjector.TodoForOccurrence(
            snapshot,
            new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-21T10:00:00Z"));
        fields.GetProperty("summary").GetString().ShouldBe("Future override");
        fields.GetProperty("status").GetProperty("kind").GetString().ShouldBe("cancelled");
    }

    [Fact]
    public void Classify_PrefersExactIndividualOverride()
    {
        var snapshot = SnapshotWithIndividualOverride();
        var document = CalendarContentDocument.Parse(snapshot.AuthoritativeUtf8.Span);
        var identities = document.Properties
            .Where(property => property.Name == "RECURRENCE-ID")
            .Select(property => CalendarPatchValueSerializer.ParseTemporal(property).GetCanonicalSortKey())
            .ToArray();
        identities.ShouldHaveSingleItem();
        identities[0].ShouldBe(new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-20T10:00:00Z").GetCanonicalSortKey());
        var projected = CalendarResourceSemanticProjector.TodoForOccurrence(
            snapshot,
            new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-20T10:00:00Z"));
        projected.GetProperty("summary").GetString().ShouldBe("Individual override");

        var result = CalendarTodoCompletionClassifier.Classify(
            snapshot,
            new CalendarTemporalValue(CalendarTemporalKind.UtcDateTime, "2026-08-20T10:00:00Z"));

        result.State.ShouldBe(CalendarTodoCompletionState.Completed);
    }

    private static CalendarResourceSnapshot Snapshot(
        string? status,
        string? completed,
        int? percentComplete)
    {
        var properties = string.Concat(
            status is null ? string.Empty : $"STATUS:{status}\r\n",
            completed is null ? string.Empty : $"COMPLETED:{completed}\r\n",
            percentComplete is null ? string.Empty : $"PERCENT-COMPLETE:{percentComplete}\r\n");
        var ics = $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//tests//EN\r\nBEGIN:VTODO\r\nUID:todo-1\r\nDTSTAMP:20260819T100000Z\r\n{properties}END:VTODO\r\nEND:VCALENDAR\r\n";
        return new CalendarResourceSnapshot(
            "https://cal.example/todos/",
            "https://cal.example/todos/todo-1.ics",
            "\"1\"",
            Encoding.UTF8.GetBytes(ics),
            [],
            new CalendarResourceProjection(CalendarResourceProjectionKind.Todo, "todo-1", null),
            []);
    }

    private static CalendarResourceSnapshot SnapshotWithOverrides()
    {
        const string ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//tests//EN\r\n"
            + "BEGIN:VTODO\r\nUID:todo-range\r\nDTSTAMP:20260819T100000Z\r\n"
            + "DTSTART:20260819T100000Z\r\nRRULE:FREQ=DAILY;COUNT=4\r\nSUMMARY:Master\r\n"
            + "END:VTODO\r\nBEGIN:VTODO\r\nUID:todo-range\r\nDTSTAMP:20260819T100000Z\r\n"
            + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260820T100000Z\r\n"
            + "SUMMARY:Future override\r\nSTATUS:CANCELLED\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        return new CalendarResourceSnapshot(
            "https://cal.example/todos/",
            "https://cal.example/todos/todo-range.ics",
            "\"1\"",
            Encoding.UTF8.GetBytes(ics),
            [],
            new CalendarResourceProjection(CalendarResourceProjectionKind.Todo, "todo-range", null),
            []);
    }

    private static CalendarResourceSnapshot SnapshotWithIndividualOverride()
    {
        const string ics = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//tests//EN\r\n"
            + "BEGIN:VTODO\r\nUID:todo-individual\r\nDTSTAMP:20260819T100000Z\r\n"
            + "DTSTART:20260819T100000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nSUMMARY:Master\r\n"
            + "END:VTODO\r\nBEGIN:VTODO\r\nUID:todo-individual\r\nDTSTAMP:20260819T100000Z\r\n"
            + "RECURRENCE-ID:20260820T100000Z\r\nSUMMARY:Individual override\r\n"
            + "STATUS:COMPLETED\r\nCOMPLETED:20260820T110000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n";
        return new CalendarResourceSnapshot(
            "https://cal.example/todos/",
            "https://cal.example/todos/todo-individual.ics",
            "\"1\"",
            Encoding.UTF8.GetBytes(ics),
            [],
            new CalendarResourceProjection(CalendarResourceProjectionKind.Todo, "todo-individual", null),
            []);
    }
}
