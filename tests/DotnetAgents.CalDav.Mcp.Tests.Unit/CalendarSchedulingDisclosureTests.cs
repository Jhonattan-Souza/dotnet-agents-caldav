using System.Diagnostics;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using DotnetAgents.CalDav.Mcp.Hosting;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

[Collection("TelemetryActivityCollection")]
public sealed class CalendarSchedulingDisclosureTests
{
    private static readonly string[] GovernedTools =
    [
        "events.create", "events.patch", "todos.create", "todos.patch", "todos.complete",
        "calendar_occurrences.add", "calendar_occurrences.exclude", "calendar_occurrences.restore_exclusion",
        "calendar_occurrences.cancel", "calendar_occurrences.restore_cancellation",
        "calendar_resources.delete", "calendars.delete",
        "calendar_resources.exact_create", "calendar_resources.exact_replace"
    ];

    private static readonly string[] UngovernedTools =
    [
        "calendars.list", "calendars.create", "calendars.inspect", "calendars.patch", "calendars.free_busy",
        "calendar_resources.changes", "calendar_entities.query", "calendar_occurrences.query", "todos.query",
        "calendar_resources.get", "calendar_resources.move", "calendar_resources.exact_get",
        "calendar_resources.exact_move", "unknown.tool"
    ];

    public static TheoryData<string> Governed => new(GovernedTools);

    public static TheoryData<string> Ungoverned => new(UngovernedTools);

    [Theory]
    [MemberData(nameof(Governed))]
    public void Governs_EveryWriteThatPassesTheSchedulingLock(string toolName) =>
        CalendarSchedulingDisclosure.Governs(toolName).ShouldBeTrue();

    [Theory]
    [MemberData(nameof(Ungoverned))]
    public void Governs_NoReadMetadataOrSchedulingNeutralMove(string toolName) =>
        CalendarSchedulingDisclosure.Governs(toolName).ShouldBeFalse();

    [Fact]
    public void Governs_TheCatalogWithoutGaps()
    {
        using var stream = typeof(CalendarToolContract).Assembly
            .GetManifestResourceStream("DotnetAgents.CalDav.Mcp.CalendarToolCatalog.json")!;
        using var catalog = JsonDocument.Parse(stream);
        var catalogTools = catalog.RootElement.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!);

        GovernedTools.Concat(UngovernedTools.Where(name => name != "unknown.tool"))
            .Order(StringComparer.Ordinal)
            .ShouldBe(catalogTools.Order(StringComparer.Ordinal));
    }

    // Matrix: disclosure active x mutation state x recorded server scheduling.
    [Theory]
    [InlineData(false, "committed", true, null)]
    [InlineData(false, "unknown", true, null)]
    [InlineData(true, "committed", false, "none")]
    [InlineData(true, "committed", true, "possible")]
    [InlineData(true, "unknown", false, "none")]
    [InlineData(true, "unknown", true, "possible")]
    [InlineData(true, "not_attempted", true, null)]
    [InlineData(true, "not_committed", true, null)]
    public void Apply_DisclosesOnlyWritesThatCommittedOrMayHaveCommitted(
        bool active,
        string mutationState,
        bool possible,
        string? expected)
    {
        var result = Result(new Dictionary<string, object?> { ["outcome"] = "success", ["mutationState"] = mutationState });
        var state = CalendarOperationProgress.CreateState();
        if (possible)
            state.MarkSchedulingSideEffectsPossible();

        using (CalendarOperationProgress.Attach(state))
        using (CalendarSchedulingDisclosure.Attach(active))
            CalendarSchedulingDisclosure.Apply(result);

        var structured = result.StructuredContent!.Value;
        if (expected is null)
            structured.TryGetProperty("schedulingSideEffects", out _).ShouldBeFalse();
        else
            structured.GetProperty("schedulingSideEffects").GetString().ShouldBe(expected);
        structured.GetProperty("mutationState").GetString().ShouldBe(mutationState);
    }

    [Fact]
    public void Apply_IgnoresResultsWithoutATextualMutationState()
    {
        var missing = Result(new Dictionary<string, object?> { ["outcome"] = "success" });
        var numeric = Result(new Dictionary<string, object?> { ["mutationState"] = 1 });
        var array = new CallToolResult { StructuredContent = JsonSerializer.SerializeToElement(new[] { 1 }) };
        var empty = new CallToolResult();

        using (CalendarSchedulingDisclosure.Attach(true))
        {
            CalendarSchedulingDisclosure.Apply(missing);
            CalendarSchedulingDisclosure.Apply(numeric);
            CalendarSchedulingDisclosure.Apply(array);
            CalendarSchedulingDisclosure.Apply(empty);
        }

        missing.StructuredContent!.Value.GetRawText().ShouldBe("""{"outcome":"success"}""");
        numeric.StructuredContent!.Value.GetRawText().ShouldBe("""{"mutationState":1}""");
        array.StructuredContent!.Value.GetRawText().ShouldBe("[1]");
        empty.StructuredContent.ShouldBeNull();
    }

    [Fact]
    public void Attach_RestoresThePreviousDisclosure()
    {
        CalendarSchedulingDisclosure.IsActive.ShouldBeFalse();
        using (CalendarSchedulingDisclosure.Attach(true))
        {
            CalendarSchedulingDisclosure.IsActive.ShouldBeTrue();
            using (CalendarSchedulingDisclosure.Attach(false))
                CalendarSchedulingDisclosure.IsActive.ShouldBeFalse();
            CalendarSchedulingDisclosure.IsActive.ShouldBeTrue();
        }
        CalendarSchedulingDisclosure.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void ConfirmationWarning_AppearsOnlyWhileDisclosureIsActive()
    {
        const string message = "Confirm calendar_resources.delete for href https://cal.example/e.ics.";

        CalendarSchedulingDisclosure.WithConfirmationWarning(message).ShouldBe(message);
        using (CalendarSchedulingDisclosure.Attach(true))
        {
            var warned = CalendarSchedulingDisclosure.WithConfirmationWarning(message);
            warned.ShouldStartWith(message + " Scheduling notice: ");
            warned.ShouldContain("may automatically send invitations, updates, replies, or cancellations");
            warned.ShouldContain("organizers and attendees");
        }
    }

    [Fact]
    public void FinalizeResult_KeepsCompatibilityTextIdenticalToTheDisclosedStructuredContent()
    {
        var state = CalendarOperationProgress.CreateState();
        state.MarkSchedulingSideEffectsPossible();
        CallToolResult finalized;
        using (CalendarOperationProgress.Attach(state))
        using (CalendarSchedulingDisclosure.Attach(true))
        {
            finalized = CalendarToolResult.Success(
                Result(new Dictionary<string, object?>
                {
                    ["outcome"] = "success",
                    ["mutationState"] = "committed",
                    ["diagnostics"] = Array.Empty<object>()
                }),
                CalendarMutationState.Committed).FinalizeResult();
        }

        var structured = finalized.StructuredContent!.Value;
        structured.GetProperty("schedulingSideEffects").GetString().ShouldBe("possible");
        finalized.Content.OfType<TextContentBlock>().Single().Text.ShouldBe(structured.GetRawText());
    }

    [Fact]
    public void FinalizeResult_PayloadReplacementRetainsTheDisclosure()
    {
        var oversized = Result(new Dictionary<string, object?>
        {
            ["code"] = "committed_but_unverified",
            ["message"] = new string('x', CalendarQueryToolSupport.MaximumHumanReadableBytes + 1),
            ["mutationState"] = "committed"
        });
        CallToolResult finalized;
        using (CalendarSchedulingDisclosure.Attach(true))
        {
            finalized = CalendarToolResult.Error(
                oversized,
                new CalendarStructuredErrorFacts(
                    CalendarTelemetryErrorCode.CommittedButUnverified,
                    CalendarTelemetryErrorCategory.PostWriteTruth,
                    CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation,
                    false),
                CalendarMutationState.Committed).FinalizeResult();
        }

        var structured = finalized.StructuredContent!.Value;
        structured.GetProperty("code").GetString().ShouldBe("payload_too_large");
        structured.GetProperty("mutationState").GetString().ShouldBe("committed");
        structured.GetProperty("schedulingSideEffects").GetString().ShouldBe("none");
    }

    [Theory]
    [MemberData(nameof(Governed))]
    public void OutputSchema_AcceptsTheClosedDisclosureOnCommittedAndUnknownOutcomes(string toolName)
    {
        CalendarOutputSchemaGuard.Validate(toolName, Result(ErrorBody("unknown", "none")));
        CalendarOutputSchemaGuard.Validate(toolName, Result(ErrorBody("committed", "possible")));
        Should.Throw<InvalidOperationException>(() =>
            CalendarOutputSchemaGuard.Validate(toolName, Result(ErrorBody("unknown", "maybe"))));
    }

    [Theory]
    [InlineData(null, "events.create", false)]
    [InlineData(CalDavSchedulingModes.StorageOnly, "events.create", false)]
    [InlineData(CalDavSchedulingModes.ServerManaged, "events.create", true)]
    [InlineData(CalDavSchedulingModes.ServerManaged, "calendars.delete", true)]
    [InlineData(CalDavSchedulingModes.ServerManaged, "calendar_resources.move", false)]
    [InlineData(CalDavSchedulingModes.ServerManaged, "calendars.create", false)]
    public async Task ExecutionPolicy_ActivatesDisclosureForServerManagedGovernedTools(
        string? mode,
        string toolName,
        bool expected)
    {
        var stopped = new List<Activity>();
        using var listener = ListenToOperations(stopped);
        using var services = Services(new ServiceCollection().AddSingleton(
            Options.Create(new CalDavOptions { SchedulingMode = mode })));
        await using var transport = Transport();
        await using var server = Server(transport, services);
        bool? observed = null;
        var filtered = CalendarExecutionPolicy.CallTool((_, _) =>
        {
            observed = CalendarSchedulingDisclosure.IsActive;
            CalendarOperationProgress.SetSchedulingSideEffectsPossible();
            return ValueTask.FromResult(CalendarToolResult.Success(
                Result(new Dictionary<string, object?> { ["outcome"] = "success", ["mutationState"] = "committed" }),
                CalendarMutationState.Committed).FinalizeResult());
        });

        var result = await filtered(Context(server, toolName), TestContext.Current.CancellationToken);

        observed.ShouldBe(expected);
        CalendarSchedulingDisclosure.IsActive.ShouldBeFalse();
        var structured = result.StructuredContent!.Value;
        var operation = stopped.Single(activity => activity.OperationName == "caldav.operation");
        if (expected)
        {
            structured.GetProperty("schedulingSideEffects").GetString().ShouldBe("possible");
            operation.GetTagItem("caldav.scheduling.side_effects").ShouldBe("possible");
        }
        else
        {
            structured.TryGetProperty("schedulingSideEffects", out _).ShouldBeFalse();
            operation.GetTagItem("caldav.scheduling.side_effects").ShouldBeNull();
        }
    }

    [Fact]
    public async Task ExecutionPolicy_WithoutConfiguredOptionsKeepsTheStorageOnlyResult()
    {
        using var services = Services(new ServiceCollection());
        await using var transport = Transport();
        await using var server = Server(transport, services);
        bool? observed = null;
        var filtered = CalendarExecutionPolicy.CallTool((_, _) =>
        {
            observed = CalendarSchedulingDisclosure.IsActive;
            return ValueTask.FromResult(new CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(new { mutationState = "committed" })
            });
        });

        await filtered(Context(server, "events.create"), TestContext.Current.CancellationToken);

        observed.ShouldBe(false);
    }

    private static Dictionary<string, object?> ErrorBody(string mutationState, string sideEffects) => new()
    {
        ["code"] = "indeterminate",
        ["category"] = "postWriteTruth",
        ["message"] = "The write outcome is indeterminate.",
        ["retryable"] = false,
        ["phase"] = "postWriteVerificationOrReconciliation",
        ["mutationState"] = mutationState,
        ["schedulingSideEffects"] = sideEffects
    };

    private static CallToolResult Result(Dictionary<string, object?> body) => new()
    {
        StructuredContent = JsonSerializer.SerializeToElement(body),
        Content = []
    };

    private static ServiceProvider Services(IServiceCollection services) => services
        .AddSingleton(TimeProvider.System)
        .AddSingleton<CalendarOperationAdmission>()
        .BuildServiceProvider();

    private static StreamServerTransport Transport() => new(
        new MemoryStream(),
        new MemoryStream(),
        "scheduling-disclosure-test",
        Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

    private static McpServer Server(StreamServerTransport transport, IServiceProvider services) => McpServer.Create(
        transport,
        new McpServerOptions(),
        Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
        services);

    private static RequestContext<CallToolRequestParams> Context(McpServer server, string toolName) => new(
        server,
        new JsonRpcRequest { Id = new RequestId(1L), Method = "tools/call" },
        new CallToolRequestParams { Name = toolName });

    private static ActivityListener ListenToOperations(List<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == CalendarTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
