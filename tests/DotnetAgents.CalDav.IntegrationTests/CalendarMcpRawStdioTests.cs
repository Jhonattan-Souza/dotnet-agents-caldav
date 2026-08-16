using System.Diagnostics;
using System.Text.Json;
using DotnetAgents.CalDav.Mcp.Tools;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

/// <summary>Exercises raw JSON evidence that the SDK's dictionary client cannot represent.</summary>
public sealed class CalendarMcpRawStdioTests
{
    [Fact]
    public async Task CalendarEntityQuery_NormalInvalidArgumentsReturnTypedInvalidInputBeforeNetwork()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"calendar_entities.query","arguments":{"scope":{"mode":"default"},"entityKinds":["event"],"unknown":true}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
    }

    [Fact]
    public async Task CalendarEntityQuery_SelectedNameWithInternalSpacesReachesTheService()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"calendar_entities.query","arguments":{"scope":{"mode":"selected","calendar":{"by":"name","name":"No such authorized calendar"}},"entityKinds":["todo"]}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "upstream_unavailable", "execution");
    }

    [Fact]
    public async Task CalendarEntityQuery_RootDuplicateArgumentsReturnTypedInvalidInputBeforeNetwork()
    {
        const string request = """
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"calendar_entities.query","arguments":{"scope":{"mode":"default"},"scope":{"mode":"all"},"entityKinds":["event"]}}}
            """;

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "invalid_input", "schemaLexicalDiscriminator");
    }

    [Fact]
    public async Task CalendarEntityQuery_OversizedDuplicateArgumentsPreferPayloadAdmissionFailure()
    {
        var padding = new string('x', CalendarEntityTools.MaximumArgumentBytes);
        var request = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"calendar_entities.query\",\"arguments\":{\"scope\":{\"mode\":\"default\"},\"scope\":{\"mode\":\"all\"},\"entityKinds\":[\"event\"],\"padding\":\""
            + padding
            + "\"}}}";

        var result = await InvokeRawAsync(request);

        AssertTypedError(result, "payload_too_large", "admissionAndPayload");
    }

    private static async Task<JsonElement> InvokeRawAsync(string toolRequest)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var process = StartServer();
        try
        {
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2026-07-28\",\"capabilities\":{},\"clientInfo\":{\"name\":\"raw-test\",\"version\":\"1\"}}}");
            await process.StandardInput.FlushAsync(timeout.Token);
            _ = await ReadResponseAsync(process, 1, timeout.Token);
            await process.StandardInput.WriteLineAsync(
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            await process.StandardInput.WriteLineAsync(toolRequest);
            await process.StandardInput.FlushAsync(timeout.Token);
            var response = await ReadResponseAsync(process, 2, timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            (await process.StandardError.ReadToEndAsync(timeout.Token)).ShouldBeEmpty();
            return response.GetProperty("result").Clone();
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    private static Process StartServer()
    {
        var startInfo = new ProcessStartInfo("dotnet", GetServerAssemblyPath())
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment["CALDAV_URL"] = "http://127.0.0.1:1";
        startInfo.Environment["CALDAV_USERNAME"] = "test";
        startInfo.Environment["CALDAV_PASSWORD"] = "test";
        startInfo.Environment["CALDAV_CALENDAR_HREFS"] = "http://127.0.0.1:1/calendars/test/";
        return Process.Start(startInfo)!;
    }

    private static async Task<JsonElement> ReadResponseAsync(
        Process process,
        int expectedId,
        CancellationToken cancellationToken)
    {
        while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("id", out var id) && id.GetInt32() == expectedId)
                return document.RootElement.Clone();
        }
        throw new InvalidOperationException("The MCP server closed stdout before returning the expected response.");
    }

    private static void AssertTypedError(JsonElement result, string code, string phase)
    {
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("code").GetString().ShouldBe(code);
        structured.GetProperty("phase").GetString().ShouldBe(phase);
        structured.TryGetProperty("items", out _).ShouldBeFalse();
    }

    private static string GetServerAssemblyPath()
    {
        var directory = AppContext.BaseDirectory;
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory,
                "src",
                "DotnetAgents.CalDav.Mcp",
                "bin",
                "Release",
                "net10.0",
                "DotnetAgents.CalDav.Mcp.dll");
            if (File.Exists(candidate))
                return candidate;
            directory = Directory.GetParent(directory)?.FullName;
        }
        throw new FileNotFoundException("Could not locate the built MCP server assembly.");
    }
}
