using Json.Pointer;

namespace DotnetAgents.CalDav.Mcp.Hosting;

/// <summary>A failed <c>oneOf</c> or <c>anyOf</c> with no branch whose discriminator matched.</summary>
internal sealed record UnresolvedChoice(SchemaFailure Choice, IReadOnlyList<SchemaFailure> Discriminators);

/// <summary>
/// Reduces a list-format schema evaluation to the failures a caller can act on: failures inside a choice that
/// another branch satisfied are noise, and inside a failed choice only the branch whose discriminator matched
/// (or, failing that, the fewest failures) is kept.
/// </summary>
internal static class CalendarSchemaBranchSelection
{
    private static readonly HashSet<string> LeafKeywords = new(StringComparer.Ordinal)
    {
        "required", "", "type", "enum", "const", "pattern", "format", "minimum", "maximum", "exclusiveMinimum",
        "exclusiveMaximum", "minLength", "maxLength", "minItems", "maxItems", "uniqueItems", "not",
        "dependentRequired", "minProperties", "maxProperties", "multipleOf"
    };

    internal static (IReadOnlyList<SchemaFailure> Leaves, IReadOnlyList<UnresolvedChoice> Choices) Select(
        IReadOnlyList<SchemaFailure> failures)
    {
        var failedChoices = failures.Where(IsChoice)
            .GroupBy(failure => failure.Detail.EvaluationPath.ToString(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        // Resolve the deepest choices first so an outer branch is judged only by failures that survive inside it.
        var chosen = new Dictionary<string, int?>(StringComparer.Ordinal);
        foreach (var choice in failedChoices.Keys.OrderByDescending(key => key.Length))
            chosen[choice] = ChooseBranch(choice, failures, failedChoices, chosen);
        var leaves = failures.Where(failure => LeafKeywords.Contains(failure.Keyword)
                && IsOnChosenPath(failure.Detail.EvaluationPath, failedChoices, chosen))
            .ToArray();
        var choices = failedChoices
            .Where(pair => chosen[pair.Key] is null
                && IsOnChosenPath(pair.Value.Detail.EvaluationPath, failedChoices, chosen))
            .Select(pair => new UnresolvedChoice(pair.Value, Discriminators(pair.Key, pair.Value, failures)))
            .ToArray();
        return (leaves, choices);
    }

    private static bool IsChoice(SchemaFailure failure) => failure.Keyword is "oneOf" or "anyOf";

    /// <summary>
    /// A detail is relevant only if every choice it sits in failed and selected its branch. Choices at or above
    /// <paramref name="withinChoice"/> are ignored, so a branch can be scored before its outer choice is resolved.
    /// </summary>
    private static bool IsOnChosenPath(
        JsonPointer evaluationPath,
        IReadOnlyDictionary<string, SchemaFailure> failedChoices,
        IReadOnlyDictionary<string, int?> chosen,
        string? withinChoice = null)
    {
        var segments = evaluationPath.ToString().Split('/')[1..];
        for (var index = 0; index + 1 < segments.Length; index++)
        {
            if (segments[index] is not ("oneOf" or "anyOf") || !int.TryParse(segments[index + 1], out var branch))
                continue;
            var choice = Prefix(segments, index);
            if (withinChoice is not null && choice.Length <= withinChoice.Length)
                continue;
            if (!failedChoices.ContainsKey(choice) || chosen[choice] != branch)
                return false;
        }
        return true;
    }

    private static int? ChooseBranch(
        string choice,
        IReadOnlyList<SchemaFailure> failures,
        IReadOnlyDictionary<string, SchemaFailure> failedChoices,
        IReadOnlyDictionary<string, int?> chosen)
    {
        var choiceLocation = Location(choice, failures);
        var viable = Branches(choice, failures)
            .Select(group => (group.Key, Failures: group
                .Where(failure => IsOnChosenPath(failure.Detail.EvaluationPath, failedChoices, chosen, choice))
                .ToArray()))
            .Where(group => !group.Failures.Any(failure => IsDiscriminatorMismatch(failure, choiceLocation)))
            .Select(group => (Branch: group.Key, Count: group.Failures.Count(failure => LeafKeywords.Contains(failure.Keyword))))
            .OrderBy(candidate => candidate.Count)
            .ThenBy(candidate => candidate.Branch)
            .ToArray();
        return viable.Length == 0 ? null : viable[0].Branch;
    }

    private static IReadOnlyList<SchemaFailure> Discriminators(
        string choice,
        SchemaFailure choiceFailure,
        IReadOnlyList<SchemaFailure> failures)
    {
        var choiceLocation = choiceFailure.Detail.InstanceLocation.ToString();
        var branches = Branches(choice, failures).ToArray();
        var discriminators = branches
            .Select(group => group.FirstOrDefault(failure => IsDiscriminatorMismatch(failure, choiceLocation)))
            .ToArray();
        return discriminators.All(failure => failure is not null)
            && discriminators.Select(failure => failure!.Detail.InstanceLocation.ToString()).Distinct().Count() == 1
                ? discriminators.OfType<SchemaFailure>().ToArray()
                : [];
    }

    private static IEnumerable<IGrouping<int, SchemaFailure>> Branches(string choice, IReadOnlyList<SchemaFailure> failures)
    {
        foreach (var keyword in new[] { "/oneOf/", "/anyOf/" })
        {
            var prefix = choice + keyword;
            foreach (var group in failures
                         .Select(failure => (Failure: failure, Path: failure.Detail.EvaluationPath.ToString()))
                         .Where(item => item.Path.StartsWith(prefix, StringComparison.Ordinal))
                         .GroupBy(item => BranchIndex(item.Path[prefix.Length..]), item => item.Failure)
                         .Where(group => group.Key >= 0))
                yield return group;
        }
    }

    private static int BranchIndex(string rest)
    {
        var end = rest.IndexOf('/', StringComparison.Ordinal);
        return int.TryParse(end < 0 ? rest : rest[..end], out var branch) ? branch : -1;
    }

    /// <summary>A const or enum mismatch on a direct member of the choice's own object names the wrong branch.</summary>
    private static bool IsDiscriminatorMismatch(SchemaFailure failure, string choiceLocation)
    {
        if (failure.Keyword is not ("const" or "enum"))
            return false;
        var location = failure.Detail.InstanceLocation.ToString();
        var separator = location.LastIndexOf('/');
        return separator >= 0 && string.Equals(location[..separator], choiceLocation, StringComparison.Ordinal);
    }

    private static string Location(string choice, IReadOnlyList<SchemaFailure> failures) => failures
        .First(failure => IsChoice(failure)
            && string.Equals(failure.Detail.EvaluationPath.ToString(), choice, StringComparison.Ordinal))
        .Detail.InstanceLocation.ToString();

    /// <summary>Rebuilds the escaped evaluation-path prefix, matching <see cref="JsonPointer.ToString()"/>.</summary>
    private static string Prefix(IReadOnlyList<string> segments, int count) =>
        count == 0 ? string.Empty : "/" + string.Join('/', segments.Take(count));
}
