using System.Text;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Xml;

public sealed class CalendarMetadataProtocolTests
{
    private const string Href = "https://cal.example/calendar/";
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly string[] IntegerLimitNames = ["max-resource-size", "max-instances", "max-attendees-per-instance"];
    private static readonly CalendarMetadataPatch Patch = new(DisplayName: new("set", "New name"));

    [Theory]
    [InlineData("-1")]
    [InlineData("-9223372036854775808")]
    [InlineData("9223372036854775808")]
    [InlineData("+1")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    [InlineData("")]
    public void InvalidAdvertisedIntegerLimitsCannotBecomeSuccessfulMetadata(string value)
    {
        foreach (var name in IntegerLimitNames)
        {
            var response = Response(Metadata(new XElement(CalDav + name, value)));

            Should.Throw<CalendarProtocolException>(() => CalendarMetadataProtocol.ParseMetadata(Href, response))
                .Code.ShouldBe("upstream_protocol_error");
        }
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1", 1L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("\n 001 \t", 1L)]
    public void NonnegativeAdvertisedIntegerBoundariesRemainExact(string value, long expected)
    {
        var properties = IntegerLimitNames.Select(name => new XElement(CalDav + name, value)).ToArray();

        var snapshot = CalendarMetadataProtocol.ParseMetadata(Href, Response(Metadata(properties)), TestContext.Current.CancellationToken).Snapshot;

        snapshot.Limits.MaximumResourceBytes.ShouldBe(expected);
        snapshot.Limits.MaximumInstances.ShouldBe(expected);
        snapshot.Limits.MaximumAttendeesPerInstance.ShouldBe(expected);
    }

    [Fact]
    public void MissingOrFailedAdvertisedLimitsStayUnknown()
    {
        var body = Metadata();
        var missing = CalendarMetadataProtocol.ParseMetadata(Href, Response(body), TestContext.Current.CancellationToken).Snapshot;
        body.Element(Dav + "response")!.Add(Propstat("HTTP/1.1 404 Not Found",
            IntegerLimitNames.Select(name => new XElement(CalDav + name, "-1")).ToArray()));

        var unavailable = CalendarMetadataProtocol.ParseMetadata(Href, Response(body), TestContext.Current.CancellationToken).Snapshot;

        missing.Limits.ShouldBe(new CalendarAdvertisedLimits(null, null, null, null, null));
        unavailable.Limits.ShouldBe(missing.Limits);
        unavailable.Properties.Where(property => IntegerLimitNames.Contains(property.LocalName))
            .ShouldAllBe(property => property.StatusCode == 404);
    }

    [Theory]
    [InlineData("HTTP/1.1 0200 OK")]
    [InlineData("HTTP/bogus 200 OK")]
    [InlineData("HTTP/1.x 200 OK")]
    [InlineData("HTTP/ 200 OK")]
    [InlineData("HTTP/1.1 +200 OK")]
    [InlineData("HTTP/1.1 600 Unknown")]
    [InlineData("HTTP/1.1 099 Unknown")]
    [InlineData("HTTP/1.1 200 OK\nHTTP/1.1 403 Forbidden")]
    [InlineData("HTTP/1.1 200 OK\rHTTP/1.1 403 Forbidden")]
    [InlineData("HTTP/1.1 200 OK\r\nHTTP/1.1 403 Forbidden")]
    public void MalformedPropertyStatusCannotAuthorizeMetadataOrConfirmAWrite(string status)
    {
        var metadata = Metadata();
        metadata.Descendants(Dav + "status").Single().Value = status;
        var acknowledgement = MultiStatus(Propstat(status, new XElement(Dav + "displayname")));

        Should.Throw<CalendarProtocolException>(() => CalendarMetadataProtocol.ParseMetadata(Href, Response(metadata)))
            .Code.ShouldBe("upstream_protocol_error");
        var dispatch = CalendarMetadataPatchProtocol.ReadDispatch(Href, Patch, Response(acknowledgement));
        dispatch.State.ShouldBe(CalendarMutationState.Unknown);
        dispatch.Error!.Code.ShouldBe("indeterminate");
    }

    [Theory]
    [InlineData("HTTP/1.1 0403 Forbidden")]
    [InlineData("HTTP/bogus 403 Forbidden")]
    public void MalformedResponseStatusCannotEstablishANoncommitOutcome(string status)
    {
        var response = Response(MultiStatus(new XElement(Dav + "status", status)));

        Should.Throw<CalendarProtocolException>(() => CalendarMetadataProtocol.ParseMetadata(Href, response))
            .Code.ShouldBe("upstream_protocol_error");
        CalendarMetadataPatchProtocol.ReadDispatch(Href, Patch, response).State.ShouldBe(CalendarMutationState.Unknown);
    }

    [Theory]
    [InlineData("nested")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    public void AmbiguousStatusElementsCannotBecomeSuccessfulEvidence(string kind)
    {
        var propertyStatus = Propstat("HTTP/1.1 200 OK", new XElement(Dav + "displayname"));
        var status = propertyStatus.Element(Dav + "status")!;
        if (kind == "nested")
            status.ReplaceNodes(new XElement(XName.Get("text", "urn:extension"), "HTTP/1.1 200 OK"));
        else if (kind == "duplicate")
            propertyStatus.Add(new XElement(status));
        else
            status.Remove();
        var response = Response(MultiStatus(propertyStatus));

        Should.Throw<CalendarProtocolException>(() => CalendarMetadataProtocol.ReadProperties(Href, response.Body))
            .Code.ShouldBe("upstream_protocol_error");
        CalendarMetadataPatchProtocol.ReadDispatch(Href, Patch, response).State.ShouldBe(CalendarMutationState.Unknown);
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK", 200)]
    [InlineData("HTTP/2 200 OK", 200)]
    [InlineData("HTTP/1.1 204 No Content", 204)]
    [InlineData("\r\n HTTP/1.1 200 OK \r\n", 200)]
    public void ValidStatusLinesRetainTheOriginalPropertyStatus(string status, int expected)
    {
        var response = Response(MultiStatus(Propstat(status, new XElement(Dav + "displayname"))));

        var properties = CalendarMetadataProtocol.ReadProperties(Href, response.Body);

        properties[Dav + "displayname"].StatusCode.ShouldBe(expected);
        CalendarMetadataPatchProtocol.ReadDispatch(Href, Patch, response).State.ShouldBe(CalendarMutationState.Committed);
    }

    private static XElement Metadata(params XElement[] properties) => MultiStatus(Propstat("HTTP/1.1 200 OK",
        new[] { new XElement(Dav + "resourcetype", new XElement(Dav + "collection"), new XElement(CalDav + "calendar")) }
            .Concat(properties).ToArray()));

    private static XElement Propstat(string status, params XElement[] properties) => new(Dav + "propstat",
        new XElement(Dav + "prop", properties), new XElement(Dav + "status", status));

    private static XElement MultiStatus(params XElement[] elements) => new(Dav + "multistatus",
        new XElement(Dav + "response", new XElement(Dav + "href", Href), elements));

    private static CalendarProtocolResponse Response(XElement body) => new(207, Href,
        Encoding.UTF8.GetBytes(body.ToString()), "application/xml", []);
}
