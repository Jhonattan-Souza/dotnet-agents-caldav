using System.Text;
using System.Text.Json;
using System.Xml;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarResourceDeleteToolsTests
{
    [Fact]
    public async Task DeleteRawAsync_FirstRoundReturnsOpaqueRevisionBoundConfirmationWithoutDeleting()
    {
        var service = Substitute.For<ICalendarService>();
        var snapshot = TodoSnapshot();
        service.GetResourceAsync(snapshot.ResourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(snapshot.ResourceHref, snapshot.EntityTag, snapshot.AuthoritativeUtf8) with
            {
                Snapshot = snapshot
            });
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var protector = new CalendarMutationRequestStateProtector(
            timeProvider,
            Options.Create(CreateOptions()),
            Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        var sut = new CalendarResourceDeleteTools(
            service,
            protector,
            timeProvider,
            new CalendarMutationAdmission(timeProvider));

        var exception = await Should.ThrowAsync<InputRequiredException>(() => sut.DeleteRawAsync(
            ValidArguments(),
            requestState: null,
            inputResponses: null,
            mrtrSupported: true,
            CancellationToken.None));

        exception.Result.RequestState.ShouldNotBeNullOrWhiteSpace();
        exception.Result.RequestState!.Length.ShouldBeLessThanOrEqualTo(
            CalendarMutationRequestStateProtector.MaximumRequestStateCharacters);
        exception.Result.RequestState.ShouldNotContain(snapshot.ResourceHref);
        exception.Result.RequestState.ShouldNotContain(snapshot.Projection.EntityUid!);
        exception.Result.RequestState.ShouldNotContain(snapshot.EntityTag);
        var request = exception.Result.InputRequests.ShouldNotBeNull()["confirm_delete"];
        var elicitation = request.ElicitationParams.ShouldNotBeNull();
        elicitation.Message.ShouldBe(
            "Confirm calendar_resources.delete for href https://cal.example/tasks/a.ics, UID todo-1, kind todo, and expected ETag \"r1\".");
        elicitation.Message.ShouldNotContain("Private");
        elicitation.Message.ShouldNotContain("user");
        elicitation.Message.ShouldNotContain("secret");
        elicitation.Message.ShouldNotContain(exception.Result.RequestState);
        var schema = elicitation.RequestedSchema.ShouldNotBeNull();
        schema.Properties["confirm"].ShouldBeOfType<ElicitRequestParams.BooleanSchema>().Default.ShouldBe(false);
        await service.Received(1).GetResourceAsync(snapshot.ResourceHref, Arg.Any<CancellationToken>());
        await service.DidNotReceive().DeleteResourceAsync(
            Arg.Any<CalendarResourceRevisionReference>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarQueryToolSupport.MaximumHumanReadableBytes, true)]
    [InlineData(CalendarQueryToolSupport.MaximumHumanReadableBytes + 1, false)]
    public async Task DeleteRawAsync_EnforcesExactHumanPreviewBoundaryWithoutTruncatingIdentity(
        int previewBytes,
        bool expectsConfirmation)
    {
        const string href = "https://cal.example/tasks/a.ics";
        const string prefix = "Confirm calendar_resources.delete for href https://cal.example/tasks/a.ics, UID ";
        const string suffix = ", kind todo, and expected ETag \"r1\".";
        const string title = "Confirm deletion";
        const string description = "Delete exactly the reviewed resource revision.";
        var fixedPreviewBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Message = prefix + suffix,
            Title = title,
            Description = description
        }).Length;
        var uid = new string('u', previewBytes - fixedPreviewBytes);
        var expectedMessage = prefix + uid + suffix;
        var snapshot = TodoSnapshot() with
        {
            Projection = new CalendarResourceProjection(CalendarResourceProjectionKind.Todo, uid, "Private")
        };
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(href, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(href, snapshot.EntityTag, snapshot.AuthoritativeUtf8) with
            {
                Snapshot = snapshot
            });
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);
        var arguments = ArgumentsWithIdentity(href, uid);

        if (expectsConfirmation)
        {
            var exception = await Should.ThrowAsync<InputRequiredException>(() => sut.DeleteRawAsync(
                arguments,
                requestState: null,
                inputResponses: null,
                mrtrSupported: true,
                CancellationToken.None));

            var message = exception.Result.InputRequests.ShouldNotBeNull()["confirm_delete"]
                .ElicitationParams.ShouldNotBeNull().Message;
            exception.Result.RequestState.ShouldNotBeNull().Length.ShouldBeLessThanOrEqualTo(
                CalendarMutationRequestStateProtector.MaximumRequestStateCharacters);
            message.ShouldBe(expectedMessage);
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                Message = message,
                Title = title,
                Description = description
            }).Length.ShouldBe(previewBytes);
        }
        else
        {
            var result = await sut.DeleteRawAsync(
                arguments,
                requestState: null,
                inputResponses: null,
                mrtrSupported: true,
                CancellationToken.None);

            result.IsError.ShouldBe(true);
            var structured = result.StructuredContent!.Value;
            structured.GetProperty("code").GetString().ShouldBe("payload_too_large");
            structured.GetProperty("phase").GetString().ShouldBe("admissionAndPayload");
            structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
            JsonSerializer.Serialize(result).ShouldNotContain(uid);
        }

        await service.Received(expectsConfirmation ? 1 : 0)
            .GetResourceAsync(href, Arg.Any<CancellationToken>());
        await service.DidNotReceive().DeleteResourceAsync(
            Arg.Any<CalendarResourceRevisionReference>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_AcceptedContinuationRefetchesThenReturnsVerifiedDeletionReceipt()
    {
        var service = Substitute.For<ICalendarService>();
        var snapshot = TodoSnapshot();
        service.GetResourceAsync(snapshot.ResourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(snapshot.ResourceHref, snapshot.EntityTag, snapshot.AuthoritativeUtf8) with
            {
                Snapshot = snapshot
            });
        service.DeleteResourceAsync(Arg.Any<CalendarResourceRevisionReference>(), Arg.Any<CancellationToken>())
            .Returns(CalendarResourceDeleteResult.Success(new CalendarResourceDeletionReceipt(
                snapshot.ResourceHref,
                snapshot.Projection.EntityUid!,
                CalendarEntityKind.Todo,
                snapshot.EntityTag)));
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);
        var firstRound = await Should.ThrowAsync<InputRequiredException>(() => sut.DeleteRawAsync(
            ValidArguments(),
            requestState: null,
            inputResponses: null,
            mrtrSupported: true,
            CancellationToken.None));

        var result = await sut.DeleteRawAsync(
            ValidArguments(),
            firstRound.Result.RequestState,
            AcceptedConfirmation(),
            mrtrSupported: true,
            CancellationToken.None);

        result.IsError.ShouldBe(false);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("outcome").GetString().ShouldBe("success");
        structured.GetProperty("mutationState").GetString().ShouldBe("committed");
        var receipt = structured.GetProperty("deletionReceipt");
        receipt.GetProperty("href").GetString().ShouldBe(snapshot.ResourceHref);
        receipt.GetProperty("entityUid").GetString().ShouldBe("todo-1");
        receipt.GetProperty("entityKind").GetString().ShouldBe("todo");
        receipt.GetProperty("consumedEntityTag").GetString().ShouldBe("\"r1\"");
        await service.Received(2).GetResourceAsync(snapshot.ResourceHref, Arg.Any<CancellationToken>());
        await service.Received(1).DeleteResourceAsync(
            new CalendarResourceRevisionReference(
                snapshot.ResourceHref,
                "todo-1",
                CalendarEntityKind.Todo,
                "\"r1\""),
            Arg.Any<CancellationToken>());
        result.Content.OfType<TextContentBlock>().Single().Text.ShouldNotContain("todo-1");
    }

    [Theory]
    [InlineData("decline", null)]
    [InlineData("cancel", null)]
    [InlineData("accept", false)]
    public async Task DeleteRawAsync_NegativeConfirmationIsSuccessfulNonMutation(
        string action,
        bool? confirmation)
    {
        var service = ReviewedService();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);
        var firstRound = await BeginAsync(sut);
        var content = confirmation is null
            ? null
            : new Dictionary<string, JsonElement>
            {
                ["confirm"] = JsonSerializer.SerializeToElement(confirmation.Value)
            };

        var result = await sut.DeleteRawAsync(
            ValidArguments(),
            firstRound.Result.RequestState,
            new Dictionary<string, InputResponse>
            {
                ["confirm_delete"] = InputResponse.FromElicitResult(new ElicitResult
                {
                    Action = action,
                    Content = content
                })
            },
            mrtrSupported: true,
            CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("confirmation_declined");
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        await service.DidNotReceive().DeleteResourceAsync(
            Arg.Any<CalendarResourceRevisionReference>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_TamperedStateReturnsFrozenConfirmationMismatch()
    {
        var service = ReviewedService();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);
        var firstRound = await BeginAsync(sut);
        var state = firstRound.Result.RequestState!;
        var tampered = $"{state[..^1]}{(state[^1] == 'A' ? 'B' : 'A')}";

        var result = await sut.DeleteRawAsync(
            ValidArguments(),
            tampered,
            AcceptedConfirmation(),
            mrtrSupported: true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("confirmation_mismatch");
        structured.GetProperty("category").GetString().ShouldBe("confirmation");
        structured.GetProperty("phase").GetString().ShouldBe("mrtr");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        await service.DidNotReceive().DeleteResourceAsync(
            Arg.Any<CalendarResourceRevisionReference>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_ExpiredStateReturnsFrozenConfirmationExpired()
    {
        var service = ReviewedService();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);
        var firstRound = await BeginAsync(sut);
        timeProvider.Advance(TimeSpan.FromMinutes(10));

        var result = await sut.DeleteRawAsync(
            ValidArguments(),
            firstRound.Result.RequestState,
            AcceptedConfirmation(),
            mrtrSupported: true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("confirmation_expired");
        structured.GetProperty("category").GetString().ShouldBe("confirmation");
        structured.GetProperty("phase").GetString().ShouldBe("mrtr");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        await service.DidNotReceive().DeleteResourceAsync(
            Arg.Any<CalendarResourceRevisionReference>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_ChangedReviewedRevisionRejectsStateBeforeRefetchOrDelete()
    {
        var service = ReviewedService();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);
        var firstRound = await BeginAsync(sut);
        var changed = ValidArguments();
        changed["revision"] = JsonSerializer.SerializeToElement(new
        {
            href = "https://cal.example/tasks/a.ics",
            entityUid = "todo-2",
            entityKind = "todo",
            entityTag = "\"r1\""
        });

        var result = await sut.DeleteRawAsync(
            changed,
            firstRound.Result.RequestState,
            AcceptedConfirmation(),
            mrtrSupported: true,
            CancellationToken.None);

        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_mismatch");
        await service.Received(1).GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().DeleteResourceAsync(
            Arg.Any<CalendarResourceRevisionReference>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_RevisionChangedAfterConfirmationReturnsConflictWithCurrentSnapshot()
    {
        var first = TodoSnapshot();
        var changed = first with { EntityTag = "\"r2\"" };
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(first.ResourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(first.ResourceHref, first.EntityTag, first.AuthoritativeUtf8) with { Snapshot = first },
            CalendarResourceRead.Success(changed.ResourceHref, changed.EntityTag, changed.AuthoritativeUtf8) with { Snapshot = changed });
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);
        var firstRound = await BeginAsync(sut);

        var result = await sut.DeleteRawAsync(
            ValidArguments(),
            firstRound.Result.RequestState,
            AcceptedConfirmation(),
            mrtrSupported: true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("conflict");
        structured.GetProperty("phase").GetString().ShouldBe("targetRevision");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        structured.GetProperty("currentSnapshot").GetProperty("resourceRevision")
            .GetProperty("entityTag").GetString().ShouldBe("\"r2\"");
        await service.DidNotReceive().DeleteResourceAsync(
            Arg.Any<CalendarResourceRevisionReference>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(CalendarResourceDeleteCode.NotFound, CalendarMutationState.NotCommitted, "not_found", "selection", "execution", false)]
    [InlineData(CalendarResourceDeleteCode.EntityKindMismatch, CalendarMutationState.NotAttempted, "entity_kind_mismatch", "state", "targetRevision", false)]
    [InlineData(CalendarResourceDeleteCode.UnsupportedCapability, CalendarMutationState.NotCommitted, "unsupported_capability", "capabilityAndProjection", "execution", false)]
    [InlineData(CalendarResourceDeleteCode.PayloadTooLarge, CalendarMutationState.NotCommitted, "payload_too_large", "limitsAndAdmission", "execution", false)]
    [InlineData(CalendarResourceDeleteCode.UpstreamUnauthorized, CalendarMutationState.NotCommitted, "upstream_unauthorized", "upstream", "execution", false)]
    [InlineData(CalendarResourceDeleteCode.UpstreamForbidden, CalendarMutationState.NotCommitted, "upstream_forbidden", "upstream", "execution", false)]
    [InlineData(CalendarResourceDeleteCode.UpstreamRateLimited, CalendarMutationState.NotCommitted, "upstream_rate_limited", "upstream", "execution", true)]
    [InlineData(CalendarResourceDeleteCode.UpstreamUnavailable, CalendarMutationState.NotCommitted, "upstream_unavailable", "upstream", "execution", false)]
    [InlineData(CalendarResourceDeleteCode.UpstreamProtocolError, CalendarMutationState.NotCommitted, "upstream_protocol_error", "upstream", "execution", false)]
    [InlineData(CalendarResourceDeleteCode.CommittedButUnverified, CalendarMutationState.Committed, "committed_but_unverified", "postWriteTruth", "postWriteVerificationOrReconciliation", false)]
    [InlineData(CalendarResourceDeleteCode.Indeterminate, CalendarMutationState.Unknown, "indeterminate", "postWriteTruth", "postWriteVerificationOrReconciliation", false)]
    public async Task DeleteRawAsync_MapsFrozenDeletionFailureOutcome(
        CalendarResourceDeleteCode code,
        CalendarMutationState mutationState,
        string expectedCode,
        string expectedCategory,
        string expectedPhase,
        bool expectedRetryable)
    {
        var service = ReviewedService();
        service.DeleteResourceAsync(Arg.Any<CalendarResourceRevisionReference>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceDeleteResult(code, mutationState, Retryable: expectedRetryable));
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);
        var firstRound = await BeginAsync(sut);

        var result = await sut.DeleteRawAsync(
            ValidArguments(),
            firstRound.Result.RequestState,
            AcceptedConfirmation(),
            mrtrSupported: true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe(expectedCode);
        structured.GetProperty("category").GetString().ShouldBe(expectedCategory);
        structured.GetProperty("phase").GetString().ShouldBe(expectedPhase);
        structured.GetProperty("mutationState").GetString().ShouldBe(MutationStateText(mutationState));
        structured.GetProperty("retryable").GetBoolean().ShouldBe(expectedRetryable);
    }

    [Fact]
    public async Task DeleteRawAsync_DefiniteRateLimitIsRetryableAndPreservesRetryAfter()
    {
        var service = ReviewedService();
        service.DeleteResourceAsync(Arg.Any<CalendarResourceRevisionReference>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceDeleteResult(
                CalendarResourceDeleteCode.UpstreamRateLimited,
                CalendarMutationState.NotCommitted,
                RetryAfterMilliseconds: 3_000,
                Retryable: true));
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);
        var firstRound = await BeginAsync(sut);

        var result = await sut.DeleteRawAsync(
            ValidArguments(),
            firstRound.Result.RequestState,
            AcceptedConfirmation(),
            mrtrSupported: true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("upstream_rate_limited");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_committed");
        structured.GetProperty("retryable").GetBoolean().ShouldBeTrue();
        structured.GetProperty("retryAfterMs").GetInt32().ShouldBe(3_000);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"revision\":{\"href\":\"https://cal.example/tasks/a.ics\",\"entityUid\":\"todo-1\",\"entityKind\":\"todo\",\"entityTag\":\"\\\"r1\\\"\"},\"extra\":true}")]
    [InlineData("{\"revision\":{\"href\":\"https://cal.example/tasks/a.ics\",\"entityUid\":\"todo-1\",\"entityKind\":\"todo\",\"entityTag\":\"\\\"r1\\\"\",\"extra\":true}}")]
    [InlineData("{\"revision\":{\"href\":\"relative/a.ics\",\"entityUid\":\"todo-1\",\"entityKind\":\"todo\",\"entityTag\":\"\\\"r1\\\"\"}}")]
    [InlineData("{\"revision\":{\"href\":\"https://cal.example/tasks/a.ics\",\"entityUid\":\" todo-1\",\"entityKind\":\"todo\",\"entityTag\":\"\\\"r1\\\"\"}}")]
    [InlineData("{\"revision\":{\"href\":\"https://cal.example/tasks/a.ics\",\"entityUid\":\"todo-1\",\"entityKind\":\"task\",\"entityTag\":\"\\\"r1\\\"\"}}")]
    [InlineData("{\"revision\":{\"href\":\"https://cal.example/tasks/a.ics\",\"entityUid\":\"todo-1\",\"entityKind\":\"todo\",\"entityTag\":\"*\"}}")]
    public async Task DeleteRawAsync_RejectsNonFrozenOrLexicallyInvalidInputBeforeReading(string json)
    {
        var service = Substitute.For<ICalendarService>();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);

        var result = await sut.DeleteRawAsync(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json),
            requestState: null,
            inputResponses: null,
            mrtrSupported: true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("invalid_input");
        structured.GetProperty("phase").GetString().ShouldBe("schemaLexicalDiscriminator");
        await service.DidNotReceive().GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_WeakRevisionReturnsConcurrencyUnavailableBeforeReading()
    {
        var service = Substitute.For<ICalendarService>();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);
        var arguments = ValidArguments();
        arguments["revision"] = JsonSerializer.SerializeToElement(new
        {
            href = "https://cal.example/tasks/a.ics",
            entityUid = "todo-1",
            entityKind = "todo",
            entityTag = "W/\"r1\""
        });

        var result = await sut.DeleteRawAsync(
            arguments,
            requestState: null,
            inputResponses: null,
            mrtrSupported: true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("concurrency_unavailable");
        structured.GetProperty("phase").GetString().ShouldBe("targetRevision");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        await service.DidNotReceive().GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().DeleteResourceAsync(
            Arg.Any<CalendarResourceRevisionReference>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_DirectTargetNotFoundReturnsTargetRevisionFailureWithoutDelete()
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);

        var result = await sut.DeleteRawAsync(
            ValidArguments(),
            requestState: null,
            inputResponses: null,
            mrtrSupported: true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("not_found");
        structured.GetProperty("phase").GetString().ShouldBe("targetRevision");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        await service.DidNotReceive().DeleteResourceAsync(
            Arg.Any<CalendarResourceRevisionReference>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_WithoutMrtrSupportReturnsTypedCapabilityErrorAfterReadOnlyPreview()
    {
        var service = ReviewedService();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);

        var result = await sut.DeleteRawAsync(
            ValidArguments(),
            requestState: null,
            inputResponses: null,
            mrtrSupported: false,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("unsupported_capability");
        structured.GetProperty("phase").GetString().ShouldBe("mrtr");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        await service.Received(1).GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().DeleteResourceAsync(
            Arg.Any<CalendarResourceRevisionReference>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_ContinuationWithoutMrtrSupportFailsBeforeStateOrServiceAccess()
    {
        var service = Substitute.For<ICalendarService>();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);
        const string opaqueRequestState = "opaque-state-that-must-not-be-unprotected";

        var result = await sut.DeleteRawAsync(
            ValidArguments(),
            opaqueRequestState,
            AcceptedConfirmation(),
            mrtrSupported: false,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("unsupported_capability");
        structured.GetProperty("phase").GetString().ShouldBe("mrtr");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        JsonSerializer.Serialize(result).ShouldNotContain(opaqueRequestState);
        await service.DidNotReceive().GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().DeleteResourceAsync(
            Arg.Any<CalendarResourceRevisionReference>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("https://other.example/tasks/a.ics")]
    [InlineData("HTTPS://cal.example/tasks/a.ics")]
    [InlineData("https://user@cal.example/tasks/a.ics")]
    [InlineData("https://cal.example/tasks/a.ics?view=full")]
    [InlineData("https://cal.example/tasks/a.ics#component")]
    [InlineData("https://cal.example/tasks%2Fa.ics")]
    [InlineData("https://cal.example/tasks%5Ca.ics")]
    public async Task DeleteRawAsync_ServicePreflightHrefRejectionUsesOriginScopePhaseWithoutDelete(string href)
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(href, Arg.Any<CancellationToken>())
            .Returns(new CalendarResourceRead(CalendarResourceReadCode.InvalidInput));
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);
        var arguments = ArgumentsWithHref(href);

        var result = await sut.DeleteRawAsync(
            arguments,
            requestState: null,
            inputResponses: null,
            mrtrSupported: true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("invalid_input");
        structured.GetProperty("phase").GetString().ShouldBe("originScopeAuthorization");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        await service.Received(1).GetResourceAsync(href, Arg.Any<CancellationToken>());
        await service.DidNotReceive().DeleteResourceAsync(
            Arg.Any<CalendarResourceRevisionReference>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void MutationRequestState_IsNonceRandomCredentialBoundAndInvalidatedByKeyRotation()
    {
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var key = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var original = new CalendarMutationRequestStateProtector(
            timeProvider,
            Options.Create(CreateOptions()),
            key);
        var changedCredentials = CreateOptions();
        changedCredentials.Password = "different";
        var redistributed = new CalendarMutationRequestStateProtector(
            timeProvider,
            Options.Create(changedCredentials),
            key);
        var rotated = new CalendarMutationRequestStateProtector(
            timeProvider,
            Options.Create(CreateOptions()),
            key.Select(value => (byte)(value + 1)).ToArray());
        var revision = new CalendarResourceRevisionReference(
            "https://cal.example/tasks/a.ics",
            "todo-1",
            CalendarEntityKind.Todo,
            "\"r1\"");

        var first = original.Protect(revision);
        var second = original.Protect(revision);

        first.ShouldNotBe(second);
        original.TryUnprotect(first, revision, out var expired).ShouldBeTrue();
        expired.ShouldBeFalse();
        redistributed.TryUnprotect(first, revision, out _).ShouldBeFalse();
        rotated.TryUnprotect(first, revision, out _).ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteRawAsync_RejectsOversizeRequestStateBeforeRefetchOrDelete()
    {
        var service = Substitute.For<ICalendarService>();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);

        var result = await sut.DeleteRawAsync(
            ValidArguments(),
            new string('A', CalendarMutationRequestStateProtector.MaximumRequestStateCharacters + 1),
            AcceptedConfirmation(),
            mrtrSupported: true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_mismatch");
        await service.DidNotReceive().GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await service.DidNotReceive().DeleteResourceAsync(
            Arg.Any<CalendarResourceRevisionReference>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_ReplayAfterSuccessRevalidatesAndDoesNotDeleteAgain()
    {
        var snapshot = TodoSnapshot();
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(snapshot.ResourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(snapshot.ResourceHref, snapshot.EntityTag, snapshot.AuthoritativeUtf8) with { Snapshot = snapshot },
            CalendarResourceRead.Success(snapshot.ResourceHref, snapshot.EntityTag, snapshot.AuthoritativeUtf8) with { Snapshot = snapshot },
            new CalendarResourceRead(CalendarResourceReadCode.NotFound));
        service.DeleteResourceAsync(Arg.Any<CalendarResourceRevisionReference>(), Arg.Any<CancellationToken>())
            .Returns(CalendarResourceDeleteResult.Success(new CalendarResourceDeletionReceipt(
                snapshot.ResourceHref,
                snapshot.Projection.EntityUid!,
                CalendarEntityKind.Todo,
                snapshot.EntityTag)));
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);
        var firstRound = await BeginAsync(sut);

        var first = await sut.DeleteRawAsync(
            ValidArguments(),
            firstRound.Result.RequestState,
            AcceptedConfirmation(),
            mrtrSupported: true,
            CancellationToken.None);
        var replay = await sut.DeleteRawAsync(
            ValidArguments(),
            firstRound.Result.RequestState,
            AcceptedConfirmation(),
            mrtrSupported: true,
            CancellationToken.None);

        first.IsError.ShouldBe(false);
        replay.IsError.ShouldBe(true);
        replay.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("not_found");
        await service.Received(1).DeleteResourceAsync(
            Arg.Any<CalendarResourceRevisionReference>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_PreviewDeadlineReturnsLimitExhaustedWithoutWrite()
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
            new TaskCompletionSource<CalendarResourceRead>(TaskCreationOptions.RunContinuationsAsynchronously)
                .Task.WaitAsync(call.Arg<CancellationToken>()));
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);
        var pending = sut.DeleteRawAsync(
            ValidArguments(),
            requestState: null,
            inputResponses: null,
            mrtrSupported: true,
            CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromSeconds(30));
        var result = await pending;

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("limit_exhausted");
        structured.GetProperty("category").GetString().ShouldBe("limitsAndAdmission");
        structured.GetProperty("phase").GetString().ShouldBe("execution");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        await service.DidNotReceive().DeleteResourceAsync(
            Arg.Any<CalendarResourceRevisionReference>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_AdmissionTimeoutReturnsFrozenBusyBeforeSchemaOrRead()
    {
        var service = Substitute.For<ICalendarService>();
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var admission = new CalendarMutationAdmission(timeProvider);
        using var active = (await admission.AcquireAsync(CancellationToken.None))!;
        var sut = CreateTool(service, timeProvider, admission);
        var pending = sut.DeleteRawAsync(
            new Dictionary<string, JsonElement>(),
            requestState: null,
            inputResponses: null,
            mrtrSupported: true,
            CancellationToken.None);

        timeProvider.Advance(TimeSpan.FromSeconds(2));
        var result = await pending;

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("busy");
        structured.GetProperty("phase").GetString().ShouldBe("admissionAndPayload");
        structured.GetProperty("retryAfterMs").GetInt32().ShouldBe(2_000);
        await service.DidNotReceive().GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("io")]
    [InlineData("unexpected")]
    public async Task DeleteRawAsync_SanitizesExceptionalMutationHandoffAsIndeterminate(string failure)
    {
        var service = ReviewedService();
        service.DeleteResourceAsync(Arg.Any<CalendarResourceRevisionReference>(), Arg.Any<CancellationToken>())
            .Returns<CalendarResourceDeleteResult>(_ => throw (failure == "io"
                ? new IOException("secret upstream body")
                : new InvalidOperationException("secret internal detail")));
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);
        var firstRound = await BeginAsync(sut);

        var result = await sut.DeleteRawAsync(
            ValidArguments(),
            firstRound.Result.RequestState,
            AcceptedConfirmation(),
            mrtrSupported: true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("indeterminate");
        structured.GetProperty("phase").GetString().ShouldBe("postWriteVerificationOrReconciliation");
        structured.GetProperty("mutationState").GetString().ShouldBe("unknown");
        JsonSerializer.Serialize(result).ShouldNotContain("secret");
    }

    [Theory]
    [InlineData("protocol", "upstream_protocol_error", "upstream", "targetRevision", false)]
    [InlineData("xml", "upstream_protocol_error", "upstream", "targetRevision", false)]
    [InlineData("unsupported", "unsupported_capability", "capabilityAndProjection", "selectionDiscoveryCapability", false)]
    [InlineData("io", "upstream_unavailable", "upstream", "targetRevision", true)]
    [InlineData("unexpected", "upstream_protocol_error", "upstream", "targetRevision", false)]
    public async Task DeleteRawAsync_SanitizesPreviewFailures(
        string failure,
        string expectedCode,
        string expectedCategory,
        string expectedPhase,
        bool expectedRetryable)
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<CalendarResourceRead>(_ => throw failure switch
            {
                "protocol" => new CalendarDiscoveryProtocolException("secret protocol"),
                "xml" => new XmlException("secret xml"),
                "unsupported" => new CalendarDiscoveryUnsupportedCapabilityException("secret unsupported"),
                "io" => new IOException("secret io"),
                _ => new InvalidOperationException("secret unexpected")
            });
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);

        var result = await sut.DeleteRawAsync(
            ValidArguments(),
            requestState: null,
            inputResponses: null,
            mrtrSupported: true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe(expectedCode);
        structured.GetProperty("category").GetString().ShouldBe(expectedCategory);
        structured.GetProperty("phase").GetString().ShouldBe(expectedPhase);
        structured.GetProperty("retryable").GetBoolean().ShouldBe(expectedRetryable);
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        JsonSerializer.Serialize(result).ShouldNotContain("secret");
    }

    [Theory]
    [InlineData(401, "upstream_unauthorized", "selectionDiscoveryCapability", false)]
    [InlineData(403, "upstream_forbidden", "selectionDiscoveryCapability", false)]
    [InlineData(404, "upstream_protocol_error", "selectionDiscoveryCapability", false)]
    [InlineData(409, "conflict", "selectionDiscoveryCapability", false)]
    [InlineData(412, "conflict", "selectionDiscoveryCapability", false)]
    [InlineData(413, "payload_too_large", "selectionDiscoveryCapability", false)]
    [InlineData(429, "upstream_rate_limited", "selectionDiscoveryCapability", true)]
    [InlineData(405, "unsupported_capability", "selectionDiscoveryCapability", false)]
    [InlineData(501, "unsupported_capability", "selectionDiscoveryCapability", false)]
    [InlineData(507, "upstream_unavailable", "selectionDiscoveryCapability", false)]
    [InlineData(503, "upstream_unavailable", "selectionDiscoveryCapability", true)]
    public async Task DeleteRawAsync_MapsPreviewHttpStatusWithoutDeleting(
        int statusCode,
        string expectedCode,
        string expectedPhase,
        bool expectedRetryable)
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<CalendarResourceRead>(_ => throw new HttpRequestException(
                "secret upstream response",
                inner: null,
                (System.Net.HttpStatusCode)statusCode));
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);

        var result = await sut.DeleteRawAsync(
            ValidArguments(),
            requestState: null,
            inputResponses: null,
            mrtrSupported: true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe(expectedCode);
        structured.GetProperty("phase").GetString().ShouldBe(expectedPhase);
        structured.GetProperty("retryable").GetBoolean().ShouldBe(expectedRetryable);
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        JsonSerializer.Serialize(result).ShouldNotContain("secret");
        await service.DidNotReceive().DeleteResourceAsync(
            Arg.Any<CalendarResourceRevisionReference>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_MapsPreviewDiscoveryLimitWithoutWrite()
    {
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<CalendarResourceRead>(_ => throw new CalendarDiscoveryLimitException(257));
        var timeProvider = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(service, timeProvider);

        var result = await sut.DeleteRawAsync(
            ValidArguments(),
            requestState: null,
            inputResponses: null,
            mrtrSupported: true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        var structured = result.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("limit_exhausted");
        structured.GetProperty("phase").GetString().ShouldBe("selectionDiscoveryCapability");
        structured.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
        structured.GetProperty("limits").GetProperty("calendarCount").GetInt32().ShouldBe(257);
        await service.DidNotReceive().DeleteResourceAsync(
            Arg.Any<CalendarResourceRevisionReference>(),
            Arg.Any<CancellationToken>());
    }

    private static CalendarResourceDeleteTools CreateTool(
        ICalendarService service,
        TimeProvider timeProvider,
        CalendarMutationAdmission? admission = null)
    {
        var protector = new CalendarMutationRequestStateProtector(
            timeProvider,
            Options.Create(CreateOptions()),
            Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        return new CalendarResourceDeleteTools(
            service,
            protector,
            timeProvider,
            admission ?? new CalendarMutationAdmission(timeProvider));
    }

    private static Task<InputRequiredException> BeginAsync(CalendarResourceDeleteTools sut) =>
        Should.ThrowAsync<InputRequiredException>(() => sut.DeleteRawAsync(
            ValidArguments(),
            requestState: null,
            inputResponses: null,
            mrtrSupported: true,
            CancellationToken.None));

    private static ICalendarService ReviewedService()
    {
        var snapshot = TodoSnapshot();
        var service = Substitute.For<ICalendarService>();
        service.GetResourceAsync(snapshot.ResourceHref, Arg.Any<CancellationToken>()).Returns(
            CalendarResourceRead.Success(snapshot.ResourceHref, snapshot.EntityTag, snapshot.AuthoritativeUtf8) with
            {
                Snapshot = snapshot
            });
        return service;
    }

    private static Dictionary<string, InputResponse> AcceptedConfirmation() => new()
    {
        ["confirm_delete"] = InputResponse.FromElicitResult(new ElicitResult
        {
            Action = "accept",
            Content = new Dictionary<string, JsonElement>
            {
                ["confirm"] = JsonSerializer.SerializeToElement(true)
            }
        })
    };

    private static string MutationStateText(CalendarMutationState state) => state switch
    {
        CalendarMutationState.NotAttempted => "not_attempted",
        CalendarMutationState.NotCommitted => "not_committed",
        CalendarMutationState.Committed => "committed",
        _ => "unknown"
    };

    private static Dictionary<string, JsonElement> ValidArguments() => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
        """
        {"revision":{"href":"https://cal.example/tasks/a.ics","entityUid":"todo-1","entityKind":"todo","entityTag":"\"r1\""}}
        """)!;

    private static Dictionary<string, JsonElement> ArgumentsWithHref(string href) =>
        ArgumentsWithIdentity(href, "todo-1");

    private static Dictionary<string, JsonElement> ArgumentsWithIdentity(string href, string entityUid) => new()
    {
        ["revision"] = JsonSerializer.SerializeToElement(new
        {
            href,
            entityUid,
            entityKind = "todo",
            entityTag = "\"r1\""
        })
    };

    private static CalendarResourceSnapshot TodoSnapshot()
    {
        var bytes = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nBEGIN:VTODO\r\nUID:todo-1\r\nSUMMARY:Private\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");
        return new CalendarResourceSnapshot(
            "https://cal.example/tasks/",
            "https://cal.example/tasks/a.ics",
            "\"r1\"",
            bytes,
            [],
            new CalendarResourceProjection(CalendarResourceProjectionKind.Todo, "todo-1", "Private"),
            []);
    }

    private static CalDavOptions CreateOptions() => new()
    {
        BaseUrl = "https://cal.example/",
        Username = "user",
        Password = "secret",
        CalendarHrefs = "https://cal.example/tasks/"
    };

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
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
            FixedTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset? _dueAt = dueTime == Timeout.InfiniteTimeSpan
                ? null
                : owner.GetUtcNow() + dueTime;
            private bool _disposed;

            public bool Change(TimeSpan newDueTime, TimeSpan newPeriod)
            {
                if (_disposed)
                    return false;
                period = newPeriod;
                _dueAt = newDueTime == Timeout.InfiniteTimeSpan
                    ? null
                    : owner.GetUtcNow() + newDueTime;
                return true;
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void FireIfDue()
            {
                if (_disposed || _dueAt is null || owner.GetUtcNow() < _dueAt)
                    return;
                _dueAt = period == Timeout.InfiniteTimeSpan ? null : owner.GetUtcNow() + period;
                callback(state);
            }
        }
    }
}
