using System.Text;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Xml;

public sealed class CalendarPropertyRejectionReaderTests
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly XNamespace Ical = "http://apple.com/ns/ical/";
    private static readonly Dictionary<XName, string> Requested = new()
    {
        [Dav + "displayname"] = "displayName",
        [Ical + "calendar-color"] = "color",
        [Ical + "calendar-order"] = "order",
        [CalDav + "calendar-timezone"] = "timeZone"
    };

    [Theory]
    [InlineData("mkcalendar-response")]
    [InlineData("mkcol-response")]
    [InlineData("multistatus")]
    public void Failed_atomic_create_names_rejected_and_dependent_properties(string rootName)
    {
        var root = new XElement((rootName == "mkcalendar-response" ? CalDav : Dav) + rootName,
            Propstat("HTTP/1.1 403 Forbidden", Ical + "calendar-order"),
            Propstat("HTTP/1.1 424 Failed Dependency", Dav + "displayname", Ical + "calendar-color"));
        var body = rootName == "multistatus"
            ? new XElement(root.Name, new XElement(Dav + "response", new XElement(Dav + "href", "/c/"), root.Elements()))
            : root;

        var rejections = Read(body);

        rejections.ShouldBe([
            new CalendarPropertyRejection("color", 424),
            new CalendarPropertyRejection("displayName", 424),
            new CalendarPropertyRejection("order", 403)
        ]);
    }

    [Fact]
    public void Successful_and_unrequested_property_statuses_are_not_rejections()
    {
        var rejections = Read(new XElement(CalDav + "mkcalendar-response",
            Propstat("HTTP/1.1 200 OK", Dav + "displayname"),
            Propstat("HTTP/1.1 409 Conflict", CalDav + "calendar-timezone", XName.Get("other", "urn:x"))));

        rejections.ShouldBe([new CalendarPropertyRejection("timeZone", 409)]);
    }

    [Theory]
    [InlineData("dependency-only")]
    [InlineData("unrequested-only")]
    [InlineData("wrong-root")]
    [InlineData("missing-status")]
    [InlineData("duplicate-status")]
    public void Evidence_without_an_attributable_failure_keeps_http_status_classification(string kind)
    {
        XElement body = kind switch
        {
            "dependency-only" => new XElement(CalDav + "mkcalendar-response",
                Propstat("HTTP/1.1 424 Failed Dependency", Dav + "displayname")),
            "unrequested-only" => new XElement(CalDav + "mkcalendar-response",
                Propstat("HTTP/1.1 403 Forbidden", XName.Get("other", "urn:x"))),
            "wrong-root" => new XElement(Dav + "error", Propstat("HTTP/1.1 403 Forbidden", Dav + "displayname")),
            "missing-status" => new XElement(CalDav + "mkcalendar-response",
                new XElement(Dav + "propstat", new XElement(Dav + "prop", new XElement(Dav + "displayname")))),
            _ => new XElement(CalDav + "mkcalendar-response",
                new XElement(Dav + "propstat", new XElement(Dav + "prop", new XElement(Dav + "displayname")),
                    new XElement(Dav + "status", "HTTP/1.1 403 Forbidden"),
                    new XElement(Dav + "status", "HTTP/1.1 403 Forbidden")))
        };

        Read(body).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("<broken")]
    [InlineData("<!DOCTYPE x [<!ENTITY a \"b\">]><x/>")]
    [InlineData("plain text failure")]
    public void Unusable_bodies_yield_no_rejections(string body)
    {
        CalendarPropertyRejectionReader.Read(Encoding.UTF8.GetBytes(body), null, Requested).ShouldBeEmpty();
    }

    [Fact]
    public void Malformed_status_line_yields_no_rejections()
    {
        Read(new XElement(CalDav + "mkcalendar-response",
            Propstat("HTTP/1.1 4033 Forbidden", Dav + "displayname"))).ShouldBeEmpty();
    }

    [Fact]
    public void Invalid_encoding_yields_no_rejections()
    {
        CalendarPropertyRejectionReader.Read([0x3C, 0x78, 0xFF, 0x2F, 0x3E], "utf-8", Requested).ShouldBeEmpty();
    }

    [Fact]
    public void Excessive_nesting_yields_no_rejections()
    {
        var body = new XElement(CalDav + "mkcalendar-response", Propstat("HTTP/1.1 403 Forbidden", Dav + "displayname"));
        var leaf = body;
        for (var depth = 0; depth < 20; depth++)
        {
            var child = new XElement(XName.Get("nested", "urn:x"));
            leaf.Add(child);
            leaf = child;
        }

        Read(body).ShouldBeEmpty();
    }

    private static IReadOnlyList<CalendarPropertyRejection> Read(XElement body) =>
        CalendarPropertyRejectionReader.Read(Encoding.UTF8.GetBytes(body.ToString()), "utf-8", Requested);

    private static XElement Propstat(string status, params XName[] names) => new(Dav + "propstat",
        new XElement(Dav + "prop", names.Select(name => new XElement(name))),
        new XElement(Dav + "status", status));
}
