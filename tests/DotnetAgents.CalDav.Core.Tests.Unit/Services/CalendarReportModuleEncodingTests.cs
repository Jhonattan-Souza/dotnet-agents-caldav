using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public partial class CalendarReportModuleTests
{
    [Theory]
    [InlineData("iso-8859-1")]
    [InlineData("utf-16BE")]
    public async Task Sync_http_charset_preserves_member_identity_etag_and_checkpoint_replay(string encoding)
    {
        using var fixture = new Fixture();
        var firstPage = SyncResponse("urn:sync:one", EncodedChange());
        AddEncodedXml(fixture, 207, firstPage, encoding);
        AddEncodedXml(fixture, 207, SyncResponse("urn:sync:two", Changed("next.ics")), encoding);
        AddEncodedXml(fixture, 207, SyncResponse("urn:sync:two", Changed("next.ics")), encoding);

        var first = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Start(CalendarHref), TestContext.Current.CancellationToken);
        var next = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Continue(first.Checkpoint), TestContext.Current.CancellationToken);
        var replay = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Continue(first.Checkpoint), TestContext.Current.CancellationToken);

        first.Changes.Single().Href.ShouldBe(CalendarHref + "caf%C3%A9.ics");
        first.Changes.Single().Etag.ShouldBe("\"révision1\"");
        next.Changes.ShouldBe(replay.Changes);
        replay.Checkpoint.ShouldBe(next.Checkpoint);
        SyncRequestToken(fixture.Handler.Requests[1]).ShouldBe("urn:sync:one");
        SyncRequestToken(fixture.Handler.Requests[2]).ShouldBe("urn:sync:one");
    }

    [Theory]
    [InlineData("utf-16BE")]
    [InlineData("iso-8859-1")]
    public async Task Encoded_initial_limit_error_permits_the_single_standard_negotiation(string encoding)
    {
        using var fixture = new Fixture();
        AddEncodedXml(fixture, 507, InitialLimitError, encoding);
        AddEncodedXml(fixture, 207, SyncResponse("urn:sync:one", EncodedChange()), encoding);
        fixture.Add(207, SyncResponse("urn:sync:one", string.Empty));

        var first = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Start(CalendarHref), TestContext.Current.CancellationToken);
        var next = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Continue(first.Checkpoint), TestContext.Current.CancellationToken);

        first.Changes.Single().Etag.ShouldBe("\"révision1\"");
        next.Changes.ShouldBeEmpty();
        HasSyncLimit(fixture.Handler.Requests[0]).ShouldBeTrue();
        fixture.Handler.Requests.Skip(1).ShouldAllBe(request => !HasSyncLimit(request));
    }

    [Theory]
    [InlineData(403, "valid-sync-token", "sync_reset_required")]
    [InlineData(409, "number-of-matches-within-limits", "limit_exhausted")]
    [InlineData(403, "supported-report", "unsupported_capability")]
    public async Task Encoded_native_error_keeps_the_rfc_recovery_instruction(int status, string condition, string expected)
    {
        using var fixture = new Fixture();
        AddEncodedXml(fixture, status, "<d:error xmlns:d='DAV:'><d:" + condition + "/></d:error>", "utf-16BE");

        var error = await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Start(CalendarHref), TestContext.Current.CancellationToken));

        error.Code.ShouldBe(expected);
        fixture.Handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("unknown-xml-encoding")]
    [InlineData("utf-8")]
    public async Task Unreadable_sync_encoding_cannot_advance_checkpoint_before_corrected_retry(string charset)
    {
        using var fixture = new Fixture();
        fixture.Add(207, SyncResponse("urn:sync:one", Changed("existing.ics")));
        var nextPage = SyncResponse("urn:sync:two", Changed("valid-first.ics") + EncodedChange());
        AddEncodedXml(fixture, 207, nextPage, "iso-8859-1", charset);
        AddEncodedXml(fixture, 207, nextPage, "iso-8859-1");
        var first = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Start(CalendarHref), TestContext.Current.CancellationToken);

        await Should.ThrowAsync<XmlException>(() => fixture.Module.ChangesAsync(
            new CalendarResourceChangesRequest.Continue(first.Checkpoint), TestContext.Current.CancellationToken));
        var recovered = await fixture.Module.ChangesAsync(new CalendarResourceChangesRequest.Continue(first.Checkpoint), TestContext.Current.CancellationToken);

        recovered.Changes.Count.ShouldBe(2);
        recovered.Changes[1].Etag.ShouldBe("\"révision1\"");
        SyncRequestToken(fixture.Handler.Requests[1]).ShouldBe("urn:sync:one");
        SyncRequestToken(fixture.Handler.Requests[2]).ShouldBe("urn:sync:one");
    }

    private static string EncodedChange() => Changed("café.ics").Replace("r1", "révision1", StringComparison.Ordinal);

    private static void AddEncodedXml(Fixture fixture, int status, string xml, string encoding, string? charset = null)
    {
        var content = new ByteArrayContent(Encoding.GetEncoding(encoding).GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + xml));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = charset ?? encoding };
        fixture.Handler.Replies.Enqueue(new HttpResponseMessage((HttpStatusCode)status) { Content = content });
    }
}
