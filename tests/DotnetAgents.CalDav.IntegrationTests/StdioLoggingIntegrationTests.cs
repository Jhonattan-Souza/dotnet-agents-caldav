using System.Diagnostics;
using System.Reflection;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

/// <summary>
/// Verifies that the MCP server process keeps stdout clean for JSON-RPC
/// by redirecting all .NET console logging to stderr.
/// </summary>
public sealed class StdioLoggingIntegrationTests
{
    /// <summary>
    /// Launches the MCP server exe with no CalDAV env vars so that config
    /// validation fails and the process exits with code 1. Asserts that
    /// stdout is completely empty (no log lines polluting JSON-RPC) and
    /// that the validation error appears on stderr.
    /// </summary>
    [Fact]
    public async Task McpProcess_WritesNoLogLinesToStdout()
    {
        var mcpDll = GetMcpDllPath();

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = mcpDll,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Strip all CALDAV_ env vars so the process hits validation failure
        // and exits immediately — no server needed for this test.
        process.StartInfo.Environment.Remove("CALDAV_URL");
        process.StartInfo.Environment.Remove("CALDAV_USERNAME");
        process.StartInfo.Environment.Remove("CALDAV_PASSWORD");

        process.Start();

        // Close stdin so the stdio transport doesn't block waiting for input.
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(cts.Token);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

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

    /// <summary>
    /// Resolves the path to the MCP server DLL relative to the test assembly,
    /// walking up from the test bin/ to the src project bin/.
    /// </summary>
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
