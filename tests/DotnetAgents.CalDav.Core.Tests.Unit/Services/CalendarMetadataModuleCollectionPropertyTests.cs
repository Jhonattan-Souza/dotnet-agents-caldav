using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed partial class CalendarMetadataModuleTests
{
    private static readonly XNamespace Ical = "http://apple.com/ns/ical/";

    [Fact]
    public async Task Patch_sets_color_order_and_time_zone_in_one_atomic_write_and_verifies_readback()
    {
        using var fixture = new Fixture();
        fixture.Enqueue(207, Metadata("Work", null).ToString());
        fixture.Enqueue(207, PatchStatus((Ical + "calendar-color", 200), (Ical + "calendar-order", 200),
            (Cal + "calendar-timezone", 200)).ToString());
        fixture.Enqueue(207, WithProperties(Metadata("Work", null),
            new XElement(Ical + "calendar-color", "#ff2968FF"),
            new XElement(Ical + "calendar-order", "3"),
            new XElement(Cal + "calendar-timezone", CalendarCollectionPropertyValues.SerializeTimeZone("Europe/Berlin"))).ToString());

        var result = await fixture.Module.PatchAsync(Href, new CalendarMetadataPatch(
            Color: new("set", "#FF2968"), Order: new("set", 3), TimeZone: new("set", "Europe/Berlin")), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Error.ShouldBeNull();
        result.Calendar!.Color.ShouldBe("#ff2968");
        result.Calendar.Order.ShouldBe(3);
        result.Calendar.TimeZoneIds.ShouldBe(["Europe/Berlin"]);
        fixture.Methods.ShouldBe(["PROPFIND", "PROPPATCH", "PROPFIND"]);
        var body = XElement.Parse(fixture.Bodies[1]!);
        body.Elements(Dav + "set").Count().ShouldBe(3);
        body.Descendants(Ical + "calendar-color").Single().Value.ShouldBe("#FF2968");
        body.Descendants(Ical + "calendar-order").Single().Value.ShouldBe("3");
        var timeZone = body.Descendants(Cal + "calendar-timezone").Single().Value;
        CalendarMetadataTimeZoneReaderIds(timeZone).ShouldBe(["Europe/Berlin"]);
        timeZone.ShouldBe(CalendarCollectionPropertyValues.SerializeTimeZone("Europe/Berlin"));
        fixture.Bodies[1]!.ShouldContain("BEGIN:VCALENDAR&#xD;\n");
        var inspect = XElement.Parse(fixture.Bodies[0]!);
        inspect.Descendants(Ical + "calendar-color").ShouldHaveSingleItem();
        inspect.Descendants(Ical + "calendar-order").ShouldHaveSingleItem();
        inspect.Descendants(Dav + "description").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Patch_removes_color_order_and_time_zone_and_verifies_their_absence()
    {
        using var fixture = new Fixture();
        fixture.Enqueue(207, Metadata("Work", null).ToString());
        fixture.Enqueue(207, PatchStatus((Ical + "calendar-color", 200), (Ical + "calendar-order", 200),
            (Cal + "calendar-timezone", 200)).ToString());
        fixture.Enqueue(207, WithMissing(Metadata("Work", null),
            Ical + "calendar-color", Ical + "calendar-order", Cal + "calendar-timezone").ToString());

        var result = await fixture.Module.PatchAsync(Href, new CalendarMetadataPatch(
            Color: new("remove"), Order: new("remove"), TimeZone: new("remove")), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Error.ShouldBeNull();
        result.Calendar!.Color.ShouldBeNull();
        result.Calendar.Order.ShouldBeNull();
        result.Calendar.TimeZoneIds.ShouldBeEmpty();
        var body = XElement.Parse(fixture.Bodies[1]!);
        body.Elements(Dav + "remove").Count().ShouldBe(3);
        body.Descendants(Cal + "calendar-timezone").Single().Value.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("time_zone_other", "timeZone")]
    [InlineData("time_zone_unreadable", "timeZone")]
    [InlineData("order_other", "order")]
    [InlineData("color_other", "color")]
    [InlineData("color_nested", "color")]
    [InlineData("color_missing", "color")]
    public async Task Patch_acknowledged_collection_property_with_mismatching_readback_is_unverified(string readback, string member)
    {
        using var fixture = new Fixture();
        fixture.Enqueue(207, Metadata("Work", null).ToString());
        var name = member switch
        {
            "timeZone" => Cal + "calendar-timezone",
            "order" => Ical + "calendar-order",
            _ => Ical + "calendar-color"
        };
        fixture.Enqueue(207, PatchStatus((name, 200)).ToString());
        var after = readback switch
        {
            "time_zone_other" => WithProperties(Metadata("Work", null), new XElement(name, CalendarCollectionPropertyValues.SerializeTimeZone("Europe/Paris"))),
            "time_zone_unreadable" => WithProperties(Metadata("Work", null), new XElement(name, "BEGIN:VCALENDAR")),
            "order_other" => WithProperties(Metadata("Work", null), new XElement(name, "4")),
            "color_other" => WithProperties(Metadata("Work", null), new XElement(name, "#000000")),
            "color_nested" => WithProperties(Metadata("Work", null), new XElement(name, new XElement(Ical + "value", "#FF2968"))),
            _ => WithMissing(Metadata("Work", null), name)
        };
        fixture.Enqueue(207, after.ToString());
        var patch = member switch
        {
            "timeZone" => new CalendarMetadataPatch(TimeZone: new("set", "Europe/Berlin")),
            "order" => new CalendarMetadataPatch(Order: new("set", 3)),
            _ => new CalendarMetadataPatch(Color: new("set", "#FF2968"))
        };

        var result = await fixture.Module.PatchAsync(Href, patch, CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Error!.Code.ShouldBe("committed_but_unverified");
    }

    [Theory]
    [InlineData(403, "upstream_forbidden")]
    [InlineData(409, "conflict")]
    [InlineData(507, "upstream_unavailable")]
    public async Task Patch_atomic_property_failure_names_every_unapplied_property(int status, string code)
    {
        using var fixture = new Fixture();
        fixture.Enqueue(207, Metadata("Old", null).ToString());
        fixture.Enqueue(207, PatchStatus((Dav + "displayname", 424), (Ical + "calendar-color", 424),
            (Cal + "calendar-timezone", status)).ToString());

        var result = await fixture.Module.PatchAsync(Href, new CalendarMetadataPatch(
            DisplayName: new("set", "New"), Color: new("set", "#102030"), TimeZone: new("set", "Asia/Tokyo")), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        result.Error!.Code.ShouldBe(code);
        result.Error.RejectedProperties.ShouldBe([
            new CalendarPropertyRejection("displayName", 424),
            new CalendarPropertyRejection("color", 424),
            new CalendarPropertyRejection("timeZone", status)
        ]);
        fixture.Methods.ShouldBe(["PROPFIND", "PROPPATCH"]);
    }

    [Fact]
    public async Task Patch_nonatomic_partial_property_failure_stays_uncertain_and_names_the_failure()
    {
        using var fixture = new Fixture();
        fixture.Enqueue(207, Metadata("Old", null).ToString());
        fixture.Enqueue(207, PatchStatus((Ical + "calendar-color", 200), (Ical + "calendar-order", 403)).ToString());
        fixture.Enqueue(207, WithProperties(Metadata("Old", null), new XElement(Ical + "calendar-color", "#102030")).ToString());

        var result = await fixture.Module.PatchAsync(Href, new CalendarMetadataPatch(
            Color: new("set", "#102030"), Order: new("set", 1)), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.Unknown);
        result.Error!.Code.ShouldBe("indeterminate");
        result.Error.RejectedProperties.ShouldBe([new CalendarPropertyRejection("order", 403)]);
        result.Calendar!.Color.ShouldBe("#102030");
        fixture.Methods.ShouldBe(["PROPFIND", "PROPPATCH", "PROPFIND"]);
    }

    [Theory]
    [MemberData(nameof(InvalidCollectionPropertyPatches))]
    public async Task Patch_invalid_collection_property_fails_before_network(CalendarMetadataPatch patch)
    {
        using var fixture = new Fixture();

        var error = await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.PatchAsync(Href, patch, CancellationToken.None));

        error.Code.ShouldBe("invalid_input");
        fixture.Methods.ShouldBeEmpty();
    }

    public static TheoryData<CalendarMetadataPatch> InvalidCollectionPropertyPatches => new()
    {
        new CalendarMetadataPatch(Color: new("set", "#FF2968FF")),
        new CalendarMetadataPatch(Color: new("set", "red")),
        new CalendarMetadataPatch(Color: new("set", "#FF2968", "en")),
        new CalendarMetadataPatch(Color: new("set")),
        new CalendarMetadataPatch(Color: new("remove", "#FF2968")),
        new CalendarMetadataPatch(Color: new("replace", "#FF2968")),
        new CalendarMetadataPatch(Order: new("set", -1)),
        new CalendarMetadataPatch(Order: new("set")),
        new CalendarMetadataPatch(Order: new("remove", 1)),
        new CalendarMetadataPatch(Order: new("clear")),
        new CalendarMetadataPatch(TimeZone: new("set", "Mars/Base")),
        new CalendarMetadataPatch(TimeZone: new("set", "+01:00")),
        new CalendarMetadataPatch(TimeZone: new("set", "Europe/Berlin", "de")),
        new CalendarMetadataPatch(TimeZone: new("remove", null, "de"))
    };

    private static IReadOnlyList<string> CalendarMetadataTimeZoneReaderIds(string value) =>
        DotnetAgents.CalDav.Core.Internal.Ical.CalendarMetadataTimeZoneReader.Read(value, CancellationToken.None);

    private static XElement WithProperties(XElement metadata, params XElement[] properties)
    {
        metadata.Descendants(Dav + "prop").First().Add(properties);
        return metadata;
    }

    private static XElement WithMissing(XElement metadata, params XName[] names)
    {
        metadata.Descendants(Dav + "prop").Last().Add(names.Select(name => new XElement(name)));
        return metadata;
    }
}
