using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Services;

/// <inheritdoc/>
internal sealed class TaskListResolver : ITaskListResolver
{
    private readonly IOptions<CalDavOptions> _options;

    public TaskListResolver(IOptions<CalDavOptions> options)
    {
        _options = options;
    }

    /// <inheritdoc/>
    public Task<TaskList> ResolveAsync(
        IReadOnlyList<TaskList> taskLists,
        string? userInput,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (taskLists.Count == 0)
        {
            throw new TaskListResolutionException(
                userInput ?? "(null)",
                [],
                "No task lists available on the server.");
        }

        var availableNames = taskLists.Select(t => t.DisplayName).ToList();
        var defaultName = _options.Value.DefaultTaskList;

        var resolvedFromInput = TryResolveFromUserInput(taskLists, userInput, availableNames, defaultName);
        if (resolvedFromInput is not null)
        {
            return Task.FromResult(resolvedFromInput);
        }

        // Rule 3: Configured default
        var defaultList = ResolveDefaultList(taskLists, defaultName);
        if (defaultList is not null)
        {
            return Task.FromResult(WithDefaultFlag(defaultList, defaultName));
        }

        // Rule 4: Fail with candidates
        var message = string.IsNullOrWhiteSpace(userInput)
            ? (string.IsNullOrWhiteSpace(defaultName)
                ? "No default task list configured and no list name provided."
                : $"Default task list '{defaultName}' not found.")
            : $"Task list '{userInput}' not found.";

        throw new TaskListResolutionException(
            userInput ?? "(null)",
            availableNames,
            BuildResolutionFailureMessage(userInput, defaultName));
    }

    private static TaskList? TryResolveFromUserInput(
        IReadOnlyList<TaskList> taskLists,
        string? userInput,
        IReadOnlyList<string> availableNames,
        string? defaultName)
    {
        if (string.IsNullOrWhiteSpace(userInput))
        {
            return null;
        }

        var exactMatches = taskLists
            .Where(t => string.Equals(t.DisplayName, userInput, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (exactMatches.Count == 1)
        {
            return WithDefaultFlag(exactMatches[0], defaultName);
        }

        if (exactMatches.Count > 1)
        {
            throw new TaskListResolutionException(
                userInput,
                availableNames,
                $"Ambiguous task list name '{userInput}' matched {exactMatches.Count} lists.");
        }

        var aliasMap = BuildAliasMap(taskLists);
        return aliasMap.TryGetValue(userInput.Trim(), out var aliasMatch)
            ? WithDefaultFlag(aliasMatch, defaultName)
            : null;
    }

    private static string BuildResolutionFailureMessage(string? userInput, string? defaultName)
    {
        return string.IsNullOrWhiteSpace(userInput)
            ? (string.IsNullOrWhiteSpace(defaultName)
                ? "No default task list configured and no list name provided."
                : $"Default task list '{defaultName}' not found.")
            : $"Task list '{userInput}' not found.";
    }

    private static IReadOnlyDictionary<string, TaskList> BuildAliasMap(IReadOnlyList<TaskList> taskLists)
    {
        var suffixes = new[] { " list", " tasks", " task list" };
        var aliases = new Dictionary<string, List<TaskList>>(StringComparer.OrdinalIgnoreCase);

        foreach (var list in taskLists)
        {
            var name = list.DisplayName.Trim();

            foreach (var suffix in suffixes)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length)
                {
                    var stripped = name[..^suffix.Length].Trim();
                    if (!string.IsNullOrWhiteSpace(stripped))
                    {
                        if (!aliases.TryGetValue(stripped, out var existing))
                        {
                            existing = [];
                            aliases[stripped] = existing;
                        }
                        existing.Add(list);
                    }
                }
            }

            if (name.Length > 1 && name.EndsWith('s'))
            {
                var singular = name[..^1];
                if (!aliases.TryGetValue(singular, out var existing))
                {
                    existing = [];
                    aliases[singular] = existing;
                }
                existing.Add(list);
            }
        }

        return aliases
            .Where(kvp => kvp.Value.Count == 1)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value[0], StringComparer.OrdinalIgnoreCase);
    }

    private static TaskList? ResolveDefaultList(IReadOnlyList<TaskList> taskLists, string? defaultName)
    {
        if (string.IsNullOrWhiteSpace(defaultName))
        {
            return null;
        }

        return taskLists
            .FirstOrDefault(t => string.Equals(t.DisplayName, defaultName, StringComparison.OrdinalIgnoreCase));
    }

    private static TaskList WithDefaultFlag(TaskList list, string? defaultName) =>
        list with { IsDefault = !string.IsNullOrWhiteSpace(defaultName) && string.Equals(list.DisplayName, defaultName, StringComparison.OrdinalIgnoreCase) };
}
