using System.Net;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public partial class CalendarReportModuleTests
{
    private const string InitialLimitError = "<d:error xmlns:d='DAV:' xmlns:x='urn:server-diagnostic'>"
        + "<x:message>Any server-specific diagnostic is ignored.</x:message><d:number-of-matches-within-limits/></d:error>";

    [Fact]
    public async Task InitialLimitNegotiationRetainsUnpagedRequestsThroughoutItsCheckpointChain()
    {
        using var fixture = new Fixture();
        fixture.Add(507, InitialLimitError);
        fixture.Add(207, SyncResponse("urn:sync:one", Changed("initial.ics")));
        fixture.Add(207, SyncResponse("urn:sync:two", Changed("a.ics") + Changed("b.ics")));
        fixture.Add(207, SyncResponse("urn:sync:two", string.Empty));

        var first = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Start(CalendarHref), CancellationToken.None);
        var second = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Continue(first.Checkpoint), CancellationToken.None);
        var third = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Continue(second.Checkpoint), CancellationToken.None);

        first.Mode.ShouldBe("initial");
        second.Mode.ShouldBe("incremental");
        second.Changes.Select(change => change.Href).ShouldBe(new[] { CalendarHref + "a.ics", CalendarHref + "b.ics" });
        third.Changes.ShouldBeEmpty();
        var requests = fixture.Handler.Requests;
        requests.Count.ShouldBe(4);
        HasSyncLimit(requests[0]).ShouldBeTrue();
        requests.Skip(1).ShouldAllBe(request => !HasSyncLimit(request));
        SyncRequestToken(requests[0]).ShouldBeEmpty();
        SyncRequestToken(requests[1]).ShouldBeEmpty();
        SyncRequestToken(requests[2]).ShouldBe("urn:sync:one");
        SyncRequestToken(requests[3]).ShouldBe("urn:sync:two");
        var binding = fixture.Protector.ConfigurationBinding(fixture.Options);
        fixture.Protector.Unprotect(third.Checkpoint, binding).OmitLimit.ShouldBeTrue();
    }

    [Fact]
    public async Task InitialLimitNegotiationPreservesNativeTruncationAndInitialMode()
    {
        using var fixture = new Fixture();
        fixture.Add(507, InitialLimitError);
        fixture.Add(207, SyncResponse("urn:sync:one", Changed("a.ics") + Self507()));
        fixture.Add(207, SyncResponse("urn:sync:two", Changed("b.ics")));

        var first = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Start(CalendarHref, 1), CancellationToken.None);
        var second = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Continue(first.Checkpoint, 1), CancellationToken.None);

        first.Mode.ShouldBe("initial");
        first.HasMore.ShouldBeTrue();
        second.Mode.ShouldBe("initial");
        second.HasMore.ShouldBeFalse();
        fixture.Handler.Requests.Count.ShouldBe(3);
        fixture.Handler.Requests.Skip(1).ShouldAllBe(request => !HasSyncLimit(request));
    }

    [Fact]
    public async Task EmbeddedSelf507NeverTriggersOptionalLimitNegotiation()
    {
        using var fixture = new Fixture();
        fixture.Add(207, SyncResponse("urn:sync:one", Changed("a.ics") + Self507()));

        var page = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Start(CalendarHref, 1), CancellationToken.None);

        page.HasMore.ShouldBeTrue();
        fixture.Handler.Requests.Count.ShouldBe(1);
        fixture.Protector.Unprotect(page.Checkpoint, fixture.Protector.ConfigurationBinding(fixture.Options)).OmitLimit.ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<d:error xmlns:d='DAV:'/>")]
    [InlineData("<d:multistatus xmlns:d='DAV:'><d:number-of-matches-within-limits/></d:multistatus>")]
    [InlineData("<x:error xmlns:x='urn:other' xmlns:d='DAV:'><d:number-of-matches-within-limits/></x:error>")]
    [InlineData("<d:error xmlns:d='DAV:'><d:wrapper><d:number-of-matches-within-limits/></d:wrapper></d:error>")]
    [InlineData("<d:error xmlns:d='DAV:'><d:number-of-matches-within-limits/><d:valid-sync-token/></d:error>")]
    [InlineData("<d:error xmlns:d='DAV:'><d:number-of-matches-within-limits/><d:number-of-matches-within-limits/></d:error>")]
    [InlineData("<d:error xmlns:d='DAV:'><d:number-of-matches-within-limits>unexpected</d:number-of-matches-within-limits></d:error>")]
    [InlineData("<d:error xmlns:d='DAV:'><d:number-of-matches-within-limits><d:child/></d:number-of-matches-within-limits></d:error>")]
    public async Task UnrelatedOrAmbiguous507ErrorDoesNotTriggerNegotiation(string body)
    {
        using var fixture = new Fixture();
        fixture.Add(507, body);

        var error = await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Start(CalendarHref), CancellationToken.None));

        error.Code.ShouldBe("limit_exhausted");
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(403)]
    [InlineData(409)]
    public async Task LimitPreconditionWithAnotherFailureStatusDoesNotNegotiate(int status)
    {
        using var fixture = new Fixture();
        fixture.Add(status, InitialLimitError);

        (await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Start(CalendarHref), CancellationToken.None))).Code.ShouldBe("limit_exhausted");
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(500)]
    public async Task UnrelatedHttpStatusDoesNotNegotiateEvenWithLimitPrecondition(int status)
    {
        using var fixture = new Fixture();
        fixture.Add(status, InitialLimitError);

        (await Should.ThrowAsync<HttpRequestException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Start(CalendarHref), CancellationToken.None))).StatusCode.ShouldBe((HttpStatusCode)status);
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExistingCheckpointNeverNegotiatesOrResetsInventory(bool initial, bool omitLimit)
    {
        using var fixture = new Fixture();
        var binding = fixture.Protector.ConfigurationBinding(fixture.Options);
        var checkpoint = fixture.Protector.Protect(new(CalendarHref, "urn:sync:prior", initial, binding, OmitLimit: omitLimit));
        fixture.Add(507, InitialLimitError);

        (await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Continue(checkpoint), CancellationToken.None))).Code.ShouldBe("limit_exhausted");

        fixture.Handler.Requests.Count.ShouldBe(1);
        SyncRequestToken(fixture.Handler.Requests[0]).ShouldBe("urn:sync:prior");
        HasSyncLimit(fixture.Handler.Requests[0]).ShouldBe(!omitLimit);
    }

    [Fact]
    public async Task InitialNegotiationHasExactlyOneFallbackWhenTheSecondReportAlsoFails()
    {
        using var fixture = new Fixture();
        fixture.Add(507, InitialLimitError);
        fixture.Add(507, InitialLimitError);

        (await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Start(CalendarHref), CancellationToken.None))).Code.ShouldBe("limit_exhausted");

        fixture.Handler.Requests.Count.ShouldBe(2);
        HasSyncLimit(fixture.Handler.Requests[0]).ShouldBeTrue();
        HasSyncLimit(fixture.Handler.Requests[1]).ShouldBeFalse();
    }

    [Fact]
    public async Task UnpagedOverflowRetainsPriorCheckpointForLargerPageSizeRetry()
    {
        using var fixture = new Fixture();
        fixture.Add(507, InitialLimitError);
        fixture.Add(207, SyncResponse("urn:sync:one", Changed("initial.ics")));
        var delta = SyncResponse("urn:sync:two", Changed("a.ics") + Changed("b.ics"));
        fixture.Add(207, delta);
        fixture.Add(207, delta);
        var first = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Start(CalendarHref, 1), CancellationToken.None);

        var error = await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Continue(first.Checkpoint, 1), CancellationToken.None));
        var recovered = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Continue(first.Checkpoint, 2), CancellationToken.None);

        error.Code.ShouldBe("limit_exhausted");
        error.Message.ShouldContain("larger pageSize up to 500");
        error.Message.ShouldContain("same checkpoint");
        recovered.Changes.Count.ShouldBe(2);
        recovered.Mode.ShouldBe("incremental");
        SyncRequestToken(fixture.Handler.Requests[2]).ShouldBe("urn:sync:one");
        SyncRequestToken(fixture.Handler.Requests[3]).ShouldBe("urn:sync:one");
        fixture.Handler.Requests.Skip(1).ShouldAllBe(request => !HasSyncLimit(request));
    }

    [Fact]
    public async Task InitialFallbackOverflowCannotPublishAPartialInventoryCheckpoint()
    {
        using var fixture = new Fixture();
        var inventory = SyncResponse("urn:sync:one", Changed("a.ics") + Changed("b.ics"));
        fixture.Add(507, InitialLimitError);
        fixture.Add(207, inventory);
        fixture.Add(507, InitialLimitError);
        fixture.Add(207, inventory);

        (await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Start(CalendarHref, 1), CancellationToken.None))).Code.ShouldBe("limit_exhausted");
        var retried = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Start(CalendarHref, 2), CancellationToken.None);

        retried.Mode.ShouldBe("initial");
        retried.Changes.Count.ShouldBe(2);
        fixture.Handler.Requests.Count.ShouldBe(4);
        fixture.Handler.Requests.ShouldAllBe(request => SyncRequestToken(request) == string.Empty);
    }

    [Fact]
    public async Task MalformedFallbackCannotPublishACompletionCheckpoint()
    {
        using var fixture = new Fixture();
        fixture.Add(507, InitialLimitError);
        fixture.Add(207, SyncResponse("urn:sync:advanced", Changed("a.ics").Replace("&quot;r1&quot;", string.Empty, StringComparison.Ordinal)));

        (await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Start(CalendarHref), CancellationToken.None))).Code.ShouldBe("upstream_protocol_error");
        fixture.Handler.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task FallbackRetainsTransportByteLimitAndOriginalCancellation()
    {
        using var oversized = new Fixture();
        oversized.Add(507, InitialLimitError);
        oversized.Add(207, new string('x', 4 * 1024 * 1024 + 1));
        (await Should.ThrowAsync<HttpRequestException>(() => oversized.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Start(CalendarHref), CancellationToken.None))).StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        oversized.Handler.Requests.Count.ShouldBe(2);

        using var cancelled = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancelled.Add(507, InitialLimitError);
        cancelled.Handler.BeforeReply = cancellation.Cancel;
        await Should.ThrowAsync<OperationCanceledException>(() => cancelled.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Start(CalendarHref), cancellation.Token));
        cancelled.Handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task LimitFailureFromAnotherResponseIdentityCannotNegotiate()
    {
        using var fixture = new Fixture();
        fixture.Handler.Replies.Enqueue(new HttpResponseMessage((HttpStatusCode)507)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://cal.example/other/"),
            Content = new StringContent(InitialLimitError)
        });

        (await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Start(CalendarHref), CancellationToken.None))).Code.ShouldBe("upstream_protocol_error");
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    private static bool HasSyncLimit(ObservedRequest request) => XDocument.Parse(request.Body).Root!.Element(Dav + "limit") is not null;

    private static string SyncRequestToken(ObservedRequest request) => XDocument.Parse(request.Body).Root!.Element(Dav + "sync-token")!.Value;
}
