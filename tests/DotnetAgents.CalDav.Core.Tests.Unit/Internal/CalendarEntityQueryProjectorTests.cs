using System.Text.Json;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarEntityQueryProjectorTests
{
    [Theory]
    [InlineData(CalendarResourceProjectionKind.Event, "event", true)]
    [InlineData(CalendarResourceProjectionKind.Todo, "todo", true)]
    [InlineData(CalendarResourceProjectionKind.Opaque, "opaque", false)]
    public void ProjectEmitsEveryClosedProjectionAndRevisionShape(
        CalendarResourceProjectionKind kind,
        string expectedKind,
        bool semanticRevision)
    {
        var snapshot = Snapshot(kind);

        var projected = CalendarEntityQueryProjector.Project(snapshot);
        using var document = JsonDocument.Parse(projected.JsonUtf8);
        var root = document.RootElement;

        root.GetProperty("projection").GetProperty("kind").GetString().ShouldBe(expectedKind);
        root.TryGetProperty("entityRevision", out _).ShouldBe(semanticRevision);
        root.GetProperty("calendarProperties")[0].GetProperty("valueType").GetString().ShouldBe("date-time");
        root.GetProperty("calendarProperties")[1].GetProperty("valueType").GetString().ShouldBe("uri");
        root.GetProperty("diagnostics").EnumerateArray()
            .Select(item => item.GetProperty("severity").GetString())
            .ShouldBe(["info", "warning", "error"]);
    }

    [Theory]
    [InlineData(CalendarResourceDiagnosticSeverity.Info, "info")]
    [InlineData(CalendarResourceDiagnosticSeverity.Warning, "warning")]
    [InlineData(CalendarResourceDiagnosticSeverity.Error, "error")]
    public void DiagnosticMapsEveryClosedSeverity(CalendarResourceDiagnosticSeverity severity, string expected)
    {
        var diagnostic = CalendarEntityQueryProjector.Diagnostic(new CalendarResourceDiagnostic(
            "safe",
            "Safe diagnostic.",
            severity));

        diagnostic.Severity.ShouldBe(expected);
    }

    [Fact]
    public void DiagnosticRejectsUndefinedSeverity()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => CalendarEntityQueryProjector.Diagnostic(
            new CalendarResourceDiagnostic("safe", "Safe diagnostic.", (CalendarResourceDiagnosticSeverity)99)));
    }

    private static CalendarResourceSnapshot Snapshot(CalendarResourceProjectionKind kind) => new(
        "https://cal.example/calendars/work/",
        "https://cal.example/calendars/work/item.ics",
        "\"r1\"",
        ReadOnlyMemory<byte>.Empty,
        [
            new CalendarProperty(
                [new CalendarComponentPathSegment("VCALENDAR", 0), new CalendarComponentPathSegment("VEVENT", 0)],
                "DTSTART",
                [new CalendarParameter("TZID", ["Etc/UTC"])],
                CalendarPropertyValueType.DateTime,
                "20260823T120000Z",
                "DTSTART:20260823T120000Z"),
            new CalendarProperty(
                [new CalendarComponentPathSegment("VCALENDAR", 0)],
                "URL",
                [],
                CalendarPropertyValueType.Uri,
                "https://example.invalid/value",
                "URL:https://example.invalid/value")
        ],
        new CalendarResourceProjection(kind, kind == CalendarResourceProjectionKind.Opaque ? null : "uid", "Summary"),
        [
            new CalendarResourceDiagnostic("info", "Info.", CalendarResourceDiagnosticSeverity.Info),
            new CalendarResourceDiagnostic("warning", "Warning.", CalendarResourceDiagnosticSeverity.Warning),
            new CalendarResourceDiagnostic("error", "Error.", CalendarResourceDiagnosticSeverity.Error)
        ]);
}
