using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace DotnetAgents.CalDav.IntegrationTests.Fixtures;

internal sealed record McpStdioServerLaunch(
    string Command,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> EnvironmentVariables,
    Action<string>? StandardErrorLines = null);

internal static class McpStdioClientFactory
{
    internal static readonly TimeSpan ShutdownTimeout = TimeSpan.FromMilliseconds(250);

    public static McpStdioServerLaunch CreateBuiltServerLaunch(
        IReadOnlyDictionary<string, string?> environmentVariables,
        Action<string>? standardErrorLines = null) => new(
            "dotnet",
            [GetBuiltServerAssemblyPath()],
            AppContext.BaseDirectory,
            environmentVariables,
            standardErrorLines);

    public static Task<McpClient> ConnectAsync(
        McpStdioServerLaunch launch,
        McpClientOptions? clientOptions = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = launch.Command,
            Arguments = [.. launch.Arguments],
            WorkingDirectory = launch.WorkingDirectory,
            InheritEnvironmentVariables = true,
            EnvironmentVariables = launch.EnvironmentVariables.ToDictionary(),
            StandardErrorLines = launch.StandardErrorLines,
            ShutdownTimeout = ShutdownTimeout
        }, loggerFactory);
        return McpClient.CreateAsync(transport, clientOptions, loggerFactory, cancellationToken);
    }

    private static string GetBuiltServerAssemblyPath()
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
