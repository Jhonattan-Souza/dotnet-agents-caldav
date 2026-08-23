using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarQueryCapabilityStateTests
{
    [Fact]
    public void FullStateStopsCachingWithoutEvictingVerifiedEvidence()
    {
        var state = new CalendarQueryCapabilityState();
        var options = Options("user", "password");
        for (var index = 0; index < CalendarQueryCapabilityState.MaximumEntries; index++)
        {
            var observation = state.ObserveContext(options, $"https://cal.example/calendars/{index:D3}/");
            state.ObserveUnavailable(observation);
        }
        var first = state.ObserveContext(options, "https://cal.example/calendars/000/");
        var overflow = state.ObserveContext(options, "https://cal.example/calendars/overflow/");

        state.ObserveUnavailable(overflow);

        state.Count.ShouldBe(CalendarQueryCapabilityState.MaximumEntries);
        state.IsUnavailable(first).ShouldBeTrue();
        state.IsUnavailable(overflow).ShouldBeFalse();
    }

    [Fact]
    public void ChangedCredentialContextRejectsStaleInflightObservationAcrossTransientClients()
    {
        var state = new CalendarQueryCapabilityState();
        const string calendarHref = "https://cal.example/calendars/work/";
        var oldObservation = state.ObserveContext(Options("user-a", "password-a"), calendarHref);
        var currentObservation = state.ObserveContext(Options("user-b", "password-b"), calendarHref);

        state.ObserveUnavailable(oldObservation);

        state.Count.ShouldBe(0);
        state.IsUnavailable(currentObservation).ShouldBeFalse();
        state.IsUnavailable(oldObservation).ShouldBeFalse();
    }

    [Fact]
    public void CredentialFieldsAreCanonicallyDelimitedBeforeOpaqueFingerprinting()
    {
        var state = new CalendarQueryCapabilityState();
        const string calendarHref = "https://cal.example/calendars/work/";
        var first = state.ObserveContext(Options("a\nb", "c"), calendarHref);
        state.ObserveUnavailable(first);

        var second = state.ObserveContext(Options("a", "b\nc"), calendarHref);

        state.IsUnavailable(second).ShouldBeFalse();
        state.IsUnavailable(first).ShouldBeFalse();
        state.Count.ShouldBe(0);
    }

    private static CalDavOptions Options(string username, string password) => new()
    {
        BaseUrl = "https://cal.example",
        Username = username,
        Password = password
    };
}
