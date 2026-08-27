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
    internal static readonly TimeSpan SdkDefaultShutdownTimeout =
        new StdioClientTransportOptions { Command = "dotnet" }.ShutdownTimeout;

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
        CancellationToken cancellationToken = default) => ConnectCoreAsync(
            launch,
            clientOptions,
            loggerFactory,
            ShutdownTimeout,
            cancellationToken);

    internal static Task<McpClient> ConnectWithSdkDefaultShutdownAsync(
        McpStdioServerLaunch launch,
        McpClientOptions? clientOptions,
        ILoggerFactory? loggerFactory,
        CancellationToken cancellationToken) => ConnectCoreAsync(
            launch,
            clientOptions,
            loggerFactory,
            shutdownTimeout: null,
            cancellationToken);

    private static Task<McpClient> ConnectCoreAsync(
        McpStdioServerLaunch launch,
        McpClientOptions? clientOptions,
        ILoggerFactory? loggerFactory,
        TimeSpan? shutdownTimeout,
        CancellationToken cancellationToken)
    {
        var transportOptions = new StdioClientTransportOptions
        {
            Command = launch.Command,
            Arguments = [.. launch.Arguments],
            WorkingDirectory = launch.WorkingDirectory,
            InheritEnvironmentVariables = true,
            EnvironmentVariables = launch.EnvironmentVariables.ToDictionary(),
            StandardErrorLines = launch.StandardErrorLines
        };
        if (shutdownTimeout is { } explicitShutdownTimeout)
            transportOptions.ShutdownTimeout = explicitShutdownTimeout;
        var transport = new StdioClientTransport(transportOptions, loggerFactory);
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
