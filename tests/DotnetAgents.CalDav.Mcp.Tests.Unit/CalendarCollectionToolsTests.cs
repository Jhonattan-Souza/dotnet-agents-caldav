using System.Net;
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

public sealed class CalendarCollectionToolsTests
{
    [Fact]
    public async Task CreateRawAsync_ValidInputReturnsCreatedCalendar()
    {
        var module = Substitute.For<ICalendarCollectionModule>();
        var descriptor = Descriptor("https://cal.example/calendars/user/planning/", eventKind: true, todo: true);
        module.CreateAsync(Arg.Any<CalendarCollectionCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(CalendarCollectionCreateResult.Success(descriptor));
        var sut = CreateTool(module, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));

        var result = await sut.CreateRawAsync(
            CreateArguments("Planning", ["event", "todo"]),
            CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("success");
        result.StructuredContent.Value.GetProperty("calendar").GetProperty("calendar").GetProperty("href").GetString()
            .ShouldBe(descriptor.Href);
        await module.Received(1).CreateAsync(
            Arg.Is<CalendarCollectionCreateRequest>(request =>
                request.DisplayName == "Planning"
                && request.EntityKinds.SequenceEqual(new[] { CalendarEntityKind.Event, CalendarEntityKind.Todo })
                && request.DestinationHref == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateRawAsync_InvalidArgumentShapesReturnInvalidInput()
    {
        var module = Substitute.For<ICalendarCollectionModule>();
        var sut = CreateTool(module, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));
        var invalidArguments = new List<IDictionary<string, JsonElement>?>
        {
            null,
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, JsonElement>
            {
                ["displayName"] = JsonSerializer.SerializeToElement("Planning"),
                ["entityKinds"] = JsonSerializer.SerializeToElement(new[] { "event" }),
                ["destinationHref"] = JsonSerializer.SerializeToElement("https://cal.example/calendars/user/planning/"),
                ["extra"] = JsonSerializer.SerializeToElement(true)
            },
            new Dictionary<string, JsonElement>
            {
                ["entityKinds"] = JsonSerializer.SerializeToElement(new[] { "event" })
            },
            new Dictionary<string, JsonElement>
            {
                ["displayName"] = JsonSerializer.SerializeToElement(42),
                ["entityKinds"] = JsonSerializer.SerializeToElement(new[] { "event" })
            },
            new Dictionary<string, JsonElement>
            {
                ["displayName"] = JsonSerializer.SerializeToElement(" "),
                ["entityKinds"] = JsonSerializer.SerializeToElement(new[] { "event" })
            },
            new Dictionary<string, JsonElement>
            {
                ["displayName"] = JsonSerializer.SerializeToElement("Planning"),
                ["entityKinds"] = JsonSerializer.SerializeToElement("event")
            },
            new Dictionary<string, JsonElement>
            {
                ["displayName"] = JsonSerializer.SerializeToElement("Planning"),
                ["entityKinds"] = JsonSerializer.SerializeToElement(new[] { 1 })
            },
            new Dictionary<string, JsonElement>
            {
                ["displayName"] = JsonSerializer.SerializeToElement("Planning"),
                ["entityKinds"] = JsonSerializer.SerializeToElement(new[] { "unknown" })
            },
            new Dictionary<string, JsonElement>
            {
                ["displayName"] = JsonSerializer.SerializeToElement("Planning"),
                ["entityKinds"] = JsonSerializer.SerializeToElement(Array.Empty<string>())
            },
            new Dictionary<string, JsonElement>
            {
                ["displayName"] = JsonSerializer.SerializeToElement("Planning"),
                ["entityKinds"] = JsonSerializer.SerializeToElement(new[] { "event", "event" })
            },
            new Dictionary<string, JsonElement>
            {
                ["displayName"] = JsonSerializer.SerializeToElement("Planning"),
                ["entityKinds"] = JsonSerializer.SerializeToElement(new[] { "event", "todo", "event" })
            },
            new Dictionary<string, JsonElement>
            {
                ["displayName"] = JsonSerializer.SerializeToElement("Planning"),
                ["entityKinds"] = JsonSerializer.SerializeToElement(new[] { "event" }),
                ["destinationHref"] = JsonSerializer.SerializeToElement(42)
            },
            new Dictionary<string, JsonElement>
            {
                ["displayName"] = JsonSerializer.SerializeToElement("Planning"),
                ["entityKinds"] = JsonSerializer.SerializeToElement(new[] { "event" }),
                ["destinationHref"] = JsonSerializer.SerializeToElement(" ")
            }
        };

        foreach (var arguments in invalidArguments)
        {
            var result = await sut.CreateRawAsync(arguments, CancellationToken.None);
            result.IsError.ShouldBe(true);
            result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        }

        await module.DidNotReceive().CreateAsync(
            Arg.Any<CalendarCollectionCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_InvalidArgumentShapesReturnInvalidInput()
    {
        var module = Substitute.For<ICalendarCollectionModule>();
        var sut = CreateTool(module, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));
        var invalidArguments = new List<IDictionary<string, JsonElement>?>
        {
            null,
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, JsonElement>
            {
                ["href"] = JsonSerializer.SerializeToElement("https://cal.example/calendars/user/tasks/"),
                ["extra"] = JsonSerializer.SerializeToElement(true)
            },
            new Dictionary<string, JsonElement>
            {
                ["other"] = JsonSerializer.SerializeToElement("https://cal.example/calendars/user/tasks/")
            },
            new Dictionary<string, JsonElement>
            {
                ["href"] = JsonSerializer.SerializeToElement(42)
            },
            new Dictionary<string, JsonElement>
            {
                ["href"] = JsonSerializer.SerializeToElement(" ")
            }
        };

        foreach (var arguments in invalidArguments)
        {
            var result = await sut.DeleteRawAsync(arguments, null, null, true, CancellationToken.None);
            result.IsError.ShouldBe(true);
            result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("invalid_input");
        }

        await module.DidNotReceive().ReviewDeleteAsync(
            Arg.Any<CalendarCollectionDeleteRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateRawAsync_RejectsOversizedArgumentsBeforeAdmission()
    {
        var module = Substitute.For<ICalendarCollectionModule>();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(module, time);

        var result = await sut.CreateRawAsync(
            CreateArguments(new string('x', CalendarCollectionTools.MaximumArgumentBytes)),
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        await module.DidNotReceive().CreateAsync(
            Arg.Any<CalendarCollectionCreateRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_RejectsOversizedArgumentsBeforeAdmission()
    {
        var module = Substitute.For<ICalendarCollectionModule>();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(module, time);

        var result = await sut.DeleteRawAsync(
            new Dictionary<string, JsonElement>
            {
                ["href"] = JsonSerializer.SerializeToElement(new string('x', CalendarCollectionTools.MaximumArgumentBytes))
            },
            null,
            null,
            true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        await module.DidNotReceive().ReviewDeleteAsync(
            Arg.Any<CalendarCollectionDeleteRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateRawAsync_ReturnsBusyWhenAdmissionQueueIsFull()
    {
        var module = Substitute.For<ICalendarCollectionModule>();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var admission = new CalendarMutationAdmission(time);
        using var active = await admission.AcquireAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiters = Enumerable.Range(0, CalendarMutationAdmission.MaximumQueuedMutations)
            .Select(_ => admission.AcquireAsync(cancellation.Token).AsTask())
            .ToArray();
        var sut = CreateTool(module, time, admission);

        var result = await sut.CreateRawAsync(CreateArguments(), CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("busy");
        cancellation.Cancel();
        foreach (var waiter in waiters)
            await Should.ThrowAsync<OperationCanceledException>(async () => await waiter);
    }

    [Fact]
    public async Task DeleteRawAsync_ReturnsBusyWhenAdmissionQueueIsFull()
    {
        var module = Substitute.For<ICalendarCollectionModule>();
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var admission = new CalendarMutationAdmission(time);
        using var active = await admission.AcquireAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var waiters = Enumerable.Range(0, CalendarMutationAdmission.MaximumQueuedMutations)
            .Select(_ => admission.AcquireAsync(cancellation.Token).AsTask())
            .ToArray();
        var sut = CreateTool(module, time, admission);

        var result = await sut.DeleteRawAsync(
            DeleteArguments("https://cal.example/calendars/user/tasks/"),
            null,
            null,
            true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("busy");
        cancellation.Cancel();
        foreach (var waiter in waiters)
            await Should.ThrowAsync<OperationCanceledException>(async () => await waiter);
    }

    [Fact]
    public async Task DeleteRawAsync_DeclinedConfirmationDoesNotExecute()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var module = Substitute.For<ICalendarCollectionModule>();
        var descriptor = Descriptor(href);
        var binding = new CalendarCollectionDeleteReviewBinding(href, "digest");
        module.ReviewDeleteAsync(Arg.Any<CalendarCollectionDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarCollectionDeleteReviewResult(null, binding, descriptor));
        var sut = CreateTool(module, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.DeleteRawAsync(
            DeleteArguments(href), null, null, true, CancellationToken.None));

        var result = await sut.DeleteRawAsync(
            DeleteArguments(href),
            first.Result.RequestState,
            new Dictionary<string, InputResponse>
            {
                ["confirm_delete"] = InputResponse.FromElicitResult(new ElicitResult
                {
                    Action = "accept",
                    Content = new Dictionary<string, JsonElement>
                    {
                        ["confirm"] = JsonSerializer.SerializeToElement(false)
                    }
                })
            },
            true,
            CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("confirmation_declined");
        await module.DidNotReceive().ExecuteConfirmedDeleteAsync(
            Arg.Any<CalendarCollectionDeleteRequest>(),
            Arg.Any<CalendarCollectionDeleteReviewBinding>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_ContinuationWithoutMrtrReturnsUnsupportedCapability()
    {
        var module = Substitute.For<ICalendarCollectionModule>();
        var sut = CreateTool(module, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));

        var result = await sut.DeleteRawAsync(
            DeleteArguments("https://cal.example/calendars/user/tasks/"),
            "state",
            new Dictionary<string, InputResponse>(),
            false,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("unsupported_capability");
        await module.DidNotReceive().ReviewDeleteAsync(
            Arg.Any<CalendarCollectionDeleteRequest>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_FirstRoundWithoutMrtrReturnsUnsupportedCapability()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var module = Substitute.For<ICalendarCollectionModule>();
        module.ReviewDeleteAsync(Arg.Any<CalendarCollectionDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarCollectionDeleteReviewResult(
                null,
                new CalendarCollectionDeleteReviewBinding(href, "digest"),
                Descriptor(href)));
        var sut = CreateTool(module, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));

        var result = await sut.DeleteRawAsync(DeleteArguments(href), null, null, false, CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("unsupported_capability");
    }

    [Fact]
    public async Task DeleteRawAsync_MalformedContinuationReturnsConfirmationMismatch()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var module = Substitute.For<ICalendarCollectionModule>();
        var binding = new CalendarCollectionDeleteReviewBinding(href, "digest");
        module.ReviewDeleteAsync(Arg.Any<CalendarCollectionDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarCollectionDeleteReviewResult(null, binding, Descriptor(href)));
        var sut = CreateTool(module, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.DeleteRawAsync(
            DeleteArguments(href), null, null, true, CancellationToken.None));

        var result = await sut.DeleteRawAsync(
            DeleteArguments(href),
            first.Result.RequestState,
            new Dictionary<string, InputResponse>(),
            true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_mismatch");
        await module.DidNotReceive().ExecuteConfirmedDeleteAsync(
            Arg.Any<CalendarCollectionDeleteRequest>(),
            Arg.Any<CalendarCollectionDeleteReviewBinding>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_ContinuationShapeChecksReturnConfirmationMismatch()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var module = Substitute.For<ICalendarCollectionModule>();
        var binding = new CalendarCollectionDeleteReviewBinding(href, "digest");
        module.ReviewDeleteAsync(Arg.Any<CalendarCollectionDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarCollectionDeleteReviewResult(null, binding, Descriptor(href)));
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(module, time);
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.DeleteRawAsync(
            DeleteArguments(href), null, null, true, CancellationToken.None));
        var accepted = new Dictionary<string, InputResponse>
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

        var cases = new (string? State, IDictionary<string, InputResponse>? Responses)[]
        {
            (null, accepted),
            (first.Result.RequestState, null),
            (first.Result.RequestState, new Dictionary<string, InputResponse> { ["confirm_delete"] = null! })
        };
        foreach (var (state, responses) in cases)
        {
            var result = await sut.DeleteRawAsync(DeleteArguments(href), state, responses, true, CancellationToken.None);
            result.IsError.ShouldBe(true);
            result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_mismatch");
        }
    }

    [Fact]
    public async Task DeleteRawAsync_UnknownElicitationActionReturnsConfirmationMismatch()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var module = Substitute.For<ICalendarCollectionModule>();
        var binding = new CalendarCollectionDeleteReviewBinding(href, "digest");
        module.ReviewDeleteAsync(Arg.Any<CalendarCollectionDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarCollectionDeleteReviewResult(null, binding, Descriptor(href)));
        var sut = CreateTool(module, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.DeleteRawAsync(
            DeleteArguments(href), null, null, true, CancellationToken.None));

        var result = await sut.DeleteRawAsync(
            DeleteArguments(href),
            first.Result.RequestState,
            new Dictionary<string, InputResponse>
            {
                ["confirm_delete"] = InputResponse.FromElicitResult(new ElicitResult { Action = "unknown" })
            },
            true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_mismatch");
    }

    [Fact]
    public async Task DeleteRawAsync_ExpiredConfirmationReturnsConfirmationExpired()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var module = Substitute.For<ICalendarCollectionModule>();
        var binding = new CalendarCollectionDeleteReviewBinding(href, "digest");
        module.ReviewDeleteAsync(Arg.Any<CalendarCollectionDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarCollectionDeleteReviewResult(null, binding, Descriptor(href)));
        var time = new AdvancingTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(module, time);
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.DeleteRawAsync(
            DeleteArguments(href), null, null, true, CancellationToken.None));
        time.Advance(TimeSpan.FromMinutes(11));

        var result = await sut.DeleteRawAsync(
            DeleteArguments(href),
            first.Result.RequestState,
            new Dictionary<string, InputResponse>
            {
                ["confirm_delete"] = InputResponse.FromElicitResult(new ElicitResult
                {
                    Action = "accept",
                    Content = new Dictionary<string, JsonElement>
                    {
                        ["confirm"] = JsonSerializer.SerializeToElement(true)
                    }
                })
            },
            true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_expired");
    }

    [Fact]
    public async Task DeleteRawAsync_ConfirmationMessageCoversEventOnlyAndMissingDisplayName()
    {
        const string href = "https://cal.example/calendars/user/events/";
        var eventModule = Substitute.For<ICalendarCollectionModule>();
        eventModule.ReviewDeleteAsync(Arg.Any<CalendarCollectionDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarCollectionDeleteReviewResult(
                null,
                new CalendarCollectionDeleteReviewBinding(href, "digest"),
                Descriptor(href, eventKind: true, todo: false)));
        var eventTool = CreateTool(eventModule, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));
        var eventRequest = await Should.ThrowAsync<InputRequiredException>(() => eventTool.DeleteRawAsync(
            DeleteArguments(href), null, null, true, CancellationToken.None));
        eventRequest.Result.InputRequests!["confirm_delete"].ElicitationParams!.Message
            .ShouldContain("Advertised kinds: event.");

        var unnamedModule = Substitute.For<ICalendarCollectionModule>();
        unnamedModule.ReviewDeleteAsync(Arg.Any<CalendarCollectionDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarCollectionDeleteReviewResult(
                null,
                new CalendarCollectionDeleteReviewBinding(href, "digest"),
                new CalendarDescriptor
                {
                    Href = href,
                    DisplayName = null,
                    DisplayNameProvenance = DisplayNameProvenance.Missing,
                    EventSupport = EntityKindSupport.NotAdvertised,
                    TodoSupport = EntityKindSupport.NotAdvertised
                }));
        var unnamedTool = CreateTool(unnamedModule, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));
        var unnamedRequest = await Should.ThrowAsync<InputRequiredException>(() => unnamedTool.DeleteRawAsync(
            DeleteArguments(href), null, null, true, CancellationToken.None));
        unnamedRequest.Result.InputRequests!["confirm_delete"].ElicitationParams!.Message
            .ShouldContain($"'{href}'");
        unnamedRequest.Result.InputRequests!["confirm_delete"].ElicitationParams!.Message
            .ShouldEndWith("Advertised kinds: .");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "upstream_unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "upstream_forbidden")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "unsupported_capability")]
    [InlineData(HttpStatusCode.NotImplemented, "unsupported_capability")]
    [InlineData(HttpStatusCode.Conflict, "conflict")]
    [InlineData(HttpStatusCode.PreconditionFailed, "conflict")]
    [InlineData(HttpStatusCode.TooManyRequests, "upstream_rate_limited")]
    [InlineData(HttpStatusCode.NotFound, "upstream_unavailable")]
    [InlineData(null, "upstream_unavailable")]
    public async Task CreateRawAsync_MapsHttpExceptions(
        HttpStatusCode? statusCode,
        string expectedCode)
    {
        var module = Substitute.For<ICalendarCollectionModule>();
        module.CreateAsync(Arg.Any<CalendarCollectionCreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<CalendarCollectionCreateResult>(
                new HttpRequestException("collection request failed", null, statusCode)));
        var sut = CreateTool(module, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));

        var result = await sut.CreateRawAsync(CreateArguments(), CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "upstream_unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "upstream_forbidden")]
    [InlineData(HttpStatusCode.MethodNotAllowed, "unsupported_capability")]
    [InlineData(HttpStatusCode.NotImplemented, "unsupported_capability")]
    [InlineData(HttpStatusCode.Conflict, "conflict")]
    [InlineData(HttpStatusCode.PreconditionFailed, "conflict")]
    [InlineData(HttpStatusCode.TooManyRequests, "upstream_rate_limited")]
    [InlineData(HttpStatusCode.NotFound, "upstream_unavailable")]
    [InlineData(null, "upstream_unavailable")]
    public async Task DeleteRawAsync_MapsReviewHttpExceptions(
        HttpStatusCode? statusCode,
        string expectedCode)
    {
        var module = Substitute.For<ICalendarCollectionModule>();
        module.ReviewDeleteAsync(Arg.Any<CalendarCollectionDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<CalendarCollectionDeleteReviewResult>(
                new HttpRequestException("collection request failed", null, statusCode)));
        var sut = CreateTool(module, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));

        var result = await sut.DeleteRawAsync(
            DeleteArguments("https://cal.example/calendars/user/tasks/"),
            null,
            null,
            true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
    }

    [Fact]
    public async Task CreateRawAsync_MapsDiscoveryAndTransportExceptions()
    {
        var cases = new (Exception Exception, string Code)[]
        {
            (new OperationCanceledException(), "upstream_unavailable"),
            (new CalendarDiscoveryLimitException(300), "limit_exhausted"),
            (new CalendarDiscoveryUnsupportedCapabilityException("unsupported"), "unsupported_capability"),
            (new XmlException("malformed"), "upstream_protocol_error"),
            (new CalendarDiscoveryProtocolException("invalid"), "upstream_protocol_error"),
            (new IOException("unavailable"), "upstream_unavailable"),
            (new TimeoutException("timeout"), "upstream_unavailable")
        };

        foreach (var (exception, expectedCode) in cases)
        {
            var module = Substitute.For<ICalendarCollectionModule>();
            module.CreateAsync(Arg.Any<CalendarCollectionCreateRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromException<CalendarCollectionCreateResult>(exception));
            var sut = CreateTool(module, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));

            var result = await sut.CreateRawAsync(CreateArguments(), CancellationToken.None);

            result.IsError.ShouldBe(true);
            result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
        }
    }

    [Fact]
    public async Task CreateRawAsync_MapsEveryModuleOutcomeToCatalogCode()
    {
        var cases = new Dictionary<CalendarCollectionCreateCode, string>
        {
            [CalendarCollectionCreateCode.Success] = "indeterminate",
            [CalendarCollectionCreateCode.InvalidInput] = "invalid_input",
            [CalendarCollectionCreateCode.OutsideScope] = "outside_scope",
            [CalendarCollectionCreateCode.Conflict] = "conflict",
            [CalendarCollectionCreateCode.DestinationConflict] = "destination_conflict",
            [CalendarCollectionCreateCode.UnsupportedCapability] = "unsupported_capability",
            [CalendarCollectionCreateCode.PayloadTooLarge] = "payload_too_large",
            [CalendarCollectionCreateCode.UpstreamUnauthorized] = "upstream_unauthorized",
            [CalendarCollectionCreateCode.UpstreamForbidden] = "upstream_forbidden",
            [CalendarCollectionCreateCode.UpstreamRateLimited] = "upstream_rate_limited",
            [CalendarCollectionCreateCode.UpstreamUnavailable] = "upstream_unavailable",
            [CalendarCollectionCreateCode.UpstreamProtocolError] = "upstream_protocol_error",
            [CalendarCollectionCreateCode.CommittedButUnverified] = "committed_but_unverified",
            [CalendarCollectionCreateCode.Indeterminate] = "indeterminate"
        };

        foreach (var (code, expectedCode) in cases)
        {
            var module = Substitute.For<ICalendarCollectionModule>();
            module.CreateAsync(Arg.Any<CalendarCollectionCreateRequest>(), Arg.Any<CancellationToken>())
                .Returns(new CalendarCollectionCreateResult(code, StateFor(code)));
            var sut = CreateTool(module, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));

            var result = await sut.CreateRawAsync(CreateArguments(), CancellationToken.None);

            result.IsError.ShouldBe(true);
            result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
        }
    }

    [Fact]
    public async Task DeleteRawAsync_MapsEveryReviewedOutcomeToCatalogCode()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var cases = new Dictionary<CalendarCollectionDeleteCode, string>
        {
            [CalendarCollectionDeleteCode.InvalidInput] = "invalid_input",
            [CalendarCollectionDeleteCode.NotFound] = "not_found",
            [CalendarCollectionDeleteCode.OutsideScope] = "outside_scope",
            [CalendarCollectionDeleteCode.Conflict] = "conflict",
            [CalendarCollectionDeleteCode.UnsupportedCapability] = "unsupported_capability",
            [CalendarCollectionDeleteCode.PayloadTooLarge] = "payload_too_large",
            [CalendarCollectionDeleteCode.UpstreamUnauthorized] = "upstream_unauthorized",
            [CalendarCollectionDeleteCode.UpstreamForbidden] = "upstream_forbidden",
            [CalendarCollectionDeleteCode.UpstreamRateLimited] = "upstream_rate_limited",
            [CalendarCollectionDeleteCode.UpstreamUnavailable] = "upstream_unavailable",
            [CalendarCollectionDeleteCode.UpstreamProtocolError] = "upstream_protocol_error",
            [CalendarCollectionDeleteCode.ConfirmationMismatch] = "confirmation_mismatch",
            [CalendarCollectionDeleteCode.CommittedButUnverified] = "committed_but_unverified",
            [CalendarCollectionDeleteCode.Indeterminate] = "indeterminate"
        };

        foreach (var (code, expectedCode) in cases)
        {
            var module = Substitute.For<ICalendarCollectionModule>();
            module.ReviewDeleteAsync(Arg.Any<CalendarCollectionDeleteRequest>(), Arg.Any<CancellationToken>())
                .Returns(new CalendarCollectionDeleteReviewResult(
                    new CalendarCollectionDeleteResult(code, StateFor(code)),
                    null,
                    null));
            var sut = CreateTool(module, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));

            var result = await sut.DeleteRawAsync(DeleteArguments(href), null, null, true, CancellationToken.None);

            result.IsError.ShouldBe(true);
            result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
        }
    }

    [Fact]
    public async Task DeleteRawAsync_FirstRoundReviewsExactHrefAndRequiresConfirmation()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var module = Substitute.For<ICalendarCollectionModule>();
        var descriptor = Descriptor(href);
        var binding = new CalendarCollectionDeleteReviewBinding(href, "digest");
        module.ReviewDeleteAsync(Arg.Any<CalendarCollectionDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarCollectionDeleteReviewResult(null, binding, descriptor));
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(module, time);

        var exception = await Should.ThrowAsync<InputRequiredException>(() => sut.DeleteRawAsync(
            new Dictionary<string, JsonElement>
            {
                ["href"] = JsonSerializer.SerializeToElement(href)
            },
            null,
            null,
            true,
            CancellationToken.None));

        exception.Result.RequestState.ShouldNotBeNullOrWhiteSpace();
        var inputRequests = exception.Result.InputRequests!;
        inputRequests.ShouldContainKey("confirm_delete");
        inputRequests["confirm_delete"].ElicitationParams!.Message.ShouldContain(href);
        await module.DidNotReceive().ExecuteConfirmedDeleteAsync(
            Arg.Any<CalendarCollectionDeleteRequest>(),
            Arg.Any<CalendarCollectionDeleteReviewBinding>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_AcceptedContinuationExecutesOnlyAfterStateMatches()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var module = Substitute.For<ICalendarCollectionModule>();
        var descriptor = Descriptor(href);
        var binding = new CalendarCollectionDeleteReviewBinding(href, "digest");
        module.ReviewDeleteAsync(Arg.Any<CalendarCollectionDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarCollectionDeleteReviewResult(null, binding, descriptor));
        module.ExecuteConfirmedDeleteAsync(
                Arg.Any<CalendarCollectionDeleteRequest>(),
                Arg.Any<CalendarCollectionDeleteReviewBinding>(),
                Arg.Any<CancellationToken>())
            .Returns(CalendarCollectionDeleteResult.Success(descriptor));
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(module, time);
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.DeleteRawAsync(
            new Dictionary<string, JsonElement>
            {
                ["href"] = JsonSerializer.SerializeToElement(href)
            },
            null,
            null,
            true,
            CancellationToken.None));

        var result = await sut.DeleteRawAsync(
            new Dictionary<string, JsonElement>
            {
                ["href"] = JsonSerializer.SerializeToElement(href)
            },
            first.Result.RequestState,
            new Dictionary<string, InputResponse>
            {
                ["confirm_delete"] = InputResponse.FromElicitResult(new ElicitResult
                {
                    Action = "accept",
                    Content = new Dictionary<string, JsonElement>
                    {
                        ["confirm"] = JsonSerializer.SerializeToElement(true)
                    }
                })
            },
            true,
            CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("success");
        await module.Received(1).ExecuteConfirmedDeleteAsync(
            new CalendarCollectionDeleteRequest(href),
            binding,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_MapsConfirmedExecutionFailure()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var module = Substitute.For<ICalendarCollectionModule>();
        var descriptor = Descriptor(href);
        var binding = new CalendarCollectionDeleteReviewBinding(href, "digest");
        module.ReviewDeleteAsync(Arg.Any<CalendarCollectionDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarCollectionDeleteReviewResult(null, binding, descriptor));
        module.ExecuteConfirmedDeleteAsync(
                Arg.Any<CalendarCollectionDeleteRequest>(),
                Arg.Any<CalendarCollectionDeleteReviewBinding>(),
                Arg.Any<CancellationToken>())
            .Returns(new CalendarCollectionDeleteResult(
                CalendarCollectionDeleteCode.ConfirmationMismatch,
                CalendarMutationState.NotAttempted,
                descriptor));
        var sut = CreateTool(module, new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z")));
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.DeleteRawAsync(
            DeleteArguments(href), null, null, true, CancellationToken.None));

        var result = await sut.DeleteRawAsync(
            DeleteArguments(href),
            first.Result.RequestState,
            new Dictionary<string, InputResponse>
            {
                ["confirm_delete"] = InputResponse.FromElicitResult(new ElicitResult
                {
                    Action = "accept",
                    Content = new Dictionary<string, JsonElement>
                    {
                        ["confirm"] = JsonSerializer.SerializeToElement(true)
                    }
                })
            },
            true,
            CancellationToken.None);

        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("confirmation_mismatch");
    }

    private static CalendarCollectionTools CreateTool(
        ICalendarCollectionModule module,
        TimeProvider time,
        CalendarMutationAdmission? admission = null) => new(
            module,
            new CalendarMutationRequestStateProtector(
                time,
                Options.Create(new CalDavOptions
                {
                    BaseUrl = "https://cal.example",
                    Username = "user",
                    Password = "password"
                }),
                Enumerable.Range(0, 64).Select(value => (byte)value).ToArray()),
            time,
            admission ?? new CalendarMutationAdmission(time));

    private static Dictionary<string, JsonElement> CreateArguments(
        string displayName = "Planning",
        IReadOnlyList<string>? entityKinds = null) => new()
    {
        ["displayName"] = JsonSerializer.SerializeToElement(displayName),
        ["entityKinds"] = JsonSerializer.SerializeToElement(entityKinds ?? ["event"])
    };

    private static Dictionary<string, JsonElement> DeleteArguments(string href) => new()
    {
        ["href"] = JsonSerializer.SerializeToElement(href)
    };

    private static CalendarMutationState StateFor(CalendarCollectionCreateCode code) => code switch
    {
        CalendarCollectionCreateCode.CommittedButUnverified => CalendarMutationState.Committed,
        CalendarCollectionCreateCode.Indeterminate or CalendarCollectionCreateCode.UpstreamUnavailable => CalendarMutationState.Unknown,
        _ => CalendarMutationState.NotCommitted
    };

    private static CalendarMutationState StateFor(CalendarCollectionDeleteCode code) => code switch
    {
        CalendarCollectionDeleteCode.CommittedButUnverified => CalendarMutationState.Committed,
        CalendarCollectionDeleteCode.Indeterminate or CalendarCollectionDeleteCode.UpstreamUnavailable => CalendarMutationState.Unknown,
        _ => CalendarMutationState.NotCommitted
    };

    private static CalendarDescriptor Descriptor(
        string href,
        bool eventKind = false,
        bool todo = true) => new()
    {
        Href = href,
        DisplayName = "Tasks",
        DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
        EventSupport = eventKind ? EntityKindSupport.Advertised : EntityKindSupport.NotAdvertised,
        TodoSupport = todo ? EntityKindSupport.Advertised : EntityKindSupport.NotAdvertised
    };

    private sealed class AdvancingTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
