using System.Net.Http.Headers;
using System.Text;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed partial class CalendarMetadataModuleTests
{
    [Theory]
    [InlineData("iso-8859-1")]
    [InlineData("utf-16BE")]
    public async Task Inspect_uses_http_charset_and_preserves_non_ascii_property_values(string encoding)
    {
        using var fixture = new Fixture();
        fixture.EnqueueContent(207, EncodedXml(Metadata("Réunions", "Planejamento e café").ToString(), encoding));
        fixture.EnqueueOptions("1, calendar-access");

        var result = await fixture.Module.InspectAsync(Href, TestContext.Current.CancellationToken);

        result.DisplayName.ShouldBe("Réunions");
        result.Description.ShouldBe("Planejamento e café");
        result.Scheduling.State.ShouldBe("not_advertised");
        fixture.Methods.ShouldBe(["PROPFIND", "OPTIONS"]);
    }

    [Theory]
    [InlineData("iso-8859-1", "utf-16BE", "iso-8859-1")]
    [InlineData("utf-16BE", "iso-8859-1", "utf-16BE")]
    public async Task Patch_decodes_each_response_independently_and_verifies_one_write(
        string beforeEncoding, string acknowledgementEncoding, string afterEncoding)
    {
        using var fixture = new Fixture();
        fixture.EnqueueContent(207, EncodedXml(Metadata("Réunions", "Café").ToString(), beforeEncoding));
        fixture.EnqueueContent(207, EncodedXml(PatchStatus((Dav + "displayname", 200)).ToString(), acknowledgementEncoding));
        fixture.EnqueueContent(207, EncodedXml(Metadata("Férias", "Café").ToString(), afterEncoding));

        var result = await fixture.Module.PatchAsync(Href,
            new CalendarMetadataPatch(new CalendarMetadataTextPatch("set", "Férias")), TestContext.Current.CancellationToken);

        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Error.ShouldBeNull();
        result.Calendar!.DisplayName.ShouldBe("Férias");
        result.Calendar.Description.ShouldBe("Café");
        fixture.Methods.ShouldBe(["PROPFIND", "PROPPATCH", "PROPFIND"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_xml_charset_cannot_confirm_a_write_or_erase_an_acknowledged_commit(bool duringReadback)
    {
        using var fixture = new Fixture();
        fixture.Enqueue(207, Metadata("Old", "Café").ToString());
        var acknowledgement = EncodedXml(PatchStatus((Dav + "displayname", 200)).ToString(), "utf-16BE");
        var readback = EncodedXml(Metadata("Férias", "Café").ToString(), "iso-8859-1");
        (duringReadback ? readback : acknowledgement).Headers.ContentType!.CharSet = "unknown-xml-encoding";
        fixture.EnqueueContent(207, acknowledgement);
        fixture.EnqueueContent(207, readback);

        var result = await fixture.Module.PatchAsync(Href,
            new CalendarMetadataPatch(new CalendarMetadataTextPatch("set", "Férias")), TestContext.Current.CancellationToken);

        result.MutationState.ShouldBe(duringReadback ? CalendarMutationState.Committed : CalendarMutationState.Unknown);
        result.Error!.Code.ShouldBe(duringReadback ? "committed_but_unverified" : "indeterminate");
        fixture.Methods.ShouldBe(["PROPFIND", "PROPPATCH", "PROPFIND"]);
    }

    private static ByteArrayContent EncodedXml(string xml, string encoding)
    {
        // The HTTP charset is authoritative even when the declaration disagrees.
        var bytes = Encoding.GetEncoding(encoding).GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + xml);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = encoding };
        return content;
    }
}
