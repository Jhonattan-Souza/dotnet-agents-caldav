using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarEntityPatchToolsTests
{
    [Fact]
    public async Task PatchEventRawAsync_AppliesDirectScalarPatchThroughPublicToolContract()
    {
        var service = Substitute.For<ICalendarService>();
        service.PatchEventAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityPatchResult.Success(EventSnapshot()));
        var sut = new CalendarEntityPatchTools(service, TimeProvider.System);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """
            {"snapshot":{"href":"https://cal.example/events/event-1.ics","entityUid":"event-1","entityKind":"event","entityTag":"\"r1\""},"target":{"scope":"master"},"patch":{"scalars":[{"field":"summary","operation":"set","value":"Updated"}]}}
            """);

        var result = await sut.PatchEventRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("success");
        await service.Received(1).PatchEventAsync(
            Arg.Is<CalendarEventPatchRequest>(request =>
                request.Snapshot.EntityTag == "\"r1\""
                && request.Patch.Summary!.Operation == CalendarScalarPatchOperation.Set
                && request.Patch.Summary.Value == "Updated"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchTodoRawAsync_ParsesTypedTodoScalar()
    {
        var service = Substitute.For<ICalendarService>();
        service.PatchTodoAsync(Arg.Any<CalendarTodoPatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityPatchResult.Success(TodoSnapshot()));
        var sut = new CalendarEntityPatchTools(service, TimeProvider.System);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """
            {"snapshot":{"href":"https://cal.example/tasks/todo-1.ics","entityUid":"todo-1","entityKind":"todo","entityTag":"\"r1\""},"target":{"scope":"master"},"patch":{"scalars":[{"field":"percentComplete","operation":"set","value":50}]}}
            """);

        var result = await sut.PatchTodoRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        await service.Received(1).PatchTodoAsync(
            Arg.Is<CalendarTodoPatchRequest>(request => request.Patch.PercentComplete!.Value == 50),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PatchEventRawAsync_ParsesExactTypedAttendeeCollection()
    {
        var service = Substitute.For<ICalendarService>();
        service.PatchEventAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityPatchResult.Success(EventSnapshot()));
        var sut = new CalendarEntityPatchTools(service, TimeProvider.System);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """
            {"snapshot":{"href":"https://cal.example/events/event-1.ics","entityUid":"event-1","entityKind":"event","entityTag":"\"r1\""},"target":{"scope":"master"},"patch":{"collections":[{"field":"attendees","operation":"addRemove","add":[{"uri":"mailto:person@example.com","parameters":[]}] }]}}
            """);

        var result = await sut.PatchEventRawAsync(arguments, CancellationToken.None);

        result.IsError.ShouldBe(false);
        await service.Received(1).PatchEventAsync(
            Arg.Is<CalendarEventPatchRequest>(request => HasExpectedAttendee(request)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Parser_maps_every_frozen_structured_collection_to_its_exact_typed_field()
    {
        var text = JsonSerializer.SerializeToElement(new { value = "Value", parameters = Array.Empty<object>() });
        var named = JsonSerializer.SerializeToElement(new { uri = "https://example.test/value", parameters = Array.Empty<object>() });
        var uri = JsonSerializer.SerializeToElement(new { uri = "https://example.test/value", parameters = Array.Empty<object>() });
        var cases = new (string Field, JsonElement Item)[]
        {
            ("attendees", JsonSerializer.SerializeToElement(new { uri = "mailto:person@example.test", parameters = Array.Empty<object>() })),
            ("participants", JsonSerializer.SerializeToElement(new
            {
                uid = new { value = "participant-1", parameters = Array.Empty<object>() },
                participantType = new { value = "ACTIVE", parameters = Array.Empty<object>() }
            })),
            ("contacts", text),
            ("resources", text),
            ("relatedTo", JsonSerializer.SerializeToElement(new { value = "parent", parameters = Array.Empty<object>() })),
            ("requestStatuses", JsonSerializer.SerializeToElement(new
            {
                code = "2.0",
                description = "Success",
                parameters = Array.Empty<object>()
            })),
            ("alarms", JsonSerializer.SerializeToElement(new
            {
                action = new { value = "display", parameters = Array.Empty<object>() },
                trigger = new { value = "-PT5M", parameters = Array.Empty<object>() }
            })),
            ("attachments", named),
            ("comments", text),
            ("styledDescriptions", text),
            ("images", named),
            ("conferences", named),
            ("links", named),
            ("concepts", uri),
            ("structuredDataUris", uri),
            ("locationUris", named),
            ("resourceUris", named)
        };

        foreach (var item in cases)
        {
            var arguments = PatchArguments(new
            {
                collections = new[]
                {
                    new { field = item.Field, operation = "addRemove", add = new[] { item.Item } }
                }
            });

            CalendarEntityPatchArgumentParser.TryParseEvent(arguments, out var request).ShouldBeTrue(item.Field);
            var collection = request.Patch.Collections.ShouldNotBeNull().Single();
            collection.Field.ToString().ShouldBe(item.Field, StringCompareShould.IgnoreCase);
            collection.AddValues.ShouldNotBeNull().Count.ShouldBe(1);
        }
    }

    [Fact]
    public void Parser_maps_every_frozen_scalar_set_and_clear_variant()
    {
        var temporal = new { kind = "utcDateTime", value = "2026-08-21T10:00:00Z" };
        var eventSets = new (string Field, object Value)[]
        {
            ("summary", "Summary"),
            ("description", "Description"),
            ("start", temporal),
            ("end", temporal),
            ("duration", "PT1H"),
            ("location", "Room"),
            ("geo", new { latitude = -23.5, longitude = -46.6 }),
            ("status", "confirmed"),
            ("transparency", "opaque"),
            ("classification", "private"),
            ("priority", 5),
            ("url", "https://example.test/event"),
            ("organizer", new { uri = "mailto:owner@example.test", parameters = Array.Empty<object>() }),
            ("recurrenceSet", new { rrules = new[] { "FREQ=DAILY" } })
        };
        foreach (var item in eventSets)
        {
            CalendarEntityPatchArgumentParser.TryParseEvent(
                ScalarArguments("event", item.Field, "set", item.Value), out var request).ShouldBeTrue(item.Field);
            if (item.Field == "recurrenceSet")
                request.Patch.RecurrenceSetAddressed.ShouldBeTrue();
        }

        var todoSets = new (string Field, object Value)[]
        {
            ("summary", "Summary"),
            ("description", "Description"),
            ("start", temporal),
            ("due", temporal),
            ("duration", "PT1H"),
            ("status", "in-process"),
            ("priority", 5),
            ("percentComplete", 50),
            ("organizer", new { uri = "mailto:owner@example.test", parameters = Array.Empty<object>() }),
            ("recurrenceSet", new { rrules = new[] { "FREQ=DAILY" } })
        };
        foreach (var item in todoSets)
            CalendarEntityPatchArgumentParser.TryParseTodo(
                ScalarArguments("todo", item.Field, "set", item.Value), out _).ShouldBeTrue(item.Field);

        foreach (var field in eventSets.Select(item => item.Field))
        {
            CalendarEntityPatchArgumentParser.TryParseEvent(
                ScalarArguments("event", field, "clear"), out var request).ShouldBeTrue(field);
            if (field == "recurrenceSet")
                request.Patch.RecurrenceSetAddressed.ShouldBeTrue();
        }
    }

    [Theory]
    [InlineData("set")]
    [InlineData("clear")]
    public void Todo_patch_rejects_completed_scalar_reserved_for_todos_complete(string operation)
    {
        var arguments = ScalarArguments(
            "todo",
            "completed",
            operation,
            new { kind = "utcDateTime", value = "2026-08-21T10:00:00Z" });

        CalendarEntityPatchArgumentParser.TryParseTodo(arguments, out _).ShouldBeFalse();
    }

    [Fact]
    public void Parser_rejects_ambiguous_or_non_frozen_patch_shapes_atomically()
    {
        var invalidPatches = new[]
        {
            "{}",
            "{\"extra\":true}",
            "{\"scalars\":[]}",
            "{\"collections\":[]}",
            "{\"scalars\":[{\"field\":\"summary\",\"operation\":\"edit\",\"value\":\"x\"}]}",
            "{\"scalars\":[{\"field\":\"summary\",\"operation\":\"set\"}]}",
            "{\"scalars\":[{\"field\":\"summary\",\"operation\":\"clear\",\"value\":\"x\"}]}",
            "{\"scalars\":[{\"field\":\"unknown\",\"operation\":\"clear\"}]}",
            "{\"scalars\":[{\"field\":\"summary\",\"operation\":\"clear\"},{\"field\":\"summary\",\"operation\":\"clear\"}]}",
            "{\"collections\":[{\"field\":\"\",\"operation\":\"addRemove\",\"add\":[\"x\"]}]}",
            "{\"collections\":[{\"field\":\"1\",\"operation\":\"replaceAll\",\"values\":[]}]}",
            "{\"collections\":[{\"field\":\" attendees \",\"operation\":\"replaceAll\",\"values\":[]}]}",
            "{\"collections\":[{\"field\":\"Attendees\",\"operation\":\"replaceAll\",\"values\":[]}]}",
            "{\"collections\":[{\"field\":\"categories\",\"operation\":\"addRemove\"}]}",
            "{\"collections\":[{\"field\":\"categories\",\"operation\":\"addRemove\",\"add\":[]}]}",
            "{\"collections\":[{\"field\":\"categories\",\"operation\":\"replaceAll\",\"add\":[\"x\"]}]}",
            "{\"collections\":[{\"field\":\"categories\",\"operation\":\"addRemove\",\"add\":[1]}]}",
            "{\"collections\":[{\"field\":\"attendees\",\"operation\":\"addRemove\",\"add\":[{\"uri\":\"mailto:a@example.test\"}]}]}",
            "{\"collections\":[{\"field\":\"categories\",\"operation\":\"replaceAll\",\"values\":[]},{\"field\":\"categories\",\"operation\":\"replaceAll\",\"values\":[]}]}",
            "{\"scalars\":[{\"field\":\"summary\",\"operation\":\"clear\"}],\"collections\":[],\"extra\":true}"
        };

        foreach (var patch in invalidPatches)
        {
            var arguments = PatchArguments(JsonSerializer.Deserialize<JsonElement>(patch));
            CalendarEntityPatchArgumentParser.TryParseEvent(arguments, out _).ShouldBeFalse(patch);
        }
    }

    [Fact]
    public async Task Public_tool_maps_every_patch_failure_to_frozen_error_shape()
    {
        var cases = new (CalendarEntityPatchCode Code, string Category, CalendarEntityPatchPhase DomainPhase, string Phase)[]
        {
            (CalendarEntityPatchCode.InvalidInput, "input", CalendarEntityPatchPhase.CompleteResourceSemantics, "completeResourceSemantics"),
            (CalendarEntityPatchCode.InvalidCalendarData, "input", CalendarEntityPatchPhase.CompleteResourceSemantics, "completeResourceSemantics"),
            (CalendarEntityPatchCode.NotFound, "selection", CalendarEntityPatchPhase.SelectionDiscoveryCapability, "selectionDiscoveryCapability"),
            (CalendarEntityPatchCode.OutsideScope, "selection", CalendarEntityPatchPhase.OriginScopeAuthorization, "originScopeAuthorization"),
            (CalendarEntityPatchCode.EntityKindMismatch, "selection", CalendarEntityPatchPhase.CompleteResourceSemantics, "completeResourceSemantics"),
            (CalendarEntityPatchCode.OpaqueResource, "capabilityAndProjection", CalendarEntityPatchPhase.CompleteResourceSemantics, "completeResourceSemantics"),
            (CalendarEntityPatchCode.Conflict, "state", CalendarEntityPatchPhase.TargetRevision, "targetRevision"),
            (CalendarEntityPatchCode.ConcurrencyUnavailable, "state", CalendarEntityPatchPhase.TargetRevision, "targetRevision"),
            (CalendarEntityPatchCode.UnsupportedCapability, "capabilityAndProjection", CalendarEntityPatchPhase.SelectionDiscoveryCapability, "selectionDiscoveryCapability"),
            (CalendarEntityPatchCode.PayloadTooLarge, "limitsAndAdmission", CalendarEntityPatchPhase.AdmissionAndPayload, "admissionAndPayload"),
            (CalendarEntityPatchCode.LimitExhausted, "limitsAndAdmission", CalendarEntityPatchPhase.Execution, "execution"),
            (CalendarEntityPatchCode.UpstreamUnauthorized, "upstream", CalendarEntityPatchPhase.Execution, "execution"),
            (CalendarEntityPatchCode.UpstreamForbidden, "upstream", CalendarEntityPatchPhase.Execution, "execution"),
            (CalendarEntityPatchCode.UpstreamRateLimited, "upstream", CalendarEntityPatchPhase.Execution, "execution"),
            (CalendarEntityPatchCode.UpstreamUnavailable, "upstream", CalendarEntityPatchPhase.Execution, "execution"),
            (CalendarEntityPatchCode.UpstreamProtocolError, "upstream", CalendarEntityPatchPhase.SelectionDiscoveryCapability, "selectionDiscoveryCapability"),
            (CalendarEntityPatchCode.FidelityFailure, "postWriteTruth", CalendarEntityPatchPhase.PostWriteVerificationOrReconciliation, "postWriteVerificationOrReconciliation"),
            (CalendarEntityPatchCode.CommittedButUnverified, "postWriteTruth", CalendarEntityPatchPhase.PostWriteVerificationOrReconciliation, "postWriteVerificationOrReconciliation"),
            (CalendarEntityPatchCode.CommittedButConcurrencyUnavailable, "postWriteTruth", CalendarEntityPatchPhase.PostWriteVerificationOrReconciliation, "postWriteVerificationOrReconciliation"),
            (CalendarEntityPatchCode.Indeterminate, "postWriteTruth", CalendarEntityPatchPhase.PostWriteVerificationOrReconciliation, "postWriteVerificationOrReconciliation")
        };

        foreach (var item in cases)
        {
            var service = Substitute.For<ICalendarService>();
            service.PatchEventAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>()).Returns(
                new CalendarEntityPatchResult(
                    item.Code,
                    CalendarMutationState.Unknown,
                    Retryable: true,
                    Phase: item.DomainPhase));
            var result = await new CalendarEntityPatchTools(service, TimeProvider.System).PatchEventRawAsync(
                DirectArguments(), CancellationToken.None);
            var structured = result.StructuredContent!.Value;
            result.IsError.ShouldBe(true, item.Code.ToString());
            structured.GetProperty("category").GetString().ShouldBe(item.Category, item.Code.ToString());
            structured.GetProperty("phase").GetString().ShouldBe(item.Phase, item.Code.ToString());
            structured.GetProperty("mutationState").GetString().ShouldBe("unknown", item.Code.ToString());
        }
    }

    [Fact]
    public async Task Public_tool_reports_elapsed_time_limit_truthfully()
    {
        var service = Substitute.For<ICalendarService>();
        service.PatchEventAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>()).Returns(
            new CalendarEntityPatchResult(
                CalendarEntityPatchCode.LimitExhausted,
                CalendarMutationState.NotAttempted,
                LimitDimension: CalendarEntityPatchLimitDimension.ElapsedTime));

        var result = await new CalendarEntityPatchTools(service, TimeProvider.System).PatchEventRawAsync(
            DirectArguments(), CancellationToken.None);

        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("limit_exhausted");
        structured.GetProperty("category").GetString().ShouldBe("limitsAndAdmission");
        structured.GetProperty("message").GetString().ShouldBe(
            "The Calendar Entity patch exceeded its execution time limit.");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        structured.GetProperty("limits").GetProperty("dimension").GetString().ShouldBe("elapsed_time");
    }

    [Theory]
    [InlineData(CalendarEntityPatchCode.RemovalNotFound, "not_found", "No requested collection occurrence matched.")]
    [InlineData(CalendarEntityPatchCode.RemovalAmbiguous, "ambiguous", "The requested collection removal was ambiguous.")]
    public async Task Public_tool_maps_typed_removal_failures_without_echoing_values(
        CalendarEntityPatchCode code,
        string expectedWireCode,
        string expectedMessage)
    {
        var service = Substitute.For<ICalendarService>();
        service.PatchEventAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>()).Returns(
            new CalendarEntityPatchResult(
                code,
                CalendarMutationState.NotAttempted,
                Phase: CalendarEntityPatchPhase.CompleteResourceSemantics));

        var result = await new CalendarEntityPatchTools(service, TimeProvider.System).PatchEventRawAsync(
            DirectArguments(), CancellationToken.None);

        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe(expectedWireCode);
        structured.GetProperty("phase").GetString().ShouldBe("completeResourceSemantics");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        var message = structured.GetProperty("message").GetString();
        message.ShouldBe(expectedMessage);
        message.ShouldNotBeNull().ShouldNotContain("mailto:");
    }

    [Fact]
    public async Task Public_tool_distinguishes_invalid_input_no_change_and_bounded_output()
    {
        var service = Substitute.For<ICalendarService>();
        var sut = new CalendarEntityPatchTools(service, TimeProvider.System);

        var invalid = await sut.PatchEventRawAsync(new Dictionary<string, JsonElement>(), CancellationToken.None);
        invalid.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");

        service.PatchEventAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>()).Returns(
            new CalendarEntityPatchResult(CalendarEntityPatchCode.NoChange, CalendarMutationState.NotAttempted));
        var noChange = await sut.PatchEventRawAsync(DirectArguments(), CancellationToken.None);
        noChange.IsError.ShouldBe(false);
        noChange.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("no_change");

        var huge = EventSnapshot() with
        {
            AuthoritativeUtf8 = new byte[CalendarQueryToolSupport.MaximumStructuredResultBytes + 1]
        };
        service.PatchEventAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>()).Returns(
            CalendarEntityPatchResult.Success(huge));
        var bounded = await sut.PatchEventRawAsync(DirectArguments(), CancellationToken.None);
        bounded.IsError.ShouldBe(true);
        bounded.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        bounded.StructuredContent!.Value.GetProperty("mutationState").GetString().ShouldBe("committed");
    }

    [Fact]
    public async Task Todo_replaceAll_dry_reviews_before_reporting_unsupported_mrtr()
    {
        var service = Substitute.For<ICalendarService>();
        service.ReviewTodoPatchAsync(Arg.Any<CalendarTodoPatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarEntityPatchReviewResult(
                null,
                Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()));
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, time);
        var arguments = ReplaceAllArguments();
        arguments["snapshot"] = JsonSerializer.SerializeToElement(new
        {
            href = "https://cal.example/tasks/todo-1.ics",
            entityUid = "todo-1",
            entityKind = "todo",
            entityTag = "\"r1\""
        });

        var result = await sut.PatchTodoRawAsync(arguments, null, null, false, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("unsupported_capability");
        result.StructuredContent.Value.GetProperty("phase").GetString().ShouldBe("mrtr");
        await service.Received(1).ReviewTodoPatchAsync(
            Arg.Any<CalendarTodoPatchRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().PatchTodoAsync(
            Arg.Any<CalendarTodoPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarEntityPatchCode.OutsideScope, "outside_scope", CalendarEntityPatchPhase.OriginScopeAuthorization)]
    [InlineData(CalendarEntityPatchCode.Conflict, "conflict", CalendarEntityPatchPhase.TargetRevision)]
    [InlineData(CalendarEntityPatchCode.InvalidInput, "invalid_input", CalendarEntityPatchPhase.CompleteResourceSemantics)]
    public async Task ReplaceAll_dry_review_failure_precedes_unsupported_mrtr(
        CalendarEntityPatchCode code,
        string expectedCode,
        CalendarEntityPatchPhase phase)
    {
        var service = Substitute.For<ICalendarService>();
        service.ReviewEventPatchAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarEntityPatchReviewResult(new CalendarEntityPatchResult(
                code,
                CalendarMutationState.NotAttempted,
                Phase: phase)));
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));

        var result = await CreateTool(service, time).PatchEventRawAsync(
            ReplaceAllArguments(), null, null, false, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
        await service.Received(1).ReviewEventPatchAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().PatchEventAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarEntityPatchCode.Conflict, "conflict", CalendarEntityPatchPhase.TargetRevision)]
    [InlineData(CalendarEntityPatchCode.InvalidInput, "invalid_input", CalendarEntityPatchPhase.CompleteResourceSemantics)]
    public async Task ReplaceAll_continuation_dry_review_failure_precedes_unavailable_mrtr(
        CalendarEntityPatchCode code,
        string expectedCode,
        CalendarEntityPatchPhase phase)
    {
        var digest = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var service = Substitute.For<ICalendarService>();
        service.ReviewEventPatchAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>()).Returns(
            new CalendarEntityPatchReviewResult(null, digest),
            new CalendarEntityPatchReviewResult(new CalendarEntityPatchResult(
                code,
                CalendarMutationState.NotAttempted,
                Phase: phase)));
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, time);
        var first = await BeginAsync(sut);

        var result = await sut.PatchEventRawAsync(
            ReplaceAllArguments(),
            first.Result.RequestState,
            Confirmation("accept", true),
            false,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
        await service.Received(2).ReviewEventPatchAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().PatchEventAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Still_valid_replaceAll_continuation_reports_unavailable_mrtr_after_dry_review()
    {
        var service = ReviewedService();
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, time);
        var first = await BeginAsync(sut);

        var result = await sut.PatchEventRawAsync(
            ReplaceAllArguments(),
            first.Result.RequestState,
            Confirmation("accept", true),
            false,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("unsupported_capability");
        await service.Received(2).ReviewEventPatchAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().PatchEventAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarEntityPatchCode.InvalidInput, "invalid_input")]
    [InlineData(CalendarEntityPatchCode.NotFound, "not_found")]
    [InlineData(CalendarEntityPatchCode.OutsideScope, "outside_scope")]
    [InlineData(CalendarEntityPatchCode.ConcurrencyUnavailable, "concurrency_unavailable")]
    [InlineData(CalendarEntityPatchCode.PayloadTooLarge, "payload_too_large")]
    [InlineData(CalendarEntityPatchCode.UpstreamProtocolError, "upstream_protocol_error")]
    public async Task ReplaceAll_review_maps_read_failure_without_mutation(
        CalendarEntityPatchCode reviewCode,
        string expectedCode)
    {
        var service = Substitute.For<ICalendarService>();
        service.ReviewEventPatchAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarEntityPatchReviewResult(new CalendarEntityPatchResult(
                reviewCode,
                CalendarMutationState.NotAttempted)));
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));

        var result = await CreateTool(service, time).PatchEventRawAsync(
            ReplaceAllArguments(), null, null, true, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
        await service.DidNotReceive().PatchEventAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplaceAll_preview_deadline_returns_typed_limit_without_mutation()
    {
        var service = Substitute.For<ICalendarService>();
        var pendingReview = new TaskCompletionSource<CalendarEntityPatchReviewResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.ReviewEventPatchAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(pendingReview.Task);
        service.When(client => client.ReviewEventPatchAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>())).Do(call =>
            call.Arg<CancellationToken>().Register(() =>
                pendingReview.TrySetCanceled(call.Arg<CancellationToken>())));
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var pending = CreateTool(service, time).PatchEventRawAsync(
            ReplaceAllArguments(), null, null, true, CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        pending.IsCompleted.ShouldBeFalse();
        time.Advance(TimeSpan.FromTicks(1));
        var result = await pending;

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("limit_exhausted");
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        result.StructuredContent.Value.GetProperty("limits").GetProperty("dimension").GetString()
            .ShouldBe("elapsed_time");
        await service.DidNotReceive().PatchEventAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Accepted_continuation_sanitizes_unexpected_mutation_failure_as_indeterminate()
    {
        var service = ReviewedService();
        service.PatchEventAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromException<CalendarEntityPatchResult>(new IOException("secret upstream body")));
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, time);
        var first = await BeginAsync(sut);

        var result = await sut.PatchEventRawAsync(
            ReplaceAllArguments(), first.Result.RequestState, Confirmation("accept", true), true, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("indeterminate");
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("unknown");
        JsonSerializer.Serialize(result).ShouldNotContain("secret");
    }

    [Fact]
    public async Task Admission_timeout_returns_busy_before_parsing_or_service_access()
    {
        var service = Substitute.For<ICalendarService>();
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var admission = new CalendarMutationAdmission(time);
        using var active = (await admission.AcquireAsync(CancellationToken.None))!;
        var sut = CreateTool(service, time, admission);
        var pending = sut.PatchEventRawAsync(new Dictionary<string, JsonElement>(), CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(2));
        var result = await pending;

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("busy");
        result.StructuredContent.Value.GetProperty("retryAfterMs").GetInt32().ShouldBe(2_000);
        await service.DidNotReceive().ReviewEventPatchAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Malformed_replaceAll_continuations_are_mismatches_with_zero_write()
    {
        var service = ReviewedService();
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, time);
        var first = await BeginAsync(sut);
        var validState = first.Result.RequestState!;
        var cases = new (string? State, IDictionary<string, InputResponse>? Responses)[]
        {
            (null, Confirmation("accept", true)),
            (validState, null),
            (validState, new Dictionary<string, InputResponse>()),
            (validState, new Dictionary<string, InputResponse>
            {
                ["wrong_key"] = Confirmation("accept", true).Single().Value
            }),
            (validState, new Dictionary<string, InputResponse>
            {
                ["confirm_replace_all"] = Confirmation("accept", true).Single().Value,
                ["extra"] = Confirmation("accept", true).Single().Value
            }),
            (validState, new Dictionary<string, InputResponse>
            {
                ["confirm_replace_all"] = InputResponse.FromElicitResult(new ElicitResult { Action = "accept" })
            }),
            (validState, new Dictionary<string, InputResponse>
            {
                ["confirm_replace_all"] = InputResponse.FromElicitResult(new ElicitResult
                {
                    Action = "accept",
                    Content = new Dictionary<string, JsonElement>
                    {
                        ["confirm"] = JsonSerializer.SerializeToElement("yes")
                    }
                })
            }),
            (validState, new Dictionary<string, InputResponse>
            {
                ["confirm_replace_all"] = InputResponse.FromElicitResult(new ElicitResult { Action = "unknown" })
            }),
            (new string('A', CalendarMutationRequestStateProtector.MaximumRequestStateCharacters + 1),
                Confirmation("accept", true))
        };

        foreach (var item in cases)
        {
            var result = await sut.PatchEventRawAsync(
                ReplaceAllArguments(), item.State, item.Responses, true, CancellationToken.None);
            result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_mismatch");
        }
        await service.Received(1).ReviewEventPatchAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().PatchEventAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarMutationState.NotAttempted, "not_attempted")]
    [InlineData(CalendarMutationState.NotCommitted, "not_committed")]
    [InlineData(CalendarMutationState.Committed, "committed")]
    public async Task Public_error_output_preserves_non_success_mutation_state(
        CalendarMutationState state,
        string expected)
    {
        var service = Substitute.For<ICalendarService>();
        service.PatchEventAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>()).Returns(
            new CalendarEntityPatchResult(CalendarEntityPatchCode.UpstreamUnavailable, state));

        var result = await new CalendarEntityPatchTools(service, TimeProvider.System).PatchEventRawAsync(
            DirectArguments(), CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("mutationState").GetString().ShouldBe(expected);
    }

    [Fact]
    public async Task Preview_cancellation_and_unexpected_failure_are_sanitized_but_caller_cancellation_propagates()
    {
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        foreach (var failure in new Exception[] { new OperationCanceledException(), new InvalidOperationException("secret") })
        {
            var service = Substitute.For<ICalendarService>();
            service.ReviewEventPatchAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>()).Returns(
                Task.FromException<CalendarEntityPatchReviewResult>(failure));
            var result = await CreateTool(service, time).PatchEventRawAsync(
                ReplaceAllArguments(), null, null, true, CancellationToken.None);
            result.IsError.ShouldBe(true);
            JsonSerializer.Serialize(result).ShouldNotContain("secret");
        }

        var canceledService = Substitute.For<ICalendarService>();
        canceledService.ReviewEventPatchAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>()).Returns(
            Task.FromException<CalendarEntityPatchReviewResult>(new OperationCanceledException()));
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => CreateTool(canceledService, time).PatchEventRawAsync(
            ReplaceAllArguments(), null, null, true, caller.Token));
    }

    [Fact]
    public async Task PatchEventRawAsync_ReplaceAllRequiresReviewAndMrtrBeforeMutation()
    {
        var service = ReviewedService();
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, time);
        var arguments = ReplaceAllArguments();

        var exception = await Should.ThrowAsync<InputRequiredException>(() => sut.PatchEventRawAsync(
            arguments,
            null,
            null,
            true,
            CancellationToken.None));

        exception.Result.RequestState.ShouldNotBeNullOrWhiteSpace();
        exception.Result.RequestState!.Length.ShouldBeLessThanOrEqualTo(
            CalendarMutationRequestStateProtector.MaximumRequestStateCharacters);
        exception.Result.RequestState.ShouldNotContain("Work");
        var elicitation = exception.Result.InputRequests.ShouldNotBeNull()["confirm_replace_all"]
            .ElicitationParams.ShouldNotBeNull();
        elicitation.Message.ShouldBe(
            "Confirm events.patch replaceAll for href https://cal.example/events/event-1.ics, UID event-1, kind event, scope master, expected ETag \"r1\". Destructive fields and replacement counts: attendees=0, categories=1.");
        elicitation.Message.ShouldNotContain("Work");
        elicitation.Message.ShouldNotContain("private@example.test");
        await service.DidNotReceive().PatchEventAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarQueryToolSupport.MaximumHumanReadableBytes, true)]
    [InlineData(CalendarQueryToolSupport.MaximumHumanReadableBytes + 1, false)]
    public async Task ReplaceAll_enforces_exact_human_preview_boundary_before_review(
        int previewBytes,
        bool expectsReview)
    {
        const string prefix = "Confirm events.patch replaceAll for href https://cal.example/events/event-1.ics, UID ";
        const string suffix = ", kind event, scope master, expected ETag \"r1\". Destructive fields and replacement counts: attendees=0, categories=1.";
        var fixedBytes = CalendarEntityPatchTools.GetConfirmationPreviewByteCount(prefix + suffix);
        var uid = new string('u', previewBytes - fixedBytes);
        var service = ReviewedService(uid);
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, time);
        var arguments = ReplaceAllArguments(uid);

        if (expectsReview)
        {
            var exception = await Should.ThrowAsync<InputRequiredException>(() => sut.PatchEventRawAsync(
                arguments, null, null, true, CancellationToken.None));
            var message = exception.Result.InputRequests.ShouldNotBeNull()["confirm_replace_all"]
                .ElicitationParams.ShouldNotBeNull().Message;
            CalendarEntityPatchTools.GetConfirmationPreviewByteCount(message).ShouldBe(previewBytes);
        }
        else
        {
            var result = await sut.PatchEventRawAsync(arguments, null, null, true, CancellationToken.None);
            result.IsError.ShouldBe(true);
            result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
            JsonSerializer.Serialize(result).ShouldNotContain(uid);
        }

        await service.Received(expectsReview ? 1 : 0).ReviewEventPatchAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().PatchEventAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Accepted_replaceAll_continuation_refetches_and_mutates_once()
    {
        var service = ReviewedService();
        service.PatchEventAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityPatchResult.Success(EventSnapshot()));
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, time);
        var first = await BeginAsync(sut);

        var result = await sut.PatchEventRawAsync(
            ReplaceAllArguments(), first.Result.RequestState, Confirmation("accept", true), true, CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("success");
        await service.Received(2).ReviewEventPatchAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
        await service.Received(1).PatchEventAsync(
            Arg.Is<CalendarEventPatchRequest>((CalendarEventPatchRequest request) =>
                request.Patch.Categories!.Values!.Count == 1
                && request.Patch.Collections!.Single().ReplacementValues!.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplaceAll_confirmation_remains_bound_when_engine_clock_advances()
    {
        const string href = "https://cal.example/events/event-1.ics";
        const string original = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//fixture//EN\r\nBEGIN:VEVENT\r\nUID:event-1\r\nDTSTAMP:20260816T100000Z\r\nDTSTART:20260820T100000Z\r\nSUMMARY:Before\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var client = Substitute.For<ICalendarClient>();
        var written = ReadOnlyMemory<byte>.Empty;
        client.GetCalendarsAsync(Arg.Any<CancellationToken>()).Returns([
            new CalendarDescriptor
            {
                Href = "https://cal.example/events/",
                DisplayName = "Events",
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = EntityKindSupport.Advertised,
                TodoSupport = EntityKindSupport.NotAdvertised
            }
        ]);
        client.GetCalendarResourceAsync(href, Arg.Any<CancellationToken>()).Returns(_ =>
            written.IsEmpty
                ? CalendarResourceRead.Success(href, "\"r1\"", Encoding.UTF8.GetBytes(original))
                : CalendarResourceRead.Success(href, "\"r2\"", written));
        client.UpdateCalendarResourceAsync(
                Arg.Do<CalendarResourceUpdateRequest>(request => written = request.AuthoritativeUtf8),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceUpdateDispatchResult(CalendarResourceUpdateDispatchCode.Dispatched));
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var service = new CalendarService(
            client,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example/",
                Username = "user",
                Password = "secret"
            }),
            Substitute.For<ILogger<CalendarService>>(),
            time,
            Substitute.For<ICalendarEntityIdentityGenerator>());
        var sut = CreateTool(service, time);
        var first = await BeginAsync(sut);

        time.Advance(TimeSpan.FromSeconds(1));
        var result = await sut.PatchEventRawAsync(
            ReplaceAllArguments(),
            first.Result.RequestState,
            Confirmation("accept", true),
            true,
            CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("success");
        written.IsEmpty.ShouldBeFalse();
        await client.Received(1).UpdateCalendarResourceAsync(
            Arg.Any<CalendarResourceUpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("decline", null)]
    [InlineData("cancel", null)]
    [InlineData("accept", false)]
    public async Task Negative_replaceAll_confirmation_is_truthful_non_mutation(string action, bool? confirmation)
    {
        var service = ReviewedService();
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, time);
        var first = await BeginAsync(sut);

        var result = await sut.PatchEventRawAsync(
            ReplaceAllArguments(), first.Result.RequestState, Confirmation(action, confirmation), true, CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("confirmation_declined");
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        await service.DidNotReceive().PatchEventAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplaceAll_state_binds_arguments_operation_credentials_and_expiry()
    {
        var service = ReviewedService();
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, time);
        var first = await BeginAsync(sut);
        var changed = ReplaceAllArguments();
        changed["patch"] = JsonSerializer.SerializeToElement(new
        {
            collections = new object[]
            {
                new { field = "categories", operation = "replaceAll", values = new[] { "Different" } },
                new { field = "attendees", operation = "replaceAll", values = Array.Empty<object>() }
            }
        });

        var mismatch = await sut.PatchEventRawAsync(
            changed, first.Result.RequestState, Confirmation("accept", true), true, CancellationToken.None);
        mismatch.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_mismatch");

        time.Advance(TimeSpan.FromMinutes(10));
        var expired = await sut.PatchEventRawAsync(
            ReplaceAllArguments(), first.Result.RequestState, Confirmation("accept", true), true, CancellationToken.None);
        expired.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_expired");
        await service.Received(1).ReviewEventPatchAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().PatchEventAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarEntityPatchCode.InvalidInput, "invalid_input", true)]
    [InlineData(CalendarEntityPatchCode.NoChange, "no_change", false)]
    public async Task ReplaceAll_first_round_returns_complete_dry_run_outcome_without_elicitation_or_write(
        CalendarEntityPatchCode code,
        string expectedOutcome,
        bool isError)
    {
        var service = Substitute.For<ICalendarService>();
        service.ReviewEventPatchAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarEntityPatchReviewResult(new CalendarEntityPatchResult(
                code,
                CalendarMutationState.NotAttempted)));
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));

        var result = await CreateTool(service, time).PatchEventRawAsync(
            ReplaceAllArguments(), null, null, true, CancellationToken.None);

        result.IsError.ShouldBe(isError);
        result.StructuredContent!.Value.GetProperty(isError ? "code" : "outcome")
            .GetString().ShouldBe(expectedOutcome);
        await service.Received(1).ReviewEventPatchAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().PatchEventAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplaceAll_continuation_accepts_recursively_normalized_argument_property_order()
    {
        var service = ReviewedService();
        service.PatchEventAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(CalendarEntityPatchResult.Success(EventSnapshot()));
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, time);
        var first = await BeginAsync(sut);
        var reordered = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """
            {"patch":{"collections":[{"values":["Work"],"operation":"replaceAll","field":"categories"},{"values":[],"field":"attendees","operation":"replaceAll"}]},"target":{"scope":"master"},"snapshot":{"entityTag":"\"r1\"","entityKind":"event","entityUid":"event-1","href":"https://cal.example/events/event-1.ics"}}
            """)!;

        var result = await sut.PatchEventRawAsync(
            reordered, first.Result.RequestState, Confirmation("accept", true), true, CancellationToken.None);

        result.IsError.ShouldBe(false);
        await service.Received(2).ReviewEventPatchAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
        await service.Received(1).PatchEventAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReplaceAll_continuation_rejects_changed_dry_run_intent_before_write()
    {
        var service = Substitute.For<ICalendarService>();
        service.ReviewEventPatchAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>()).Returns(
            new CalendarEntityPatchReviewResult(null, new byte[32]),
            new CalendarEntityPatchReviewResult(null, Enumerable.Repeat((byte)1, 32).ToArray()));
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, time);
        var first = await BeginAsync(sut);

        var result = await sut.PatchEventRawAsync(
            ReplaceAllArguments(), first.Result.RequestState, Confirmation("accept", true), true, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_mismatch");
        await service.Received(2).ReviewEventPatchAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().PatchEventAsync(
            Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>());
    }

    private static CalendarResourceSnapshot EventSnapshot(string uid = "event-1") => new(
        "https://cal.example/events/",
        "https://cal.example/events/event-1.ics",
        "\"r2\"",
        Encoding.UTF8.GetBytes($"BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:{uid}\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"),
        [],
        new CalendarResourceProjection(CalendarResourceProjectionKind.Event, uid, "Updated"),
        []);

    private static CalendarEntityPatchTools CreateTool(
        ICalendarService service,
        TimeProvider time,
        CalendarMutationAdmission? admission = null)
    {
        var protector = new CalendarMutationRequestStateProtector(
            time,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example/",
                Username = "user",
                Password = "secret"
            }),
            Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        return new(service, time, protector, admission ?? new CalendarMutationAdmission(time));
    }

    private static ICalendarService ReviewedService(string uid = "event-1")
    {
        var service = Substitute.For<ICalendarService>();
        service.ReviewEventPatchAsync(Arg.Any<CalendarEventPatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarEntityPatchReviewResult(null, Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()));
        return service;
    }

    private static Task<InputRequiredException> BeginAsync(CalendarEntityPatchTools sut) =>
        Should.ThrowAsync<InputRequiredException>(() => sut.PatchEventRawAsync(
            ReplaceAllArguments(), null, null, true, CancellationToken.None));

    private static Dictionary<string, InputResponse> Confirmation(string action, bool? confirmed) => new()
    {
        ["confirm_replace_all"] = InputResponse.FromElicitResult(new ElicitResult
        {
            Action = action,
            Content = confirmed is null
                ? null
                : new Dictionary<string, JsonElement>
                {
                    ["confirm"] = JsonSerializer.SerializeToElement(confirmed.Value)
                }
        })
    };

    private static Dictionary<string, JsonElement> ReplaceAllArguments(string uid = "event-1") => new()
    {
        ["snapshot"] = JsonSerializer.SerializeToElement(new
        {
            href = "https://cal.example/events/event-1.ics",
            entityUid = uid,
            entityKind = "event",
            entityTag = "\"r1\""
        }),
        ["target"] = JsonSerializer.SerializeToElement(new { scope = "master" }),
        ["patch"] = JsonSerializer.SerializeToElement(new
        {
            collections = new object[]
            {
                new { field = "categories", operation = "replaceAll", values = new[] { "Work" } },
                new { field = "attendees", operation = "replaceAll", values = Array.Empty<object>() }
            }
        })
    };

    private static Dictionary<string, JsonElement> DirectArguments() => PatchArguments(new
    {
        scalars = new[] { new { field = "summary", operation = "set", value = "Updated" } }
    });

    private static Dictionary<string, JsonElement> PatchArguments<T>(T patch) => new()
    {
        ["snapshot"] = JsonSerializer.SerializeToElement(new
        {
            href = "https://cal.example/events/event-1.ics",
            entityUid = "event-1",
            entityKind = "event",
            entityTag = "\"r1\""
        }),
        ["target"] = JsonSerializer.SerializeToElement(new { scope = "master" }),
        ["patch"] = JsonSerializer.SerializeToElement(patch)
    };

    private static Dictionary<string, JsonElement> ScalarArguments(
        string kind,
        string field,
        string operation,
        object? value = null)
    {
        var scalar = new Dictionary<string, object?>
        {
            ["field"] = field,
            ["operation"] = operation
        };
        if (operation == "set")
            scalar["value"] = value;
        var arguments = PatchArguments(new { scalars = new[] { scalar } });
        arguments["snapshot"] = JsonSerializer.SerializeToElement(new
        {
            href = $"https://cal.example/{kind}s/entity-1.ics",
            entityUid = "entity-1",
            entityKind = kind,
            entityTag = "\"r1\""
        });
        return arguments;
    }

    private static bool HasExpectedAttendee(CalendarEventPatchRequest request)
    {
        var collection = request.Patch.Collections?.SingleOrDefault();
        var attendee = collection?.AddValues?.SingleOrDefault() as CalendarAttendee;
        return collection?.Field == CalendarCollectionField.Attendees
            && attendee?.Uri == "mailto:person@example.com";
    }

    private static CalendarResourceSnapshot TodoSnapshot() => new(
        "https://cal.example/tasks/",
        "https://cal.example/tasks/todo-1.ics",
        "\"r2\"",
        "BEGIN:VCALENDAR\r\nEND:VCALENDAR\r\n"u8.ToArray(),
        [],
        new CalendarResourceProjection(CalendarResourceProjectionKind.Todo, "todo-1", "Updated"),
        []);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            _now += duration;
            foreach (var timer in _timers.ToArray())
                timer.FireIfDue();
        }

        private sealed class ManualTimer(
            MutableTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset? _dueAt = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : owner.GetUtcNow() + dueTime;

            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
            {
                dueTime = newDueTime;
                period = newPeriod;
                _dueAt = newDueTime == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + newDueTime;
                return true;
            }

            public void Dispose() => _dueAt = null;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void FireIfDue()
            {
                if (_dueAt is null || owner.GetUtcNow() < _dueAt)
                    return;
                callback(state);
                _dueAt = period == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + period;
            }
        }
    }
}
