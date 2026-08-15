using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class ContractCatalogTests
{
    [Fact]
    public void Mcp_catalog_freezes_the_semantic_and_exact_tool_contract()
    {
        var catalog = ReadJson("mcp-tool-catalog.json");

        catalog["contractVersion"]!.GetValue<string>().ShouldBe("0.2.0-draft.1");
        catalog["protocolRevision"]!.GetValue<string>().ShouldBe("2026-07-28");
        catalog["discoveryOrder"]!.AsArray().Count.ShouldBe(16);
        catalog["exactTools"]!.AsArray().Count.ShouldBe(4);
        catalog["tools"]!.AsArray().Count.ShouldBe(20);

        var calendarReference = catalog["$defs"]!["calendarReference"]!.AsObject();
        var referenceBranches = calendarReference["oneOf"]!.AsArray();
        referenceBranches.Count.ShouldBe(2);
        referenceBranches.All(branch => !branch!["additionalProperties"]!.GetValue<bool>()).ShouldBeTrue();
        referenceBranches[0]!["properties"]!["by"]!["const"]!.GetValue<string>().ShouldBe("name");
        referenceBranches[1]!["properties"]!["by"]!["const"]!.GetValue<string>().ShouldBe("href");

        foreach (var tool in catalog["tools"]!.AsArray())
        {
            var outputReference = tool!["outputSchema"]!["$ref"]!.GetValue<string>();
            var outputName = outputReference.Split('/').Last();
            catalog["$defs"]![outputName]!["oneOf"]!.AsArray().Count.ShouldBeGreaterThanOrEqualTo(2);
            tool["cache"]!["cacheScope"]!.GetValue<string>().ShouldBe("private");
            tool["cache"]!["ttlMs"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(0);
            tool["annotations"]!.AsObject().Count.ShouldBe(4);
        }

        catalog["$defs"]!["mutationOutcome"]!["oneOf"]!.ToJsonString().ShouldContain("inputRequiredOutcome");
        catalog["$defs"]!["inputRequiredOutcome"]!["properties"]!["resultType"]!["const"]!
            .GetValue<string>().ShouldBe("input_required");
        var fields = catalog["$defs"]!["semanticCreateInput"]!["properties"]!["entity"]!.AsObject();
        fields.ToJsonString().ShouldNotContain("\"fields\":{\"type\":\"object\",\"additionalProperties\":false}");

        var temporalKinds = catalog["$defs"]!["temporalValue"]!["oneOf"]!.AsArray()
            .Select(value => value!["properties"]!["kind"]!["const"]!.GetValue<string>()).ToArray();
        temporalKinds.ShouldBe(["date", "floatingDateTime", "utcDateTime", "zonedDateTime"]);
        var occurrenceInput = catalog["$defs"]!["occurrenceQueryInput"]!.AsObject();
        occurrenceInput["required"]!.ToJsonString().ShouldNotContain("evaluationTimeZone");
        occurrenceInput["properties"]!.AsObject().ShouldContainKey("evaluationTimeZone");
        catalog["$defs"]!["calendarScopeInput"]!["required"].ShouldBeNull();
        catalog["$defs"]!["exactGetSuccess"]!["properties"]!["resourceLink"]!["properties"]!["type"]!["const"]!
            .GetValue<string>().ShouldBe("resource_link");
    }

    [Fact]
    public void Evidence_catalog_has_one_complete_row_for_every_normative_requirement()
    {
        var catalog = File.ReadAllText(ContractPath("requirement-evidence-catalog.md"));
        var rows = catalog.Split('\n').Where(line => line.StartsWith("## CAL-", StringComparison.Ordinal)).ToArray();

        catalog.ShouldStartWith("# Requirement-to-evidence catalog: unified Calendar contract 0.2.0");
        rows.Length.ShouldBe(96);
        rows.Distinct(StringComparer.Ordinal).Count().ShouldBe(96);
        catalog.ShouldContain("## CAL-BASE-003");
        catalog.ShouldContain("## CAL-EVIDENCE-010");
        catalog.ShouldContain("Source and owning decision:");
        catalog.ShouldContain("Normative strength:");
        catalog.ShouldContain("Primary evidence layer:");
        catalog.ShouldContain("Objective oracle:");
        catalog.ShouldContain("Implementation status:");
        catalog.ShouldNotContain("produce the contractually specified result");
        rows.Length.ShouldBe(catalog.Split("Named scenario or fixture:", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Radicale_profile_records_both_manifests_and_all_required_variants()
    {
        var profile = ReadJson("radicale-3.7.8-profile.json");

        profile["ociIndexDigest"]!.GetValue<string>().ShouldBe(RadicaleConformanceIndexDigest);
        profile["platformManifests"]!.AsObject().Count.ShouldBe(2);
        profile["runtime"]!["python"]!.GetValue<string>().ShouldBe("3.14.7");
        profile["runtime"]!["vobject"]!.GetValue<string>().ShouldBe("0.9.9");
        profile["variants"]!.AsArray().Select(value => value!["name"]!.GetValue<string>())
            .ShouldBe(["baseline", "strict-preconditions", "alternate-time-zone"]);
        profile["legacyTaskFixturesAreEvidence"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void Compatibility_matrix_uses_independent_component_classes()
    {
        var matrix = File.ReadAllText(ContractPath("compatibility-matrix.md"));
        var rows = matrix.Split('\n').Where(line => line.StartsWith("| ") && line.Contains(" | ", StringComparison.Ordinal)).ToArray();

        rows.Length.ShouldBeGreaterThan(10);
        rows.All(row => row.Split('|').Length >= 6).ShouldBeTrue();
        matrix.ShouldContain("preserved but unevaluable` is not semantic support");
        matrix.ShouldContain("unsafe through Ical.Net");
        matrix.ShouldContain("required typed rejection");
        matrix.ShouldContain("pinned-profile-only");
    }

    private const string RadicaleConformanceIndexDigest = "sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80";

    private static JsonObject ReadJson(string fileName) => JsonNode.Parse(File.ReadAllText(ContractPath(fileName)))!.AsObject();

    private static string ContractPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotnetAgentsCalDav.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "contracts", "0.2.0", fileName);
    }
}
