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

    [Theory]
    [InlineData("other.tool", 300_000, true, null)]
    [InlineData("calendar_entities.query", null, false, null)]
    [InlineData("calendar_entities.query", 262_144, false, null)]
    [InlineData("calendar_entities.query", 262_145, false, "payload_too_large")]
    [InlineData("calendar_entities.query", 262_145, true, "payload_too_large")]
    [InlineData("calendar_entities.query", 1, true, "invalid_input")]
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
}
