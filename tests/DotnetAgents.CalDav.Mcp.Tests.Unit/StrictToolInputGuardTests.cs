using System.Text;
using System.Text.Json.Nodes;
using DotnetAgents.CalDav.Mcp.Hosting;
using DotnetAgents.CalDav.Mcp.Tools;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class StrictToolInputGuardTests
{
    [Theory]
    [InlineData("[]", false)]
    [InlineData("[1,{\"a\":true}]", false)]
    [InlineData("{\"a\":1,\"A\":2}", false)]
    [InlineData("{\"a\":1,\"a\":2}", true)]
    [InlineData("{\"a\":{\"b\":1,\"b\":2}}", true)]
    [InlineData("{\"a\":[{\"b\":1},{\"b\":2}]}", false)]
    public void InspectArguments_DetectsOrdinalDuplicatesRecursively(string json, bool expectedDuplicate)
    {
        var arguments = JsonNode.Parse(json);

        var evidence = StrictToolInputGuard.InspectArguments(arguments);

        evidence.ArgumentBytes.ShouldBe(Encoding.UTF8.GetByteCount(json));
        evidence.HasDuplicateProperty.ShouldBe(expectedDuplicate);
    }

    [Fact]
    public void InspectArguments_NullHasNoEvidence()
    {
        StrictToolInputGuard.InspectArguments(null).ShouldBe(default);
    }

    [Fact]
    public void InspectArguments_FlagsInvalidSurrogateEscapeForTypedBoundaryRejection()
    {
        var arguments = JsonNode.Parse("{\"utf8Resource\":\"\\uD800\"}");

        var evidence = StrictToolInputGuard.InspectArguments(arguments);
        var result = StrictToolInputGuard.Reject("calendar_resources.exact_create", evidence);

        evidence.HasInvalidString.ShouldBeTrue();
        result.ShouldNotBeNull();
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
    }

    [Theory]
    [InlineData("other.tool", 300_000, true, null)]
    [InlineData("calendar_entities.query", null, false, null)]
    [InlineData("calendar_entities.query", 262_144, false, null)]
    [InlineData("calendar_entities.query", 262_145, false, "payload_too_large")]
    [InlineData("calendar_entities.query", 262_145, true, "payload_too_large")]
    [InlineData("calendar_entities.query", 1, true, "invalid_input")]
    [InlineData("calendar_occurrences.query", null, false, null)]
    [InlineData("calendar_occurrences.query", 262_144, false, null)]
    [InlineData("calendar_occurrences.query", 262_145, false, "payload_too_large")]
    [InlineData("calendar_occurrences.query", 262_145, true, "payload_too_large")]
    [InlineData("calendar_occurrences.query", 1, true, "invalid_input")]
    [InlineData("calendar_resources.get", 262_144, false, null)]
    [InlineData("calendar_resources.get", 262_145, false, "payload_too_large")]
    [InlineData("calendar_resources.get", 262_145, true, "payload_too_large")]
    [InlineData("calendar_resources.get", 1, true, "invalid_input")]
    [InlineData("events.create", 262_144, false, null)]
    [InlineData("events.create", 262_145, true, "payload_too_large")]
    [InlineData("events.create", 1, true, "invalid_input")]
    [InlineData("todos.create", 262_144, false, null)]
    [InlineData("todos.create", 262_145, true, "payload_too_large")]
    [InlineData("todos.create", 1, true, "invalid_input")]
    [InlineData("calendar_resources.delete", 262_144, false, null)]
    [InlineData("calendar_resources.delete", 262_145, true, "payload_too_large")]
    [InlineData("calendar_resources.delete", 1, true, "invalid_input")]
    [InlineData("calendar_resources.move", 262_144, false, null)]
    [InlineData("calendar_resources.move", 262_145, true, "payload_too_large")]
    [InlineData("calendar_resources.move", 1, true, "invalid_input")]
    [InlineData("calendar_resources.exact_create", 25_231_360, false, null)]
    [InlineData("calendar_resources.exact_replace", 25_231_361, false, "payload_too_large")]
    [InlineData("calendar_resources.exact_get", 262_144, false, null)]
    [InlineData("calendar_resources.exact_get", 262_145, false, "payload_too_large")]
    [InlineData("calendar_resources.exact_get", 1, true, "invalid_input")]
    [InlineData("calendar_resources.exact_move", 65_536, false, null)]
    [InlineData("calendar_resources.exact_move", 65_537, false, "payload_too_large")]
    [InlineData("calendar_resources.exact_move", 1, true, "invalid_input")]
    public void Reject_AppliesQuerySpecificAdmissionBeforeDuplicateValidation(
        string toolName,
        int? argumentBytes,
        bool duplicate,
        string? expectedCode)
    {
        var result = StrictToolInputGuard.Reject(
            toolName,
            new StrictToolInputEvidence(argumentBytes, duplicate));

        if (expectedCode is null)
        {
            result.ShouldBeNull();
            return;
        }
        result.ShouldNotBeNull();
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
        result.StructuredContent.Value.TryGetProperty("items", out _).ShouldBeFalse();
    }

    [Fact]
    public void Reject_CapsThirtyThreeSafeViolationsAtThirtyTwoAndSortsByJsonPointer()
    {
        var violations = Enumerable.Range(0, 33)
            .Reverse()
            .Select(index => new CalendarInputViolation(
                $"/items/{index:D2}",
                "invalid_member",
                "A member is invalid."))
            .ToArray();

        var result = StrictToolInputGuard.Reject(
            "calendar_entities.query",
            new StrictToolInputEvidence(1, false, CollectedViolations: violations));

        result.ShouldNotBeNull();
        var serialized = result.StructuredContent!.Value;
        serialized.GetProperty("code").GetString().ShouldBe("invalid_input");
        var actual = serialized.GetProperty("violations").EnumerateArray().ToArray();
        actual.Length.ShouldBe(32);
        actual.Select(item => item.GetProperty("pointer").GetString())
            .ShouldBe(Enumerable.Range(0, 32).Select(index => $"/items/{index:D2}"));
        actual.ShouldAllBe(item => item.GetProperty("message").GetString() == "A member is invalid.");
    }

    [Theory]
    [InlineData("null", "/", "invalid_type")]
    [InlineData("[]", "/", "invalid_type")]
    [InlineData("{}", "/href", "invalid_member")]
    [InlineData("{\"href\":null}", "/href", "invalid_member")]
    [InlineData("{\"href\":1}", "/href", "invalid_member")]
    [InlineData("{\"href\":\"\"}", "/href", "invalid_member")]
    [InlineData("{\"href\":\"https://cal.example/a.ics\"}", null, null)]
    [InlineData("{\"href\":\"https://cal.example/a.ics\",\"padding\":\"x\"}", "/padding", "unknown_member")]
    public void ValidateResourceGetArguments_EnforcesTheClosedNativeShape(
        string json,
        string? expectedPointer,
        string? expectedCode)
    {
        var violations = StrictToolInputGuard.ValidateResourceGetArguments(JsonNode.Parse(json));

        if (expectedPointer is null)
        {
            violations.ShouldBeEmpty();
            return;
        }
        violations.ShouldContain(violation =>
            violation.Pointer == expectedPointer && violation.Code == expectedCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Reject_ExactWriteEnvelopeAllowsEscapeExpansionAtDecodedResourceLimit(int extraByte)
    {
        var resource = ExactResource((4 * 1024 * 1024) + extraByte);
        var arguments = new JsonObject
        {
            ["destinationHref"] = "https://cal.example/events/escaped.ics",
            ["utf8Resource"] = Encoding.UTF8.GetString(resource)
        };

        var evidence = StrictToolInputGuard.InspectArguments(arguments);
        var result = StrictToolInputGuard.Reject("calendar_resources.exact_create", evidence);

        evidence.ArgumentBytes.ShouldNotBeNull();
        evidence.ArgumentBytes.Value.ShouldBeGreaterThan((4 * 1024 * 1024) + 4096);
        result.ShouldBeNull();
    }

    private static byte[] ExactResource(int targetBytes)
    {
        const string prefix = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Tests//EN\r\n"
            + "BEGIN:VEVENT\r\nUID:escaped\r\nDTSTAMP:20260817T120000Z\r\n"
            + "DTSTART:20260818T120000Z\r\nSUMMARY:";
        const string suffix = "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        return Encoding.UTF8.GetBytes(prefix + new string('<', targetBytes - prefix.Length - suffix.Length) + suffix);
    }
}
