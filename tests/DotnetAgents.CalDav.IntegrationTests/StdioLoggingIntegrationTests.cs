using System.Diagnostics;
using System.Reflection;
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

        var stdoutTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        // Act: close stdin and measure how long the process takes to exit
        var beforeClose = DateTimeOffset.UtcNow;
        process.StandardInput.Close();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(cts.Token);
        var elapsed = DateTimeOffset.UtcNow - beforeClose;

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        // Assert: the process should exit within 2 seconds of stdin closing
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2),
            $"MCP server took too long to exit after stdin closed. " +
            $"stdout: {stdout}\nstderr: {stderr}");

        process.ExitCode.ShouldBe(0,
            $"stdout: {stdout}\nstderr: {stderr}");
        stdout.ShouldBeEmpty(
            $"stdout must remain empty for JSON-RPC, but contained:\n{stdout}");
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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(cts.Token);

        return (await stdoutTask, await stderrTask);
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
