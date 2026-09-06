using System.Net;
using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.DependencyInjection;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using DotnetAgents.CalDav.Mcp.Hosting;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed partial class CalendarProtocolFailurePhaseTests
{
    private const string Href = "https://cal.example/cal/";
    private const string Metadata = """
        <d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav">
          <d:response><d:href>/cal/</d:href><d:propstat><d:prop>
            <d:resourcetype><d:collection/><c:calendar/></d:resourcetype><d:displayname>Work</d:displayname>
          </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        </d:multistatus>
        """;

    [Theory]
    [InlineData("calendars.inspect", 403)]
    [InlineData("calendars.inspect", 503)]
    [InlineData("calendars.inspect", 207)]
    [InlineData("calendars.free_busy", 403)]
    [InlineData("calendars.free_busy", 503)]
    [InlineData("calendars.free_busy", 200)]
    [InlineData("calendar_resources.changes", 403)]
    [InlineData("calendar_resources.changes", 503)]
    [InlineData("calendar_resources.changes", 207)]
    [InlineData("calendars.patch", 403)]
    [InlineData("calendars.patch", 503)]
    [InlineData("calendars.patch", 207)]
    public async Task Authorized_protocol_http_or_body_failure_reports_execution(string tool, int status)
    {
        using var fixture = new Fixture(_ => Response(status, "malformed protocol body",
            tool == "calendars.free_busy" ? "text/calendar" : "application/xml"));

        var result = await fixture.CallAsync(tool);

        AssertPhase(tool, result, "execution");
        CalendarOperationProgress.CurrentPhase.ShouldBeNull();
        if (tool == "calendars.patch")
            result.StructuredContent!.Value.GetProperty("mutationState").GetString().ShouldBe("not_attempted");
    }

    [Theory]
    [InlineData("calendars.inspect")]
    [InlineData("calendars.free_busy")]
    [InlineData("calendar_resources.changes")]
    [InlineData("calendars.patch")]
    public async Task Unrestricted_discovery_failure_precedes_execution(string tool)
    {
        using var fixture = new Fixture(_ => Response(403, string.Empty), explicitScope: false);

        var result = await fixture.CallAsync(tool);

        AssertPhase(tool, result, "selectionDiscoveryCapability");
        fixture.Methods.ShouldAllBe(method => method == "PROPFIND");
        CalendarOperationProgress.CurrentPhase.ShouldBeNull();
    }

    [Theory]
    [InlineData(403, "not_committed", "execution")]
    [InlineData(503, "unknown", "postWriteVerificationOrReconciliation")]
    [InlineData(207, "unknown", "postWriteVerificationOrReconciliation")]
    public async Task Patch_distinguishes_dispatch_rejection_from_reconciliation(int status, string state, string phase)
    {
        using var fixture = new Fixture(request => request.Method.Method == "PROPFIND"
            ? Response(207, Metadata)
            : Response(status, "malformed protocol body"));

        var result = await fixture.CallAsync("calendars.patch");

        AssertPhase("calendars.patch", result, phase);
        result.StructuredContent!.Value.GetProperty("mutationState").GetString().ShouldBe(state);
        fixture.Methods.Count(method => method == "PROPPATCH").ShouldBe(1);
        CalendarOperationProgress.CurrentPhase.ShouldBeNull();
    }

    [Fact]
    public async Task Outer_operation_state_and_observer_receive_module_progress()
    {
        var observed = new List<CalendarOperationPhase>();
        var state = CalendarOperationProgress.CreateState(observed.Add);
        using var fixture = new Fixture(_ => Response(403, string.Empty));
        using (CalendarOperationProgress.Attach(state))
        {
            var result = await fixture.CallAsync("calendars.free_busy");

            AssertPhase("calendars.free_busy", result, "execution");
            CalendarOperationProgress.CurrentPhase.ShouldBe(CalendarOperationPhase.Fetch);
            observed.ShouldBe([CalendarOperationPhase.Fetch]);
            state.PhaseName.ShouldBe("fetch");
        }
        CalendarOperationProgress.CurrentPhase.ShouldBeNull();
    }

    [Fact]
    public async Task Concurrent_and_sequential_calls_do_not_share_failure_phase()
    {
        using var execution = new Fixture(_ => Response(403, string.Empty));
        using var discovery = new Fixture(_ => Response(403, string.Empty), explicitScope: false);

        var results = await Task.WhenAll(execution.CallAsync("calendars.inspect"), discovery.CallAsync("calendars.inspect"));

        AssertPhase("calendars.inspect", results[0], "execution");
        AssertPhase("calendars.inspect", results[1], "selectionDiscoveryCapability");
        CalendarOperationProgress.CurrentPhase.ShouldBeNull();
        AssertPhase("calendars.inspect", await discovery.CallAsync("calendars.inspect"), "selectionDiscoveryCapability");
    }

    [Fact]
    public async Task Successful_read_scope_is_disposed_before_the_next_call()
    {
        using var fixture = new Fixture(request => request.Method.Method == "PROPFIND"
            ? Response(207, Metadata) : Response(200, string.Empty));

        var result = await fixture.CallAsync("calendars.inspect");

        result.IsError.ShouldBe(false);
        CalendarOperationProgress.CurrentPhase.ShouldBeNull();
    }

    [Theory]
    [InlineData(CalendarOperationPhase.Discovery, "upstream_unavailable", "selectionDiscoveryCapability")]
    [InlineData(CalendarOperationPhase.Discovery, "limit_exhausted", "selectionDiscoveryCapability")]
    [InlineData(CalendarOperationPhase.Fetch, "limit_exhausted", "execution")]
    [InlineData(CalendarOperationPhase.Filter, "upstream_protocol_error", "execution")]
    [InlineData(CalendarOperationPhase.Expand, "upstream_protocol_error", "execution")]
    [InlineData(CalendarOperationPhase.Reconcile, "upstream_unavailable", "postWriteVerificationOrReconciliation")]
    [InlineData(CalendarOperationPhase.Fetch, "invalid_input", "schemaLexicalDiscriminator")]
    [InlineData(CalendarOperationPhase.Fetch, "sync_reset_required", "pagination")]
    [InlineData(CalendarOperationPhase.Fetch, "payload_too_large", "admissionAndPayload")]
    [InlineData(CalendarOperationPhase.Discovery, "indeterminate", "postWriteVerificationOrReconciliation")]
    [InlineData(CalendarOperationPhase.Discovery, "committed_but_unverified", "postWriteVerificationOrReconciliation")]
    public void Inherent_error_boundaries_take_precedence_over_operation_progress(
        CalendarOperationPhase phase, string code, string expected)
    {
        var state = CalendarOperationProgress.CreateState();
        using var scope = CalendarOperationProgress.Attach(state);
        state.AdvanceTo(phase);

        var result = CalendarProtocolToolSupport.Error(new CalendarProtocolException(code, "Bounded failure."));

        result.StructuredContent!.Value.GetProperty("phase").GetString().ShouldBe(expected);
    }

    private static void AssertPhase(string tool, CallToolResult result, string phase)
    {
        result.IsError.ShouldBe(true);
        result.StructuredContent!.Value.GetProperty("phase").GetString().ShouldBe(phase);
        CalendarOutputSchemaGuard.Validate(tool, result);
    }

    private static HttpResponseMessage Response(int status, string body, string contentType = "application/xml") => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, contentType)
    };

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _provider;
        internal List<string> Methods { get; } = [];

        internal Fixture(Func<HttpRequestMessage, HttpResponseMessage> response, bool explicitScope = true)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IHttpMessageHandlerBuilderFilter>(new HandlerFilter(new Handler(response, Methods)));
            services.AddCalDavCalendars(options =>
            {
                options.BaseUrl = "https://cal.example/";
                options.Username = "test";
                options.Password = "test";
                options.CalendarHrefs = explicitScope ? Href : string.Empty;
            });
            _provider = services.BuildServiceProvider();
        }

        internal Task<CallToolResult> CallAsync(string tool) => tool switch
        {
            "calendars.inspect" => new CalendarMetadataTools(_provider.GetRequiredService<ICalendarMetadataModule>())
                .InspectAsync(Href, CancellationToken.None),
            "calendars.patch" => PatchAsync(new CalendarMetadataPatch(new CalendarMetadataTextPatch("set", "Work"))),
            "calendars.free_busy" => new CalendarReportTools(_provider.GetRequiredService<ICalendarReportModule>())
                .FreeBusyRawAsync(Arguments(new { calendarHref = Href, from = "2026-09-05T00:00:00Z", to = "2026-09-06T00:00:00Z" }), CancellationToken.None),
            _ => new CalendarReportTools(_provider.GetRequiredService<ICalendarReportModule>())
                .ChangesRawAsync(Arguments(new { calendarHref = Href }), CancellationToken.None)
        };

        internal Task<CallToolResult> PatchAsync(CalendarMetadataPatch patch) =>
            new CalendarMetadataTools(_provider.GetRequiredService<ICalendarMetadataModule>())
                .PatchAsync(Href, patch, CancellationToken.None);

        private static Dictionary<string, JsonElement> Arguments(object value) =>
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(value))!;

        public void Dispose() => _provider.Dispose();
    }

    private sealed class HandlerFilter(HttpMessageHandler handler) : IHttpMessageHandlerBuilderFilter
    {
        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            builder.PrimaryHandler = handler;
        };
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response, List<string> methods) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            methods.Add(request.Method.Method);
            return response(request);
        }
    }
}
