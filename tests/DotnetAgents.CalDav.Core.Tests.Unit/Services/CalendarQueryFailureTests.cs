using System.Net;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarQueryFailureTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, QueryFailureCode.UpstreamUnauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, QueryFailureCode.UpstreamForbidden, false)]
    [InlineData(HttpStatusCode.TooManyRequests, QueryFailureCode.UpstreamRateLimited, true)]
    [InlineData(HttpStatusCode.MethodNotAllowed, QueryFailureCode.UnsupportedCapability, false)]
    [InlineData(HttpStatusCode.NotImplemented, QueryFailureCode.UnsupportedCapability, false)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, QueryFailureCode.PayloadTooLarge, false)]
    [InlineData(HttpStatusCode.RequestTimeout, QueryFailureCode.UpstreamUnavailable, true)]
    [InlineData(HttpStatusCode.InternalServerError, QueryFailureCode.UpstreamUnavailable, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, QueryFailureCode.UpstreamUnavailable, true)]
    [InlineData(HttpStatusCode.NotFound, QueryFailureCode.UpstreamProtocolError, false)]
    public void FromHttpMapsEveryStatusFamilyWithoutLeakingTransportText(
        HttpStatusCode status,
        QueryFailureCode expected,
        bool retryable)
    {
        var failure = CalendarQueryFailures.FromHttp(status);

        failure.Code.ShouldBe(expected);
        failure.Retryable.ShouldBe(retryable);
        failure.Message.ShouldNotContain("http", Case.Insensitive);
    }

    [Fact]
    public void FromHttpWithoutStatusIsRetryableUnavailable()
    {
        var failure = CalendarQueryFailures.FromHttp(null);

        failure.Code.ShouldBe(QueryFailureCode.UpstreamUnavailable);
        failure.Retryable.ShouldBe(true);
    }

    [Fact]
    public void CandidateBearingFailuresBoundAndFreezeAuthorizedEvidence()
    {
        var candidates = Enumerable.Range(0, 40).Select(index => new CalendarDescriptor
        {
            Href = $"https://cal.example/calendars/{index:D2}/",
            DisplayName = $"Calendar {index:D2}",
            DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
            EventSupport = EntityKindSupport.Advertised,
            TodoSupport = EntityKindSupport.NotAdvertised,
            EventEvidence = [new CapabilityEvidence("probe", "event")],
            TodoEvidence = []
        }).ToArray();

        var notFound = CalendarQueryFailures.NotFound([]);
        var ambiguous = CalendarQueryFailures.Ambiguous(candidates);
        var outside = CalendarQueryFailures.OutsideScope(candidates);

        notFound.AuthorizedCandidates.ShouldBeNull();
        ambiguous.AuthorizedCandidates!.Count.ShouldBe(32);
        outside.AuthorizedCandidates!.Count.ShouldBe(32);
        ambiguous.AuthorizedCandidates[0].CalendarHref.ShouldBe(candidates[0].Href);
        outside.Phase.ShouldBe(QueryFailurePhase.OriginScopeAuthorization);
    }

    [Fact]
    public void FailureFactoriesExposeClosedCategoriesPhasesAndOptionalLimits()
    {
        var failures = new[]
        {
            CalendarQueryFailures.InvalidInput(),
            CalendarQueryFailures.InvalidCursor(),
            CalendarQueryFailures.UnsafeHref(),
            CalendarQueryFailures.CursorExpired(),
            CalendarQueryFailures.Limit("limit", new QueryExecutionLimits(ItemCount: 1)),
            CalendarQueryFailures.Busy(10),
            CalendarQueryFailures.PayloadTooLarge("large"),
            CalendarQueryFailures.PayloadTooLarge("large", 32),
            CalendarQueryFailures.Protocol(),
            CalendarQueryFailures.UnsupportedCapability(),
            CalendarQueryFailures.ConcurrencyUnavailable(),
            CalendarQueryFailures.TemporalUnresolved(),
            CalendarQueryFailures.RecurrenceUnevaluable(),
            CalendarQueryFailures.UpstreamUnavailable()
        };

        failures.Length.ShouldBe(14);
        failures.ShouldAllBe(failure => Enum.IsDefined(failure.Category) && Enum.IsDefined(failure.Phase));
        failures.Single(failure => failure.Code == QueryFailureCode.Busy).RetryAfterMs.ShouldBe(10);
        failures.Count(failure => failure.Code == QueryFailureCode.PayloadTooLarge).ShouldBe(2);
    }
}
