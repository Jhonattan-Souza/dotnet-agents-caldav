using DotnetAgents.CalDav.Core.Configuration;

namespace DotnetAgents.CalDav.Mcp.Hosting;

/// <summary>
/// Maps environment variables to <see cref="CalDavOptions"/> configuration.
/// Accepts an optional <see cref="Func{T,TResult}"/> for environment variable retrieval
/// so tests can inject fakes without touching <see cref="Environment.GetEnvironmentVariable"/>.
/// </summary>
public static class CalDavEnvironmentMapper
{
    /// <summary>
    /// Creates a configure action that reads CALDAV_URL, CALDAV_USERNAME,
    /// CALDAV_PASSWORD, CALDAV_TASK_LISTS, CALDAV_DEFAULT_TASK_LIST, and CALDAV_EXPOSE_ADVANCED_TOOLS
    /// from environment variables.
    /// </summary>
    /// <param name="envProvider">
    /// Override for environment variable access. Defaults to <see cref="Environment.GetEnvironmentVariable"/>.
    /// </param>
    /// <returns>An <see cref="Action{CalDavOptions}"/> suitable for <c>ConfigureCalDav</c>.</returns>
    public static Action<CalDavOptions> MapFromEnvironment(Func<string, string?>? envProvider = null)
    {
        var getEnv = envProvider ?? Environment.GetEnvironmentVariable;
        return options =>
        {
            options.BaseUrl = getEnv("CALDAV_URL") ?? string.Empty;
            options.Username = getEnv("CALDAV_USERNAME") ?? string.Empty;
            options.Password = getEnv("CALDAV_PASSWORD") ?? string.Empty;
            options.TaskLists = getEnv("CALDAV_TASK_LISTS");
            options.DefaultTaskList = getEnv("CALDAV_DEFAULT_TASK_LIST");
            options.ExposeAdvancedTools = bool.TryParse(getEnv("CALDAV_EXPOSE_ADVANCED_TOOLS"), out var exposeAdvanced) && exposeAdvanced;
        };
    }
}