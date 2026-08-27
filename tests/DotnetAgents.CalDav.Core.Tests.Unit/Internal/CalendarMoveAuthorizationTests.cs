using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarMoveAuthorizationTests
{
    private const string SourceCalendarHref = "https://cal.example/tasks/";
    private const string DestinationCalendarHref = "https://cal.example/archive/";
    private const string SourceHref = SourceCalendarHref + "reviewed.ics";
    private const string DestinationHref =
        DestinationCalendarHref + "5Pk08yHrdsm_i1ED4KDZr-ctbmKs49PqhJeQYZv3SHo.ics";

    [Fact]
    public async Task SemanticMoveReceivesCompleteResolvedCalendarEvidence()
    {
        var source = TodoCalendar(SourceCalendarHref, "Tasks");
        var destination = TodoCalendar(DestinationCalendarHref, "Archive");
        var request = new CalendarResourceMoveRequest(
            new CalendarResourceRevisionReference(
                SourceHref,
                "reviewed",
                CalendarEntityKind.Todo,
                "\"r1\""),
            CalendarMoveDestination.Default);

        var result = await Module(
                [source, destination],
                CalendarSelectionResult.Success(destination))
            .AuthorizeAsync(
            request,
            TestContext.Current.CancellationToken);

        var authorized = result.ShouldBeOfType<CalendarMoveAuthorizationResult.Authorized>();
        authorized.Target.SourceHref.ShouldBe(SourceHref);
        authorized.Target.DestinationHref.ShouldBe(DestinationHref);
        AssertSameCalendar(source, authorized.Target.SourceCalendar);
        AssertSameCalendar(destination, authorized.Target.DestinationCalendar);
    }

    [Fact]
    public async Task ExactMoveReceivesCompleteResolvedCalendarEvidence()
    {
        var source = TodoCalendar(SourceCalendarHref, "Tasks");
        var destination = TodoCalendar(DestinationCalendarHref, "Archive");
        var request = new CalendarExactMoveRequest(
            new CalendarResourceRevisionReference(
                SourceHref,
                "reviewed",
                CalendarEntityKind.Todo,
                "\"r1\""),
            DestinationCalendarHref + "renamed.ics");

        var result = await Module(
                [source, destination],
                CalendarSelectionResult.Success(destination))
            .AuthorizeAsync(request, TestContext.Current.CancellationToken);

        var authorized = result.ShouldBeOfType<CalendarMoveAuthorizationResult.Authorized>();
        authorized.Target.SourceHref.ShouldBe(SourceHref);
        authorized.Target.DestinationHref.ShouldBe(DestinationCalendarHref + "renamed.ics");
        AssertSameCalendar(source, authorized.Target.SourceCalendar);
        AssertSameCalendar(destination, authorized.Target.DestinationCalendar);
    }

    [Theory]
    [InlineData("source-noncanonical", "NonCanonicalResourceHref")]
    [InlineData("destination-noncanonical", "NonCanonicalResourceHref")]
    [InlineData("source-origin", "OriginMismatch")]
    [InlineData("destination-origin", "OriginMismatch")]
    [InlineData("source-scope", "OutsideCalendarScope")]
    [InlineData("destination-scope", "OutsideCalendarScope")]
    [InlineData("same-resource", "SameResourceHref")]
    public async Task ExactLocalAuthorizationFailuresStopBeforeDiscovery(
        string scenario,
        string expectedReason)
    {
        var sourceHref = scenario switch
        {
            "source-noncanonical" => "https://cal.example/tasks/../reviewed.ics",
            "source-origin" => "https://other.example/tasks/reviewed.ics",
            "source-scope" => "https://cal.example/outside/reviewed.ics",
            _ => SourceHref
        };
        var destinationHref = scenario switch
        {
            "destination-noncanonical" => "https://cal.example/archive/%2e/renamed.ics",
            "destination-origin" => "https://other.example/archive/renamed.ics",
            "destination-scope" => "https://cal.example/outside/renamed.ics",
            "same-resource" => sourceHref,
            _ => DestinationCalendarHref + "renamed.ics"
        };
        var fixture = Fixture(
            [
                TodoCalendar(SourceCalendarHref, "Tasks"),
                TodoCalendar(DestinationCalendarHref, "Archive")
            ],
            CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound));
        var request = new CalendarExactMoveRequest(
            new CalendarResourceRevisionReference(
                sourceHref,
                "reviewed",
                CalendarEntityKind.Todo,
                "\"r1\""),
            destinationHref);

        var result = await fixture.Module.AuthorizeAsync(request, TestContext.Current.CancellationToken);

        var rejected = result.ShouldBeOfType<CalendarMoveAuthorizationResult.Rejected>();
        rejected.Failure.Reason.ToString().ShouldBe(expectedReason);
        fixture.Transport.DiscoveryCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("source-noncanonical", "NonCanonicalResourceHref")]
    [InlineData("source-origin", "OriginMismatch")]
    [InlineData("source-scope", "OutsideCalendarScope")]
    [InlineData("selected-both", "InvalidSelectedCalendar")]
    [InlineData("selected-null", "InvalidSelectedCalendar")]
    [InlineData("selected-noncanonical", "InvalidSelectedCalendar")]
    [InlineData("selected-origin", "OriginMismatch")]
    [InlineData("selected-scope", "OutsideCalendarScope")]
    public async Task SemanticLocalAuthorizationFailuresStopBeforeDiscovery(
        string scenario,
        string expectedReason)
    {
        var sourceHref = scenario switch
        {
            "source-noncanonical" => "https://cal.example/tasks/../reviewed.ics",
            "source-origin" => "https://other.example/tasks/reviewed.ics",
            "source-scope" => "https://cal.example/outside/reviewed.ics",
            _ => SourceHref
        };
        var destination = scenario switch
        {
            "selected-both" => CalendarMoveDestination.Selected(
                new CalendarReference("Archive", DestinationCalendarHref)),
            "selected-null" => new CalendarMoveDestination(CalendarEntityScopeMode.Selected),
            "selected-noncanonical" => CalendarMoveDestination.Selected(
                new CalendarReference(Href: "https://cal.example/archive/../archive/")),
            "selected-origin" => CalendarMoveDestination.Selected(
                new CalendarReference(Href: "https://other.example/archive/")),
            "selected-scope" => CalendarMoveDestination.Selected(
                new CalendarReference(Href: "https://cal.example/outside/")),
            _ => CalendarMoveDestination.Default
        };
        var fixture = Fixture(
            [
                TodoCalendar(SourceCalendarHref, "Tasks"),
                TodoCalendar(DestinationCalendarHref, "Archive")
            ],
            CalendarSelectionResult.Success(TodoCalendar(DestinationCalendarHref, "Archive")));
        var request = new CalendarResourceMoveRequest(
            new CalendarResourceRevisionReference(
                sourceHref,
                "reviewed",
                CalendarEntityKind.Todo,
                "\"r1\""),
            destination);

        var result = await fixture.Module.AuthorizeAsync(request, TestContext.Current.CancellationToken);

        var rejected = result.ShouldBeOfType<CalendarMoveAuthorizationResult.Rejected>();
        rejected.Failure.Reason.ToString().ShouldBe(expectedReason);
        fixture.Transport.DiscoveryCount.ShouldBe(0);
    }

    [Theory]
    [InlineData("source-missing", "SourceOwnershipMissing")]
    [InlineData("source-ambiguous", "SourceOwnershipAmbiguous")]
    [InlineData("destination-missing", "DestinationOwnershipMissing")]
    [InlineData("destination-ambiguous", "DestinationOwnershipAmbiguous")]
    [InlineData("capability", "EntityKindNotAdvertised")]
    public async Task ExactMoveRequiresUniqueDirectOwnershipAndAdvertisedCapability(
        string scenario,
        string expectedReason)
    {
        var source = TodoCalendar(SourceCalendarHref, "Tasks");
        var destination = TodoCalendar(DestinationCalendarHref, "Archive");
        IReadOnlyList<CalendarDescriptor> calendars = scenario switch
        {
            "source-missing" => [destination],
            "source-ambiguous" => [source, source with { DisplayName = "Duplicate" }, destination],
            "destination-missing" => [source],
            "destination-ambiguous" => [source, destination, destination with { DisplayName = "Duplicate" }],
            "capability" => [source, destination with { TodoSupport = EntityKindSupport.NotAdvertised }],
            _ => throw new InvalidOperationException(scenario)
        };
        var fixture = Fixture(calendars, CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound));
        var request = new CalendarExactMoveRequest(
            new CalendarResourceRevisionReference(
                SourceHref,
                "reviewed",
                CalendarEntityKind.Todo,
                "\"r1\""),
            DestinationCalendarHref + "renamed.ics");

        var result = await fixture.Module.AuthorizeAsync(request, TestContext.Current.CancellationToken);

        var rejected = result.ShouldBeOfType<CalendarMoveAuthorizationResult.Rejected>();
        rejected.Failure.Reason.ToString().ShouldBe(expectedReason);
        fixture.Transport.DiscoveryCount.ShouldBe(1);
    }

    [Theory]
    [InlineData("source-missing", "SourceOwnershipMissing")]
    [InlineData("source-ambiguous", "SourceOwnershipAmbiguous")]
    [InlineData("selection-missing", "DestinationSelectionNotFound")]
    [InlineData("selection-ambiguous", "DestinationSelectionAmbiguous")]
    [InlineData("selection-outside", "OutsideCalendarScope")]
    [InlineData("selected-invalid", "InvalidResolvedCalendar")]
    [InlineData("selected-divergent", "ResolvedCalendarIdentityDivergent")]
    [InlineData("selected-null", "ResolvedCalendarIdentityDivergent")]
    [InlineData("capability", "EntityKindNotAdvertised")]
    [InlineData("profile", "InteroperabilityProfileUnverified")]
    [InlineData("same-calendar", "SameCalendarNotAllowed")]
    public async Task SemanticMoveRequiresCompleteAuthorityBeforeSuccess(
        string scenario,
        string expectedReason)
    {
        var source = TodoCalendar(SourceCalendarHref, "Tasks");
        var destination = TodoCalendar(DestinationCalendarHref, "Archive");
        IReadOnlyList<CalendarDescriptor> calendars = scenario switch
        {
            "source-missing" => [destination],
            "source-ambiguous" => [source, source with { DisplayName = "Duplicate" }, destination],
            "selected-invalid" => [source, destination with { Href = "https://other.example/archive/" }],
            "same-calendar" => [source],
            _ => [source, destination]
        };
        var selected = scenario switch
        {
            "selection-missing" => CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, calendars),
            "selection-ambiguous" => CalendarSelectionResult.Failure(CalendarSelectionCode.Ambiguous, calendars),
            "selection-outside" => CalendarSelectionResult.Failure(CalendarSelectionCode.OutsideScope, calendars),
            "selected-invalid" => CalendarSelectionResult.Success(calendars[1]),
            "selected-divergent" => CalendarSelectionResult.Success(destination with { Description = "Divergent" }),
            "selected-null" => new CalendarSelectionResult(CalendarSelectionCode.Success, null, []),
            "capability" => CalendarSelectionResult.Success(destination with
            {
                TodoSupport = EntityKindSupport.NotAdvertised
            }),
            "same-calendar" => CalendarSelectionResult.Success(source),
            _ => CalendarSelectionResult.Success(destination)
        };
        if (scenario == "capability")
            calendars = [source, selected.Calendar!];
        var fixture = Fixture(
            calendars,
            selected,
            interoperabilityProfile: scenario == "profile" ? null : CalDavInteroperabilityProfiles.Radicale_3_7_8);
        var request = new CalendarResourceMoveRequest(
            new CalendarResourceRevisionReference(
                SourceHref,
                "reviewed",
                CalendarEntityKind.Todo,
                "\"r1\""),
            CalendarMoveDestination.Default);

        var result = await fixture.Module.AuthorizeAsync(request, TestContext.Current.CancellationToken);

        var rejected = result.ShouldBeOfType<CalendarMoveAuthorizationResult.Rejected>();
        rejected.Failure.Reason.ToString().ShouldBe(expectedReason);
        fixture.Transport.DiscoveryCount.ShouldBe(1);
    }

    [Fact]
    public async Task SemanticSelectionFailureCarriesOnlyFrozenAuthorizedCandidates()
    {
        var source = TodoCalendar(SourceCalendarHref, "Tasks");
        var first = TodoCalendar(DestinationCalendarHref, "Archive");
        var second = TodoCalendar("https://cal.example/other/", "Archive");
        var fixture = Fixture(
            [source, first, second],
            CalendarSelectionResult.Failure(CalendarSelectionCode.Ambiguous, [first, second]));
        var request = new CalendarResourceMoveRequest(
            new CalendarResourceRevisionReference(
                SourceHref,
                "reviewed",
                CalendarEntityKind.Todo,
                "\"r1\""),
            CalendarMoveDestination.Default);

        var result = await fixture.Module.AuthorizeAsync(request, TestContext.Current.CancellationToken);

        var failure = result.ShouldBeOfType<CalendarMoveAuthorizationResult.Rejected>().Failure;
        failure.Reason.ShouldBe(CalendarMoveAuthorizationFailureReason.DestinationSelectionAmbiguous);
        failure.AuthorizedCandidates.Select(calendar => calendar.Href).ShouldBe([
            DestinationCalendarHref,
            "https://cal.example/other/"
        ]);
    }

    private static CalendarMoveAuthorization Module(
        IReadOnlyList<CalendarDescriptor> calendars,
        CalendarSelectionResult todoDefault) => Fixture(calendars, todoDefault).Module;

    private static AuthorizationFixture Fixture(
        IReadOnlyList<CalendarDescriptor> calendars,
        CalendarSelectionResult todoDefault,
        string? interoperabilityProfile = CalDavInteroperabilityProfiles.Radicale_3_7_8)
    {
        var options = new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = "user",
            Password = "secret",
            CalendarHrefs = $"{SourceCalendarHref},{DestinationCalendarHref}",
            InteroperabilityProfile = interoperabilityProfile
        };
        var transport = new FixedDiscoveryTransport(calendars);
        var discovery = new CalendarOperationDiscovery(
            transport,
            Microsoft.Extensions.Options.Options.Create(options),
            discovered => new CalendarDiscoveryResult(discovered, []),
            (kind, _, _) => kind == CalendarEntityKind.Todo
                ? todoDefault
                : CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, calendars));
        return new AuthorizationFixture(new CalendarMoveAuthorization(discovery, options), transport);
    }

    private static CalendarDescriptor TodoCalendar(string href, string name) => new()
    {
        Href = href,
        DisplayName = name,
        DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
        EventSupport = EntityKindSupport.NotAdvertised,
        TodoSupport = EntityKindSupport.Advertised
    };

    private static void AssertSameCalendar(CalendarDescriptor expected, CalendarDescriptor actual)
    {
        actual.Href.ShouldBe(expected.Href);
        actual.DisplayName.ShouldBe(expected.DisplayName);
        actual.DisplayNameProvenance.ShouldBe(expected.DisplayNameProvenance);
        actual.Description.ShouldBe(expected.Description);
        actual.Color.ShouldBe(expected.Color);
        actual.EventSupport.ShouldBe(expected.EventSupport);
        actual.TodoSupport.ShouldBe(expected.TodoSupport);
        actual.EventEvidence.ShouldBe(expected.EventEvidence);
        actual.TodoEvidence.ShouldBe(expected.TodoEvidence);
        actual.UnavailableProperties.ShouldBe(expected.UnavailableProperties);
    }

    private sealed class FixedDiscoveryTransport(IReadOnlyList<CalendarDescriptor> calendars)
        : ICalendarDiscoveryTransport
    {
        internal int DiscoveryCount { get; private set; }

        public Task<IReadOnlyList<CalendarDescriptor>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult(CountAndReturn());

        private IReadOnlyList<CalendarDescriptor> CountAndReturn()
        {
            DiscoveryCount++;
            return calendars;
        }
    }

    private sealed record AuthorizationFixture(
        CalendarMoveAuthorization Module,
        FixedDiscoveryTransport Transport);
}
