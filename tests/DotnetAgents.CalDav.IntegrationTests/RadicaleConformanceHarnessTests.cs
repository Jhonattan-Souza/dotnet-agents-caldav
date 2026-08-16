using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.IntegrationTests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

[Collection("RadicaleConformanceCollection")]
public sealed class RadicaleConformanceHarnessTests(RadicaleConformanceFixture fixture, ITestOutputHelper output)
{
    internal const string ConformanceUsername = RadicaleConformanceFixture.Username;
    internal const string ConformancePassword = RadicaleConformanceFixture.Password;

    [Fact]
    public void Pinned_profile_records_the_runtime_and_selected_variant()
    {
        output.WriteLine(JsonSerializer.Serialize(fixture.Runtime));
        fixture.Runtime.IndexDigest.ShouldBe(RadicaleConformanceFixture.IndexDigest);
        new[] { RadicaleConformanceFixture.Amd64ManifestDigest, RadicaleConformanceFixture.Arm64ManifestDigest }
            .ShouldContain(fixture.Runtime.ResolvedPlatformManifestDigest);
        fixture.Runtime.ResolvedPlatformManifestDigest.ShouldBe(fixture.Runtime.RuntimeArchitecture switch
        {
            "x86_64" => RadicaleConformanceFixture.Amd64ManifestDigest,
            "aarch64" => RadicaleConformanceFixture.Arm64ManifestDigest,
            _ => throw new InvalidOperationException($"Unsupported architecture {fixture.Runtime.RuntimeArchitecture}")
        });
        fixture.Runtime.RadicaleVersion.ShouldBe("3.7.8");
        fixture.Runtime.PythonVersion.ShouldBe("3.14.7");
        fixture.Runtime.VobjectVersion.ShouldBe("0.9.9");
        fixture.Runtime.RuntimeTimeZone.ShouldBe(fixture.Variant.TimeZone);
        fixture.Runtime.StrictPreconditions.ShouldBe(fixture.Variant.StrictPreconditions);
    }

    [Fact]
    public async Task Pinned_profile_preserves_occurrence_boundary_dst_leap_range_and_typed_failures()
    {
        var calendarHref = $"{fixture.BaseUrl}/conformance/conformance/";
        var boundaryFrom = await PutAndGetAsync(calendarHref, "boundary-from.ics", Event(
            "boundary-from", "DTSTART:20260816T100000Z\r\nDURATION:PT1M\r\n"));
        _ = await PutAndGetAsync(calendarHref, "boundary-to.ics", Event(
            "boundary-to", "DTSTART:20260816T110000Z\r\nDURATION:PT1M\r\n"));
        _ = await PutAndGetAsync(calendarHref, "dst.ics", Event(
            "dst", "DTSTART;TZID=America/New_York:20260307T100000\r\nDURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=3\r\n"));
        _ = await PutAndGetAsync(calendarHref, "leap.ics", Event(
            "leap", "DTSTART:20240229T100000Z\r\nDURATION:PT1H\r\nRRULE:FREQ=YEARLY;COUNT=3\r\n"));
        var range = await PutAndGetAsync(calendarHref, "range.ics", RangeEvent());
        await using var provider = CreateProvider(fixture.BaseUrl, calendarHref);
        var service = provider.GetRequiredService<ICalendarService>();
        var scope = CalendarEntityScope.Selected(new CalendarReference(Href: calendarHref));

        var boundary = await service.QueryOccurrencesAsync(new CalendarOccurrenceQuery(
            scope,
            DateTimeOffset.Parse("2026-08-16T10:00:00Z"),
            DateTimeOffset.Parse("2026-08-16T11:00:00Z")), TestContext.Current.CancellationToken);
        var dst = await service.QueryOccurrencesAsync(new CalendarOccurrenceQuery(
            scope,
            DateTimeOffset.Parse("2026-03-08T14:30:00Z"),
            DateTimeOffset.Parse("2026-03-08T14:45:00Z")), TestContext.Current.CancellationToken);
        var leap = await service.QueryOccurrencesAsync(new CalendarOccurrenceQuery(
            scope,
            DateTimeOffset.Parse("2028-02-29T10:30:00Z"),
            DateTimeOffset.Parse("2028-02-29T10:45:00Z")), TestContext.Current.CancellationToken);
        var moved = await service.QueryOccurrencesAsync(new CalendarOccurrenceQuery(
            scope,
            DateTimeOffset.Parse("2026-08-17T13:30:00Z"),
            DateTimeOffset.Parse("2026-08-17T13:45:00Z")), TestContext.Current.CancellationToken);

        boundary.Items.ShouldHaveSingleItem().Snapshot.Projection.EntityUid.ShouldBe("boundary-from");
        boundary.Items[0].Snapshot.AuthoritativeUtf8.ToArray().ShouldBe(boundaryFrom.Utf8);
        dst.Items.ShouldHaveSingleItem().Timing.EvaluatedStartUtc!.Value.ShouldBe("2026-03-08T14:00:00Z");
        leap.Items.ShouldHaveSingleItem().RecurrenceIdentity.Value.ShouldBe("2028-02-29T10:00:00Z");
        var movedOccurrence = moved.Items.ShouldHaveSingleItem();
        movedOccurrence.RecurrenceIdentity.Value.ShouldBe("2026-08-17T09:00:00Z");
        movedOccurrence.Timing.EffectiveStart.Value.ShouldBe("2026-08-17T13:00:00Z");
        movedOccurrence.Snapshot.AuthoritativeUtf8.ToArray().ShouldBe(range.Utf8);

        var unresolved = await PutAndGetAsync(calendarHref, "unresolved.ics", Event(
            "unresolved", "DTSTART;TZID=Private/Unknown:20260816T100000\r\nDURATION:PT1H\r\n"));
        var unresolvedResult = await service.QueryOccurrencesAsync(new CalendarOccurrenceQuery(
            scope,
            DateTimeOffset.Parse("2026-08-16T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-17T00:00:00Z")), TestContext.Current.CancellationToken);
        unresolvedResult.Code.ShouldBe(CalendarOccurrenceQueryCode.TemporalUnresolved);
        unresolvedResult.Items.ShouldBeEmpty();
        await DeleteAsync(unresolved, TestContext.Current.CancellationToken);

        var unevaluable = await PutAndGetAsync(calendarHref, "unevaluable.ics", Event(
            "unevaluable", "DTSTART:20260816T100000Z\r\nDURATION:PT1H\r\n"
            + "RRULE:FREQ=DAILY;COUNT=2\r\nRRULE:FREQ=WEEKLY;COUNT=2\r\n"));
        var unevaluableResult = await service.QueryOccurrencesAsync(new CalendarOccurrenceQuery(
            scope,
            DateTimeOffset.Parse("2026-08-16T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-17T00:00:00Z")), TestContext.Current.CancellationToken);
        unevaluableResult.Code.ShouldBe(CalendarOccurrenceQueryCode.RecurrenceUnevaluable);
        unevaluableResult.Items.ShouldBeEmpty();
        await DeleteAsync(unevaluable, TestContext.Current.CancellationToken);
    }

    internal static ServiceProvider CreateProvider(string baseUrl, string calendarHref)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCalDavTasks(options =>
        {
            options.BaseUrl = baseUrl;
            options.CalendarHrefs = calendarHref;
            options.Username = ConformanceUsername;
            options.Password = ConformancePassword;
        });
        return services.BuildServiceProvider();
    }

    private async Task<ObservedResource> PutAndGetAsync(
        string calendarHref,
        string name,
        string content)
    {
        var href = $"{calendarHref}{name}";
        var observed = await fixture.SeedResourceAsync(
            name,
            content,
            TestContext.Current.CancellationToken);
        return new ObservedResource(name, href, observed.EntityTag, observed.Utf8);
    }

    private Task DeleteAsync(
        ObservedResource resource,
        CancellationToken cancellationToken) =>
        fixture.DeleteResourceAsync(resource.Name, resource.EntityTag, cancellationToken);

    private static string Event(string uid, string temporalLines) =>
        $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Conformance//EN\r\nBEGIN:VEVENT\r\n"
        + $"UID:{uid}\r\nDTSTAMP:20260815T120000Z\r\n{temporalLines}END:VEVENT\r\nEND:VCALENDAR\r\n";

    private static string RangeEvent() =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Conformance//EN\r\n"
        + "BEGIN:VEVENT\r\nUID:range\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20260814T090000Z\r\n"
        + "DURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=5\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:range\r\nDTSTAMP:20260815T120000Z\r\n"
        + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260816T090000Z\r\n"
        + "DTSTART:20260816T110000Z\r\nDURATION:PT1H\r\nEND:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:range\r\nDTSTAMP:20260815T120000Z\r\n"
        + "RECURRENCE-ID;RANGE=THISANDFUTURE:20260817T090000Z\r\n"
        + "DTSTART:20260817T130000Z\r\nDURATION:PT2H\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";

    private sealed record ObservedResource(string Name, string Href, string EntityTag, byte[] Utf8);
}

public sealed class RadicaleConformanceHarnessConfigurationTests
{
    [Fact]
    public void Provider_resolves_calendar_service_with_nonempty_conformance_credentials()
    {
        using var provider = RadicaleConformanceHarnessTests.CreateProvider(
            "http://localhost:5232",
            "http://localhost:5232/conformance/conformance/");

        RadicaleConformanceFixture.Username.ShouldNotBeNullOrWhiteSpace();
        RadicaleConformanceFixture.Password.ShouldNotBeNullOrWhiteSpace();
        provider.GetRequiredService<ICalendarService>().ShouldNotBeNull();
    }
}
