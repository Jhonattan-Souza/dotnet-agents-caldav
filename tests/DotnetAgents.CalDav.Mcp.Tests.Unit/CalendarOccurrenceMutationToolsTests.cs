using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarOccurrenceMutationToolsTests
{
    [Fact]
    public async Task CompleteTodoRawAsync_UsesFrozenRevisionWithoutCallerCompletionTime()
    {
        var service = Substitute.For<ICalendarService>();
        CalendarTodoCompletionRequest? observed = null;
        service.CompleteTodoAsync(
                Arg.Do<CalendarTodoCompletionRequest>(request => observed = request),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarEntityPatchResult(
                CalendarEntityPatchCode.NoChange,
                CalendarMutationState.NotAttempted));
        var sut = CreateSut(service);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"snapshot\":{\"href\":\"https://cal.example/work/series.ics\","
            + "\"entityUid\":\"series-1\",\"entityKind\":\"todo\",\"entityTag\":\"\\\"v1\\\"\"}}");

        var result = await sut.CompleteTodoRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        observed.ShouldNotBeNull();
        observed.Snapshot.EntityKind.ShouldBe(CalendarEntityKind.Todo);
        observed.RecurrenceIdentity.ShouldBeNull();
    }

    [Fact]
    public async Task CompleteTodoRawAsync_RejectsLegacyCallerCompletionTimeWithoutServiceCall()
    {
        var service = Substitute.For<ICalendarService>();
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"snapshot\":{\"href\":\"https://cal.example/work/series.ics\","
            + "\"entityUid\":\"series-1\",\"entityKind\":\"todo\",\"entityTag\":\"\\\"v1\\\"\"},"
            + "\"completedAt\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-17T12:00:00Z\"}}");

        var result = await CreateSut(service).CompleteTodoRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        await service.DidNotReceive().CompleteTodoAsync(
            Arg.Any<CalendarTodoCompletionRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("this-and-future")]
    [InlineData("entire-set")]
    public async Task CompleteTodoRawAsync_RejectsBroadCompletionScopeWithoutServiceCall(string scope)
    {
        var service = Substitute.For<ICalendarService>();
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"snapshot\":{\"href\":\"https://cal.example/work/series.ics\","
            + "\"entityUid\":\"series-1\",\"entityKind\":\"todo\",\"entityTag\":\"\\\"v1\\\"\"},"
            + $"\"scope\":\"{scope}\"}}");

        var result = await CreateSut(service).CompleteTodoRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        await service.DidNotReceive().CompleteTodoAsync(
            Arg.Any<CalendarTodoCompletionRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddRawAsync_UsesFrozenRevisionAndOriginalIdentity()
    {
        var service = Substitute.For<ICalendarService>();
        CalendarOccurrenceMutationRequest? observed = null;
        service.AddOccurrenceAsync(
                Arg.Do<CalendarOccurrenceMutationRequest>(request => observed = request),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarEntityPatchResult(
                CalendarEntityPatchCode.NoChange,
                CalendarMutationState.NotAttempted));
        var sut = new CalendarOccurrenceMutationTools(service);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"snapshot\":{\"href\":\"https://cal.example/work/series.ics\","
            + "\"entityUid\":\"series-1\",\"entityKind\":\"event\",\"entityTag\":\"\\\"v1\\\"\"},"
            + "\"recurrenceIdentity\":{\"value\":{\"kind\":\"utcDateTime\","
            + "\"value\":\"2026-08-18T09:00:00Z\"}}}");

        var result = await sut.AddRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("no_change");
        observed.ShouldNotBeNull();
        observed.Snapshot.ShouldBe(new CalendarResourceRevisionReference(
            "https://cal.example/work/series.ics",
            "series-1",
            CalendarEntityKind.Event,
            "\"v1\""));
        observed.RecurrenceIdentity.ShouldBe(new CalendarTemporalValue(
            CalendarTemporalKind.UtcDateTime,
            "2026-08-18T09:00:00Z"));
    }

    [Theory]
    [InlineData("exclude")]
    [InlineData("restore_exclusion")]
    [InlineData("cancel")]
    [InlineData("restore_cancellation")]
    public async Task DedicatedMutationRawMethods_DispatchOnlyTheirNamedServiceOperation(string operation)
    {
        var service = Substitute.For<ICalendarService>();
        var noChange = new CalendarEntityPatchResult(
            CalendarEntityPatchCode.NoChange,
            CalendarMutationState.NotAttempted);
        service.ExcludeOccurrenceAsync(Arg.Any<CalendarOccurrenceMutationRequest>(), Arg.Any<CancellationToken>())
            .Returns(noChange);
        service.RestoreOccurrenceExclusionAsync(Arg.Any<CalendarOccurrenceMutationRequest>(), Arg.Any<CancellationToken>())
            .Returns(noChange);
        service.CancelOccurrenceAsync(Arg.Any<CalendarOccurrenceMutationRequest>(), Arg.Any<CancellationToken>())
            .Returns(noChange);
        service.RestoreOccurrenceCancellationAsync(Arg.Any<CalendarOccurrenceMutationRequest>(), Arg.Any<CancellationToken>())
            .Returns(noChange);
        var sut = new CalendarOccurrenceMutationTools(service);
        var arguments = ValidArguments();

        var result = operation switch
        {
            "exclude" => await sut.ExcludeRawAsync(arguments, CancellationToken.None),
            "restore_exclusion" => await sut.RestoreExclusionRawAsync(arguments, CancellationToken.None),
            "cancel" => await sut.CancelRawAsync(arguments, CancellationToken.None),
            _ => await sut.RestoreCancellationRawAsync(arguments, CancellationToken.None)
        };

        result.IsError.ShouldBe(false);
        await ReceivedNamedOperation(service, operation);
    }

    [Theory]
    [InlineData(CalendarEntityPatchCode.InvalidInput, CalendarMutationState.NotAttempted, CalendarEntityPatchPhase.SchemaLexicalDiscriminator, "invalid_input", "input", "not_attempted")]
    [InlineData(CalendarEntityPatchCode.NotFound, CalendarMutationState.NotAttempted, CalendarEntityPatchPhase.SelectionDiscoveryCapability, "not_found", "selection", "not_attempted")]
    [InlineData(CalendarEntityPatchCode.OpaqueResource, CalendarMutationState.NotAttempted, CalendarEntityPatchPhase.CompleteResourceSemantics, "opaque_resource", "capabilityAndProjection", "not_attempted")]
    [InlineData(CalendarEntityPatchCode.TemporalUnresolved, CalendarMutationState.NotAttempted, CalendarEntityPatchPhase.CompleteResourceSemantics, "temporal_unresolved", "capabilityAndProjection", "not_attempted")]
    [InlineData(CalendarEntityPatchCode.Conflict, CalendarMutationState.NotCommitted, CalendarEntityPatchPhase.TargetRevision, "conflict", "state", "not_committed")]
    [InlineData(CalendarEntityPatchCode.PayloadTooLarge, CalendarMutationState.NotAttempted, CalendarEntityPatchPhase.AdmissionAndPayload, "payload_too_large", "limitsAndAdmission", "not_attempted")]
    [InlineData(CalendarEntityPatchCode.FidelityFailure, CalendarMutationState.Committed, CalendarEntityPatchPhase.PostWriteVerificationOrReconciliation, "fidelity_failure", "postWriteTruth", "committed")]
    [InlineData(CalendarEntityPatchCode.UpstreamUnavailable, CalendarMutationState.Unknown, CalendarEntityPatchPhase.Execution, "upstream_unavailable", "upstream", "unknown")]
    public async Task AddRawAsync_MapsFrozenTypedFailures(
        CalendarEntityPatchCode code,
        CalendarMutationState mutationState,
        CalendarEntityPatchPhase phase,
        string expectedCode,
        string expectedCategory,
        string expectedMutationState)
    {
        var service = Substitute.For<ICalendarService>();
        service.AddOccurrenceAsync(Arg.Any<CalendarOccurrenceMutationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarEntityPatchResult(code, mutationState, Phase: phase));
        var sut = CreateSut(service);

        var result = await sut.AddRawAsync(ValidArguments(), CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe(expectedCode);
        structured.GetProperty("category").GetString().ShouldBe(expectedCategory);
        structured.GetProperty("mutationState").GetString().ShouldBe(expectedMutationState);
    }

    [Fact]
    public async Task AddRawAsync_MapsVerifiedSnapshotSuccess()
    {
        var service = Substitute.For<ICalendarService>();
        service.AddOccurrenceAsync(Arg.Any<CalendarOccurrenceMutationRequest>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityPatchResult.Success(EventSnapshot()));

        var result = await CreateSut(service).AddRawAsync(ValidArguments(), CancellationToken.None);

        result.IsError.ShouldBe(false);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("outcome").GetString().ShouldBe("success");
        structured.GetProperty("mutationState").GetString().ShouldBe("committed");
        structured.GetProperty("snapshot").GetProperty("resourceRevision").GetProperty("entityTag").GetString()
            .ShouldBe("\"v2\"");
    }

    [Theory]
    [InlineData("date", "2026-08-18", null, CalendarTemporalKind.Date)]
    [InlineData("floatingDateTime", "2026-08-18T09:00:00", null, CalendarTemporalKind.FloatingDateTime)]
    [InlineData("utcDateTime", "2026-08-18T09:00:00Z", null, CalendarTemporalKind.UtcDateTime)]
    [InlineData("zonedDateTime", "2026-08-18T09:00:00", "Europe/Zurich", CalendarTemporalKind.ZonedDateTime)]
    public async Task AddRawAsync_ParsesEveryTemporalIdentityFamily(
        string kind,
        string value,
        string? timeZone,
        CalendarTemporalKind expectedKind)
    {
        var service = Substitute.For<ICalendarService>();
        CalendarOccurrenceMutationRequest? observed = null;
        service.AddOccurrenceAsync(
                Arg.Do<CalendarOccurrenceMutationRequest>(request => observed = request),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarEntityPatchResult(
                CalendarEntityPatchCode.NoChange,
                CalendarMutationState.NotAttempted));

        var result = await CreateSut(service).AddRawAsync(
            Arguments(kind, value, timeZone),
            CancellationToken.None);

        result.IsError.ShouldBe(false);
        observed!.RecurrenceIdentity.Kind.ShouldBe(expectedKind);
        observed.RecurrenceIdentity.TimeZoneId.ShouldBe(timeZone);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"snapshot\":{},\"recurrenceIdentity\":{}}")]
    [InlineData("{\"snapshot\":{\"href\":\"https://cal.example/work/series.ics\",\"entityUid\":\"series-1\",\"entityKind\":\"event\",\"entityTag\":\"\\\"v1\\\"\"},\"recurrenceIdentity\":{\"value\":{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T09:00:00\"}}}")]
    public async Task AddRawAsync_RejectsInvalidFrozenShapesWithoutServiceCall(string json)
    {
        var service = Substitute.For<ICalendarService>();
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

        var result = await CreateSut(service).AddRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        await service.DidNotReceive().AddOccurrenceAsync(
            Arg.Any<CalendarOccurrenceMutationRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddRawAsync_SanitizesUnexpectedServiceFailure()
    {
        var service = Substitute.For<ICalendarService>();
        service.AddOccurrenceAsync(Arg.Any<CalendarOccurrenceMutationRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<CalendarEntityPatchResult>>(_ => throw new InvalidOperationException("private marker"));

        var result = await CreateSut(service).AddRawAsync(ValidArguments(), CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("indeterminate");
        JsonSerializer.Serialize(result).ShouldNotContain("private marker");
    }

    private static async Task ReceivedNamedOperation(ICalendarService service, string operation)
    {
        var request = Arg.Any<CalendarOccurrenceMutationRequest>();
        var token = Arg.Any<CancellationToken>();
        switch (operation)
        {
            case "exclude":
                await service.Received(1).ExcludeOccurrenceAsync(request, token);
                break;
            case "restore_exclusion":
                await service.Received(1).RestoreOccurrenceExclusionAsync(request, token);
                break;
            case "cancel":
                await service.Received(1).CancelOccurrenceAsync(request, token);
                break;
            default:
                await service.Received(1).RestoreOccurrenceCancellationAsync(request, token);
                break;
        }
    }

    private static CalendarOccurrenceMutationTools CreateSut(ICalendarService service) => new(service);

    private static CalendarResourceSnapshot EventSnapshot()
    {
        var bytes = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Test//EN\r\nBEGIN:VEVENT\r\nUID:series-1\r\nDTSTAMP:20260816T120000Z\r\nDTSTART:20260818T090000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        return new CalendarResourceSnapshot(
            "https://cal.example/work/",
            "https://cal.example/work/series.ics",
            "\"v2\"",
            bytes,
            [],
            new CalendarResourceProjection(CalendarResourceProjectionKind.Event, "series-1", null),
            []);
    }

    private static Dictionary<string, JsonElement> Arguments(string kind, string value, string? timeZone)
    {
        var temporal = timeZone is null
            ? $"{{\"kind\":\"{kind}\",\"value\":\"{value}\"}}"
            : $"{{\"kind\":\"{kind}\",\"value\":\"{value}\",\"timeZoneId\":\"{timeZone}\"}}";
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"snapshot\":{\"href\":\"https://cal.example/work/series.ics\","
            + "\"entityUid\":\"series-1\",\"entityKind\":\"event\",\"entityTag\":\"\\\"v1\\\"\"},"
            + "\"recurrenceIdentity\":{\"value\":" + temporal + "}}")!;
    }

    private static Dictionary<string, JsonElement> ValidArguments() => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
        "{\"snapshot\":{\"href\":\"https://cal.example/work/series.ics\","
        + "\"entityUid\":\"series-1\",\"entityKind\":\"event\",\"entityTag\":\"\\\"v1\\\"\"},"
        + "\"recurrenceIdentity\":{\"value\":{\"kind\":\"utcDateTime\","
        + "\"value\":\"2026-08-18T09:00:00Z\"}}}")!;

    [Theory]
    [MemberData(nameof(RejectedOccurrenceInputs))]
    public async Task Public_tools_name_the_rejected_argument_or_stored_state_without_writing(
        string tool,
        string kind,
        string stored,
        string identityAndTag,
        string expectedCode,
        string reasonCode,
        string? pointer)
    {
        var href = $"https://cal.example/{kind}s/entity-1.ics";
        var component = kind == "event" ? "VEVENT" : "VTODO";
        var content = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\n"
            + stored.Replace("$C", component, StringComparison.Ordinal) + "END:VCALENDAR\r\n";
        var client = Substitute.For<ICalendarClient>();
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = $"https://cal.example/{kind}s/",
                DisplayName = "Entities",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.Advertised
            }
        ]);
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>())
            .Returns(CalendarResourceRead.Success(href, "\"r1\"", Encoding.UTF8.GetBytes(content)));
        using var serviceHost = CalendarServiceTestHost.Create(client, options => options.BaseUrl = "https://cal.example/");
        var sut = new CalendarOccurrenceMutationTools(serviceHost.Service);
        var parts = identityAndTag.Split('|');
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            "{\"snapshot\":{\"href\":\"" + href + "\",\"entityUid\":\"entity-1\",\"entityKind\":\"" + kind
            + "\",\"entityTag\":" + JsonSerializer.Serialize(parts[1]) + "}"
            + (parts[0].Length == 0 ? string.Empty : ",\"recurrenceIdentity\":{\"value\":" + parts[0] + "}") + "}");

        var result = tool switch
        {
            "complete" => await sut.CompleteTodoRawAsync(arguments, CancellationToken.None),
            "add" => await sut.AddRawAsync(arguments, CancellationToken.None),
            _ => await sut.ExcludeRawAsync(arguments, CancellationToken.None)
        };

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe(expectedCode);
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        structured.GetProperty("message").GetString().ShouldNotBeNull().ShouldNotContain("could not be completed");
        if (pointer is null)
        {
            structured.TryGetProperty("violations", out _).ShouldBeFalse();
        }
        else
        {
            var violation = structured.GetProperty("violations").EnumerateArray().ShouldHaveSingleItem();
            violation.GetProperty("pointer").GetString().ShouldBe(pointer);
            violation.GetProperty("code").GetString().ShouldBe(reasonCode);
        }
        await client.DidNotReceive().UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    public static TheoryData<string, string, string, string, string, string, string?> RejectedOccurrenceInputs()
    {
        const string Strong = "\"r1\"";
        const string Series = "BEGIN:$C\r\nUID:entity-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260817T100000Z\r\nRRULE:FREQ=DAILY;COUNT=3\r\nEXDATE:20260818T100000Z\r\nEND:$C\r\n";
        const string Single = "BEGIN:$C\r\nUID:entity-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260817T100000Z\r\nEND:$C\r\n";
        const string Day = "{\"kind\":\"utcDateTime\",\"value\":\"2026-08-17T10:00:00Z\"}";
        const string Excluded = "{\"kind\":\"utcDateTime\",\"value\":\"2026-08-18T10:00:00Z\"}";
        const string Missing = "{\"kind\":\"utcDateTime\",\"value\":\"2026-08-25T10:00:00Z\"}";
        return new TheoryData<string, string, string, string, string, string, string?>
        {
            { "complete", "todo", Series, "|" + Strong, "invalid_input", "recurrence_identity_required", "/recurrenceIdentity" },
            { "complete", "todo", Single, Day + "|" + Strong, "invalid_input", "recurrence_identity_not_applicable", "/recurrenceIdentity" },
            { "complete", "todo", Single.Replace("DTSTART", "STATUS:CANCELLED\r\nDTSTART", StringComparison.Ordinal), "|" + Strong,
                "invalid_input", "cancelled_not_completable", null },
            { "complete", "todo", Series, Excluded + "|" + Strong, "not_found", "occurrence_excluded", "/recurrenceIdentity" },
            { "complete", "todo", Series, "{\"kind\":\"date\",\"value\":\"2026-08-17\"}|" + Strong,
                "invalid_input", "recurrence_family_mismatch", "/recurrenceIdentity/value" },
            { "complete", "todo", Single, "|\"r0\"", "conflict", "revision_changed", "/snapshot/entityTag" },
            { "complete", "todo", Single, "|W/\"r1\"", "concurrency_unavailable", "weak_entity_tag", "/snapshot/entityTag" },
            { "add", "event", Series, Excluded + "|" + Strong, "invalid_input", "occurrence_excluded", "/recurrenceIdentity" },
            { "exclude", "event", Series, Missing + "|" + Strong, "not_found", "occurrence_not_found", "/recurrenceIdentity" }
        };
    }
}
