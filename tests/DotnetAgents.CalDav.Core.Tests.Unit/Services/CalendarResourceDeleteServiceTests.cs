using System.Text;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarResourceDeleteServiceTests
{
    [Theory]
    [InlineData("", CalendarResourceDeleteCode.InvalidInput)]
    [InlineData("r1", CalendarResourceDeleteCode.InvalidInput)]
    [InlineData("W/\"r1\"", CalendarResourceDeleteCode.ConcurrencyUnavailable)]
    [InlineData("*", CalendarResourceDeleteCode.InvalidInput)]
    public async Task DeleteResourceAsync_RejectsInvalidOrWeakRevisionBeforeAnyNetwork(
        string entityTag,
        CalendarResourceDeleteCode expectedCode)
    {
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client);

        var result = await sut.DeleteResourceAsync(
            new CalendarResourceRevisionReference(
                "https://cal.example/tasks/reviewed.ics",
                "reviewed-delete",
                CalendarEntityKind.Todo,
                entityTag),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetCalendarResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().DeleteCalendarResourceAsync(
            Arg.Any<CalendarResourceDeleteRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("https://other.example/tasks/reviewed.ics")]
    [InlineData("HTTPS://cal.example/tasks/reviewed.ics")]
    [InlineData("https://user@cal.example/tasks/reviewed.ics")]
    [InlineData("https://cal.example/tasks/reviewed.ics?view=full")]
    [InlineData("https://cal.example/tasks/reviewed.ics#component")]
    [InlineData("https://cal.example/tasks%2Freviewed.ics")]
    [InlineData("https://cal.example/tasks%5Creviewed.ics")]
    public async Task DeleteResourceAsync_RejectsUnsafeAbsoluteHrefBeforeDelete(string href)
    {
        var client = Substitute.For<ICalendarClient>();
        var sut = CreateService(client);

        var result = await sut.DeleteResourceAsync(
            new CalendarResourceRevisionReference(
                href,
                "reviewed-delete",
                CalendarEntityKind.Todo,
                "\"r1\""),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceDeleteCode.InvalidInput);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetCalendarResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().DeleteCalendarResourceAsync(
            Arg.Any<CalendarResourceDeleteRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("uid", CalendarResourceDeleteCode.Conflict)]
    [InlineData("kind", CalendarResourceDeleteCode.EntityKindMismatch)]
    [InlineData("tag", CalendarResourceDeleteCode.Conflict)]
    public async Task DeleteResourceAsync_RefetchesAndRejectsChangedRevisionOrOwnershipBeforeDelete(
        string changedField,
        CalendarResourceDeleteCode expectedCode)
    {
        const string calendarHref = "https://cal.example/tasks/";
        const string resourceHref = "https://cal.example/tasks/reviewed.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([TodoCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(resourceHref, "\"current\"", "current-uid"));
        var sut = CreateService(client);
        var revision = new CalendarResourceRevisionReference(
            resourceHref,
            changedField == "uid" ? "reviewed-uid" : "current-uid",
            changedField == "kind" ? CalendarEntityKind.Event : CalendarEntityKind.Todo,
            changedField == "tag" ? "\"reviewed\"" : "\"current\"");

        var result = await sut.DeleteResourceAsync(revision, CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.CurrentSnapshot.ShouldNotBeNull();
        result.CurrentSnapshot.EntityTag.ShouldBe("\"current\"");
        await client.DidNotReceive().DeleteCalendarResourceAsync(
            Arg.Any<CalendarResourceDeleteRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("absent", CalendarResourceDeleteCode.Success, CalendarMutationState.Committed)]
    [InlineData("unchanged", CalendarResourceDeleteCode.UpstreamUnavailable, CalendarMutationState.NotCommitted)]
    [InlineData("changed", CalendarResourceDeleteCode.Indeterminate, CalendarMutationState.Unknown)]
    [InlineData("unavailable", CalendarResourceDeleteCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task DeleteResourceAsync_ReconcilesPossiblyDispatchedDeleteWithoutRetry(
        string observation,
        CalendarResourceDeleteCode expectedCode,
        CalendarMutationState expectedState)
    {
        const string calendarHref = "https://cal.example/tasks/";
        const string resourceHref = "https://cal.example/tasks/reviewed.ics";
        const string entityTag = "\"r1\"";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([TodoCalendar(calendarHref)]);
        var observed = observation switch
        {
            "absent" => new CalendarResourceRead(CalendarResourceReadCode.NotFound),
            "unchanged" => Resource(resourceHref, entityTag, "reviewed-delete"),
            "changed" => Resource(resourceHref, "\"r2\"", "reviewed-delete"),
            _ => new CalendarResourceRead(CalendarResourceReadCode.UpstreamProtocolError)
        };
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(resourceHref, entityTag, "reviewed-delete"),
            observed);
        client.DeleteCalendarResourceAsync(Arg.Any<CalendarResourceDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceDeleteDispatchResult(CalendarResourceDeleteDispatchCode.PossiblyDispatched));
        var sut = CreateService(client);

        var result = await sut.DeleteResourceAsync(
            new CalendarResourceRevisionReference(
                resourceHref,
                "reviewed-delete",
                CalendarEntityKind.Todo,
                entityTag),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
        (result.DeletionReceipt is not null).ShouldBe(observation == "absent");
        await client.Received(1).DeleteCalendarResourceAsync(
            Arg.Any<CalendarResourceDeleteRequest>(),
            Arg.Any<CancellationToken>());
        await client.Received(2).GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceDeleteDispatchCode.NotFound, CalendarResourceDeleteCode.NotFound)]
    [InlineData(CalendarResourceDeleteDispatchCode.Conflict, CalendarResourceDeleteCode.Conflict)]
    [InlineData(CalendarResourceDeleteDispatchCode.UnsupportedCapability, CalendarResourceDeleteCode.UnsupportedCapability)]
    [InlineData(CalendarResourceDeleteDispatchCode.PayloadTooLarge, CalendarResourceDeleteCode.PayloadTooLarge)]
    [InlineData(CalendarResourceDeleteDispatchCode.UpstreamUnauthorized, CalendarResourceDeleteCode.UpstreamUnauthorized)]
    [InlineData(CalendarResourceDeleteDispatchCode.UpstreamForbidden, CalendarResourceDeleteCode.UpstreamForbidden)]
    [InlineData(CalendarResourceDeleteDispatchCode.UpstreamRateLimited, CalendarResourceDeleteCode.UpstreamRateLimited)]
    [InlineData(CalendarResourceDeleteDispatchCode.UpstreamUnavailable, CalendarResourceDeleteCode.UpstreamUnavailable)]
    [InlineData(CalendarResourceDeleteDispatchCode.UpstreamProtocolError, CalendarResourceDeleteCode.UpstreamProtocolError)]
    public async Task DeleteResourceAsync_MapsDefinitiveDeleteRejectionAsNotCommitted(
        CalendarResourceDeleteDispatchCode dispatchCode,
        CalendarResourceDeleteCode expectedCode)
    {
        const string calendarHref = "https://cal.example/tasks/";
        const string resourceHref = "https://cal.example/tasks/reviewed.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([TodoCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(resourceHref, "\"r1\"", "reviewed-delete"));
        client.DeleteCalendarResourceAsync(Arg.Any<CalendarResourceDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceDeleteDispatchResult(dispatchCode));
        var sut = CreateService(client);

        var result = await sut.DeleteResourceAsync(
            new CalendarResourceRevisionReference(
                resourceHref,
                "reviewed-delete",
                CalendarEntityKind.Todo,
                "\"r1\""),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        await client.Received(1).DeleteCalendarResourceAsync(
            Arg.Any<CalendarResourceDeleteRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteResourceAsync_PreservesRateLimitRetryAfterAsNotCommitted()
    {
        const string calendarHref = "https://cal.example/tasks/";
        const string resourceHref = "https://cal.example/tasks/reviewed.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([TodoCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(resourceHref, "\"r1\"", "reviewed-delete"));
        client.DeleteCalendarResourceAsync(Arg.Any<CalendarResourceDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceDeleteDispatchResult(
                CalendarResourceDeleteDispatchCode.UpstreamRateLimited,
                RetryAfterMilliseconds: 3_000));
        var sut = CreateService(client);

        var result = await sut.DeleteResourceAsync(
            new CalendarResourceRevisionReference(
                resourceHref,
                "reviewed-delete",
                CalendarEntityKind.Todo,
                "\"r1\""),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceDeleteCode.UpstreamRateLimited);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        result.RetryAfterMilliseconds.ShouldBe(3_000);
    }

    [Fact]
    public async Task DeleteResourceAsync_ConflictRefetchesCurrentAuthorizedSnapshot()
    {
        const string calendarHref = "https://cal.example/tasks/";
        const string resourceHref = "https://cal.example/tasks/reviewed.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([TodoCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(resourceHref, "\"r1\"", "reviewed-delete"),
            Resource(resourceHref, "\"r2\"", "reviewed-delete"));
        client.DeleteCalendarResourceAsync(Arg.Any<CalendarResourceDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceDeleteDispatchResult(CalendarResourceDeleteDispatchCode.Conflict));
        var sut = CreateService(client);

        var result = await sut.DeleteResourceAsync(
            new CalendarResourceRevisionReference(
                resourceHref,
                "reviewed-delete",
                CalendarEntityKind.Todo,
                "\"r1\""),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceDeleteCode.Conflict);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        result.CurrentSnapshot.ShouldNotBeNull().EntityTag.ShouldBe("\"r2\"");
        await client.Received(2).GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>());
        await client.Received(1).DeleteCalendarResourceAsync(
            Arg.Any<CalendarResourceDeleteRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteResourceAsync_UsesReviewedStrongRevisionAndReturnsReceiptOnlyAfterVerifiedAbsence()
    {
        const string calendarHref = "https://cal.example/tasks/";
        const string resourceHref = "https://cal.example/tasks/reviewed.ics";
        const string entityTag = "\"r1\"";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([TodoCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            Resource(resourceHref, entityTag, "reviewed-delete"),
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.DeleteCalendarResourceAsync(Arg.Any<CalendarResourceDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceDeleteDispatchResult(CalendarResourceDeleteDispatchCode.Dispatched));
        var sut = CreateService(client);
        var revision = new CalendarResourceRevisionReference(
            resourceHref,
            "reviewed-delete",
            CalendarEntityKind.Todo,
            entityTag);

        var result = await sut.DeleteResourceAsync(revision, CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceDeleteCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.DeletionReceipt.ShouldBe(new CalendarResourceDeletionReceipt(
            resourceHref,
            "reviewed-delete",
            CalendarEntityKind.Todo,
            entityTag));
        await client.Received(1).DeleteCalendarResourceAsync(
            new CalendarResourceDeleteRequest(resourceHref, entityTag),
            Arg.Any<CancellationToken>());
        await client.Received(2).GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteResourceAsync_DefiniteDispatchVerificationOutlivesCallerCancellation()
    {
        const string calendarHref = "https://cal.example/tasks/";
        const string resourceHref = "https://cal.example/tasks/reviewed.ics";
        using var caller = new CancellationTokenSource();
        var readTokens = new List<CancellationToken>();
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([TodoCalendar(calendarHref)]);
        var readIndex = 0;
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(call =>
        {
            readTokens.Add(call.Arg<CancellationToken>());
            return readIndex++ == 0
                ? Resource(resourceHref, "\"r1\"", "reviewed-delete")
                : new CalendarResourceRead(CalendarResourceReadCode.NotFound);
        });
        client.DeleteCalendarResourceAsync(Arg.Any<CalendarResourceDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                caller.Cancel();
                return new CalendarResourceDeleteDispatchResult(CalendarResourceDeleteDispatchCode.Dispatched);
            });
        var sut = CreateService(client);

        var result = await sut.DeleteResourceAsync(
            new CalendarResourceRevisionReference(
                resourceHref,
                "reviewed-delete",
                CalendarEntityKind.Todo,
                "\"r1\""),
            caller.Token);

        result.Code.ShouldBe(CalendarResourceDeleteCode.Success);
        readTokens.Count.ShouldBe(2);
        readTokens[0].CanBeCanceled.ShouldBeTrue();
        readTokens[1].IsCancellationRequested.ShouldBeFalse();
    }

    [Theory]
    [InlineData("io")]
    [InlineData("xml")]
    [InlineData("protocol")]
    [InlineData("unsupported")]
    [InlineData("unexpected")]
    public async Task DeleteResourceAsync_DefiniteDispatchVerificationFailureIsCommittedButUnverified(
        string failure)
    {
        const string calendarHref = "https://cal.example/tasks/";
        const string resourceHref = "https://cal.example/tasks/reviewed.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([TodoCalendar(calendarHref)]);
        var readIndex = 0;
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(_ =>
            readIndex++ == 0
                ? Task.FromResult(Resource(resourceHref, "\"r1\"", "reviewed-delete"))
                : Task.FromException<CalendarResourceRead>(failure switch
                {
                    "io" => new IOException("secret transport failure"),
                    "xml" => new System.Xml.XmlException("secret xml failure"),
                    "protocol" => new CalendarDiscoveryProtocolException("secret protocol failure"),
                    "unsupported" => new CalendarDiscoveryUnsupportedCapabilityException("secret capability failure"),
                    _ => new InvalidOperationException("secret unexpected failure")
                }));
        client.DeleteCalendarResourceAsync(Arg.Any<CalendarResourceDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceDeleteDispatchResult(CalendarResourceDeleteDispatchCode.Dispatched));
        var sut = CreateService(client);

        var result = await sut.DeleteResourceAsync(
            new CalendarResourceRevisionReference(
                resourceHref,
                "reviewed-delete",
                CalendarEntityKind.Todo,
                "\"r1\""),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceDeleteCode.CommittedButUnverified);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.DeletionReceipt.ShouldBeNull();
    }

    [Theory]
    [InlineData("io", CalendarResourceDeleteCode.UpstreamUnavailable)]
    [InlineData("cancel", CalendarResourceDeleteCode.UpstreamUnavailable)]
    [InlineData("protocol", CalendarResourceDeleteCode.UpstreamProtocolError)]
    [InlineData("unsupported", CalendarResourceDeleteCode.UnsupportedCapability)]
    public async Task DeleteResourceAsync_PreflightFailureIsNotAttemptedAndNeverDeletes(
        string failure,
        CalendarResourceDeleteCode expectedCode)
    {
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<CalendarDescriptor>>(_ =>
            throw failure switch
            {
                "io" => new IOException("secret io"),
                "cancel" => new OperationCanceledException("secret cancel"),
                "protocol" => new CalendarDiscoveryProtocolException("secret protocol"),
                _ => new CalendarDiscoveryUnsupportedCapabilityException("secret unsupported")
            });
        var sut = CreateService(client);

        var result = await sut.DeleteResourceAsync(
            new CalendarResourceRevisionReference(
                "https://cal.example/tasks/reviewed.ics",
                "reviewed-delete",
                CalendarEntityKind.Todo,
                "\"r1\""),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().DeleteCalendarResourceAsync(
            Arg.Any<CalendarResourceDeleteRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(401, CalendarResourceDeleteCode.UpstreamUnauthorized, false)]
    [InlineData(403, CalendarResourceDeleteCode.UpstreamForbidden, false)]
    [InlineData(404, CalendarResourceDeleteCode.UpstreamProtocolError, false)]
    [InlineData(409, CalendarResourceDeleteCode.Conflict, false)]
    [InlineData(412, CalendarResourceDeleteCode.Conflict, false)]
    [InlineData(413, CalendarResourceDeleteCode.PayloadTooLarge, false)]
    [InlineData(429, CalendarResourceDeleteCode.UpstreamRateLimited, true)]
    [InlineData(405, CalendarResourceDeleteCode.UnsupportedCapability, false)]
    [InlineData(501, CalendarResourceDeleteCode.UnsupportedCapability, false)]
    [InlineData(507, CalendarResourceDeleteCode.UpstreamUnavailable, false)]
    [InlineData(503, CalendarResourceDeleteCode.UpstreamUnavailable, true)]
    public async Task DeleteResourceAsync_MapsPreflightHttpStatusWithoutDelete(
        int statusCode,
        CalendarResourceDeleteCode expectedCode,
        bool expectedRetryable)
    {
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<CalendarDescriptor>>(_ =>
            throw new HttpRequestException(
                "secret upstream response",
                inner: null,
                (System.Net.HttpStatusCode)statusCode));
        var sut = CreateService(client);

        var result = await sut.DeleteResourceAsync(
            new CalendarResourceRevisionReference(
                "https://cal.example/tasks/reviewed.ics",
                "reviewed-delete",
                CalendarEntityKind.Todo,
                "\"r1\""),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.Retryable.ShouldBe(expectedRetryable);
        await client.DidNotReceive().DeleteCalendarResourceAsync(
            Arg.Any<CalendarResourceDeleteRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteResourceAsync_FetchedWeakRevisionReturnsConcurrencyUnavailableWithoutDelete()
    {
        const string calendarHref = "https://cal.example/tasks/";
        const string resourceHref = "https://cal.example/tasks/reviewed.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([TodoCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.ConcurrencyUnavailable));
        var sut = CreateService(client);

        var result = await sut.DeleteResourceAsync(
            new CalendarResourceRevisionReference(
                resourceHref,
                "reviewed-delete",
                CalendarEntityKind.Todo,
                "\"r1\""),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarResourceDeleteCode.ConcurrencyUnavailable);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().DeleteCalendarResourceAsync(
            Arg.Any<CalendarResourceDeleteRequest>(),
            Arg.Any<CancellationToken>());
    }

    private static CalendarService CreateService(ICalendarClient client) => new(
        client,
        Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = "user",
            Password = "secret",
            CalendarHrefs = "https://cal.example/tasks/"
        }),
        Substitute.For<ILogger<CalendarService>>());

    private static CalendarDescriptor TodoCalendar(string href) => new()
    {
        Href = href,
        DisplayName = "Tasks",
        DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
        EventSupport = EntityKindSupport.NotAdvertised,
        TodoSupport = EntityKindSupport.Advertised
    };

    private static CalendarResourceRead Resource(string href, string entityTag, string uid)
    {
        var content = Encoding.UTF8.GetBytes(
            $"BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\nBEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260815T120000Z\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");
        return CalendarResourceRead.Success(href, entityTag, content);
    }
}
