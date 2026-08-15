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

        catalog["contractVersion"]!.GetValue<string>().ShouldBe("0.2.0");
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
            tool["description"]!.GetValue<string>().Length.ShouldBeGreaterThan(0);
            tool["annotations"]!["openWorldHint"]!.GetValue<bool>().ShouldBeTrue();
        }

        catalog["$defs"]!["snapshotMutationOutcome"]!["oneOf"]!.AsArray().Count.ShouldBe(3);
        catalog["$defs"]!["deleteMutationOutcome"]!["oneOf"]!.AsArray().Count.ShouldBe(3);
        var mrtr = catalog["mrtrWireContract"]!.AsObject();
        mrtr["toolsCallParams"]!["oneOf"]!.AsArray().Count.ShouldBe(20);
        mrtr["toolsCallParams"]!["oneOf"]![0]!["properties"]!["arguments"]!["$ref"].ShouldNotBeNull();
        var callBranches = mrtr["toolsCallParams"]!["oneOf"]!.AsArray();
        callBranches.All(branch => branch!["required"]!.ToJsonString().Contains("_meta", StringComparison.Ordinal)).ShouldBeTrue();
        callBranches.Single(branch => branch!["properties"]!["name"]!["const"]!.GetValue<string>() == "calendars.list")!["required"]!
            .ToJsonString().ShouldNotContain("arguments");
        var metadata = callBranches[0]!["properties"]!["_meta"]!.AsObject();
        metadata["properties"]!["progressToken"]!["type"]!.GetValue<string>().ShouldBe("number");
        metadata["properties"]!["io.modelcontextprotocol/clientCapabilities"]!["properties"]!["elicitation"]!["properties"]!
            .AsObject().ShouldContainKey("form");
        metadata["properties"]!["io.modelcontextprotocol/clientInfo"]!["properties"]!.AsObject().ShouldContainKey("websiteUrl");
        catalog["$defs"]!["mrtrRequestedProperty"]!["properties"]!["type"]!["const"]!.GetValue<string>().ShouldBe("boolean");
        catalog["$defs"]!["mrtrResponseValue"]!["oneOf"]!.ToJsonString().ShouldContain("array");
        catalog["$defs"]!["mrtrResponseValue"]!.ToJsonString().ShouldNotContain("\"null\"");
        mrtr["outerResult"]!["properties"]!["resultType"]!["const"]!.GetValue<string>()
            .ShouldBe("input_required");
        catalog["$defs"]!["eventCreateInput"]!["properties"]!["entity"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/eventCreateEntity");
        catalog["$defs"]!["todoCreateInput"]!["properties"]!["entity"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/todoCreateEntity");

        var temporalKinds = catalog["$defs"]!["temporalValue"]!["oneOf"]!.AsArray()
            .Select(value => value!["properties"]!["kind"]!["const"]!.GetValue<string>()).ToArray();
        temporalKinds.ShouldBe(["date", "floatingDateTime", "utcDateTime", "zonedDateTime"]);
        var occurrenceInput = catalog["$defs"]!["occurrenceQueryInput"]!.AsObject();
        occurrenceInput["required"]!.ToJsonString().ShouldNotContain("evaluationTimeZone");
        occurrenceInput["properties"]!.AsObject().ShouldContainKey("evaluationTimeZone");
        var calendarListInput = catalog["$defs"]!["calendarScopeInput"]!.AsObject();
        calendarListInput["required"].ShouldBeNull();
        calendarListInput["properties"].ShouldBeNull();
        calendarListInput["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        FindTool(catalog, "calendars.list")["inputSchema"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/calendarScopeInput");
        FindTool(catalog, "calendar_resources.get")["inputSchema"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/resourceAddressInput");
        FindTool(catalog, "calendar_resources.exact_get")["inputSchema"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/resourceAddressInput");
        FindTool(catalog, "calendar_resources.delete")["inputSchema"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/deleteInput");
        FindTool(catalog, "events.create")["inputSchema"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/eventCreateInput");
        FindTool(catalog, "todos.create")["inputSchema"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/todoCreateInput");
        FindTool(catalog, "events.patch")["inputSchema"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/eventPatchInput");
        FindTool(catalog, "todos.patch")["inputSchema"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/todoPatchInput");
        FindTool(catalog, "calendar_resources.move")["inputSchema"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/semanticMoveInput");
        FindTool(catalog, "calendar_resources.exact_move")["inputSchema"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/exactMoveInput");
        catalog["$defs"]!["calendarDestination"]!["oneOf"]!.AsArray().Count.ShouldBe(2);
        catalog["$defs"]!["exactCreateInput"]!["properties"]!.AsObject().ShouldContainKey("destinationHref");
        catalog["$defs"]!["exactMoveInput"]!["properties"]!.AsObject().ShouldNotContainKey("requestState");
        catalog["$defs"]!["exactGetSuccess"]!["properties"]!["resourceLink"]!["properties"]!["type"]!["const"]!
            .GetValue<string>().ShouldBe("resource_link");
        var capabilities = catalog["$defs"]!["calendarDescriptor"]!["properties"]!["entityKinds"]!.AsObject();
        capabilities["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        capabilities["required"]!.ToJsonString().ShouldContain("event");
        capabilities["required"]!.ToJsonString().ShouldContain("todo");
        var snapshot = catalog["$defs"]!["calendarSnapshot"]!["properties"]!.AsObject();
        snapshot.ShouldContainKey("calendarProperties");
        snapshot.ShouldContainKey("authoritativePayload");
        snapshot.ShouldContainKey("resourceRevision");
        snapshot["calendar"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/calendarHref");
        catalog["$defs"]!["calendarDescriptor"]!["properties"]!["calendar"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/calendarHref");
        catalog["$defs"]!["authorizedCandidate"]!["properties"]!["calendar"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/calendarHref");
        snapshot["entityRevision"]!.ShouldNotBeNull();
        catalog["$defs"]!["occurrenceTiming"]!["anyOf"].ShouldBeNull();
        catalog["$defs"]!["recurrenceSet"]!["properties"]!.AsObject().ShouldContainKey("overrides");
        catalog["$defs"]!["structuredData"]!["properties"]!.AsObject().ShouldContainKey("attendees");
        catalog["$defs"]!["errorOutcome"]!["properties"]!["category"]!["enum"]!.AsArray().Count.ShouldBe(8);
        catalog["$defs"]!["errorOutcome"]!["properties"]!["phase"]!["enum"]!.AsArray().Count.ShouldBe(10);
        FindOpenSchemaNodes(catalog).ShouldBeEmpty();
    }

    [Fact]
    public void Evidence_catalog_has_one_complete_row_for_every_normative_requirement()
    {
        var catalog = File.ReadAllText(ContractPath("requirement-evidence-catalog.md"));
        var rows = catalog.Split('\n').Where(line => line.StartsWith("## CAL-", StringComparison.Ordinal)).ToArray();

        catalog.ShouldStartWith("# Requirement-to-evidence catalog: unified Calendar contract 0.2.0");
        rows.Length.ShouldBe(96);
        rows.Distinct(StringComparer.Ordinal).Count().ShouldBe(96);
        rows.Select(row => row[3..]).OrderBy(id => id, StringComparer.Ordinal)
            .ShouldBe(ExpectedRequirementIds().OrderBy(id => id, StringComparer.Ordinal));
        catalog.ShouldContain("## CAL-BASE-003");
        catalog.ShouldContain("## CAL-EVIDENCE-010");
        catalog.ShouldContain("Source and owning decision:");
        catalog.ShouldContain("Normative strength:");
        catalog.ShouldContain("Primary evidence layer:");
        catalog.ShouldContain("Objective oracle:");
        catalog.ShouldContain("Implementation status:");
        catalog.ShouldNotContain("produce the contractually specified result");
        catalog.ShouldNotContain("assert this observable result:");
        catalog.ShouldNotContain("Run the catalog verifier and assert this observable result:");
        catalog.ShouldNotContain("committed named scenario must emit");
        var oracles = catalog.Split('\n').Where(line => line.StartsWith("- Objective oracle:", StringComparison.Ordinal)).ToArray();
        oracles.Length.ShouldBe(96);
        oracles.Distinct(StringComparer.Ordinal).Count().ShouldBe(96);
        foreach (var row in catalog.Split("\n## CAL-", StringSplitOptions.None).Skip(1))
        {
            var statement = ExtractRowField(row, "Normative statement:");
            var oracle = ExtractRowField(row, "Objective oracle:");
            Normalize(statement).ShouldNotBe(Normalize(oracle));
            Normalize(oracle).ShouldNotContain(Normalize(statement));
        }
        rows.Length.ShouldBe(catalog.Split("Named scenario or fixture:", StringSplitOptions.None).Length - 1);
        foreach (var row in catalog.Split("\n## CAL-", StringSplitOptions.None).Skip(1))
        {
            row.ShouldContain("Normative statement:");
            row.ShouldContain("Source and owning decision:");
            row.ShouldContain("Normative strength:");
            row.ShouldContain("Primary evidence layer:");
            row.ShouldContain("Named scenario or fixture:");
            row.ShouldContain("Objective oracle:");
            row.ShouldContain("Implementation status:");
            row.ShouldContain("Evidence status:");
        }
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
        rows.All(row => row.Split('|').Length >= 7).ShouldBeTrue();
        var classes = new[] { "supported", "required typed rejection", "preserved but unevaluable", "pinned-profile-only", "unsafe through Ical.Net" };
        foreach (var row in rows.Where(row => !row.Contains("---", StringComparison.Ordinal)).Skip(1))
        {
            var cells = row.Split('|', StringSplitOptions.TrimEntries);
            cells[2].ShouldBeOneOf(classes);
            cells[3].ShouldBeOneOf(classes);
            cells[4].ShouldBeOneOf(classes);
        }
        matrix.ShouldContain("preserved but unevaluable` is not semantic support");
        matrix.ShouldContain("unsafe through Ical.Net");
        matrix.ShouldContain("required typed rejection");
        matrix.ShouldContain("pinned-profile-only");
    }

    private const string RadicaleConformanceIndexDigest = "sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80";

    private static JsonObject ReadJson(string fileName) => JsonNode.Parse(File.ReadAllText(ContractPath(fileName)))!.AsObject();

    private static IReadOnlyList<string> FindOpenSchemaNodes(JsonNode node, string path = "$")
    {
        var findings = new List<string>();
        Visit(node, path, findings);
        return findings;
    }

    private static void Visit(JsonNode? node, string path, ICollection<string> findings)
    {
        switch (node)
        {
            case JsonObject obj:
                VisitObject(obj, path, findings);
                break;
            case JsonArray array:
                VisitArray(array, path, findings);
                break;
        }
    }

    private static void VisitObject(JsonObject obj, string path, ICollection<string> findings)
    {
        AddOpenObjectFinding(obj, path, findings);
        AddUntypedArrayFinding(obj, path, findings);

        foreach (var property in obj)
        {
            Visit(property.Value, $"{path}.{property.Key}", findings);
        }
    }

    private static void VisitArray(JsonArray array, string path, ICollection<string> findings)
    {
        for (var index = 0; index < array.Count; index++)
        {
            Visit(array[index], $"{path}[{index}]", findings);
        }
    }

    private static void AddOpenObjectFinding(JsonObject obj, string path, ICollection<string> findings)
    {
        if (HasType(obj, "object") && !IsClosed(obj) && !IsProtocolMap(path))
        {
            findings.Add(path);
        }
    }

    private static bool IsProtocolMap(string path) =>
        path.Contains(".inputResponses", StringComparison.Ordinal) ||
        path.Contains(".inputRequests", StringComparison.Ordinal) ||
        path.Contains(".requestedSchema.properties.properties", StringComparison.Ordinal) ||
        path.Contains("io.modelcontextprotocol/clientCapabilities", StringComparison.Ordinal) ||
        path.Contains(".properties._meta", StringComparison.Ordinal) ||
        path.Contains("$defs.mrtrInputResponse.oneOf[0].properties.content", StringComparison.Ordinal);

    private static bool IsClosed(JsonObject obj) =>
        obj["additionalProperties"] is JsonValue value && value.TryGetValue<bool>(out var closed) && !closed;

    private static void AddUntypedArrayFinding(JsonObject obj, string path, ICollection<string> findings)
    {
        if (HasType(obj, "array") && obj["items"] is null)
        {
            findings.Add(path);
        }
    }

    private static bool HasType(JsonObject obj, string expected) =>
        obj["type"] is JsonValue value && value.TryGetValue<string>(out var actual) && actual == expected;

    private static JsonObject FindTool(JsonObject catalog, string name) =>
        catalog["tools"]!.AsArray().Single(tool => tool!["name"]!.GetValue<string>() == name)!.AsObject();

    private static IEnumerable<string> ExpectedRequirementIds()
    {
        var areas = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["BASE"] = 4, ["MODEL"] = 7, ["RESOURCE"] = 13, ["DISC"] = 6, ["DAV"] = 6,
            ["EVENT"] = 7, ["TIME"] = 4, ["RECUR"] = 7, ["MCP"] = 13, ["BOUND"] = 8,
            ["ERROR"] = 3, ["SEC"] = 3, ["RELEASE"] = 5, ["EVIDENCE"] = 10
        };

        return areas.SelectMany(area => Enumerable.Range(1, area.Value)
            .Select(number => $"CAL-{area.Key}-{number:000}"));
    }

    private static string ContractPath(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotnetAgentsCalDav.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "contracts", "0.2.0", fileName);
    }

    private static string ExtractRowField(string row, string label) =>
        row.Split('\n').Single(line => line.StartsWith($"- {label}", StringComparison.Ordinal))[($"- {label} ").Length..];

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
