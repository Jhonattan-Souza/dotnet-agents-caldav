using System.Text.Json;
using System.Text.Json.Serialization;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Hosting;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed partial class CalendarProtocolFailurePhaseTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Metadata_scalar_boundaries_match_schema_and_reach_one_verified_write(bool description, bool combining)
    {
        var limit = description ? 4096 : 256;
        var value = string.Concat(Enumerable.Repeat(combining ? "\U0001F600\u0301" : "\U0001F600", combining ? limit / 2 : limit));
        var patch = description ? new CalendarMetadataPatch(Description: new CalendarMetadataTextPatch("set", value))
            : new CalendarMetadataPatch(DisplayName: new CalendarMetadataTextPatch("set", value));
        var arguments = JsonSerializer.SerializeToNode(new { calendarHref = Href, patch },
            new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
        using var fixture = new Fixture(request => Response(207,
            request.Method.Method == "PROPPATCH" ? UnicodeAcknowledgement(description) : UnicodeMetadata(description, value)));

        CalendarProtocolInputGuard.Validate("calendars.patch", arguments).ShouldBeEmpty();
        var result = await fixture.PatchAsync(patch);

        result.IsError.ShouldBe(false);
        var body = result.StructuredContent!.Value;
        body.GetProperty("mutationState").GetString().ShouldBe("committed");
        body.GetProperty("calendar").GetProperty(description ? "description" : "displayName").GetString().ShouldBe(value);
        CalendarOutputSchemaGuard.Validate("calendars.patch", result);
        fixture.Methods.ShouldBe(["PROPFIND", "PROPPATCH", "PROPFIND"]);
    }

    private static string UnicodeMetadata(bool description, string value) => description
        ? Metadata.Replace("</d:prop>", "<c:calendar-description>" + value + "</c:calendar-description></d:prop>", StringComparison.Ordinal)
        : Metadata.Replace("Work", value, StringComparison.Ordinal);

    private static string UnicodeAcknowledgement(bool description)
    {
        var property = description ? "c:calendar-description" : "d:displayname";
        return "<d:multistatus xmlns:d='DAV:' xmlns:c='urn:ietf:params:xml:ns:caldav'><d:response><d:href>/cal/</d:href>"
            + "<d:propstat><d:prop><" + property + "/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>";
    }
}
