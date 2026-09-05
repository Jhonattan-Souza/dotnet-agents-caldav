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
    [InlineData(ExactLocalFailure.SourceNonCanonical)]
    [InlineData(ExactLocalFailure.DestinationNonCanonical)]
    [InlineData(ExactLocalFailure.SourceOrigin)]
    [InlineData(ExactLocalFailure.DestinationOrigin)]
    [InlineData(ExactLocalFailure.SourceScope)]
    [InlineData(ExactLocalFailure.DestinationScope)]
    [InlineData(ExactLocalFailure.SameResource)]
    public async Task ExactLocalAuthorizationFailuresStopBeforeDiscovery(ExactLocalFailure scenario)
    {
        var sourceHref = scenario switch
        {
            ExactLocalFailure.SourceNonCanonical => "https://cal.example/tasks/../reviewed.ics",
            ExactLocalFailure.SourceOrigin => "https://other.example/tasks/reviewed.ics",
            ExactLocalFailure.SourceScope => "https://cal.example/outside/reviewed.ics",
            _ => SourceHref
        };
        var destinationHref = scenario switch
        {
            ExactLocalFailure.DestinationNonCanonical => "https://cal.example/archive/%2e/renamed.ics",
            ExactLocalFailure.DestinationOrigin => "https://other.example/archive/renamed.ics",
            ExactLocalFailure.DestinationScope => "https://cal.example/outside/renamed.ics",
            ExactLocalFailure.SameResource => sourceHref,
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
        rejected.Failure.Reason.ShouldBe(scenario switch
        {
            ExactLocalFailure.SourceNonCanonical or ExactLocalFailure.DestinationNonCanonical =>
                CalendarMoveAuthorizationFailureReason.NonCanonicalResourceHref,
            ExactLocalFailure.SourceOrigin or ExactLocalFailure.DestinationOrigin =>
                CalendarMoveAuthorizationFailureReason.OriginMismatch,
            ExactLocalFailure.SourceScope or ExactLocalFailure.DestinationScope =>
                CalendarMoveAuthorizationFailureReason.OutsideCalendarScope,
            ExactLocalFailure.SameResource => CalendarMoveAuthorizationFailureReason.SameResourceHref,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        });
        fixture.Transport.DiscoveryCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(SemanticLocalFailure.SourceNonCanonical)]
    [InlineData(SemanticLocalFailure.SourceOrigin)]
    [InlineData(SemanticLocalFailure.SourceScope)]
    [InlineData(SemanticLocalFailure.SelectedBoth)]
    [InlineData(SemanticLocalFailure.SelectedNull)]
    [InlineData(SemanticLocalFailure.SelectedNonCanonical)]
    [InlineData(SemanticLocalFailure.SelectedOrigin)]
    [InlineData(SemanticLocalFailure.SelectedScope)]
    public async Task SemanticLocalAuthorizationFailuresStopBeforeDiscovery(SemanticLocalFailure scenario)
    {
        var sourceHref = scenario switch
        {
            SemanticLocalFailure.SourceNonCanonical => "https://cal.example/tasks/../reviewed.ics",
            SemanticLocalFailure.SourceOrigin => "https://other.example/tasks/reviewed.ics",
            SemanticLocalFailure.SourceScope => "https://cal.example/outside/reviewed.ics",
            _ => SourceHref
        };
        var destination = scenario switch
        {
            SemanticLocalFailure.SelectedBoth => CalendarMoveDestination.Selected(
                new CalendarReference("Archive", DestinationCalendarHref)),
            SemanticLocalFailure.SelectedNull => new CalendarMoveDestination(CalendarEntityScopeMode.Selected),
            SemanticLocalFailure.SelectedNonCanonical => CalendarMoveDestination.Selected(
                new CalendarReference(Href: "https://cal.example/archive/../archive/")),
            SemanticLocalFailure.SelectedOrigin => CalendarMoveDestination.Selected(
                new CalendarReference(Href: "https://other.example/archive/")),
            SemanticLocalFailure.SelectedScope => CalendarMoveDestination.Selected(
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
        rejected.Failure.Reason.ShouldBe(scenario switch
        {
            SemanticLocalFailure.SourceNonCanonical => CalendarMoveAuthorizationFailureReason.NonCanonicalResourceHref,
            SemanticLocalFailure.SourceOrigin or SemanticLocalFailure.SelectedOrigin =>
                CalendarMoveAuthorizationFailureReason.OriginMismatch,
            SemanticLocalFailure.SourceScope or SemanticLocalFailure.SelectedScope =>
                CalendarMoveAuthorizationFailureReason.OutsideCalendarScope,
            SemanticLocalFailure.SelectedBoth
                or SemanticLocalFailure.SelectedNull
                or SemanticLocalFailure.SelectedNonCanonical =>
                CalendarMoveAuthorizationFailureReason.InvalidSelectedCalendar,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        });
        fixture.Transport.DiscoveryCount.ShouldBe(0);
    }

    [Theory]
    [InlineData(ExactAuthorityFailure.SourceMissing)]
    [InlineData(ExactAuthorityFailure.SourceAmbiguous)]
    [InlineData(ExactAuthorityFailure.DestinationMissing)]
    [InlineData(ExactAuthorityFailure.DestinationAmbiguous)]
    [InlineData(ExactAuthorityFailure.Capability)]
    public async Task ExactMoveRequiresUniqueDirectOwnershipAndAdvertisedCapability(ExactAuthorityFailure scenario)
    {
        var source = TodoCalendar(SourceCalendarHref, "Tasks");
        var destination = TodoCalendar(DestinationCalendarHref, "Archive");
        IReadOnlyList<CalendarDescriptor> calendars = scenario switch
        {
            ExactAuthorityFailure.SourceMissing => [destination],
            ExactAuthorityFailure.SourceAmbiguous => [source, source with { DisplayName = "Duplicate" }, destination],
            ExactAuthorityFailure.DestinationMissing => [source],
            ExactAuthorityFailure.DestinationAmbiguous => [source, destination, destination with { DisplayName = "Duplicate" }],
            ExactAuthorityFailure.Capability => [source, destination with { TodoSupport = EntityKindSupport.NotAdvertised }],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
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
        rejected.Failure.Reason.ShouldBe(scenario switch
        {
            ExactAuthorityFailure.SourceMissing => CalendarMoveAuthorizationFailureReason.SourceOwnershipMissing,
            ExactAuthorityFailure.SourceAmbiguous => CalendarMoveAuthorizationFailureReason.SourceOwnershipAmbiguous,
            ExactAuthorityFailure.DestinationMissing => CalendarMoveAuthorizationFailureReason.DestinationOwnershipMissing,
            ExactAuthorityFailure.DestinationAmbiguous => CalendarMoveAuthorizationFailureReason.DestinationOwnershipAmbiguous,
            ExactAuthorityFailure.Capability => CalendarMoveAuthorizationFailureReason.EntityKindNotAdvertised,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        });
        fixture.Transport.DiscoveryCount.ShouldBe(1);
    }

    [Theory]
    [InlineData(SemanticAuthorityFailure.SourceMissing)]
    [InlineData(SemanticAuthorityFailure.SourceAmbiguous)]
    [InlineData(SemanticAuthorityFailure.SelectionMissing)]
    [InlineData(SemanticAuthorityFailure.SelectionAmbiguous)]
    [InlineData(SemanticAuthorityFailure.SelectionOutside)]
    [InlineData(SemanticAuthorityFailure.SelectedInvalid)]
    [InlineData(SemanticAuthorityFailure.SelectedDivergent)]
    [InlineData(SemanticAuthorityFailure.SelectedNull)]
    [InlineData(SemanticAuthorityFailure.Capability)]
    [InlineData(SemanticAuthorityFailure.Profile)]
    [InlineData(SemanticAuthorityFailure.SameCalendar)]
    public async Task SemanticMoveRequiresCompleteAuthorityBeforeSuccess(SemanticAuthorityFailure scenario)
    {
        var source = TodoCalendar(SourceCalendarHref, "Tasks");
        var destination = TodoCalendar(DestinationCalendarHref, "Archive");
        IReadOnlyList<CalendarDescriptor> calendars = scenario switch
        {
            SemanticAuthorityFailure.SourceMissing => [destination],
            SemanticAuthorityFailure.SourceAmbiguous => [source, source with { DisplayName = "Duplicate" }, destination],
            SemanticAuthorityFailure.SelectedInvalid => [source, destination with { Href = "https://other.example/archive/" }],
            SemanticAuthorityFailure.SameCalendar => [source],
            _ => [source, destination]
        };
        var selected = scenario switch
        {
            SemanticAuthorityFailure.SelectionMissing => CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, calendars),
            SemanticAuthorityFailure.SelectionAmbiguous => CalendarSelectionResult.Failure(CalendarSelectionCode.Ambiguous, calendars),
            SemanticAuthorityFailure.SelectionOutside => CalendarSelectionResult.Failure(CalendarSelectionCode.OutsideScope, calendars),
            SemanticAuthorityFailure.SelectedInvalid => CalendarSelectionResult.Success(calendars[1]),
            SemanticAuthorityFailure.SelectedDivergent => CalendarSelectionResult.Success(destination with { Description = "Divergent" }),
            SemanticAuthorityFailure.SelectedNull => new CalendarSelectionResult(CalendarSelectionCode.Success, null, []),
            SemanticAuthorityFailure.Capability => CalendarSelectionResult.Success(destination with
            {
                TodoSupport = EntityKindSupport.NotAdvertised
            }),
            SemanticAuthorityFailure.SameCalendar => CalendarSelectionResult.Success(source),
            _ => CalendarSelectionResult.Success(destination)
        };
        if (scenario == SemanticAuthorityFailure.Capability)
            calendars = [source, selected.Calendar!];
        var fixture = Fixture(
            calendars,
            selected,
            interoperabilityProfile: scenario == SemanticAuthorityFailure.Profile
                ? null
                : CalDavInteroperabilityProfiles.Radicale_3_7_8);
        var request = new CalendarResourceMoveRequest(
            new CalendarResourceRevisionReference(
                SourceHref,
                "reviewed",
                CalendarEntityKind.Todo,
                "\"r1\""),
            CalendarMoveDestination.Default);

        var result = await fixture.Module.AuthorizeAsync(request, TestContext.Current.CancellationToken);

        var rejected = result.ShouldBeOfType<CalendarMoveAuthorizationResult.Rejected>();
        rejected.Failure.Reason.ShouldBe(scenario switch
        {
            SemanticAuthorityFailure.SourceMissing => CalendarMoveAuthorizationFailureReason.SourceOwnershipMissing,
            SemanticAuthorityFailure.SourceAmbiguous => CalendarMoveAuthorizationFailureReason.SourceOwnershipAmbiguous,
            SemanticAuthorityFailure.SelectionMissing => CalendarMoveAuthorizationFailureReason.DestinationSelectionNotFound,
            SemanticAuthorityFailure.SelectionAmbiguous => CalendarMoveAuthorizationFailureReason.DestinationSelectionAmbiguous,
            SemanticAuthorityFailure.SelectionOutside => CalendarMoveAuthorizationFailureReason.OutsideCalendarScope,
            SemanticAuthorityFailure.SelectedInvalid => CalendarMoveAuthorizationFailureReason.InvalidResolvedCalendar,
            SemanticAuthorityFailure.SelectedDivergent or SemanticAuthorityFailure.SelectedNull =>
                CalendarMoveAuthorizationFailureReason.ResolvedCalendarIdentityDivergent,
            SemanticAuthorityFailure.Capability => CalendarMoveAuthorizationFailureReason.EntityKindNotAdvertised,
            SemanticAuthorityFailure.Profile => CalendarMoveAuthorizationFailureReason.InteroperabilityProfileUnverified,
            SemanticAuthorityFailure.SameCalendar => CalendarMoveAuthorizationFailureReason.SameCalendarNotAllowed,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        });
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
        failure.AuthorizedCandidates.Select(calendar => calendar.Href).ShouldBe([DestinationCalendarHref]);
    }

    [Fact]
    public async Task SemanticSelectionFailureExcludesCandidatesOutsideTheAuthorizationBoundary()
    {
        var source = TodoCalendar(SourceCalendarHref, "Tasks");
        var authorized = TodoCalendar(DestinationCalendarHref, "Archive");
        var nonCanonical = TodoCalendar("https://cal.example/archive/../archive/", "Archive");
        var offOrigin = TodoCalendar("https://other.example/archive/", "Archive");
        var outsideScope = TodoCalendar("https://cal.example/private/", "Archive");
        var fixture = Fixture(
            [source, authorized],
            CalendarSelectionResult.Failure(
                CalendarSelectionCode.Ambiguous,
                [authorized, nonCanonical, offOrigin, outsideScope]));
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
        failure.AuthorizedCandidates.ShouldBe([authorized]);
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

    public enum ExactLocalFailure
    {
        SourceNonCanonical,
        DestinationNonCanonical,
        SourceOrigin,
        DestinationOrigin,
        SourceScope,
        DestinationScope,
        SameResource
    }

    public enum SemanticLocalFailure
    {
        SourceNonCanonical,
        SourceOrigin,
        SourceScope,
        SelectedBoth,
        SelectedNull,
        SelectedNonCanonical,
        SelectedOrigin,
        SelectedScope
    }

    public enum ExactAuthorityFailure
    {
        SourceMissing,
        SourceAmbiguous,
        DestinationMissing,
        DestinationAmbiguous,
        Capability
    }

    public enum SemanticAuthorityFailure
    {
        SourceMissing,
        SourceAmbiguous,
        SelectionMissing,
        SelectionAmbiguous,
        SelectionOutside,
        SelectedInvalid,
        SelectedDivergent,
        SelectedNull,
        Capability,
        Profile,
        SameCalendar
    }
}
