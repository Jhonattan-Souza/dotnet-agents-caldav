using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

/// <summary>
/// Validated local text semantics. The local match over the authoritative Calendar Object Resource is the only
/// result truth; a CalDAV text-match pre-filter may only reduce the candidates this matcher inspects.
/// </summary>
/// <remarks>
/// Matching folds ASCII letters to lowercase and other letters with the invariant Unicode lowercase mapping, but
/// never folds a non-ASCII character into ASCII. Any ASCII run of a folded term therefore matches only ASCII text,
/// which a server evaluating <c>i;ascii-casemap</c>, <c>i;unicode-casemap</c>, or plain lowercasing also matches.
/// </remarks>
internal sealed class CalendarTextCriteria
{
    internal const int MaximumTextLength = 256;
    internal const int MaximumTerms = 16;
    internal const int MaximumCategories = 16;
    internal const int MaximumCategoryLength = 128;
    internal const int MinimumServerTermLength = 3;
    private static readonly string[] TextPropertyNames = ["SUMMARY", "DESCRIPTION", "LOCATION"];
    private static readonly string[] BranchPropertyNames = ["SUMMARY", "DESCRIPTION", "LOCATION", "CATEGORIES"];

    private CalendarTextCriteria(IReadOnlyList<string> terms, IReadOnlyList<string> categories)
    {
        Terms = terms.Select(Fold).ToArray();
        Categories = categories.Select(Fold).ToArray();
        Prefilter = CreatePrefilter(terms, categories);
    }

    /// <summary>Folded terms; each must occur in one searched property of the matching component.</summary>
    internal IReadOnlyList<string> Terms { get; }

    /// <summary>Folded categories; each must equal one CATEGORIES value of the matching component.</summary>
    internal IReadOnlyList<string> Categories { get; }

    /// <summary>The conservative server candidate reduction derived from these criteria.</summary>
    internal CalendarTextPrefilter Prefilter { get; }

    /// <summary>Validates an optional caller filter; an absent filter yields no criteria.</summary>
    internal static bool TryCreate(CalendarTextFilter? filter, out CalendarTextCriteria? criteria)
    {
        criteria = null;
        if (filter is null)
            return true;
        if (filter.Text is null && filter.Categories is null
            || !TryReadTerms(filter.Text, out var terms)
            || !TryReadCategories(filter.Categories, out var categories))
            return false;
        criteria = new CalendarTextCriteria(terms, categories);
        return true;
    }

    /// <summary>Whether the master or any Recurrence Override component of the Calendar Entity matches.</summary>
    internal bool MatchesAnyComponent(CalendarContentDocument document, CalendarEntityKind kind)
    {
        var componentName = ComponentName(kind);
        return document.Components.Any(component => component.Path.Count == 2
            && component.Path[^1].Name.Equals(componentName, StringComparison.OrdinalIgnoreCase)
            && MatchesComponent(document, component));
    }

    /// <summary>Whether the component effective for one Occurrence matches, using only its own properties.</summary>
    internal bool MatchesOccurrence(
        CalendarContentDocument document,
        CalendarEntityKind kind,
        CalendarTemporalValue recurrenceIdentity) => MatchesComponent(
        document,
        CalendarOccurrenceComponentSelector.Select(document, recurrenceIdentity, kind));

    /// <summary>Canonical UTF-8 identity of the folded criteria, bound into a Query Result Snapshot.</summary>
    internal byte[] EncodeBinding() => JsonSerializer.SerializeToUtf8Bytes(new[] { Terms, Categories });

    /// <summary>Folds ASCII letters to ASCII lowercase and other runes with the invariant lowercase mapping.</summary>
    internal static string Fold(string value)
    {
        var folded = new StringBuilder(value.Length);
        Span<char> buffer = stackalloc char[2];
        foreach (var rune in value.EnumerateRunes())
        {
            var lower = rune.IsAscii ? Rune.ToLowerInvariant(rune) : NonAsciiLower(rune);
            folded.Append(buffer[..lower.EncodeToUtf16(buffer)]);
        }
        return folded.ToString();
    }

    private bool MatchesComponent(CalendarContentDocument document, CalendarContentComponent component)
    {
        var owned = document.Properties
            .Where(property => property.ComponentPath.SequenceEqual(component.Path))
            .ToArray();
        var categories = owned
            .Where(property => property.Name.Equals("CATEGORIES", StringComparison.OrdinalIgnoreCase))
            .SelectMany(property => CalendarResourceSemanticProjectionMapper.SplitEscaped(property.RawEncodedValue, ','))
            .Select(value => Fold(CalendarContentDocument.DecodeText(value).Trim()))
            .ToArray();
        if (!Categories.All(category => categories.Contains(category, StringComparer.Ordinal)))
            return false;
        var searched = owned
            .Where(property => TextPropertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
            .Select(property => Fold(CalendarContentDocument.DecodeText(property.RawEncodedValue)))
            .Concat(categories)
            .ToArray();
        return Terms.All(term => searched.Any(value => value.Contains(term, StringComparison.Ordinal)));
    }

    private static Rune NonAsciiLower(Rune rune)
    {
        var lower = Rune.ToLowerInvariant(rune);
        return lower.IsAscii ? rune : lower;
    }

    private static bool TryReadTerms(string? text, out IReadOnlyList<string> terms)
    {
        terms = [];
        if (text is null)
            return true;
        if (text.Length > MaximumTextLength || HasControlCharacter(text, allowWhiteSpace: true))
            return false;
        terms = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return terms.Count is >= 1 and <= MaximumTerms;
    }

    private static bool TryReadCategories(IReadOnlyList<string>? values, out IReadOnlyList<string> categories)
    {
        categories = values ?? [];
        return values is null
            || values.Count is >= 1 and <= MaximumCategories
            && values.Distinct(StringComparer.Ordinal).Count() == values.Count
            && values.All(IsValidCategory);
    }

    private static bool IsValidCategory(string? value) => value is { Length: >= 1 and <= MaximumCategoryLength }
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && !HasControlCharacter(value, allowWhiteSpace: false);

    private static bool HasControlCharacter(string value, bool allowWhiteSpace) => value.Any(character =>
        char.IsControl(character) && !(allowWhiteSpace && char.IsWhiteSpace(character)));

    private static CalendarTextPrefilter CreatePrefilter(
        IReadOnlyList<string> terms,
        IReadOnlyList<string> categories)
    {
        var term = terms.Select(value => LongestServerRun(value, allowSpace: false))
            .Where(run => run.Length >= MinimumServerTermLength)
            .OrderByDescending(run => run.Length)
            .FirstOrDefault();
        var categoryMatches = categories.Select(value => LongestServerRun(value, allowSpace: true))
            .Where(run => run.Trim().Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Select(run => new CalendarTextPropertyMatch("CATEGORIES", run))
            .ToArray();
        if (term is null)
            return new CalendarTextPrefilter(categoryMatches.Length == 0 ? [] : [categoryMatches]);
        return new CalendarTextPrefilter(BranchPropertyNames
            .Select(name => (IReadOnlyList<CalendarTextPropertyMatch>)
                [new CalendarTextPropertyMatch(name, term), .. categoryMatches])
            .ToArray());
    }

    // Only printable ASCII outside iCalendar TEXT escapes reaches a server, so any conforming collation and any
    // escaped or unescaped server comparison matches at least every locally matching value.
    private static string LongestServerRun(string value, bool allowSpace)
    {
        var longest = string.Empty;
        var start = 0;
        for (var index = 0; index <= value.Length; index++)
        {
            if (index < value.Length && IsServerCharacter(value[index], allowSpace))
                continue;
            if (index - start > longest.Length)
                longest = value[start..index];
            start = index + 1;
        }
        return Fold(longest);
    }

    private static bool IsServerCharacter(char character, bool allowSpace) =>
        character is >= '!' and <= '~' and not ('\\' or ',' or ';') || allowSpace && character == ' ';

    private static string ComponentName(CalendarEntityKind kind) =>
        kind == CalendarEntityKind.Event ? "VEVENT" : "VTODO";
}

/// <summary>One CalDAV <c>prop-filter</c>/<c>text-match</c> pair of a server candidate pre-filter.</summary>
internal sealed record CalendarTextPropertyMatch(string PropertyName, string Text);

/// <summary>
/// Server candidate reduction for a text filter. Each branch is one calendar-query whose property matches all
/// apply to one component; the union of the branches is a superset of the locally matching resources.
/// </summary>
internal sealed record CalendarTextPrefilter(IReadOnlyList<IReadOnlyList<CalendarTextPropertyMatch>> Branches)
{
    internal bool IsEmpty => Branches.Count == 0;
}
