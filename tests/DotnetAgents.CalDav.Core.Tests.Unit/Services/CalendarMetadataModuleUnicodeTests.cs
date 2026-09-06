using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed partial class CalendarMetadataModuleTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Patch_accepts_the_exact_unicode_scalar_limit_and_preserves_the_text(bool description, bool combining)
    {
        using var fixture = new Fixture();
        var value = BoundaryText(description, combining);
        var name = description ? Cal + "calendar-description" : Dav + "displayname";
        fixture.Enqueue(207, Metadata("Old", "Old description").ToString());
        fixture.Enqueue(207, PatchStatus((name, 200)).ToString());
        fixture.Enqueue(207, Metadata(description ? "Old" : value, description ? value : "Old description").ToString());

        var result = await fixture.Module.PatchAsync(Href, TextPatch(description, value), TestContext.Current.CancellationToken);

        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Error.ShouldBeNull();
        (description ? result.Calendar!.Description : result.Calendar!.DisplayName).ShouldBe(value);
        XElement.Parse(fixture.Bodies[1]!).Descendants(name).Single().Value.ShouldBe(value);
        fixture.Methods.ShouldBe(["PROPFIND", "PROPPATCH", "PROPFIND"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Patch_rejects_one_scalar_beyond_the_limit_before_network(bool description, bool combining)
    {
        using var fixture = new Fixture();
        var value = BoundaryText(description, combining) + "x";

        var error = await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.PatchAsync(
            Href, TextPatch(description, value), TestContext.Current.CancellationToken));

        error.Code.ShouldBe("invalid_input");
        fixture.Methods.ShouldBeEmpty();
    }

    [Theory]
    [InlineData('\uD800')]
    [InlineData('\uDC00')]
    public async Task Patch_scalar_count_does_not_replace_or_accept_unpaired_surrogates(char invalid)
    {
        using var fixture = new Fixture();
        var value = string.Concat(Enumerable.Repeat("\U0001F600", 200)) + invalid;

        var error = await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.PatchAsync(
            Href, TextPatch(false, value), TestContext.Current.CancellationToken));

        error.Code.ShouldBe("invalid_input");
        fixture.Methods.ShouldBeEmpty();
    }

    private static string BoundaryText(bool description, bool combining)
    {
        var limit = description ? 4096 : 256;
        return string.Concat(Enumerable.Repeat(combining ? "\U0001F600\u0301" : "\U0001F600", combining ? limit / 2 : limit));
    }

    private static CalendarMetadataPatch TextPatch(bool description, string value) => description
        ? new CalendarMetadataPatch(Description: new CalendarMetadataTextPatch("set", value))
        : new CalendarMetadataPatch(DisplayName: new CalendarMetadataTextPatch("set", value));
}
