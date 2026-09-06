using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DotnetAgents.CalDav.Mcp.Hosting;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed partial class CalendarProtocolFailurePhaseTests
{
    private const string EncodedSync = """
        <d:multistatus xmlns:d="DAV:"><d:response><d:href>/cal/café.ics</d:href>
        <d:propstat><d:prop><d:getetag>"révision1"</d:getetag></d:prop>
        <d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        <d:sync-token>urn:sync:one</d:sync-token></d:multistatus>
        """;
    private const string EncodedPatchStatus = """
        <d:multistatus xmlns:d="DAV:"><d:response><d:href>/cal/</d:href>
        <d:propstat><d:prop><d:displayname/></d:prop><d:status>HTTP/1.1 200 OK</d:status>
        </d:propstat></d:response></d:multistatus>
        """;

    [Theory]
    [InlineData("calendars.inspect")]
    [InlineData("calendar_resources.changes")]
    public async Task Non_utf8_xml_produces_complete_schema_valid_unicode_results(string tool)
    {
        using var fixture = new Fixture(request => request.Method.Method == "OPTIONS" ? Response(200, string.Empty)
            : XmlEncodingResponse(tool == "calendars.inspect" ? Metadata.Replace("Work", "Férias", StringComparison.Ordinal) : EncodedSync));

        var result = await fixture.CallAsync(tool);

        result.IsError.ShouldBe(false);
        CalendarOutputSchemaGuard.Validate(tool, result);
        var body = result.StructuredContent!.Value;
        if (tool == "calendars.inspect")
            body.GetProperty("displayName").GetString().ShouldBe("Férias");
        else
        {
            body.GetProperty("changes")[0].GetProperty("href").GetString().ShouldBe(Href + "caf%C3%A9.ics");
            body.GetProperty("changes")[0].GetProperty("etag").GetString().ShouldBe("\"révision1\"");
        }
    }

    [Theory]
    [InlineData("calendars.inspect")]
    [InlineData("calendar_resources.changes")]
    [InlineData("calendars.patch")]
    public async Task Unknown_xml_encoding_returns_typed_error_before_any_write_or_checkpoint(string tool)
    {
        using var fixture = new Fixture(_ => XmlEncodingResponse(Metadata, "unknown-xml-encoding"));

        var result = await fixture.CallAsync(tool);

        AssertPhase(tool, result, "execution");
        var body = result.StructuredContent!.Value;
        body.GetProperty("code").GetString().ShouldBe("upstream_protocol_error");
        body.TryGetProperty("checkpoint", out _).ShouldBeFalse();
        fixture.Methods.ShouldNotContain("PROPPATCH");
        if (tool == "calendars.patch")
            body.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
    }

    [Fact]
    public async Task Encoded_property_acknowledgement_keeps_schema_valid_committed_outcome()
    {
        using var fixture = new Fixture(request => XmlEncodingResponse(
            request.Method.Method == "PROPPATCH" ? EncodedPatchStatus : Metadata, "utf-16BE", "utf-16BE"));

        var result = await fixture.CallAsync("calendars.patch");

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("mutationState").GetString().ShouldBe("committed");
        CalendarOutputSchemaGuard.Validate("calendars.patch", result);
        fixture.Methods.ShouldBe(["PROPFIND", "PROPPATCH", "PROPFIND"]);
    }

    private static HttpResponseMessage XmlEncodingResponse(string body, string charset = "iso-8859-1", string encoding = "iso-8859-1")
    {
        var content = new ByteArrayContent(Encoding.GetEncoding(encoding).GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = charset };
        return new HttpResponseMessage(HttpStatusCode.MultiStatus) { Content = content };
    }
}
