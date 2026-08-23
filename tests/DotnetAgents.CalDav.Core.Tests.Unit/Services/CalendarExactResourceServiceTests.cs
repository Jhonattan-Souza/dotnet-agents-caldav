using System.Text;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarExactResourceServiceTests
{
    [Fact]
    public async Task ExactCreateResourceAsync_InvalidMultipleMastersWritesNothingAfterScopedDiscovery()
    {
        const string destinationHref = "https://cal.example/events/invalid.ics";
        var invalid = Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Exact Tests//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:duplicate-master\r\nDTSTAMP:20260817T120000Z\r\nDTSTART:20260818T120000Z\r\nEND:VEVENT\r\n"
            + "BEGIN:VEVENT\r\nUID:duplicate-master\r\nDTSTAMP:20260817T120000Z\r\nDTSTART:20260818T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar("https://cal.example/events/")]);
        var sut = CreateService(client, "https://cal.example/events/");

        var result = await sut.ExactCreateResourceAsync(
            new CalendarExactCreateRequest(destinationHref, invalid),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.InvalidCalendarData);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.Received(1).GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("create")]
    [InlineData("replace")]
    public async Task ExactWrite_RejectsPathologicalComponentDepthWithoutDispatch(string operation)
    {
        const string calendarHref = "https://cal.example/events/";
        const string href = "https://cal.example/events/depth.ics";
        var content = new StringBuilder(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Exact Tests//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:depth\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\n");
        for (var depth = 0; depth < 128; depth++)
            content.Append("BEGIN:X-NEST\r\n");
        for (var depth = 0; depth < 128; depth++)
            content.Append("END:X-NEST\r\n");
        content.Append("END:VCALENDAR\r\n");
        var pathological = Encoding.UTF8.GetBytes(content.ToString());
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>()).Returns(
            operation == "create"
                ? new CalendarResourceRead(CalendarResourceReadCode.NotFound)
                : CalendarResourceRead.Success(href, "\"r1\"", EventResource("depth", "Before")));
        var service = CreateService(client, calendarHref);

        var result = operation == "create"
            ? await service.ExactCreateResourceAsync(
                new CalendarExactCreateRequest(href, pathological), CancellationToken.None)
            : await service.ExactReplaceResourceAsync(
                new CalendarExactReplaceRequest(Revision(href, "depth"), pathological), CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.InvalidCalendarData);
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactCreateResourceAsync_RejectsOutOfScopeBeforeCompleteResourceSemantics()
    {
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar("https://cal.example/events/")]);

        var result = await CreateService(client, "https://cal.example/events/").ExactCreateResourceAsync(
            new CalendarExactCreateRequest(
                "https://cal.example/outside/invalid.ics",
                Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.OutsideScope);
        result.Phase.ShouldBe(CalendarExactResourcePhase.OriginScopeAuthorization);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetCalendarResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("https://other.example/events/")]
    [InlineData("https://cal.example/tasks/")]
    [InlineData("https://cal.example/events/nested/")]
    public async Task ExactCreateResourceAsync_RejectsNonMatchingConfiguredScopeWithoutDiscovery(string configuredScope)
    {
        var client = Substitute.For<ICalendarClient>();

        var result = await CreateService(client, configuredScope).ExactCreateResourceAsync(
            new CalendarExactCreateRequest(
                "https://cal.example/events/scoped.ics",
                EventResource("configured-scope", "Scoped")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.OutsideScope);
        result.Phase.ShouldBe(CalendarExactResourcePhase.OriginScopeAuthorization);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactReplaceResourceAsync_RejectsConfiguredScopeMissBeforeDiscovery()
    {
        const string scopedCalendarHref = "https://cal.example/events/";
        const string outsideHref = "https://cal.example/outside/source.ics";
        var client = Substitute.For<ICalendarClient>();
        var service = CreateService(client, scopedCalendarHref);

        var result = await service.ExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(
                Revision(outsideHref, "outside"),
                EventResource("outside", "After")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.OutsideScope);
        result.Phase.ShouldBe(CalendarExactResourcePhase.OriginScopeAuthorization);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("relative.ics")]
    [InlineData("ftp://cal.example/events/a.ics")]
    [InlineData("http://cal.example/events/a.ics")]
    [InlineData("https://user@cal.example/events/a.ics")]
    [InlineData("https://cal.example/events/a.ics?query=1")]
    [InlineData("https://cal.example/events/a.ics#fragment")]
    [InlineData("https://cal.example/events%2Fa.ics")]
    [InlineData("https://cal.example/events%5Ca.ics")]
    [InlineData("https://cal.example/events/%2e/a.ics")]
    [InlineData("https://other.example/events/a.ics")]
    public async Task ExactCreateResourceAsync_RejectsUnsafeDestinationBeforeDiscovery(string destinationHref)
    {
        var client = Substitute.For<ICalendarClient>();

        var result = await CreateService(client, "https://cal.example/events/").ExactCreateResourceAsync(
            new CalendarExactCreateRequest(destinationHref, EventResource("unsafe", "Unsafe")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.InvalidInput);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("not-a-calendar-href", "https://cal.example/events/a.ics")]
    [InlineData("https://other.example/events/", "https://cal.example/events/a.ics")]
    [InlineData("https://cal.example/events/", "https://cal.example/events/nested/a.ics")]
    public async Task ExactCreateResourceAsync_RejectsDiscoveredCalendarThatDoesNotDirectlyOwnDestination(
        string discoveredCalendarHref,
        string destinationHref)
    {
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(discoveredCalendarHref)]);

        var result = await CreateService(client, string.Empty).ExactCreateResourceAsync(
            new CalendarExactCreateRequest(destinationHref, EventResource("discovered-owner", "Owner")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.OutsideScope);
        await client.DidNotReceive().GetCalendarResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(-1, CalendarExactResourceCode.UpstreamProtocolError)]
    [InlineData(0, CalendarExactResourceCode.UpstreamProtocolError)]
    [InlineData(1, CalendarExactResourceCode.PayloadTooLarge)]
    public async Task ExactCreateResourceAsync_EnforcesExactPayloadBounds(
        int extraByte,
        CalendarExactResourceCode expectedCode)
    {
        const string calendarHref = "https://cal.example/events/";
        const string destinationHref = "https://cal.example/events/bounds.ics";
        var client = PreparedCreateClient(calendarHref, destinationHref);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(CalendarResourceCreateCode.InvalidInput, destinationHref));
        var content = ExactSizedEvent((4 * 1024 * 1024) + extraByte);

        var result = await CreateService(client, calendarHref).ExactCreateResourceAsync(
            new CalendarExactCreateRequest(destinationHref, content),
            CancellationToken.None);

        content.Length.ShouldBe((4 * 1024 * 1024) + extraByte);
        if (extraByte <= 0)
            result.Phase.ShouldBe(CalendarExactResourcePhase.Execution);
        result.Code.ShouldBe(expectedCode);
        if (extraByte <= 0)
            await client.Received(1).CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>());
        else
            await client.DidNotReceive().CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>());
    }

    private static byte[] ExactSizedEvent(int targetBytes)
    {
        const string prefix = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Exact Tests//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:bounds\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART:20260818T120000Z\r\nX-PAD:";
        const string suffix = "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        return Encoding.UTF8.GetBytes(prefix + new string('a', targetBytes - prefix.Length - suffix.Length) + suffix);
    }

    [Fact]
    public async Task ExactCreateResourceAsync_RequiresAdvertisedEntityKind()
    {
        const string calendarHref = "https://cal.example/events/";
        const string destinationHref = "https://cal.example/events/todo.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);

        var result = await CreateService(client, calendarHref).ExactCreateResourceAsync(
            new CalendarExactCreateRequest(destinationHref, TodoResource("todo-capability", "Todo")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.UnsupportedCapability);
        await client.DidNotReceive().GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("relative", "uid", 0, "\"r1\"", CalendarExactResourceCode.InvalidInput)]
    [InlineData("https://cal.example/events/a.ics", " ", 0, "\"r1\"", CalendarExactResourceCode.InvalidInput)]
    [InlineData("https://cal.example/events/a.ics", "uid", 99, "\"r1\"", CalendarExactResourceCode.InvalidInput)]
    [InlineData("https://cal.example/events/a.ics", "uid", 0, "malformed", CalendarExactResourceCode.InvalidInput)]
    [InlineData("https://cal.example/events/a.ics", "uid", 0, "*", CalendarExactResourceCode.InvalidInput)]
    [InlineData("https://cal.example/events/a.ics", "uid", 0, "W/\"r1\"", CalendarExactResourceCode.ConcurrencyUnavailable)]
    public async Task ExactReplaceResourceAsync_RejectsInvalidRevisionShapeBeforeDiscovery(
        string href,
        string uid,
        int kind,
        string entityTag,
        CalendarExactResourceCode expectedCode)
    {
        var client = Substitute.For<ICalendarClient>();
        var revision = new CalendarResourceRevisionReference(href, uid, (CalendarEntityKind)kind, entityTag);

        var result = await CreateService(client, "https://cal.example/events/").ExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(revision, EventResource("uid", "After")), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(0, CalendarExactResourceCode.InvalidInput)]
    [InlineData((4 * 1024 * 1024) + 1, CalendarExactResourceCode.PayloadTooLarge)]
    public async Task ExactReplaceResourceAsync_EnforcesExactPayloadBounds(
        int length,
        CalendarExactResourceCode expectedCode)
    {
        var client = Substitute.For<ICalendarClient>();

        var result = await CreateService(client, "https://cal.example/events/").ExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(
                Revision("https://cal.example/events/bounds.ics", "bounds"),
                new byte[length]),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("weak")]
    [InlineData("outside")]
    public async Task ExactReplaceResourceAsync_RejectsOversizePayloadBeforeRevisionAndAuthorization(string revisionKind)
    {
        var revision = revisionKind switch
        {
            "invalid" => new CalendarResourceRevisionReference("relative", "uid", CalendarEntityKind.Event, "bad"),
            "weak" => new CalendarResourceRevisionReference(
                "https://cal.example/events/a.ics", "uid", CalendarEntityKind.Event, "W/\"r1\""),
            _ => Revision("https://cal.example/outside/a.ics", "uid")
        };
        var client = Substitute.For<ICalendarClient>();

        var result = await CreateService(client, "https://cal.example/events/").ExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(revision, new byte[(4 * 1024 * 1024) + 1]),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.PayloadTooLarge);
        result.Phase.ShouldBe(CalendarExactResourcePhase.SchemaLexicalDiscriminator);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactReplaceResourceAsync_RejectsForeignOriginSourceInAuthorizationPhase()
    {
        var client = Substitute.For<ICalendarClient>();
        var revision = Revision("https://other.example/events/source.ics", "foreign");
        var service = CreateService(client, "https://cal.example/events/");

        var result = await service.ExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(revision, EventResource("foreign", "After")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.InvalidInput);
        result.Phase.ShouldBe(CalendarExactResourcePhase.OriginScopeAuthorization);
        await client.DidNotReceive().GetCalendarsAsync(Arg.Any<CancellationToken>());
        await client.DidNotReceive().GetCalendarResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactCreateResourceAsync_SendsCallerUtf8UnchangedAndReturnsAuthoritativeReadback()
    {
        const string calendarHref = "https://cal.example/events/";
        const string destinationHref = "https://cal.example/events/caller-name.ics";
        var submitted = EventResource("exact-create-1", "Caller exact bytes");
        var observed = EventResource("exact-create-1", "Caller exact bytes");
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound),
            CalendarResourceRead.Success(destinationHref, "\"r1\"", observed));
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(CalendarResourceCreateResult.Dispatched(destinationHref));
        var sut = CreateService(client, calendarHref);

        var result = await sut.ExactCreateResourceAsync(
            new CalendarExactCreateRequest(destinationHref, submitted),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Snapshot.ShouldNotBeNull().ResourceHref.ShouldBe(destinationHref);
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Is<CalendarResourceCreateRequest>(request =>
                request.CalendarHref == calendarHref
                && request.ResourceHref == destinationHref
                && request.AuthoritativeUtf8.ToArray().SequenceEqual(submitted)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewExactCreateResourceAsync_ValidatesIntentWithoutDispatch()
    {
        const string calendarHref = "https://cal.example/events/";
        const string destinationHref = "https://cal.example/events/review.ics";
        var client = PreparedCreateClient(calendarHref, destinationHref);

        var review = await CreateService(client, calendarHref).ReviewExactCreateResourceAsync(
            new CalendarExactCreateRequest(destinationHref, EventResource("review-create", "Review")),
            CancellationToken.None);

        review.Outcome.ShouldBeNull();
        review.Binding.ShouldNotBeNull();
        review.Binding.DestinationHref.ShouldBe(destinationHref);
        review.Binding.EntityUid.ShouldBe("review-create");
        review.Binding.EntityKind.ShouldBe(CalendarEntityKind.Event);
        review.Binding.IntentDigest.Length.ShouldBe(32);
        review.Binding.PolicyVersion.ShouldBe("1");
        review.ReviewedCreate.ShouldNotBeNull().Binding.IntentDigest.ToArray()
            .ShouldBe(review.Binding.IntentDigest.ToArray());
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactCreateResourceAsync_ReviewedIntentOwnsImmutableAuthoritativeBytes()
    {
        const string calendarHref = "https://cal.example/events/";
        const string destinationHref = "https://cal.example/events/immutable-review.ics";
        var callerBuffer = EventResource("immutable-review", "Reviewed");
        var reviewedBytes = callerBuffer.ToArray();
        CalendarResourceCreateRequest? dispatched = null;
        var client = PreparedCreateClient(calendarHref, destinationHref);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                dispatched = call.Arg<CalendarResourceCreateRequest>();
                return new CalendarResourceCreateResult(CalendarResourceCreateCode.DestinationConflict, destinationHref);
            });
        var service = CreateService(client, calendarHref);
        var review = await service.ReviewExactCreateResourceAsync(
            new CalendarExactCreateRequest(destinationHref, callerBuffer),
            CancellationToken.None);
        callerBuffer.AsSpan().Fill((byte)'X');

        var result = await service.ExactCreateResourceAsync(
            review.ReviewedCreate!,
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.DestinationConflict);
        dispatched.ShouldNotBeNull().AuthoritativeUtf8.ToArray().ShouldBe(reviewedBytes);
    }

    [Theory]
    [InlineData("empty", CalendarExactResourceCode.InvalidInput)]
    [InlineData("policy", CalendarExactResourceCode.InvalidCalendarData)]
    [InlineData("calendar", CalendarExactResourceCode.InvalidCalendarData)]
    [InlineData("digest", CalendarExactResourceCode.InvalidCalendarData)]
    [InlineData("uid", CalendarExactResourceCode.InvalidCalendarData)]
    [InlineData("kind", CalendarExactResourceCode.InvalidCalendarData)]
    public async Task ExactCreateResourceAsync_RejectsInvalidReviewedEvidenceBeforePut(
        string scenario,
        CalendarExactResourceCode expectedCode)
    {
        const string calendarHref = "https://cal.example/events/";
        const string destinationHref = "https://cal.example/events/review-integrity.ics";
        var authoritativeUtf8 = scenario == "empty"
            ? Array.Empty<byte>()
            : EventResource("review-integrity", "Reviewed");
        var binding = new CalendarExactCreateReviewBinding(
            destinationHref,
            scenario == "uid" ? "different-uid" : "review-integrity",
            scenario == "kind" ? CalendarEntityKind.Todo : CalendarEntityKind.Event,
            scenario == "digest"
                ? new byte[32]
                : System.Security.Cryptography.SHA256.HashData(authoritativeUtf8),
            scenario == "policy" ? "different-policy" : "1");
        var reviewed = new CalendarReviewedExactCreate(
            scenario == "calendar" ? "https://cal.example/tasks/" : calendarHref,
            binding,
            authoritativeUtf8);
        var client = Substitute.For<ICalendarClient>();

        var result = await CreateService(client, calendarHref).ExactCreateResourceAsync(
            reviewed,
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(
        CalendarResourceCreateCode.Dispatched,
        "reviewed-readback",
        CalendarExactResourceCode.FidelityFailure,
        CalendarMutationState.Committed)]
    [InlineData(
        CalendarResourceCreateCode.PossiblyDispatched,
        "different-readback-uid",
        CalendarExactResourceCode.Indeterminate,
        CalendarMutationState.Unknown)]
    public async Task ExactCreateResourceAsync_StrongReadbackMismatchPreservesDispatchTruth(
        CalendarResourceCreateCode dispatchCode,
        string observedUid,
        CalendarExactResourceCode expectedCode,
        CalendarMutationState expectedMutationState)
    {
        const string calendarHref = "https://cal.example/events/";
        const string destinationHref = "https://cal.example/events/readback-mismatch.ics";
        var client = PreparedCreateClient(calendarHref, destinationHref);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(dispatchCode, destinationHref));
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound),
            CalendarResourceRead.Success(
                destinationHref,
                "\"r1\"",
                EventResource(observedUid, "Changed by server")));

        var result = await CreateService(client, calendarHref).ExactCreateResourceAsync(
            new CalendarExactCreateRequest(
                destinationHref,
                EventResource("reviewed-readback", "Submitted")),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedMutationState);
    }

    [Fact]
    public async Task ExactReplaceResourceAsync_UsesReviewedStrongTagAndSendsCallerUtf8Unchanged()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/exact.ics";
        var current = EventResource("exact-replace-1", "Before");
        var replacement = EventResource("exact-replace-1", "After");
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", current),
            CalendarResourceRead.Success(resourceHref, "\"r2\"", replacement));
        client.UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        var sut = CreateService(client, calendarHref);

        var result = await sut.ExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(
                new CalendarResourceRevisionReference(
                    resourceHref,
                    "exact-replace-1",
                    CalendarEntityKind.Event,
                    "\"r1\""),
                replacement),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.Success);
        result.Snapshot.ShouldNotBeNull().EntityTag.ShouldBe("\"r2\"");
        await client.Received(1).UpdateCalendarResourceAsync(
            Arg.Is<CalendarResourceUpdateRequest>(request =>
                request.ResourceHref == resourceHref
                && request.EntityTag == "\"r1\""
                && request.AuthoritativeUtf8.ToArray().SequenceEqual(replacement)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("create")]
    [InlineData("replace")]
    public async Task ExactWrite_AcceptsOpaqueResourceWithRfc9253RelationshipsInUnknownComponent(
        string operation)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/opaque.ics";
        var content = OpaqueRfc9253Resource("opaque-write");
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            operation == "create"
                ? new CalendarResourceRead(CalendarResourceReadCode.NotFound)
                : CalendarResourceRead.Success(resourceHref, "\"r1\"", EventResource("opaque-write", "Before")),
            CalendarResourceRead.Success(resourceHref, "\"r2\"", content));
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(CalendarResourceCreateResult.Dispatched(resourceHref));
        client.UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        var service = CreateService(client, calendarHref);

        var result = operation == "create"
            ? await service.ExactCreateResourceAsync(
                new CalendarExactCreateRequest(resourceHref, content), CancellationToken.None)
            : await service.ExactReplaceResourceAsync(
                new CalendarExactReplaceRequest(Revision(resourceHref, "opaque-write"), content), CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.Success);
        result.Snapshot.ShouldNotBeNull().Projection.Kind.ShouldBe(CalendarResourceProjectionKind.Opaque);
        await client.Received(operation == "create" ? 1 : 0).CreateCalendarResourceAsync(
            Arg.Is<CalendarResourceCreateRequest>(request => request.AuthoritativeUtf8.ToArray().SequenceEqual(content)),
            Arg.Any<CancellationToken>());
        await client.Received(operation == "replace" ? 1 : 0).UpdateCalendarResourceAsync(
            Arg.Is<CalendarResourceUpdateRequest>(request => request.AuthoritativeUtf8.ToArray().SequenceEqual(content)),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("create", "name")]
    [InlineData("replace", "parameter")]
    [InlineData("create", "utf8")]
    [InlineData("replace", "utf8")]
    public async Task ExactWrite_AcceptsFoldedNameOrParameterHeaderAndSendsBytesUnchanged(
        string operation,
        string fold)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/folded.ics";
        var content = fold == "utf8"
            ? FoldedUtf8Resource("folded-write")
            : FoldedHeaderResource("folded-write", fold);
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            operation == "create"
                ? new CalendarResourceRead(CalendarResourceReadCode.NotFound)
                : CalendarResourceRead.Success(resourceHref, "\"r1\"", EventResource("folded-write", "Before")),
            CalendarResourceRead.Success(resourceHref, "\"r2\"", content));
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(CalendarResourceCreateResult.Dispatched(resourceHref));
        client.UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        var service = CreateService(client, calendarHref);

        var result = operation == "create"
            ? await service.ExactCreateResourceAsync(
                new CalendarExactCreateRequest(resourceHref, content), CancellationToken.None)
            : await service.ExactReplaceResourceAsync(
                new CalendarExactReplaceRequest(Revision(resourceHref, "folded-write"), content), CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.Success);
        await client.Received(operation == "create" ? 1 : 0).CreateCalendarResourceAsync(
            Arg.Is<CalendarResourceCreateRequest>(request => request.AuthoritativeUtf8.ToArray().SequenceEqual(content)),
            Arg.Any<CancellationToken>());
        await client.Received(operation == "replace" ? 1 : 0).UpdateCalendarResourceAsync(
            Arg.Is<CalendarResourceUpdateRequest>(request => request.AuthoritativeUtf8.ToArray().SequenceEqual(content)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewExactReplaceResourceAsync_ValidatesRevisionAndIntentWithoutDispatch()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/review.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", EventResource("review-replace", "Before")));
        var revision = Revision(resourceHref, "review-replace");

        var review = await CreateService(client, calendarHref).ReviewExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(revision, EventResource("review-replace", "After")),
            CancellationToken.None);

        review.Outcome.ShouldBeNull();
        review.BindingRevision.ShouldBe(revision);
        review.IntentDigest.Length.ShouldBe(32);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactReplaceResourceAsync_OnlyByteIdenticalPayloadIsNoChange()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/exact.ics";
        var current = EventResource("exact-no-change", "Same");
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", current));
        var sut = CreateService(client, calendarHref);

        var result = await sut.ExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(Revision(resourceHref, "exact-no-change"), current),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.NoChange);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("different-uid")]
    [InlineData("different-kind")]
    [InlineData("multiple-masters")]
    public async Task ExactReplaceResourceAsync_InvalidCompleteIdentityWritesNothing(string scenario)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/exact.ics";
        var current = EventResource("exact-identity", "Before");
        var replacement = scenario switch
        {
            "different-uid" => EventResource("other", "After"),
            "different-kind" => TodoResource("exact-identity", "After"),
            _ => MultipleMasterResource("exact-identity")
        };
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", current));

        var result = await CreateService(client, calendarHref).ExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(Revision(resourceHref, "exact-identity"), replacement),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.InvalidCalendarData);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("create", "end-before-start")]
    [InlineData("replace", "end-before-start")]
    [InlineData("create", "date-duration-with-time")]
    [InlineData("replace", "invalid-refresh")]
    [InlineData("create", "invalid-source")]
    [InlineData("replace", "invalid-action")]
    [InlineData("create", "invalid-observance")]
    [InlineData("create", "display-attach")]
    [InlineData("replace", "audio-description")]
    [InlineData("create", "invalid-observance-rrule")]
    [InlineData("replace", "invalid-entity-rrule")]
    [InlineData("create", "invalid-date-until")]
    [InlineData("replace", "invalid-observance-until")]
    [InlineData("create", "invalid-parameter-owner")]
    [InlineData("replace", "override-missing-start")]
    [InlineData("create", "repeated-parameter")]
    [InlineData("replace", "redundant-value")]
    [InlineData("create", "uri-encoding")]
    [InlineData("create", "binary-without-encoding")]
    [InlineData("replace", "styled-without-value")]
    [InlineData("create", "structured-without-format")]
    [InlineData("replace", "link-without-value")]
    [InlineData("create", "invalid-language")]
    [InlineData("replace", "invalid-location-property")]
    [InlineData("create", "source-without-value")]
    [InlineData("replace", "link-without-linkrel")]
    [InlineData("replace", "proximity-without-location")]
    [InlineData("create", "invalid-geo")]
    [InlineData("replace", "invalid-geo")]
    [InlineData("create", "invalid-image-type")]
    [InlineData("replace", "invalid-image-type")]
    [InlineData("create", "invalid-email-owner")]
    [InlineData("replace", "invalid-email-owner")]
    [InlineData("create", "bare-lf")]
    [InlineData("replace", "bare-lf")]
    [InlineData("create", "missing-final-crlf")]
    [InlineData("replace", "missing-final-crlf")]
    [InlineData("create", "rrule-overflow")]
    [InlineData("replace", "rrule-overflow")]
    [InlineData("create", "lowercase-invalid-source")]
    [InlineData("replace", "lowercase-invalid-source")]
    [InlineData("create", "invalid-raw-lines")]
    [InlineData("replace", "invalid-raw-lines")]
    [InlineData("create", "invalid-text-escape")]
    [InlineData("replace", "invalid-text-delimiter")]
    [InlineData("create", "duplicate-root-name-language")]
    [InlineData("replace", "duplicate-root-description-untagged")]
    [InlineData("create", "invalid-status-token")]
    [InlineData("replace", "invalid-action-token")]
    [InlineData("create", "invalid-text-del")]
    [InlineData("replace", "invalid-text-del")]
    [InlineData("create", "invalid-color-name")]
    [InlineData("replace", "invalid-color-number")]
    [InlineData("create", "invalid-parameter-quotes")]
    [InlineData("replace", "invalid-parameter-del")]
    [InlineData("create", "invalid-unknown-del")]
    [InlineData("replace", "invalid-unknown-control")]
    public async Task ExactWrite_RejectsInvalidCompleteResourceWithoutDispatch(string operation, string scenario)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/invalid-temporal.ics";
        var invalidBody = scenario switch
        {
            "date-duration-with-time" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART;VALUE=DATE:20260818\r\nDURATION:PT1H\r\nEND:VEVENT\r\n",
            "invalid-refresh" =>
                "REFRESH-INTERVAL:not-a-duration\r\n"
                + "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\n",
            "invalid-source" =>
                "SOURCE:not a uri\r\n"
                + "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\n",
            "invalid-action" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nBEGIN:VALARM\r\nACTION:bad,value\r\n"
                + "TRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nEND:VALARM\r\nEND:VEVENT\r\n",
            "invalid-observance" =>
                "BEGIN:VTIMEZONE\r\nTZID:Invalid/Zone\r\nBEGIN:STANDARD\r\n"
                + "DTSTART:20260101T000000Z\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\n"
                + "END:STANDARD\r\nEND:VTIMEZONE\r\n"
                + "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\n",
            "display-attach" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\n"
                + "TRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nATTACH:https://cal.example/audio.wav\r\n"
                + "END:VALARM\r\nEND:VEVENT\r\n",
            "audio-description" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nBEGIN:VALARM\r\nACTION:AUDIO\r\n"
                + "TRIGGER:-PT5M\r\nDESCRIPTION:Not allowed\r\nEND:VALARM\r\nEND:VEVENT\r\n",
            "invalid-observance-rrule" =>
                "BEGIN:VTIMEZONE\r\nTZID:Invalid/Zone\r\nBEGIN:STANDARD\r\n"
                + "DTSTART:20260101T000000\r\nRRULE:FREQ=DAILY\r\nTZOFFSETFROM:+0100\r\n"
                + "TZOFFSETTO:+0000\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n"
                + "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\n",
            "invalid-entity-rrule" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nRRULE:FREQ=WEEKLY;BYMONTHDAY=1\r\nEND:VEVENT\r\n",
            "invalid-date-until" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART;VALUE=DATE:20260818\r\nRRULE:FREQ=DAILY;UNTIL=20260820T120000Z\r\nEND:VEVENT\r\n",
            "invalid-observance-until" =>
                "BEGIN:VTIMEZONE\r\nTZID:Invalid/Zone\r\nBEGIN:STANDARD\r\n"
                + "DTSTART:20260101T000000\r\nRRULE:FREQ=YEARLY;UNTIL=20271231T235959\r\n"
                + "TZOFFSETFROM:+0100\r\nTZOFFSETTO:+0000\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n"
                + "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\n",
            "invalid-parameter-owner" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nSUMMARY;SENT-BY=mailto:user@example.com:Invalid\r\nEND:VEVENT\r\n",
            "override-missing-start" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:invalid-temporal\r\n"
                + "DTSTAMP:20260817T120000Z\r\nRECURRENCE-ID:20260818T120000Z\r\nEND:VEVENT\r\n",
            "repeated-parameter" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nATTENDEE;CN=One;CN=Two:mailto:user@example.com\r\nEND:VEVENT\r\n",
            "redundant-value" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nSUMMARY;VALUE=TEXT:Redundant\r\nEND:VEVENT\r\n",
            "uri-encoding" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nATTACH;ENCODING=BASE64:https://cal.example/file\r\nEND:VEVENT\r\n",
            "binary-without-encoding" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nATTACH;VALUE=BINARY:SGVsbG8=\r\nEND:VEVENT\r\n",
            "styled-without-value" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\n"
                + "STYLED-DESCRIPTION;FMTTYPE=text/html:<b>Missing VALUE</b>\r\nEND:VEVENT\r\n",
            "structured-without-format" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\n"
                + "STRUCTURED-DATA;VALUE=TEXT;SCHEMA=\"https://schema.org/Event\":{}\r\nEND:VEVENT\r\n",
            "link-without-value" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nLINK:https://cal.example/details\r\nEND:VEVENT\r\n",
            "invalid-language" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\n"
                + "ATTENDEE;LANGUAGE=en-abcdefghi:mailto:user@example.com\r\nEND:VEVENT\r\n",
            "invalid-location-property" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nBEGIN:VLOCATION\r\nUID:location-1\r\n"
                + "ATTACH:https://cal.example/invalid\r\nEND:VLOCATION\r\nEND:VEVENT\r\n",
            "source-without-value" =>
                "SOURCE:https://cal.example/source.ics\r\n"
                + "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\n",
            "link-without-linkrel" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\n"
                + "LINK;VALUE=URI:https://cal.example/details\r\nEND:VEVENT\r\n",
            "proximity-without-location" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\n"
                + "TRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nPROXIMITY:ARRIVE\r\n"
                + "END:VALARM\r\nEND:VEVENT\r\n",
            "invalid-geo" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\n"
                + "TRIGGER:-PT5M\r\nDESCRIPTION:Reminder\r\nPROXIMITY:ARRIVE\r\n"
                + "BEGIN:VLOCATION\r\nUID:l1\r\nURL:geo:90.00000000000000000000000000001,0\r\n"
                + "END:VLOCATION\r\n"
                + "END:VALARM\r\nEND:VEVENT\r\n",
            "invalid-image-type" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nIMAGE;VALUE=URI;FMTTYPE=application/json:"
                + "https://cal.example/image\r\nEND:VEVENT\r\n",
            "invalid-email-owner" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nSUMMARY;EMAIL=user@example.com:Invalid\r\nEND:VEVENT\r\n",
            "bare-lf" =>
                "BEGIN:VEVENT\nUID:invalid-temporal\nDTSTAMP:20260817T120000Z\n"
                + "DTSTART:20260818T120000Z\nEND:VEVENT\n",
            "missing-final-crlf" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\n",
            "rrule-overflow" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nRRULE:FREQ=DAILY;COUNT=999999999999999999999\r\n"
                + "END:VEVENT\r\n",
            "lowercase-invalid-source" =>
                "source:not a uri\r\nBEGIN:VEVENT\r\nUID:invalid-temporal\r\n"
                + "DTSTAMP:20260817T120000Z\r\nDTSTART:20260818T120000Z\r\nEND:VEVENT\r\n",
            "invalid-raw-lines" =>
                "BEGIN:VEVENT\r\nX.UID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\n",
            "invalid-text-escape" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nSUMMARY:bad\\q\r\nEND:VEVENT\r\n",
            "invalid-text-delimiter" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nSUMMARY:bad,comma\r\nEND:VEVENT\r\n",
            "duplicate-root-name-language" =>
                "NAME;LANGUAGE=en:One\r\nNAME;LANGUAGE=EN:Two\r\n"
                + "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\n",
            "duplicate-root-description-untagged" =>
                "DESCRIPTION:One\r\nDESCRIPTION:Two\r\n"
                + "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\n",
            "invalid-status-token" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nSTATUS:NOT VALID\r\nEND:VEVENT\r\n",
            "invalid-action-token" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nBEGIN:VALARM\r\nACTION:BAD VALUE\r\n"
                + "TRIGGER:-PT5M\r\nEND:VALARM\r\nEND:VEVENT\r\n",
            "invalid-text-del" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nSUMMARY:bad\u007fvalue\r\nEND:VEVENT\r\n",
            "invalid-color-name" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nCOLOR:not-a-css3-color\r\nEND:VEVENT\r\n",
            "invalid-color-number" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nCOLOR:123\r\nEND:VEVENT\r\n",
            "invalid-parameter-quotes" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nSUMMARY;X-P=\"a\"\"b\":Text\r\nEND:VEVENT\r\n",
            "invalid-parameter-del" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nSUMMARY;X-P=bad\u007fvalue:Text\r\nEND:VEVENT\r\n",
            "invalid-unknown-del" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nX-KEEP:bad\u007fvalue\r\nEND:VEVENT\r\n",
            "invalid-unknown-control" =>
                "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nX-KEEP:bad\u001fvalue\r\nEND:VEVENT\r\n",
            _ => "BEGIN:VEVENT\r\nUID:invalid-temporal\r\nDTSTAMP:20260817T120000Z\r\n"
                + "DTSTART:20260818T120000Z\r\nDTEND:20260818T110000Z\r\nEND:VEVENT\r\n"
        };
        var invalidText =
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Exact Tests//EN\r\n"
            + invalidBody
            + "END:VCALENDAR\r\n";
        if (scenario == "missing-final-crlf")
            invalidText = invalidText[..^2];
        var invalid = Encoding.UTF8.GetBytes(invalidText);
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            operation == "create"
                ? new CalendarResourceRead(CalendarResourceReadCode.NotFound)
                : CalendarResourceRead.Success(resourceHref, "\"r1\"", EventResource("invalid-temporal", "Before")));
        var service = CreateService(client, calendarHref);

        var result = operation == "create"
            ? await service.ExactCreateResourceAsync(
                new CalendarExactCreateRequest(resourceHref, invalid), CancellationToken.None)
            : await service.ExactReplaceResourceAsync(
                new CalendarExactReplaceRequest(Revision(resourceHref, "invalid-temporal"), invalid), CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.InvalidCalendarData);
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactReplaceResourceAsync_StaleRevisionReturnsCurrentSnapshotWithoutWrite()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/exact.ics";
        var current = EventResource("exact-stale", "Current");
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r2\"", current));

        var result = await CreateService(client, calendarHref).ExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(Revision(resourceHref, "exact-stale"), EventResource("exact-stale", "After")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.Conflict);
        result.Snapshot.ShouldNotBeNull().EntityTag.ShouldBe("\"r2\"");
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("invalid-current", CalendarExactResourceCode.InvalidCalendarData)]
    [InlineData("kind-changed", CalendarExactResourceCode.EntityKindMismatch)]
    public async Task ExactReplaceResourceAsync_RejectsChangedCurrentIdentity(
        string scenario,
        CalendarExactResourceCode expectedCode)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/current.ics";
        var current = scenario == "invalid-current"
            ? Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n")
            : TodoResource("current", "Changed kind");
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", current));

        var result = await CreateService(client, calendarHref).ExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(Revision(resourceHref, "current"), EventResource("current", "After")),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceCreateCode.DestinationConflict, CalendarExactResourceCode.DestinationConflict)]
    [InlineData(CalendarResourceCreateCode.UidConflict, CalendarExactResourceCode.Conflict)]
    [InlineData(CalendarResourceCreateCode.Conflict, CalendarExactResourceCode.Conflict)]
    [InlineData(CalendarResourceCreateCode.UnsupportedCapability, CalendarExactResourceCode.UnsupportedCapability)]
    [InlineData(CalendarResourceCreateCode.PayloadTooLarge, CalendarExactResourceCode.PayloadTooLarge)]
    [InlineData(CalendarResourceCreateCode.NotFound, CalendarExactResourceCode.NotFound)]
    [InlineData(CalendarResourceCreateCode.UpstreamUnauthorized, CalendarExactResourceCode.UpstreamUnauthorized)]
    [InlineData(CalendarResourceCreateCode.UpstreamForbidden, CalendarExactResourceCode.UpstreamForbidden)]
    [InlineData(CalendarResourceCreateCode.UpstreamRateLimited, CalendarExactResourceCode.UpstreamRateLimited)]
    [InlineData(CalendarResourceCreateCode.UpstreamUnavailable, CalendarExactResourceCode.UpstreamUnavailable)]
    [InlineData(CalendarResourceCreateCode.UpstreamProtocolError, CalendarExactResourceCode.UpstreamProtocolError)]
    public async Task ExactCreateResourceAsync_MapsRejectedDispatch(
        CalendarResourceCreateCode dispatchCode,
        CalendarExactResourceCode expectedCode)
    {
        const string calendarHref = "https://cal.example/events/";
        const string destinationHref = "https://cal.example/events/rejected.ics";
        var client = PreparedCreateClient(calendarHref, destinationHref);
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(dispatchCode, destinationHref));

        var result = await CreateService(client, calendarHref).ExactCreateResourceAsync(
            new CalendarExactCreateRequest(destinationHref, EventResource("exact-rejected", "Rejected")),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
    }

    [Theory]
    [InlineData(CalendarResourceReadCode.Success, CalendarExactResourceCode.DestinationConflict)]
    [InlineData(CalendarResourceReadCode.ConcurrencyUnavailable, CalendarExactResourceCode.DestinationConflict)]
    [InlineData(CalendarResourceReadCode.InvalidInput, CalendarExactResourceCode.InvalidInput)]
    [InlineData(CalendarResourceReadCode.OutsideScope, CalendarExactResourceCode.OutsideScope)]
    [InlineData(CalendarResourceReadCode.PayloadTooLarge, CalendarExactResourceCode.PayloadTooLarge)]
    [InlineData(CalendarResourceReadCode.UnsupportedCapability, CalendarExactResourceCode.UnsupportedCapability)]
    [InlineData(CalendarResourceReadCode.UpstreamProtocolError, CalendarExactResourceCode.UpstreamProtocolError)]
    public async Task ExactCreateResourceAsync_MapsDestinationPreflightRead(
        CalendarResourceReadCode readCode,
        CalendarExactResourceCode expectedCode)
    {
        const string calendarHref = "https://cal.example/events/";
        const string destinationHref = "https://cal.example/events/preflight.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(readCode));

        var result = await CreateService(client, calendarHref).ExactCreateResourceAsync(
            new CalendarExactCreateRequest(destinationHref, EventResource("preflight", "Preflight")),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExactCreateResourceAsync_DoesNotEnumerateUidCandidatesBeforeConditionalPut()
    {
        const string calendarHref = "https://cal.example/events/";
        const string destinationHref = "https://cal.example/events/new.ics";
        const string candidateHref = "https://cal.example/events/existing.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(CalendarResourceCreateCode.UidConflict, destinationHref));

        var result = await CreateService(client, calendarHref).ExactCreateResourceAsync(
            new CalendarExactCreateRequest(destinationHref, EventResource("uid-candidate", "New")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.Conflict);
        await client.DidNotReceive().GetCalendarResourceAsync(candidateHref, Arg.Any<CancellationToken>());
        await client.Received(1).CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceCreateCode.Dispatched, CalendarResourceReadCode.NotFound, CalendarExactResourceCode.CommittedButUnverified, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceCreateCode.PossiblyDispatched, CalendarResourceReadCode.NotFound, CalendarExactResourceCode.Indeterminate, CalendarMutationState.Unknown)]
    [InlineData(CalendarResourceCreateCode.Dispatched, CalendarResourceReadCode.ConcurrencyUnavailable, CalendarExactResourceCode.CommittedButConcurrencyUnavailable, CalendarMutationState.Committed)]
    public async Task ExactCreateResourceAsync_ClassifiesNonSuccessReadback(
        CalendarResourceCreateCode dispatchCode,
        CalendarResourceReadCode readCode,
        CalendarExactResourceCode expectedCode,
        CalendarMutationState expectedState)
    {
        const string calendarHref = "https://cal.example/events/";
        const string destinationHref = "https://cal.example/events/readback.ics";
        var content = EventResource("readback", "Readback");
        var client = PreparedCreateClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound),
            new CalendarResourceRead(readCode) { AuthoritativeUtf8 = content });
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(dispatchCode, destinationHref));

        var result = await CreateService(client, calendarHref).ExactCreateResourceAsync(
            new CalendarExactCreateRequest(destinationHref, content), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
    }

    [Theory]
    [InlineData(CalendarResourceCreateCode.Dispatched, CalendarExactResourceCode.FidelityFailure, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceCreateCode.PossiblyDispatched, CalendarExactResourceCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task ExactCreateResourceAsync_ClassifiesWeakTagReadbackWithDifferentContentByDispatchTruth(
        CalendarResourceCreateCode dispatchCode,
        CalendarExactResourceCode expectedCode,
        CalendarMutationState expectedState)
    {
        const string calendarHref = "https://cal.example/events/";
        const string destinationHref = "https://cal.example/events/readback-fidelity.ics";
        var client = PreparedCreateClient(calendarHref, destinationHref);
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound),
            new CalendarResourceRead(CalendarResourceReadCode.ConcurrencyUnavailable)
            {
                ResourceHref = destinationHref,
                EntityTag = "W/\"weak\"",
                AuthoritativeUtf8 = EventResource("readback-fidelity", "Drifted")
            });
        client.CreateCalendarResourceAsync(Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(dispatchCode, destinationHref));

        var result = await CreateService(client, calendarHref).ExactCreateResourceAsync(
            new CalendarExactCreateRequest(destinationHref, EventResource("readback-fidelity", "Intended")),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
    }

    [Theory]
    [InlineData(CalendarResourceCreateCode.Dispatched, CalendarExactResourceCode.CommittedButUnverified, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceCreateCode.PossiblyDispatched, CalendarExactResourceCode.Indeterminate, CalendarMutationState.Unknown)]
    public async Task ExactCreateResourceAsync_ClassifiesReadbackFailureByDispatchTruth(
        CalendarResourceCreateCode dispatchCode,
        CalendarExactResourceCode expectedCode,
        CalendarMutationState expectedState)
    {
        const string calendarHref = "https://cal.example/events/";
        const string destinationHref = "https://cal.example/events/unverified.ics";
        var client = PreparedCreateClient(calendarHref, destinationHref);
        var readCount = 0;
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(_ =>
            ++readCount == 1
                ? Task.FromResult(new CalendarResourceRead(CalendarResourceReadCode.NotFound))
                : Task.FromException<CalendarResourceRead>(new HttpRequestException("readback failed")));
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceCreateResult(dispatchCode, destinationHref));

        var result = await CreateService(client, calendarHref).ExactCreateResourceAsync(
            new CalendarExactCreateRequest(destinationHref, EventResource("unverified", "Unverified")),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
    }

    [Theory]
    [InlineData(CalendarResourceUpdateDispatchCode.InvalidInput, CalendarExactResourceCode.InvalidInput)]
    [InlineData(CalendarResourceUpdateDispatchCode.NotFound, CalendarExactResourceCode.NotFound)]
    [InlineData(CalendarResourceUpdateDispatchCode.UnsupportedCapability, CalendarExactResourceCode.UnsupportedCapability)]
    [InlineData(CalendarResourceUpdateDispatchCode.PayloadTooLarge, CalendarExactResourceCode.PayloadTooLarge)]
    [InlineData(CalendarResourceUpdateDispatchCode.UpstreamUnauthorized, CalendarExactResourceCode.UpstreamUnauthorized)]
    [InlineData(CalendarResourceUpdateDispatchCode.UpstreamForbidden, CalendarExactResourceCode.UpstreamForbidden)]
    [InlineData(CalendarResourceUpdateDispatchCode.UpstreamRateLimited, CalendarExactResourceCode.UpstreamRateLimited)]
    [InlineData(CalendarResourceUpdateDispatchCode.UpstreamUnavailable, CalendarExactResourceCode.UpstreamUnavailable)]
    [InlineData(CalendarResourceUpdateDispatchCode.UpstreamProtocolError, CalendarExactResourceCode.UpstreamProtocolError)]
    public async Task ExactReplaceResourceAsync_MapsRejectedDispatch(
        CalendarResourceUpdateDispatchCode dispatchCode,
        CalendarExactResourceCode expectedCode)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/rejected.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", EventResource("exact-rejected", "Before")));
        client.UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(dispatchCode));

        var result = await CreateService(client, calendarHref).ExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(Revision(resourceHref, "exact-rejected"), EventResource("exact-rejected", "After")),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
    }

    [Theory]
    [InlineData(CalendarResourceReadCode.InvalidInput, CalendarExactResourceCode.InvalidInput)]
    [InlineData(CalendarResourceReadCode.NotFound, CalendarExactResourceCode.NotFound)]
    [InlineData(CalendarResourceReadCode.ConcurrencyUnavailable, CalendarExactResourceCode.ConcurrencyUnavailable)]
    [InlineData(CalendarResourceReadCode.PayloadTooLarge, CalendarExactResourceCode.PayloadTooLarge)]
    [InlineData(CalendarResourceReadCode.UnsupportedCapability, CalendarExactResourceCode.UnsupportedCapability)]
    [InlineData(CalendarResourceReadCode.UpstreamProtocolError, CalendarExactResourceCode.UpstreamProtocolError)]
    public async Task ExactReplaceResourceAsync_MapsTargetReadFailure(
        CalendarResourceReadCode readCode,
        CalendarExactResourceCode expectedCode)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/read.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(new CalendarResourceRead(readCode));

        var result = await CreateService(client, calendarHref).ExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(Revision(resourceHref, "read"), EventResource("read", "After")),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceUpdateDispatchCode.Dispatched, CalendarResourceReadCode.NotFound, "other", "\"r2\"", CalendarExactResourceCode.CommittedButUnverified, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceUpdateDispatchCode.PossiblyDispatched, CalendarResourceReadCode.NotFound, "other", "\"r2\"", CalendarExactResourceCode.Indeterminate, CalendarMutationState.Unknown)]
    [InlineData(CalendarResourceUpdateDispatchCode.Dispatched, CalendarResourceReadCode.ConcurrencyUnavailable, "intended", "", CalendarExactResourceCode.CommittedButConcurrencyUnavailable, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceUpdateDispatchCode.PossiblyDispatched, CalendarResourceReadCode.ConcurrencyUnavailable, "intended", "", CalendarExactResourceCode.CommittedButConcurrencyUnavailable, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceUpdateDispatchCode.Dispatched, CalendarResourceReadCode.ConcurrencyUnavailable, "other", "W/\"weak\"", CalendarExactResourceCode.FidelityFailure, CalendarMutationState.Committed)]
    [InlineData(CalendarResourceUpdateDispatchCode.PossiblyDispatched, CalendarResourceReadCode.ConcurrencyUnavailable, "other", "W/\"weak\"", CalendarExactResourceCode.Indeterminate, CalendarMutationState.Unknown)]
    [InlineData(CalendarResourceUpdateDispatchCode.PossiblyDispatched, CalendarResourceReadCode.Success, "current", "\"r1\"", CalendarExactResourceCode.UpstreamUnavailable, CalendarMutationState.NotCommitted)]
    [InlineData(CalendarResourceUpdateDispatchCode.PossiblyDispatched, CalendarResourceReadCode.Success, "other", "\"r2\"", CalendarExactResourceCode.Indeterminate, CalendarMutationState.Unknown)]
    [InlineData(CalendarResourceUpdateDispatchCode.Dispatched, CalendarResourceReadCode.Success, "other", "\"r2\"", CalendarExactResourceCode.FidelityFailure, CalendarMutationState.Committed)]
    public async Task ExactReplaceResourceAsync_ClassifiesReadbackTruth(
        CalendarResourceUpdateDispatchCode dispatchCode,
        CalendarResourceReadCode readCode,
        string observedPayload,
        string observedTag,
        CalendarExactResourceCode expectedCode,
        CalendarMutationState expectedState)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/truth.ics";
        var current = EventResource("replace-truth", "Before");
        var intended = EventResource("replace-truth", "After");
        var observed = observedPayload switch
        {
            "intended" => intended,
            "current" => current,
            _ => EventResource("replace-truth", "Drifted")
        };
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", current),
            new CalendarResourceRead(readCode) { ResourceHref = resourceHref, EntityTag = observedTag, AuthoritativeUtf8 = observed });
        client.UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(dispatchCode));

        var result = await CreateService(client, calendarHref).ExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(Revision(resourceHref, "replace-truth"), intended), CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(expectedState);
    }

    [Theory]
    [InlineData(CalendarResourceReadCode.Success, "\"r1\"")]
    [InlineData(CalendarResourceReadCode.ConcurrencyUnavailable, "")]
    public async Task ExactReplaceResourceAsync_PossiblyDispatchedCanonicalOldObservationIsNotCommitted(
        CalendarResourceReadCode observedCode,
        string observedTag)
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/canonical-old.ics";
        var current = EventResource("canonical-old", "Same");
        var intended = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(current).Replace(
            "DTSTART:20260818T120000Z\r\nSUMMARY:Same\r\n",
            "SUMMARY:Same\r\nDTSTART:20260818T120000Z\r\n",
            StringComparison.Ordinal));
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", current),
            new CalendarResourceRead(observedCode)
            {
                ResourceHref = resourceHref,
                EntityTag = observedTag,
                AuthoritativeUtf8 = current
            });
        client.UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client, calendarHref).ExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(Revision(resourceHref, "canonical-old"), intended), CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.UpstreamUnavailable);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
    }

    [Fact]
    public async Task ExactReplaceResourceAsync_PossiblyDispatchedWeakNormalizedIntentIsCommittedWithoutConcurrency()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/normalized-intent.ics";
        var current = EventResource("normalized-intent", "Before");
        var intended = EventResource("normalized-intent", "After");
        var normalizedIntent = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(intended).Replace(
            "DTSTART:20260818T120000Z\r\nSUMMARY:After\r\n",
            "SUMMARY:After\r\nDTSTART:20260818T120000Z\r\n",
            StringComparison.Ordinal));
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", current),
            new CalendarResourceRead(CalendarResourceReadCode.ConcurrencyUnavailable)
            {
                ResourceHref = resourceHref,
                AuthoritativeUtf8 = normalizedIntent
            });
        client.UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.PossiblyDispatched));

        var result = await CreateService(client, calendarHref).ExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(Revision(resourceHref, "normalized-intent"), intended),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.CommittedButConcurrencyUnavailable);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
    }

    [Fact]
    public async Task ExactReplaceResourceAsync_DispatchConflictReturnsObservedCurrentSnapshot()
    {
        const string calendarHref = "https://cal.example/events/";
        const string resourceHref = "https://cal.example/events/conflict.ics";
        var current = EventResource("replace-conflict", "Before");
        var changed = EventResource("replace-conflict", "Concurrent");
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(resourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(resourceHref, "\"r1\"", current),
            CalendarResourceRead.Success(resourceHref, "\"r2\"", changed));
        client.UpdateCalendarResourceAsync(
                Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Conflict));

        var result = await CreateService(client, calendarHref).ExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(Revision(resourceHref, "replace-conflict"), EventResource("replace-conflict", "After")),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarExactResourceCode.Conflict);
        result.Snapshot.ShouldNotBeNull().EntityTag.ShouldBe("\"r2\"");
    }

    [Theory]
    [InlineData("create", System.Net.HttpStatusCode.Unauthorized, CalendarExactResourceCode.UpstreamUnauthorized, false)]
    [InlineData("create", System.Net.HttpStatusCode.Forbidden, CalendarExactResourceCode.UpstreamForbidden, false)]
    [InlineData("create", System.Net.HttpStatusCode.TooManyRequests, CalendarExactResourceCode.UpstreamRateLimited, true)]
    [InlineData("replace", System.Net.HttpStatusCode.Forbidden, CalendarExactResourceCode.UpstreamForbidden, false)]
    [InlineData("create", System.Net.HttpStatusCode.ServiceUnavailable, CalendarExactResourceCode.UpstreamUnavailable, true)]
    [InlineData("create", System.Net.HttpStatusCode.BadRequest, CalendarExactResourceCode.UpstreamProtocolError, false)]
    [InlineData("replace", System.Net.HttpStatusCode.BadRequest, CalendarExactResourceCode.UpstreamProtocolError, false)]
    public async Task ExactWriteReview_MapsHttpFailureWithoutDispatch(
        string operation,
        System.Net.HttpStatusCode statusCode,
        CalendarExactResourceCode expectedCode,
        bool retryable)
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<CalendarDescriptor>>>(
            _ => throw new HttpRequestException("failure", null, statusCode));
        var service = CreateService(client, calendarHref);

        var outcome = operation switch
        {
            "create" => (await service.ReviewExactCreateResourceAsync(
                new CalendarExactCreateRequest(sourceHref, EventResource("review-http", "Review")), CancellationToken.None)).Outcome,
            _ => (await service.ReviewExactReplaceResourceAsync(
                new CalendarExactReplaceRequest(Revision(sourceHref, "review-http"), EventResource("review-http", "After")), CancellationToken.None)).Outcome
        };

        outcome.ShouldNotBeNull().Code.ShouldBe(expectedCode);
        outcome.Retryable.ShouldBe(retryable);
        outcome.Phase.ShouldBe(CalendarExactResourcePhase.SelectionDiscoveryCapability);
    }

    [Theory]
    [InlineData("create", System.Net.HttpStatusCode.Unauthorized, CalendarExactResourceCode.UpstreamUnauthorized)]
    [InlineData("replace", System.Net.HttpStatusCode.Forbidden, CalendarExactResourceCode.UpstreamForbidden)]
    public async Task ExactWriteReview_AttributesTargetGetHttpFailureToRevisionPhase(
        string operation,
        System.Net.HttpStatusCode statusCode,
        CalendarExactResourceCode expectedCode)
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarResourceRead>>(_ => throw new HttpRequestException("failure", null, statusCode));
        var service = CreateService(client, calendarHref);

        var outcome = operation switch
        {
            "create" => (await service.ReviewExactCreateResourceAsync(
                new CalendarExactCreateRequest(sourceHref, EventResource("review-target", "Review")), CancellationToken.None)).Outcome,
            _ => (await service.ReviewExactReplaceResourceAsync(
                new CalendarExactReplaceRequest(Revision(sourceHref, "review-target"), EventResource("review-target", "After")), CancellationToken.None)).Outcome
        };

        outcome.ShouldNotBeNull().Code.ShouldBe(expectedCode);
        outcome.Phase.ShouldBe(CalendarExactResourcePhase.TargetRevision);
    }

    [Theory]
    [InlineData("create", "io", CalendarExactResourceCode.UpstreamUnavailable, true)]
    [InlineData("replace", "io", CalendarExactResourceCode.UpstreamUnavailable, true)]
    [InlineData("create", "timeout", CalendarExactResourceCode.UpstreamUnavailable, true)]
    [InlineData("replace", "timeout", CalendarExactResourceCode.UpstreamUnavailable, true)]
    [InlineData("create", "cancel", CalendarExactResourceCode.UpstreamUnavailable, true)]
    [InlineData("replace", "cancel", CalendarExactResourceCode.UpstreamUnavailable, true)]
    [InlineData("create", "xml", CalendarExactResourceCode.UpstreamProtocolError, false)]
    [InlineData("create", "discovery", CalendarExactResourceCode.UpstreamProtocolError, false)]
    [InlineData("replace", "discovery", CalendarExactResourceCode.UpstreamProtocolError, false)]
    public async Task ExactWriteReview_MapsNonHttpFailureWithoutDispatch(
        string operation,
        string failure,
        CalendarExactResourceCode expectedCode,
        bool retryable)
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<CalendarDescriptor>>>(
            _ => throw CreateFailure(failure));
        var service = CreateService(client, calendarHref);

        var outcome = operation switch
        {
            "create" => (await service.ReviewExactCreateResourceAsync(
                new CalendarExactCreateRequest(sourceHref, EventResource("review-failure", "Review")), CancellationToken.None)).Outcome,
            _ => (await service.ReviewExactReplaceResourceAsync(
                new CalendarExactReplaceRequest(Revision(sourceHref, "review-failure"), EventResource("review-failure", "After")), CancellationToken.None)).Outcome
        };

        outcome.ShouldNotBeNull().Code.ShouldBe(expectedCode);
        outcome.Retryable.ShouldBe(retryable);
        outcome.Phase.ShouldBe(CalendarExactResourcePhase.SelectionDiscoveryCapability);
        await client.DidNotReceive().CreateCalendarResourceAsync(
            Arg.Any<CalendarResourceCreateRequest>(), Arg.Any<CancellationToken>());
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("io", CalendarExactResourceCode.UpstreamUnavailable, true)]
    [InlineData("timeout", CalendarExactResourceCode.UpstreamUnavailable, true)]
    [InlineData("cancel", CalendarExactResourceCode.UpstreamUnavailable, true)]
    [InlineData("xml", CalendarExactResourceCode.UpstreamProtocolError, false)]
    [InlineData("discovery", CalendarExactResourceCode.UpstreamProtocolError, false)]
    public async Task ExactWriteReview_AttributesNonHttpTargetGetFailureToRevisionPhase(
        string failure,
        CalendarExactResourceCode expectedCode,
        bool retryable)
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(sourceHref, Arg.Any<CancellationToken>())
            .Returns<Task<CalendarResourceRead>>(_ => throw CreateFailure(failure));

        var review = await CreateService(client, calendarHref).ReviewExactReplaceResourceAsync(
            new CalendarExactReplaceRequest(
                Revision(sourceHref, "review-target-non-http"),
                EventResource("review-target-non-http", "After")),
            CancellationToken.None);

        review.Outcome.ShouldNotBeNull().Code.ShouldBe(expectedCode);
        review.Outcome.Retryable.ShouldBe(retryable);
        review.Outcome.Phase.ShouldBe(CalendarExactResourcePhase.TargetRevision);
    }

    [Theory]
    [InlineData("create", "http", CalendarExactResourceCode.UpstreamUnauthorized, false)]
    [InlineData("replace", "http", CalendarExactResourceCode.UpstreamUnauthorized, false)]
    [InlineData("create", "io", CalendarExactResourceCode.UpstreamUnavailable, true)]
    [InlineData("replace", "io", CalendarExactResourceCode.UpstreamUnavailable, true)]
    [InlineData("create", "cancel", CalendarExactResourceCode.UpstreamUnavailable, true)]
    [InlineData("replace", "cancel", CalendarExactResourceCode.UpstreamUnavailable, true)]
    [InlineData("create", "xml", CalendarExactResourceCode.UpstreamProtocolError, false)]
    [InlineData("replace", "xml", CalendarExactResourceCode.UpstreamProtocolError, false)]
    public async Task ExactWriteExecution_MapsPreDispatchFailure(
        string operation,
        string failure,
        CalendarExactResourceCode expectedCode,
        bool retryable)
    {
        const string calendarHref = "https://cal.example/events/";
        const string sourceHref = "https://cal.example/events/source.ics";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns<Task<IReadOnlyList<CalendarDescriptor>>>(
            _ => throw CreateFailure(failure));
        var service = CreateService(client, calendarHref);

        var result = operation switch
        {
            "create" => await service.ExactCreateResourceAsync(
                new CalendarExactCreateRequest(sourceHref, EventResource("execution", "Execution")), CancellationToken.None),
            _ => await service.ExactReplaceResourceAsync(
                new CalendarExactReplaceRequest(Revision(sourceHref, "execution"), EventResource("execution", "After")), CancellationToken.None)
        };

        result.Code.ShouldBe(expectedCode);
        result.Retryable.ShouldBe(retryable);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
    }

    [Fact]
    public async Task ExactCreateResourceAsync_ReviewedDispatchDeadlineReportsElapsedTimeDimension()
    {
        const string destinationHref = "https://cal.example/events/deadline.ics";
        var time = new ManualTimeProvider(DateTimeOffset.Parse("2026-08-17T12:00:00Z"));
        var dispatchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = Substitute.For<ICalendarClient>();
        client.CreateCalendarResourceAsync(
                Arg.Any<CalendarResourceCreateRequest>(),
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var cancellationToken = call.ArgAt<CancellationToken>(1);
                var stalled = new TaskCompletionSource<CalendarResourceCreateResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.Register(() => stalled.TrySetCanceled(cancellationToken));
                dispatchStarted.TrySetResult();
                return await stalled.Task;
            });
        var authoritativeUtf8 = EventResource("deadline", "Deadline");
        var binding = new CalendarExactCreateReviewBinding(
            destinationHref,
            "deadline",
            CalendarEntityKind.Event,
            System.Security.Cryptography.SHA256.HashData(authoritativeUtf8),
            "1");
        var reviewed = CalendarReviewedExactCreate.CreateForTest(
            binding,
            authoritativeUtf8);

        var pending = CreateService(client, "https://cal.example/events/", time)
            .ExactCreateResourceAsync(reviewed, TestContext.Current.CancellationToken);
        await dispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(30));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        result.Code.ShouldBe(CalendarExactResourceCode.LimitExhausted);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        result.Limits!.Dimension.ShouldBe(CalendarEntityCreateLimitDimension.ElapsedTime);
    }

    private static CalendarService CreateService(
        ICalendarClient client,
        string calendarHref,
        TimeProvider? timeProvider = null) => new(
        client,
        Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = "user",
            Password = "secret",
            CalendarHrefs = calendarHref,
            InteroperabilityProfile = CalDavInteroperabilityProfiles.Radicale_3_7_8
        }),
        Substitute.For<ILogger<CalendarService>>(),
        timeProvider ?? TimeProvider.System,
        Substitute.For<ICalendarEntityIdentityGenerator>());

    private static async Task<CalendarResourceRead> StallObservation(
        CancellationToken cancellationToken,
        TaskCompletionSource observationStarted)
    {
        var stalled = new TaskCompletionSource<CalendarResourceRead>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(() => stalled.TrySetCanceled(cancellationToken));
        observationStarted.TrySetResult();
        return await stalled.Task;
    }

    private static CalendarDescriptor EventCalendar(string href) => new()
    {
        Href = href,
        DisplayName = "Events",
        DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
        EventSupport = EntityKindSupport.Advertised,
        TodoSupport = EntityKindSupport.NotAdvertised
    };

    private static ICalendarClient PreparedCreateClient(string calendarHref, string destinationHref)
    {
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([EventCalendar(calendarHref)]);
        client.GetCalendarResourceAsync(destinationHref, Arg.Any<CancellationToken>()).Returns(
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        return client;
    }

    private static CalendarResourceRevisionReference Revision(string href, string uid) => new(
        href,
        uid,
        CalendarEntityKind.Event,
        "\"r1\"");

    private static Exception CreateFailure(string failure) => failure switch
    {
        "http" => new HttpRequestException("failure", null, System.Net.HttpStatusCode.Unauthorized),
        "io" => new IOException("failure"),
        "timeout" => new TimeoutException("failure"),
        "cancel" => new OperationCanceledException(),
        "discovery" => new CalendarDiscoveryProtocolException("failure"),
        _ => new XmlException("failure")
    };

    private static byte[] EventResource(string uid, string summary, string? extraLine = null) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\n"
        + "VERSION:2.0\r\n"
        + "PRODID:-//Exact Tests//EN\r\n"
        + "BEGIN:VEVENT\r\n"
        + $"UID:{uid}\r\n"
        + "DTSTAMP:20260817T120000Z\r\n"
        + "DTSTART:20260818T120000Z\r\n"
        + $"SUMMARY:{summary}\r\n"
        + "X-EXACT;P=One,one:opaque\r\n"
        + (extraLine is null ? string.Empty : extraLine + "\r\n")
        + "END:VEVENT\r\n"
        + "END:VCALENDAR\r\n");

    private static byte[] TodoResource(string uid, string summary) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Exact Tests//EN\r\n"
        + $"BEGIN:VTODO\r\nUID:{uid}\r\nDTSTAMP:20260817T120000Z\r\nSUMMARY:{summary}\r\n"
        + "END:VTODO\r\nEND:VCALENDAR\r\n");

    private static byte[] OpaqueRfc9253Resource(string uid) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Exact Tests//EN\r\nCALSCALE:X-CUSTOM\r\n"
        + "BEGIN:X-SUPPORT\r\nCONCEPT:https://schema.example/Support\r\n"
        + "LINK;VALUE=URI;LINKREL=RELATED;X-KEEP=one;X-KEEP=two:https://cal.example/support\r\n"
        + "END:X-SUPPORT\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260817T120000Z\r\n"
        + "DTSTART:20260818T120000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] FoldedHeaderResource(string uid, string fold) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Exact Tests//EN\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260817T120000Z\r\nDTSTART:20260818T120000Z\r\n"
        + (fold == "name" ? "SUMM\r\n ARY:Folded name\r\n" : "SUMMARY;X-P=foo\r\n :Folded parameter\r\n")
        + "END:VEVENT\r\nEND:VCALENDAR\r\n");

    private static byte[] FoldedUtf8Resource(string uid)
    {
        var prefix = Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Exact Tests//EN\r\n"
            + $"BEGIN:VEVENT\r\nUID:{uid}\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART:20260818T120000Z\r\nSUMMARY:Caf");
        var suffix = Encoding.UTF8.GetBytes("\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        return [.. prefix, 0xc3, (byte)'\r', (byte)'\n', (byte)' ', 0xa9, .. suffix];
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => utcNow;

        public override long GetTimestamp() => _timestamp;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan amount)
        {
            utcNow += amount;
            _timestamp += amount.Ticks;
            foreach (var timer in _timers.ToArray())
                timer.FireIfDue();
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset? _dueAt = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : owner.GetUtcNow() + dueTime;
            private bool _disposed;

            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
            {
                if (_disposed)
                    return false;
                period = newPeriod;
                _dueAt = newDueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : owner.GetUtcNow() + newDueTime;
                return true;
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void FireIfDue()
            {
                if (_disposed || _dueAt is null || owner.GetUtcNow() < _dueAt)
                    return;
                _dueAt = period == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + period;
                callback(state);
            }
        }
    }

    private static byte[] MultipleMasterResource(string uid) => Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Exact Tests//EN\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nEND:VEVENT\r\n"
        + $"BEGIN:VEVENT\r\nUID:{uid}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
}
