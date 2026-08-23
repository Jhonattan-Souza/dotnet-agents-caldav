using System.Text.Json.Nodes;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Hosting;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class ContractCatalogTests
{
    [Fact]
    public void Calendar_entity_query_catalog_equals_the_closed_typed_failure_vocabulary()
    {
        var error = ReadJson("mcp-tool-catalog.json")["$defs"]!["entityQueryErrorOutcome"]!["properties"]!;

        Enum.GetValues<QueryFailureCode>().Length.ShouldBe(EnumValues(error["code"]!).Length);
        EnumValues(error["code"]!).ShouldBe([
            "invalid_input", "cursor_expired", "limit_exhausted", "busy", "payload_too_large",
            "upstream_protocol_error", "unsupported_capability", "concurrency_unavailable",
            "temporal_unresolved", "recurrence_unevaluable", "upstream_unavailable",
            "upstream_unauthorized", "upstream_forbidden", "upstream_rate_limited", "not_found",
            "ambiguous", "outside_scope"
        ]);
        Enum.GetValues<QueryFailureCategory>().Length.ShouldBe(EnumValues(error["category"]!).Length);
        EnumValues(error["category"]!).ShouldBe([
            "input", "state", "limitsAndAdmission", "upstream", "capabilityAndProjection", "selection"
        ]);
        Enum.GetValues<QueryFailurePhase>().Length.ShouldBe(EnumValues(error["phase"]!).Length);
        EnumValues(error["phase"]!).ShouldBe([
            "schemaLexicalDiscriminator", "pagination", "execution", "admissionAndPayload",
            "selectionDiscoveryCapability", "targetRevision", "completeResourceSemantics",
            "originScopeAuthorization"
        ]);
    }

    [Fact]
    public void Telemetry_failure_vocabulary_covers_every_public_contract_dimension()
    {
        var definitions = ReadJson("mcp-tool-catalog.json")["$defs"]!;
        var error = definitions["errorOutcome"]!["properties"]!;

        CalendarTelemetryVocabulary.KnownErrorCodes.SetEquals(EnumValues(error["code"]!))
            .ShouldBeTrue();
        CalendarTelemetryVocabulary.KnownErrorCategories.SetEquals(EnumValues(error["category"]!))
            .ShouldBeTrue();
        CalendarTelemetryVocabulary.KnownErrorPhases.SetEquals(EnumValues(error["phase"]!))
            .ShouldBeTrue();
    }

    [Fact]
    public void Mcp_catalog_freezes_the_semantic_and_exact_tool_contract()
    {
        var catalog = ReadJson("mcp-tool-catalog.json");

        catalog.ShouldNotContainKey("contractVersion");
        catalog["protocolRevision"]!.GetValue<string>().ShouldBe("2026-07-28");
        catalog["discoveryOrder"]!.AsArray().Count.ShouldBe(17);
        catalog["exactTools"]!.AsArray().Count.ShouldBe(4);
        catalog["tools"]!.AsArray().Count.ShouldBe(21);
        var createSemantics = catalog["createSemantics"]!.AsObject();
        createSemantics["authoritativeOperation"]!.GetValue<string>().ShouldBe("conditional_put");
        createSemantics["preflightEnumeration"]!.GetValue<bool>().ShouldBeFalse();
        createSemantics["hrefConflictCode"]!.GetValue<string>().ShouldBe("destination_conflict");
        createSemantics["uidConflictCode"]!.GetValue<string>().ShouldBe("conflict");
        createSemantics["rejectedMutationState"]!.GetValue<string>().ShouldBe("not_committed");
        createSemantics["generatedUidMaximumAttempts"]!.GetValue<int>().ShouldBe(3);
        createSemantics["exactReviewBindingFields"]!.AsArray()
            .Select(item => item!.GetValue<string>()).ShouldBe([
                "destinationHref", "entityUid", "entityKind", "intentDigest", "policyVersion"
            ]);

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
        var executionLimits = catalog["$defs"]!["executionLimits"]!.AsObject();
        executionLimits["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        executionLimits["properties"]!["dimension"]!["enum"]!.AsArray()
            .Select(item => item!.GetValue<string>()).ShouldBe([
                "elapsed_time", "resource_count", "attempt_count", "byte_count"]);
        executionLimits["properties"]!["observed"]!["minimum"]!.GetValue<int>().ShouldBe(0);
        executionLimits["properties"]!["limit"]!["minimum"]!.GetValue<int>().ShouldBe(0);
        catalog["$defs"]!["exactMutationErrorOutcome"]!["properties"]!["limits"]!["$ref"]!
            .GetValue<string>().ShouldBe("#/$defs/executionLimits");
        var mrtr = catalog["mrtrWireContract"]!.AsObject();
        mrtr["toolsCallParams"]!["oneOf"]!.AsArray().Count.ShouldBe(21);
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
        var todoQueryInput = catalog["$defs"]!["todoQueryInput"]!.AsObject();
        todoQueryInput["properties"]!["scope"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/todoScope");
        var todoScope = catalog["$defs"]!["todoScope"]!["oneOf"]!.AsArray();
        todoScope.Count.ShouldBe(2);
        todoScope.ToJsonString().ShouldNotContain("default");
        catalog["$defs"]!["todoQueryItem"]!["properties"]!["status"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/openEnumValue");
        catalog["$defs"]!["todoQueryItem"]!["properties"]!["due"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/effectiveTemporalValue");
        catalog["$defs"]!["todoCompletionTarget"]!["oneOf"]!.AsArray().Count.ShouldBe(3);
        catalog["$defs"]!["todoRecurrenceIdentity"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/temporalValue");
        var occurrenceInput = catalog["$defs"]!["occurrenceQueryInput"]!.AsObject();
        occurrenceInput["oneOf"]![0]!["properties"]!.AsObject().ShouldContainKey("evaluationTimeZone");
        occurrenceInput["oneOf"]![1]!["properties"]!.AsObject().ShouldNotContainKey("evaluationTimeZone");
        occurrenceInput["oneOf"]![1]!["required"]!.AsArray().Select(item => item!.GetValue<string>())
            .ShouldBe(["cursor"]);
        var entityQueryInput = catalog["$defs"]!["entityQueryInput"]!.AsObject();
        entityQueryInput["oneOf"]![0]!["properties"]!.AsObject().ShouldContainKey("evaluationTimeZone");
        entityQueryInput["oneOf"]![1]!["properties"]!.AsObject().ShouldNotContainKey("evaluationTimeZone");
        var entityQuerySuccess = catalog["$defs"]!["entityQuerySuccess"]!.AsObject();
        entityQuerySuccess["properties"]!.AsObject().ShouldContainKey("temporalEvaluationContext");
        var occurrenceQuerySuccess = catalog["$defs"]!["occurrenceQuerySuccess"]!.AsObject();
        occurrenceQuerySuccess["properties"]!.AsObject().ShouldContainKey("temporalEvaluationContext");
        catalog["environment"]!.AsArray()
            .Single(item => item!["name"]!.GetValue<string>() == "CALDAV_EVALUATION_TIME_ZONE")!
            ["description"]!.GetValue<string>().ShouldContain("bounded Calendar Entity and Occurrence Start");
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
        FindTool(catalog, "todos.complete")["inputSchema"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/completionInput");
        var completionInput = catalog["$defs"]!["completionInput"]!.AsObject();
        completionInput["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        completionInput["required"]!.AsArray().Select(item => item!.GetValue<string>())
            .ShouldBe(["snapshot"]);
        completionInput["properties"]!.AsObject().ShouldContainKey("recurrenceIdentity");
        completionInput["properties"]!.AsObject().ShouldNotContainKey("completedAt");
        completionInput["properties"]!["snapshot"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/todoRevisionReference");
        FindTool(catalog, "todos.complete")["description"]!.GetValue<string>()
            .ShouldContain("explicitly supplied absolute snapshot href");
        FindTool(catalog, "calendar_resources.move")["inputSchema"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/semanticMoveInput");
        FindTool(catalog, "calendar_resources.exact_move")["inputSchema"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/exactMoveInput");
        catalog["$defs"]!["calendarDestination"]!["oneOf"]!.AsArray().Count.ShouldBe(2);
        var exactCreateInput = catalog["$defs"]!["exactCreateInput"]!;
        FindTool(catalog, "calendar_resources.exact_create")["description"]!.GetValue<string>()
            .ShouldContain("complete caller-authored Calendar Object Resource");
        FindTool(catalog, "calendar_resources.exact_replace")["description"]!.GetValue<string>()
            .ShouldContain("complete caller-authored Calendar Object Resource");
        exactCreateInput["properties"]!.AsObject().ShouldContainKey("destinationHref");
        exactCreateInput["properties"]!.AsObject().ShouldContainKey("base64Utf8Resource");
        exactCreateInput["oneOf"]!.AsArray().Count.ShouldBe(2);
        catalog["$defs"]!["exactMoveInput"]!["properties"]!.AsObject().ShouldNotContainKey("requestState");
        catalog["$defs"]!["exactGetSuccess"]!["properties"]!["resourceLink"]!["properties"]!["type"]!["const"]!
            .GetValue<string>().ShouldBe("resource_link");
        catalog["$defs"]!["calendarDescriptor"]!["properties"]!["entityKinds"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/entityKinds");
        var snapshot = catalog["$defs"]!["calendarSnapshot"]!["properties"]!.AsObject();
        snapshot.ShouldContainKey("calendarProperties");
        snapshot.ShouldNotContainKey("authoritativePayload");
        snapshot.ShouldContainKey("resourceRevision");
        snapshot["calendar"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/calendarHref");
        catalog["$defs"]!["calendarDescriptor"]!["properties"]!["calendar"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/calendarHref");
        catalog["$defs"]!["authorizedCandidate"]!["properties"]!["calendar"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/calendarHref");
        var candidate = catalog["$defs"]!["authorizedCandidate"]!.AsObject();
        candidate["required"]!.ToJsonString().ShouldContain("displayName");
        candidate["required"]!.ToJsonString().ShouldContain("entityKinds");
        candidate["properties"]!["entityKinds"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/entityKinds");
        var candidateKinds = catalog["$defs"]!["entityKinds"]!.AsObject();
        candidateKinds["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        candidateKinds["required"]!.ToJsonString().ShouldContain("event");
        candidateKinds["required"]!.ToJsonString().ShouldContain("todo");
        snapshot["entityRevision"]!.ShouldNotBeNull();
        catalog["$defs"]!["occurrenceTiming"]!["anyOf"].ShouldBeNull();
        catalog["$defs"]!["recurrenceSet"]!["properties"]!.AsObject().ShouldContainKey("overrides");
        var structuredData = catalog["$defs"]!["structuredData"]!["properties"]!.AsObject();
        structuredData.ShouldContainKey("attendees");
        structuredData.ShouldContainKey("participants");
        structuredData.ShouldContainKey("structuredDataUris");
        structuredData["attendees"]!["items"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/attendee");
        structuredData["participants"]!["items"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/participant");
        structuredData["structuredDataUris"]!["items"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/uriProperty");
        structuredData["concepts"]!["items"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/uriProperty");
        catalog["$defs"]!["participant"]!["required"]!.AsArray()
            .Select(item => item!.GetValue<string>()).ShouldBe(["uid", "participantType", "schedulable"]);
        var participant = catalog["$defs"]!["participant"]!["properties"]!.AsObject();
        participant["uid"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/textProperty");
        participant["participantType"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/openEnumValue");
        participant["created"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/utcTemporalProperty");
        participant["geo"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/geoProperty");
        participant["priority"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/priorityProperty");
        participant["sequence"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/nonNegativeIntegerProperty");
        participant["status"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/openEnumValue");
        participant["categories"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/textListProperty");
        participant["description"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/textProperty");
        participant["summary"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/textProperty");
        participant["url"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/uriProperty");
        catalog["$defs"]!["attendee"]!["properties"]!["role"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/effectiveOpenEnumValue");
        var alarm = catalog["$defs"]!["alarm"]!["properties"]!.AsObject();
        alarm["action"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/alarmActionProperty");
        alarm["trigger"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/textProperty");
        alarm["description"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/textProperty");
        alarm["repeat"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/positiveIntegerProperty");
        alarm["duration"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/durationProperty");
        alarm["summary"]!["$ref"]!.GetValue<string>().ShouldBe("#/$defs/textProperty");
        alarm.ShouldContainKey("attendees");
        alarm.ShouldContainKey("attachments");
        catalog["$defs"]!["relation"]!["required"]!.ToJsonString().ShouldContain("parameters");
        catalog["$defs"]!["requestStatus"]!["required"]!.ToJsonString().ShouldContain("parameters");
        catalog["$defs"]!["errorOutcome"]!["properties"]!["category"]!["enum"]!.AsArray().Count.ShouldBe(8);
        catalog["$defs"]!["errorOutcome"]!["properties"]!["phase"]!["enum"]!.AsArray().Count.ShouldBe(11);
        catalog["$defs"]!["errorOutcome"]!["properties"]!["code"]!["enum"]!.ToJsonString()
            .ShouldContain("completion_state_conflict");
        FindOpenSchemaNodes(catalog).ShouldBeEmpty();
    }

    [Fact]
    public void Mcp_catalog_freezes_kind_specific_create_recurrence_without_changing_read_or_patch_shapes()
    {
        var catalog = ReadJson("mcp-tool-catalog.json");

        AssertCreateRecurrenceSchema(catalog, "event");
        AssertCreateRecurrenceSchema(catalog, "todo");
        AssertPatchRecurrenceSchema(catalog, "event");
        AssertPatchRecurrenceSchema(catalog, "todo");
        catalog["$defs"]!["recurrenceDateInput"]!["oneOf"]!.AsArray().Count.ShouldBe(2);
        catalog["$defs"]!["recurrencePeriodInput"]!["oneOf"]!.AsArray().Count.ShouldBe(2);
        catalog["$defs"]!["recurrenceOverride"]!["properties"]!.AsObject().ShouldContainKey("entityKind");
        catalog["$defs"]!["recurrenceOverride"]!["properties"]!.AsObject().ShouldContainKey("fields");
        catalog["$defs"]!["recurrenceOverride"]!["allOf"]![0]!["oneOf"]!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public void Mcp_catalog_keeps_calendar_resource_entity_and_occurrence_identities_distinct()
    {
        var definitions = ReadJson("mcp-tool-catalog.json")["$defs"]!;

        definitions["calendarHref"]!["required"]!.AsArray().Select(item => item!.GetValue<string>())
            .ShouldBe(["href"]);
        definitions["resourceRevision"]!["required"]!.AsArray().Select(item => item!.GetValue<string>())
            .ShouldBe(["href", "entityTag"]);
        var entityRevision = definitions["revisionReference"]!;
        entityRevision["required"]!.AsArray().Select(item => item!.GetValue<string>())
            .ShouldBe(["href", "entityUid", "entityKind", "entityTag"]);
        entityRevision["properties"]!.AsObject().Select(property => property.Key)
            .ShouldBe(["href", "entityUid", "entityKind", "entityTag"]);
        var occurrence = definitions["occurrenceSnapshot"]!;
        occurrence["required"]!.AsArray().Select(item => item!.GetValue<string>())
            .ShouldBe(["snapshot", "recurrenceIdentity", "timing"]);
        occurrence["properties"]!["recurrenceIdentity"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/recurrenceIdentity");
        entityRevision.ToJsonString().ShouldNotContain("summary");
        entityRevision.ToJsonString().ShouldNotContain("start");
        entityRevision.ToJsonString().ShouldNotContain("name");
    }

    [Fact]
    public void Patch_catalog_models_every_structured_collection_without_a_destructive_structured_data_scalar()
    {
        var definitions = ReadJson("mcp-tool-catalog.json")["$defs"]!;
        var eventFields = ScalarFields(definitions["eventScalarPatch"]!);
        var todoFields = ScalarFields(definitions["todoScalarPatch"]!);

        eventFields.ShouldContain("organizer");
        eventFields.ShouldNotContain("structuredData");
        todoFields.ShouldContain("organizer");
        todoFields.ShouldContain("percentComplete");
        todoFields.ShouldNotContain("completed");
        todoFields.ShouldNotContain("structuredData");
        FindTool(ReadJson("mcp-tool-catalog.json"), "todos.patch")["description"]!.GetValue<string>()
            .ShouldContain("todos.complete");

        var expectedItemReferences = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["categories"] = "string",
            ["attendees"] = "#/$defs/attendeeInput",
            ["participants"] = "#/$defs/participantInput",
            ["contacts"] = "#/$defs/textProperty",
            ["resources"] = "#/$defs/textProperty",
            ["relatedTo"] = "#/$defs/relationInput",
            ["requestStatuses"] = "#/$defs/requestStatus",
            ["alarms"] = "#/$defs/alarmInput",
            ["attachments"] = "#/$defs/namedUri",
            ["comments"] = "#/$defs/textProperty",
            ["styledDescriptions"] = "#/$defs/textProperty",
            ["images"] = "#/$defs/namedUri",
            ["conferences"] = "#/$defs/namedUri",
            ["links"] = "#/$defs/namedUri",
            ["concepts"] = "#/$defs/uriProperty",
            ["structuredDataUris"] = "#/$defs/uriProperty",
            ["locationUris"] = "#/$defs/namedComponentInput",
            ["resourceUris"] = "#/$defs/resourceComponentInput"
        };
        var branches = definitions["collectionPatch"]!["oneOf"]!.AsArray();

        branches.Count.ShouldBe(expectedItemReferences.Count * 2);
        foreach (var expected in expectedItemReferences)
        {
            var fieldBranches = branches.Where(branch =>
                branch!["properties"]!["field"]!["const"]!.GetValue<string>() == expected.Key).ToArray();
            fieldBranches.Length.ShouldBe(2);
            fieldBranches.Select(branch => branch!["properties"]!["operation"]!["const"]!.GetValue<string>())
                .OrderBy(value => value, StringComparer.Ordinal)
                .ShouldBe(["addRemove", "replaceAll"]);
            foreach (var branch in fieldBranches)
            {
                branch!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
                AssertItemType(branch, expected.Value);
            }
            var addRemove = fieldBranches.Single(branch =>
                branch!["properties"]!["operation"]!["const"]!.GetValue<string>() == "addRemove")!;
            var nonEmptyAlternatives = addRemove["anyOf"]!.AsArray();
            nonEmptyAlternatives.Count.ShouldBe(2);
            foreach (var name in new[] { "add", "remove" })
            {
                var alternative = nonEmptyAlternatives.Single(node =>
                    node!["required"]!.AsArray().Single()!.GetValue<string>() == name)!;
                alternative["properties"]![name]!["minItems"]!.GetValue<int>().ShouldBe(1);
            }
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
        matrix.ShouldContain(
            "| To-do Completion | supported | unsafe through Ical.Net | pinned-profile-only | implemented |");
        matrix.ShouldContain("completion instant comes only from the injected server clock");
        matrix.ShouldContain(
            "| Other CalDAV servers | pinned-profile-only | required typed rejection | pinned-profile-only | implemented capability negotiation only |");
        matrix.ShouldContain("an unverified transcript remains operable, but no interoperability claim is made");
    }

    private const string RadicaleConformanceIndexDigest = "sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80";

    private static JsonObject ReadJson(string fileName) => JsonNode.Parse(File.ReadAllText(
        fileName == "mcp-tool-catalog.json"
            ? Path.Combine(RepositoryRoot(), "src", "DotnetAgents.CalDav.Mcp", "Contracts", fileName)
            : ContractPath(fileName)))!.AsObject();

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

    private static string[] EnumValues(JsonNode schema) => schema["enum"]!.AsArray()
        .Select(value => value!.GetValue<string>())
        .ToArray();

    private static void AssertCreateRecurrenceSchema(JsonObject catalog, string kind)
    {
        var recurrenceName = $"{kind}RecurrenceSetInput";
        var overrideName = $"{kind}RecurrenceOverrideInput";
        catalog["$defs"]![$"{kind}InputFields"]!["properties"]!["recurrenceSet"]!["$ref"]!
            .GetValue<string>().ShouldBe($"#/$defs/{recurrenceName}");
        catalog["$defs"]![recurrenceName]!["properties"]!["overrides"]!["items"]!["$ref"]!
            .GetValue<string>().ShouldBe($"#/$defs/{overrideName}");
        var recurrenceOverride = catalog["$defs"]![overrideName]!.AsObject();
        recurrenceOverride["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
        recurrenceOverride["properties"]!.AsObject().ShouldNotContainKey("entityKind");
        recurrenceOverride["properties"]!.AsObject().ShouldNotContainKey("uid");
        recurrenceOverride["required"]!.ToJsonString().ShouldContain("fields");
    }

    private static void AssertPatchRecurrenceSchema(JsonObject catalog, string kind)
    {
        var recurrenceBranches = catalog["$defs"]![$"{kind}ScalarPatch"]!["oneOf"]!.AsArray()
            .Where(node => node!["properties"]?["field"]?["const"]?.GetValue<string>() == "recurrenceSet")
            .ToArray();
        var set = recurrenceBranches.Single(node =>
            node!["properties"]?["operation"]?["const"]?.GetValue<string>() == "set")!;
        set["properties"]!["value"]!["$ref"]!.GetValue<string>()
            .ShouldBe("#/$defs/recurrenceSetInput");
        foreach (var branch in recurrenceBranches)
        {
            branch!["additionalProperties"]!.GetValue<bool>().ShouldBeFalse();
            branch["properties"]!.AsObject().ShouldContainKey("orphanReconciliations");
            branch["properties"]!["orphanReconciliations"]!["items"]!["$ref"]!.GetValue<string>()
                .ShouldBe("#/$defs/orphanReconciliation");
        }

        var reconciliations = catalog["$defs"]!["orphanReconciliation"]!["oneOf"]!.AsArray();
        reconciliations.Count.ShouldBe(2);
        var exdate = reconciliations.Single(node =>
            node!["properties"]!["kind"]!["const"]!.GetValue<string>() == "exdate")!;
        exdate["required"]!.AsArray().Select(node => node!.GetValue<string>())
            .ShouldBe(["kind", "recurrenceIdentity", "disposition"]);
        exdate["properties"]!["disposition"]!["const"]!.GetValue<string>().ShouldBe("remove");
        var occurrenceOverride = reconciliations.Single(node =>
            node!["properties"]!["kind"]!["const"]!.GetValue<string>() == "override")!;
        occurrenceOverride["required"]!.AsArray().Select(node => node!.GetValue<string>())
            .ShouldBe(["kind", "recurrenceIdentity", "overrideKind", "disposition"]);
        occurrenceOverride["properties"]!["overrideKind"]!["enum"]!.AsArray()
            .Select(node => node!.GetValue<string>()).ShouldBe(["individual", "this-and-future"]);
        occurrenceOverride["properties"]!["disposition"]!["const"]!.GetValue<string>().ShouldBe("remove");
    }

    private static string[] ScalarFields(JsonNode scalarPatch) => scalarPatch["oneOf"]!.AsArray()
        .Select(branch => branch!["properties"]!["field"]!["const"]!.GetValue<string>())
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    private static void AssertItemType(JsonNode branch, string expected)
    {
        var operation = branch["properties"]!["operation"]!["const"]!.GetValue<string>();
        var arrayName = operation == "replaceAll" ? "values" : "add";
        var items = branch["properties"]![arrayName]!["items"]!;
        if (expected == "string")
            items["type"]!.GetValue<string>().ShouldBe(expected);
        else
            items["$ref"]!.GetValue<string>().ShouldBe(expected);
    }

    private static string ContractPath(string fileName) =>
        Path.Combine(RepositoryRoot(), "contracts", "0.2.0", fileName);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotnetAgentsCalDav.slnx")))
        {
            directory = directory.Parent;
        }

        return directory!.FullName;
    }

}
