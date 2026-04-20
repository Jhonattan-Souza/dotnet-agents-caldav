using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Services;

/// <inheritdoc/>
internal sealed class TaskListResolver : ITaskListResolver
{
    private static readonly IReadOnlyDictionary<string, string> KnownAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "task list", "Tasks" },
            { "tasks", "Tasks" },
            { "shopping list", "Shopping" },
            { "shopping", "Shopping" },
        };

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

        // Rule 1: Exact case-insensitive display name match
        if (!string.IsNullOrWhiteSpace(userInput))
        {
            var exactMatches = taskLists
                .Where(t => string.Equals(t.DisplayName, userInput, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (exactMatches.Count == 1)
            {
                return Task.FromResult(WithDefaultFlag(exactMatches[0], defaultName));
            }

            if (exactMatches.Count > 1)
            {
                throw new TaskListResolutionException(
                    userInput,
                    availableNames,
                    $"Ambiguous task list name '{userInput}' matched {exactMatches.Count} lists.");
            }

            // Rule 2: Known alias match
            var aliasResolved = TryResolveAlias(taskLists, userInput.Trim());
            if (aliasResolved is not null)
            {
                return Task.FromResult(WithDefaultFlag(aliasResolved, defaultName));
            }
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
            message);
    }

    private static TaskList? TryResolveAlias(IReadOnlyList<TaskList> taskLists, string input)
    {
        if (KnownAliases.TryGetValue(input, out var canonicalPhrase))
        {
            var matches = taskLists
                .Where(t =>
                    t.DisplayName.Contains(canonicalPhrase, StringComparison.OrdinalIgnoreCase) ||
                    t.DisplayName.Contains(input, StringComparison.OrdinalIgnoreCase))
                .ToList();

            return matches.Count switch
            {
                1 => matches[0],
                > 1 => throw new TaskListResolutionException(
                    input,
                    taskLists.Select(t => t.DisplayName).ToList(),
                    $"Task list alias '{input}' is ambiguous. Matching lists: {string.Join(", ", matches.Select(t => t.DisplayName))}."),
                _ => null
            };
        }

        return null;
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
