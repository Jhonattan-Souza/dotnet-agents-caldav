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
public sealed class CalendarConfirmationPolicyTests
{
    public static TheoryData<string> ConfirmingTools => new(
        "calendars.delete",
        "calendar_resources.delete",
        "events.patch",
        "todos.patch",
        "calendar_resources.exact_create",
        "calendar_resources.exact_replace",
        "calendar_resources.exact_move");

    [Theory]
    [InlineData(null, false, true)]
    [InlineData(null, true, true)]
    [InlineData(CalDavConfirmationPolicies.Always, false, true)]
    [InlineData(CalDavConfirmationPolicies.Always, true, true)]
    [InlineData(CalDavConfirmationPolicies.DestructiveScope, false, false)]
    [InlineData(CalDavConfirmationPolicies.DestructiveScope, true, true)]
    [InlineData(CalDavConfirmationPolicies.Never, false, false)]
    [InlineData(CalDavConfirmationPolicies.Never, true, false)]
    public void RequiresConfirmation_FollowsTheAttachedPolicy(string? policy, bool destructiveScope, bool expected)
    {
        using var attached = CalendarConfirmationPolicy.Attach(policy);

        CalendarConfirmationPolicy.RequiresConfirmation(destructiveScope).ShouldBe(expected);
    }

    [Fact]
    public void RequiresConfirmation_WithoutAnAttachedPolicyConfirmsEverything()
    {
        CalendarConfirmationPolicy.Current.ShouldBe(CalDavConfirmationPolicies.Always);
        CalendarConfirmationPolicy.RequiresConfirmation(destructiveScope: false).ShouldBeTrue();
    }

    [Theory]
    [InlineData(true, "committed", true)]
    [InlineData(true, "unknown", true)]
    [InlineData(true, "not_attempted", false)]
    [InlineData(true, "not_committed", false)]
    [InlineData(false, "committed", false)]
    public void Apply_DisclosesOnlyASkippedConfirmationOnAWriteThatMayHaveCommitted(
        bool skipped,
        string mutationState,
        bool expected)
    {
        var result = Result(new Dictionary<string, object?> { ["outcome"] = "success", ["mutationState"] = mutationState });

        using (skipped ? CalendarConfirmationPolicy.Skip() : null)
            CalendarConfirmationPolicy.Apply(result);

        var disclosed = result.StructuredContent!.Value.TryGetProperty("confirmation", out var value);
        disclosed.ShouldBe(expected);
        if (expected)
            value.GetString().ShouldBe("skipped_by_policy");
    }

    [Fact]
    public void Skip_EndsWithItsScope()
    {
        using (CalendarConfirmationPolicy.Skip())
        {
        }
        var result = Result(new Dictionary<string, object?> { ["outcome"] = "success", ["mutationState"] = "committed" });

        CalendarConfirmationPolicy.Apply(result);

        result.StructuredContent!.Value.TryGetProperty("confirmation", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null, CalDavConfirmationPolicies.Always)]
    [InlineData("", CalDavConfirmationPolicies.Always)]
    [InlineData(CalDavConfirmationPolicies.Always, CalDavConfirmationPolicies.Always)]
    [InlineData(CalDavConfirmationPolicies.DestructiveScope, CalDavConfirmationPolicies.DestructiveScope)]
    [InlineData(CalDavConfirmationPolicies.Never, CalDavConfirmationPolicies.Never)]
    public async Task ExecutionPolicy_AttachesTheConfiguredPolicyForTheCall(string? configured, string expected)
    {
        using var services = Services(new ServiceCollection().AddSingleton(
            Options.Create(new CalDavOptions { ConfirmationPolicy = configured })));
        await using var transport = Transport();
        await using var server = Server(transport, services);
        string? observed = null;
        var filtered = CalendarExecutionPolicy.CallTool((_, _) =>
        {
            observed = CalendarConfirmationPolicy.Current;
            return ValueTask.FromResult(CalendarToolResult.Success(
                Result(new Dictionary<string, object?> { ["outcome"] = "success", ["mutationState"] = "committed" }),
                CalendarMutationState.Committed).FinalizeResult());
        });

        await filtered(Context(server, "calendar_resources.delete"), TestContext.Current.CancellationToken);

        observed.ShouldBe(expected);
        CalendarConfirmationPolicy.Current.ShouldBe(CalDavConfirmationPolicies.Always);
    }

    [Fact]
    public async Task ExecutionPolicy_WithoutConfiguredOptionsKeepsEveryConfirmation()
    {
        using var services = Services(new ServiceCollection());
        await using var transport = Transport();
        await using var server = Server(transport, services);
        bool? observed = null;
        var filtered = CalendarExecutionPolicy.CallTool((_, _) =>
        {
            observed = CalendarConfirmationPolicy.RequiresConfirmation(destructiveScope: false);
            return ValueTask.FromResult(new CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(new { mutationState = "committed" })
            });
        });

        await filtered(Context(server, "calendar_resources.delete"), TestContext.Current.CancellationToken);

        observed.ShouldBe(true);
    }

    [Theory]
    [MemberData(nameof(ConfirmingTools))]
    public void OutputSchema_AcceptsTheClosedSkipDisclosureOnUncertainOutcomes(string toolName)
    {
        CalendarOutputSchemaGuard.Validate(toolName, Result(ErrorBody("unknown", "skipped_by_policy")));
        CalendarOutputSchemaGuard.Validate(toolName, Result(ErrorBody("committed", "skipped_by_policy")));
        Should.Throw<InvalidOperationException>(() =>
            CalendarOutputSchemaGuard.Validate(toolName, Result(ErrorBody("unknown", "skipped"))));
    }

    private static Dictionary<string, object?> ErrorBody(string mutationState, string confirmation) => new()
    {
        ["code"] = "indeterminate",
        ["category"] = "postWriteTruth",
        ["message"] = "The write outcome is indeterminate.",
        ["retryable"] = false,
        ["phase"] = "postWriteVerificationOrReconciliation",
        ["mutationState"] = mutationState,
        ["confirmation"] = confirmation
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
        "confirmation-policy-test",
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
}
