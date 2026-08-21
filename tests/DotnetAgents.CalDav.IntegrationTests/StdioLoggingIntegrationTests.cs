using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using DotnetAgents.CalDav.IntegrationTests.Fixtures;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

/// <summary>
/// Verifies that the MCP server process keeps stdio clean for JSON-RPC.
/// </summary>
[Collection("RadicaleCollection")]
public sealed class StdioLoggingIntegrationTests
{
    private readonly RadicaleFixture _fixture;

    public StdioLoggingIntegrationTests(RadicaleFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Launches the MCP server exe with no CalDAV env vars so that config
    /// validation fails and the process exits with code 1. Asserts that
    /// stdout is completely empty (no log lines polluting JSON-RPC) and
    /// that the validation error appears on stderr.
    /// </summary>
    [Fact]
    public async Task McpProcess_WithInvalidConfig_WritesNoLogLinesToStdout()
    {
        using var process = CreateProcess();

        // Strip all CALDAV_ env vars so the process hits validation failure
        // and exits immediately — no server needed for this test.
        process.StartInfo.Environment.Remove("CALDAV_URL");
        process.StartInfo.Environment.Remove("CALDAV_USERNAME");
        process.StartInfo.Environment.Remove("CALDAV_PASSWORD");

        var (stdout, stderr) = await RunProcessToCompletionAsync(process);

        // The process should exit with code 1 (config validation failure).
        process.ExitCode.ShouldBe(1, $"stderr was: {stderr}");

        // stdout MUST be completely empty — any non-JSON-RPC line here
        // would break MCP clients parsing the stdio transport.
        stdout.ShouldBeEmpty(
            $"stdout must be empty for JSON-RPC, but contained:\n{stdout}");

        // stderr should contain the validation error, confirming logs
        // were correctly redirected.
        stderr.ShouldContain("CalDAV configuration error");
    }

    [Fact]
    public async Task McpProcess_WithValidConfig_WritesNoConsoleLogsToStdoutOrStderr()
    {
        using var process = CreateProcess();
        _fixture.ConfigureCalDavEnvironment(process.StartInfo.Environment);

        var (stdout, stderr) = await RunProcessToCompletionAsync(process);

        process.ExitCode.ShouldBe(0,
            $"stdout was: {stdout}\nstderr was: {stderr}");
        stdout.ShouldBeEmpty(
            $"stdout must remain reserved for JSON-RPC messages, but contained:\n{stdout}");
        stderr.ShouldBeEmpty(
            $"stderr must remain empty for stdio MCP compatibility, but contained:\n{stderr}");
    }

    /// <summary>
    /// Verifies that the MCP server exits promptly after the client closes stdin.
    /// A stdio MCP server should detect EOF and shut down within a reasonable time
    /// so that stale processes do not accumulate when the client reconnects.
    /// </summary>
    [Fact]
    public async Task McpProcess_WithValidConfig_ExitsWithinTwoSecondsAfterStdinCloses()
    {
        using var process = CreateProcess();
        _fixture.ConfigureCalDavEnvironment(process.StartInfo.Environment);

        process.Start();

        // Start readers before closing stdin so they are active during the
        // shutdown window and can capture any race output from the transport.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        // Act: close stdin and measure how long the process takes to exit.
        // The timestamp is captured after process.Start() so JIT/startup time
        // is excluded; only the stdin-EOF to process-exit delay is measured.
        var beforeClose = DateTimeOffset.UtcNow;
        process.StandardInput.Close();

        await WaitForExitWithTimeoutAsync(process, TestContext.Current.CancellationToken);

        var elapsed = DateTimeOffset.UtcNow - beforeClose;
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // Assert: clean exit first, then timing, then stdout cleanliness.
        process.ExitCode.ShouldBe(0,
            $"stdout: {stdout}\nstderr: {stderr}");
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2),
            $"MCP server took too long to exit after stdin closed. " +
            $"stdout: {stdout}\nstderr: {stderr}");
        stdout.ShouldBeEmpty(
            $"stdout must remain empty for JSON-RPC, but contained:\n{stdout}");
    }

    [Fact]
    public async Task McpProcess_WithUnreachableOtlpEndpoint_StaysCleanAndExitsWithinTwoSeconds()
    {
        using var process = CreateProcess();
        _fixture.ConfigureCalDavEnvironment(process.StartInfo.Environment);
        process.StartInfo.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1";
        process.StartInfo.Environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";

        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var response = await CallCalendarsListAsync(process);
        response.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeFalse();
        var beforeClose = DateTimeOffset.UtcNow;
        process.StandardInput.Close();

        await WaitForExitWithTimeoutAsync(process, TestContext.Current.CancellationToken);

        var elapsed = DateTimeOffset.UtcNow - beforeClose;
        var stdout = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = await stderrTask;
        process.ExitCode.ShouldBe(0, $"stdout: {stdout}\nstderr: {stderr}");
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2),
            $"Unreachable OTLP endpoint delayed stdin-EOF shutdown. stdout: {stdout}\nstderr: {stderr}");
        stdout.ShouldBeEmpty();
        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task McpProcess_WithHangingOtlpCollector_PreservesToolResultAndTwoSecondShutdown()
    {
        await using var receiver = OtlpLoopbackReceiver.Start(respond: false);
        using var process = CreateProcess();
        _fixture.ConfigureCalDavEnvironment(process.StartInfo.Environment);
        process.StartInfo.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] =
            receiver.Endpoint.GetLeftPart(UriPartial.Authority);
        process.StartInfo.Environment["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
        process.StartInfo.Environment["OTEL_BSP_SCHEDULE_DELAY"] = "50";
        process.StartInfo.Environment["OTEL_BLRP_SCHEDULE_DELAY"] = "50";
        process.StartInfo.Environment["OTEL_METRIC_EXPORT_INTERVAL"] = "50";
        process.StartInfo.Environment["OTEL_EXPORTER_OTLP_TIMEOUT"] = "10000";

        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        var response = await CallCalendarsListAsync(process);
        response.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeFalse();
        (await receiver.WaitForRequestAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();

        var beforeClose = DateTimeOffset.UtcNow;
        process.StandardInput.Close();
        await WaitForExitWithTimeoutAsync(process, TestContext.Current.CancellationToken);

        var elapsed = DateTimeOffset.UtcNow - beforeClose;
        var stdout = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = await stderrTask;
        process.ExitCode.ShouldBe(0, $"stdout: {stdout}\nstderr: {stderr}");
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2),
            $"A collector that accepted but did not answer delayed stdin-EOF shutdown. " +
            $"stdout: {stdout}\nstderr: {stderr}");
        stdout.ShouldBeEmpty();
        stderr.ShouldBeEmpty();
    }

    /// <summary>
    /// Resolves the path to the MCP server DLL relative to the test assembly,
    /// walking up from the test bin/ to the src project bin/.
    /// </summary>
    private static async Task<(string Stdout, string Stderr)> RunProcessToCompletionAsync(Process process)
    {
        process.Start();

        // Close stdin so the stdio transport can observe EOF and shut down.
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        await WaitForExitWithTimeoutAsync(process, TestContext.Current.CancellationToken);

        return (await stdoutTask, await stderrTask);
    }

    /// <summary>
    /// Waits for the process to exit with a 15-second safety timeout.
    /// If the timeout fires, the process (and its children) are killed
    /// so tests do not leak orphan processes.
    /// </summary>
    private static async Task WaitForExitWithTimeoutAsync(Process process, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private static async Task<JsonElement> CallCalendarsListAsync(Process process)
    {
        await process.StandardInput.WriteLineAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2026-07-28\",\"capabilities\":{},\"clientInfo\":{\"name\":\"otlp-failure-test\",\"version\":\"1\"}}}");
        await process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
        _ = await ReadResponseAsync(process, 1);
        await process.StandardInput.WriteLineAsync(
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        await process.StandardInput.WriteLineAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"calendars.list\",\"arguments\":{}}}");
        await process.StandardInput.FlushAsync(TestContext.Current.CancellationToken);
        return await ReadResponseAsync(process, 2);
    }

    private static async Task<JsonElement> ReadResponseAsync(Process process, int expectedId)
    {
        while (await process.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            if (message.TryGetProperty("id", out var id) && id.GetInt32() == expectedId)
                return message.Clone();
        }
        throw new EndOfStreamException($"MCP process ended before response {expectedId}.");
    }

    private static Process CreateProcess()
    {
        var mcpDll = GetMcpDllPath();

        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = mcpDll,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
    }

    private static string GetMcpDllPath()
    {
        // The MCP project builds to:
        //   src/DotnetAgents.CalDav.Mcp/bin/{Config}/{TFM}/DotnetAgents.CalDav.Mcp.dll
        // The test assembly is at:
        //   tests/DotnetAgents.CalDav.IntegrationTests/bin/{Config}/{TFM}/...
        var testAssemblyDir = Path.GetDirectoryName(
            Assembly.GetExecutingAssembly().Location)!;

        // Walk up: bin/{Config}/{TFM} → bin/{Config} → bin → project → tests → root
        var repoRoot = Path.GetFullPath(
            Path.Combine(testAssemblyDir, "..", "..", "..", "..", ".."));

        // Determine configuration and TFM from test assembly path
        var tfm = Path.GetFileName(testAssemblyDir); // e.g. "net10.0"
        var config = Path.GetFileName(
            Path.GetDirectoryName(testAssemblyDir)!); // e.g. "Release"

        var mcpDll = Path.Combine(repoRoot,
            "src", "DotnetAgents.CalDav.Mcp",
            "bin", config, tfm,
            "DotnetAgents.CalDav.Mcp.dll");

        if (!File.Exists(mcpDll))
        {
            throw new FileNotFoundException(
                $"MCP server DLL not found at: {mcpDll}. " +
                "Ensure the MCP project is built before running integration tests.");
        }

        return mcpDll;
    }
}
