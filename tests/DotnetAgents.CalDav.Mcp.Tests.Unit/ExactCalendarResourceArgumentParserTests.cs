using System.Text.Json;
using DotnetAgents.CalDav.Mcp.Tools;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class ExactCalendarResourceArgumentParserTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("missing-destination")]
    [InlineData("missing-resource")]
    [InlineData("unknown")]
    [InlineData("relative-destination")]
    [InlineData("non-string-destination")]
    [InlineData("non-string-resource")]
    public void TryParseCreate_RejectsEveryInvalidShape(string scenario)
    {
        var arguments = CreateArguments();
        switch (scenario)
        {
            case "null":
                arguments = null;
                break;
            case "missing-destination":
                arguments!.Remove("destinationHref");
                break;
            case "missing-resource":
                arguments!.Remove("utf8Resource");
                break;
            case "unknown":
                arguments!["extra"] = JsonSerializer.SerializeToElement(true);
                break;
            case "relative-destination":
                arguments!["destinationHref"] = JsonSerializer.SerializeToElement("relative.ics");
                break;
            case "non-string-destination":
                arguments!["destinationHref"] = JsonSerializer.SerializeToElement(42);
                break;
            case "non-string-resource":
                arguments!["utf8Resource"] = JsonSerializer.SerializeToElement(false);
                break;
        }

        ExactCalendarResourceArgumentParser.TryParseCreate(arguments, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParseCreate_AcceptsExactAbsoluteDestinationAndUtf8Resource()
    {
        ExactCalendarResourceArgumentParser.TryParseCreate(CreateArguments(), out var request).ShouldBeTrue();

        request.DestinationHref.ShouldBe(ResourceHref);
        request.AuthoritativeUtf8.ToArray().ShouldBe("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"u8.ToArray());
    }

    [Fact]
    public void TryParseCreate_AcceptsCanonicalBase64ResourceInsteadOfUnicodeText()
    {
        var bytes = new byte[] { 0xc3, (byte)'\r', (byte)'\n', (byte)' ', 0xa9 };
        var arguments = CreateArguments()!;
        arguments.Remove("utf8Resource");
        arguments["base64Utf8Resource"] = JsonSerializer.SerializeToElement(Convert.ToBase64String(bytes));

        ExactCalendarResourceArgumentParser.TryParseCreate(arguments, out var request).ShouldBeTrue();

        request.AuthoritativeUtf8.ToArray().ShouldBe(bytes);
    }

    [Theory]
    [InlineData("both")]
    [InlineData("non-canonical")]
    [InlineData("invalid")]
    public void TryParseCreate_RejectsAmbiguousOrInvalidBase64Resource(string scenario)
    {
        var arguments = CreateArguments()!;
        if (scenario != "both")
            arguments.Remove("utf8Resource");
        arguments["base64Utf8Resource"] = JsonSerializer.SerializeToElement(
            scenario == "non-canonical" ? "YQ==\n" : scenario == "invalid" ? "***" : "YQ==");

        ExactCalendarResourceArgumentParser.TryParseCreate(arguments, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParseCreate_RejectsInvalidUtf16SurrogateEscapeWithoutThrowing()
    {
        using var document = JsonDocument.Parse(
            "{\"destinationHref\":\"https://cal.example/events/a.ics\",\"utf8Resource\":\"\\uD800\"}");
        var arguments = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value);

        ExactCalendarResourceArgumentParser.TryParseCreate(arguments, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("missing-revision")]
    [InlineData("missing-resource")]
    [InlineData("unknown-root")]
    [InlineData("revision-not-object")]
    [InlineData("missing-href")]
    [InlineData("missing-uid")]
    [InlineData("missing-kind")]
    [InlineData("missing-tag")]
    public void TryParseReplace_RejectsInvalidObjectShape(string scenario)
    {
        var arguments = ReplaceArguments();
        switch (scenario)
        {
            case "null": arguments = null; break;
            case "missing-revision": arguments!.Remove("revision"); break;
            case "missing-resource": arguments!.Remove("utf8Resource"); break;
            case "unknown-root": arguments!["extra"] = JsonSerializer.SerializeToElement(true); break;
            case "revision-not-object": arguments!["revision"] = JsonSerializer.SerializeToElement("bad"); break;
            case "missing-href": SetRevision(arguments!, "entityUid", "u1", "entityKind", "event", "entityTag", "\"r1\""); break;
            case "missing-uid": SetRevision(arguments!, "href", ResourceHref, "entityKind", "event", "entityTag", "\"r1\""); break;
            case "missing-kind": SetRevision(arguments!, "href", ResourceHref, "entityUid", "u1", "entityTag", "\"r1\""); break;
            case "missing-tag": SetRevision(arguments!, "href", ResourceHref, "entityUid", "u1", "entityKind", "event"); break;
        }

        ExactCalendarResourceArgumentParser.TryParseReplace(arguments, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("unknown-revision")]
    [InlineData("relative-href")]
    [InlineData("bad-kind")]
    [InlineData("wildcard-tag")]
    [InlineData("malformed-tag")]
    [InlineData("non-string-resource")]
    public void TryParseReplace_RejectsInvalidRevisionValue(string scenario)
    {
        var arguments = ReplaceArguments();
        switch (scenario)
        {
            case "unknown-revision": SetRevision(arguments, "href", ResourceHref, "entityUid", "u1", "entityKind", "event", "entityTag", "\"r1\"", "extra", "x"); break;
            case "relative-href": SetRevision(arguments, "href", "relative", "entityUid", "u1", "entityKind", "event", "entityTag", "\"r1\""); break;
            case "bad-kind": SetRevision(arguments, "href", ResourceHref, "entityUid", "u1", "entityKind", "journal", "entityTag", "\"r1\""); break;
            case "wildcard-tag": SetRevision(arguments, "href", ResourceHref, "entityUid", "u1", "entityKind", "event", "entityTag", "*"); break;
            case "malformed-tag": SetRevision(arguments, "href", ResourceHref, "entityUid", "u1", "entityKind", "event", "entityTag", "r1"); break;
            case "non-string-resource": arguments["utf8Resource"] = JsonSerializer.SerializeToElement(1); break;
        }

        ExactCalendarResourceArgumentParser.TryParseReplace(arguments, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParseReplace_AcceptsCanonicalWeakTagForTypedConcurrencyRejection()
    {
        var arguments = ReplaceArguments();
        SetRevision(arguments, "href", ResourceHref, "entityUid", "u1", "entityKind", "event", "entityTag", "W/\"r1\"");

        ExactCalendarResourceArgumentParser.TryParseReplace(arguments, out var request).ShouldBeTrue();

        request.Revision.EntityTag.ShouldBe("W/\"r1\"");
    }

    [Fact]
    public void TryParseReplace_AcceptsCanonicalBase64ResourceInsteadOfUnicodeText()
    {
        var bytes = new byte[] { 0xc3, (byte)'\r', (byte)'\n', (byte)' ', 0xa9 };
        var arguments = ReplaceArguments();
        arguments.Remove("utf8Resource");
        arguments["base64Utf8Resource"] = JsonSerializer.SerializeToElement(Convert.ToBase64String(bytes));

        ExactCalendarResourceArgumentParser.TryParseReplace(arguments, out var request).ShouldBeTrue();

        request.AuthoritativeUtf8.ToArray().ShouldBe(bytes);
    }

    [Theory]
    [InlineData("href")]
    [InlineData("entityUid")]
    [InlineData("entityKind")]
    [InlineData("entityTag")]
    public void TryParseReplace_RejectsNonStringRevisionFields(string propertyName)
    {
        var arguments = ReplaceArguments();
        SetRevisionWithNonString(arguments, propertyName);

        ExactCalendarResourceArgumentParser.TryParseReplace(arguments, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("valid-event")]
    [InlineData("valid-todo")]
    public void TryParseReplace_AcceptsStrongRevisionAndUtf8(string scenario)
    {
        var arguments = ReplaceArguments();
        if (scenario == "valid-todo")
            SetRevision(arguments, "href", ResourceHref, "entityUid", "u1", "entityKind", "todo", "entityTag", "\"r1\"");

        ExactCalendarResourceArgumentParser.TryParseReplace(arguments, out var request).ShouldBeTrue();

        request.Revision.EntityKind.ToString().ToLowerInvariant().ShouldBe(scenario[6..]);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("missing-revision")]
    [InlineData("missing-destination")]
    [InlineData("unknown")]
    [InlineData("revision-not-object")]
    [InlineData("non-string-destination")]
    [InlineData("relative-destination")]
    public void TryParseMove_RejectsInvalidDestinationShape(string scenario)
    {
        Dictionary<string, JsonElement>? arguments = MoveArguments();
        if (scenario == "null")
            arguments = null;
        if (scenario == "missing-revision")
            arguments!.Remove("revision");
        if (scenario == "missing-destination")
            arguments!.Remove("destinationHref");
        if (scenario == "unknown")
            arguments!["extra"] = JsonSerializer.SerializeToElement(true);
        if (scenario == "revision-not-object")
            arguments!["revision"] = JsonSerializer.SerializeToElement("bad");
        if (scenario == "non-string-destination")
            arguments!["destinationHref"] = JsonSerializer.SerializeToElement(42);
        if (scenario == "relative-destination")
            arguments!["destinationHref"] = JsonSerializer.SerializeToElement("relative");

        ExactCalendarResourceArgumentParser.TryParseMove(arguments, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParseMove_AcceptsExactRevisionAndAbsoluteDestination()
    {
        ExactCalendarResourceArgumentParser.TryParseMove(MoveArguments(), out var request).ShouldBeTrue();

        request.Revision.Href.ShouldBe(ResourceHref);
        request.DestinationHref.ShouldBe("https://cal.example/events/b.ics");
    }

    private const string ResourceHref = "https://cal.example/events/a.ics";

    private static Dictionary<string, JsonElement>? CreateArguments() => new()
    {
        ["destinationHref"] = JsonSerializer.SerializeToElement(ResourceHref),
        ["utf8Resource"] = JsonSerializer.SerializeToElement("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n")
    };

    private static Dictionary<string, JsonElement> ReplaceArguments() => new()
    {
        ["revision"] = ValidRevision(),
        ["utf8Resource"] = JsonSerializer.SerializeToElement("BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n")
    };

    private static Dictionary<string, JsonElement> MoveArguments() => new()
    {
        ["revision"] = ValidRevision(),
        ["destinationHref"] = JsonSerializer.SerializeToElement("https://cal.example/events/b.ics")
    };

    private static JsonElement ValidRevision() => JsonSerializer.SerializeToElement(new
    {
        href = ResourceHref,
        entityUid = "u1",
        entityKind = "event",
        entityTag = "\"r1\""
    });

    private static void SetRevision(Dictionary<string, JsonElement> arguments, params string[] values)
    {
        var revision = new Dictionary<string, string>();
        for (var index = 0; index < values.Length; index += 2)
            revision[values[index]] = values[index + 1];
        arguments["revision"] = JsonSerializer.SerializeToElement(revision);
    }

    private static void SetRevisionWithNonString(Dictionary<string, JsonElement> arguments, string propertyName)
    {
        var revision = new Dictionary<string, object>
        {
            ["href"] = ResourceHref,
            ["entityUid"] = "u1",
            ["entityKind"] = "event",
            ["entityTag"] = "\"r1\""
        };
        revision[propertyName] = 42;
        arguments["revision"] = JsonSerializer.SerializeToElement(revision);
    }
}
