using System.Text;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Ical;

public sealed class CalendarTextCriteriaTests
{
    [Fact]
    public void AbsentFilterCreatesNoCriteria()
    {
        CalendarTextCriteria.TryCreate(null, out var criteria).ShouldBeTrue();

        criteria.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(InvalidFilters))]
    public void InvalidFiltersAreRejected(CalendarTextFilter filter)
    {
        CalendarTextCriteria.TryCreate(filter, out var criteria).ShouldBeFalse();

        criteria.ShouldBeNull();
    }

    public static TheoryData<CalendarTextFilter> InvalidFilters() => new()
    {
        new CalendarTextFilter(),
        new CalendarTextFilter(" \t\n "),
        new CalendarTextFilter(new string('a', CalendarTextCriteria.MaximumTextLength + 1)),
        new CalendarTextFilter("dentist\u0001visit"),
        new CalendarTextFilter(string.Join(' ', Enumerable.Repeat("a", CalendarTextCriteria.MaximumTerms + 1))),
        new CalendarTextFilter(Categories: []),
        new CalendarTextFilter(Categories: Enumerable.Range(0, CalendarTextCriteria.MaximumCategories + 1)
            .Select(index => $"c{index}")
            .ToArray()),
        new CalendarTextFilter(Categories: ["Work", "Work"]),
        new CalendarTextFilter(Categories: [" Work"]),
        new CalendarTextFilter(Categories: [""]),
        new CalendarTextFilter(Categories: [new string('c', CalendarTextCriteria.MaximumCategoryLength + 1)]),
        new CalendarTextFilter(Categories: ["Wo\trk"]),
        new CalendarTextFilter(Categories: [null!]),
        new CalendarTextFilter("valid", ["Work", "Work"])
    };

    [Fact]
    public void TextSplitsOnAnyWhitespaceWithinTheTermLimit()
    {
        var criteria = Create(new CalendarTextFilter(
            " Dentist\tvisit\n" + string.Join(' ', Enumerable.Repeat("x", CalendarTextCriteria.MaximumTerms - 2))));

        criteria.Terms.Count.ShouldBe(CalendarTextCriteria.MaximumTerms);
        criteria.Terms[0].ShouldBe("dentist");
        criteria.Terms[1].ShouldBe("visit");
    }

    [Theory]
    [InlineData("MiXeD", "mixed")]
    [InlineData("ÉCOLE Ärger", "école ärger")]
    [InlineData("ΣΊΣΥΦΟΣ", "σίσυφοσ")]
    [InlineData("Kİ", "Kİ")]
    [InlineData("\U00010400", "\U00010428")]
    public void FoldNeverMapsNonAsciiRunesToAscii(string value, string expected)
    {
        CalendarTextCriteria.Fold(value).ShouldBe(expected);
    }

    [Fact]
    public void KelvinSignAndDottedCapitalIDoNotMatchTheirAsciiLookalikes()
    {
        var document = Document(Event("SUMMARY:Kelvin İstanbul\r\n"));

        Create(new CalendarTextFilter("kelvin")).MatchesAnyComponent(document, CalendarEntityKind.Event)
            .ShouldBeFalse();
        Create(new CalendarTextFilter("istanbul")).MatchesAnyComponent(document, CalendarEntityKind.Event)
            .ShouldBeFalse();
        Create(new CalendarTextFilter("Kelvin")).MatchesAnyComponent(document, CalendarEntityKind.Event)
            .ShouldBeTrue();
    }

    [Fact]
    public void TermsMatchCaseInsensitiveSubstringsAcrossSearchedPropertiesOfOneComponent()
    {
        var document = Document(Event(
            "SUMMARY:Quarterly REVIEW\r\nDESCRIPTION:Bring the dentist\\, the x-rays\\; and notes\\nline two\r\n"
            + "LOCATION:Reunião room\r\nCATEGORIES:Health,Personal\r\nCATEGORIES:  Team Alpha \r\n"
            + "X-NOTE:secret\r\nBEGIN:VALARM\r\nACTION:DISPLAY\r\nDESCRIPTION:alarmonly\r\nTRIGGER:-PT5M\r\n"
            + "END:VALARM\r\n"));

        Matches(document, "review DENTIST").ShouldBeTrue();
        Matches(document, "dentist, x-rays;").ShouldBeTrue();
        Matches(document, "notes\nline").ShouldBeTrue();
        Matches(document, "REUNIÃO").ShouldBeTrue();
        Matches(document, "ealt").ShouldBeTrue();
        Matches(document, "team alpha").ShouldBeTrue();
        Matches(document, "secret").ShouldBeFalse();
        Matches(document, "alarmonly").ShouldBeFalse();
        Matches(document, "review absent").ShouldBeFalse();
    }

    [Fact]
    public void CategoriesMustAllEqualTrimmedCaseInsensitiveValuesOfTheSameComponent()
    {
        var document = Document(Event("SUMMARY:Checkup\r\nCATEGORIES:Health,Personal\r\nCATEGORIES:  Team Alpha \r\n"));

        MatchesCategories(document, "HEALTH").ShouldBeTrue();
        MatchesCategories(document, "health", "team alpha").ShouldBeTrue();
        MatchesCategories(document, "Heal").ShouldBeFalse();
        MatchesCategories(document, "health", "work").ShouldBeFalse();
        Create(new CalendarTextFilter("checkup", ["personal"]))
            .MatchesAnyComponent(document, CalendarEntityKind.Event).ShouldBeTrue();
        Create(new CalendarTextFilter("absent", ["personal"]))
            .MatchesAnyComponent(document, CalendarEntityKind.Event).ShouldBeFalse();
    }

    [Fact]
    public void EscapedCategoryCommaStaysInsideOneValue()
    {
        var document = Document(Event("SUMMARY:x\r\nCATEGORIES:Smith\\, John,Other\r\n"));

        MatchesCategories(document, "smith, john").ShouldBeTrue();
        MatchesCategories(document, "smith").ShouldBeFalse();
    }

    [Fact]
    public void EveryTermAndCategoryMustMatchTheSameComponent()
    {
        var document = Document(RecurringEvent(
            master: "SUMMARY:Weekly planning\r\nCATEGORIES:Work\r\n",
            overrideLines: "SUMMARY:Planning with dentist\r\n"));

        Matches(document, "weekly").ShouldBeTrue();
        Matches(document, "dentist").ShouldBeTrue();
        Matches(document, "weekly dentist").ShouldBeFalse();
        Create(new CalendarTextFilter("dentist", ["work"]))
            .MatchesAnyComponent(document, CalendarEntityKind.Event).ShouldBeFalse();
        Create(new CalendarTextFilter("weekly")).MatchesAnyComponent(document, CalendarEntityKind.Todo)
            .ShouldBeFalse();
    }

    [Fact]
    public void OccurrencesMatchOnlyTheirOwnEffectiveComponent()
    {
        var document = Document(RecurringEvent(
            master: "SUMMARY:Weekly planning\r\n",
            overrideLines: "SUMMARY:Planning with dentist\r\n"));
        var dentist = Create(new CalendarTextFilter("dentist"));
        var weekly = Create(new CalendarTextFilter("weekly"));
        var overridden = Utc("2026-11-03T09:00:00Z");
        var ordinary = Utc("2026-11-02T09:00:00Z");

        dentist.MatchesOccurrence(document, CalendarEntityKind.Event, overridden).ShouldBeTrue();
        dentist.MatchesOccurrence(document, CalendarEntityKind.Event, ordinary).ShouldBeFalse();
        weekly.MatchesOccurrence(document, CalendarEntityKind.Event, overridden).ShouldBeFalse();
        weekly.MatchesOccurrence(document, CalendarEntityKind.Event, ordinary).ShouldBeTrue();
    }

    [Fact]
    public void RangeOverrideGovernsLaterOccurrencesOfATodo()
    {
        var document = Document(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Text//EN\r\n"
            + "BEGIN:VTODO\r\nUID:range\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20261101T090000Z\r\n"
            + "RRULE:FREQ=DAILY;COUNT=5\r\nSUMMARY:Water plants\r\nEND:VTODO\r\n"
            + "BEGIN:VTODO\r\nUID:range\r\nDTSTAMP:20260815T120000Z\r\n"
            + "RECURRENCE-ID;RANGE=THISANDFUTURE:20261103T090000Z\r\nDTSTART:20261103T100000Z\r\n"
            + "SUMMARY:Water garden\r\nEND:VTODO\r\nEND:VCALENDAR\r\n");
        var garden = Create(new CalendarTextFilter("garden"));

        garden.MatchesOccurrence(document, CalendarEntityKind.Todo, Utc("2026-11-02T09:00:00Z")).ShouldBeFalse();
        garden.MatchesOccurrence(document, CalendarEntityKind.Todo, Utc("2026-11-03T09:00:00Z")).ShouldBeTrue();
        garden.MatchesOccurrence(document, CalendarEntityKind.Todo, Utc("2026-11-05T09:00:00Z")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("reunião", "reuni")]
    [InlineData("Dentist visit", "dentist")]
    [InlineData("ab cd", null)]
    [InlineData("çãé", null)]
    [InlineData("a,bcd;ef\\ghi", "bcd")]
    [InlineData("<tag>", "<tag>")]
    public void TextPrefilterUsesTheLongestEligibleAsciiRun(string text, string? expected)
    {
        var prefilter = Create(new CalendarTextFilter(text)).Prefilter;

        if (expected is null)
        {
            prefilter.IsEmpty.ShouldBeTrue();
            return;
        }
        prefilter.Branches.Select(branch => branch.ShouldHaveSingleItem().PropertyName)
            .ShouldBe(["SUMMARY", "DESCRIPTION", "LOCATION", "CATEGORIES"]);
        prefilter.Branches.ShouldAllBe(branch => branch[0].Text == expected);
    }

    [Fact]
    public void CategoryPrefiltersJoinEveryTextBranchOrStandAlone()
    {
        var combined = Create(new CalendarTextFilter("dentist", ["Health", "Açaí", "Team X", "é"])).Prefilter;
        var categoriesOnly = Create(new CalendarTextFilter(Categories: ["Health"])).Prefilter;
        var ineligible = Create(new CalendarTextFilter("ab", ["é"])).Prefilter;

        combined.Branches.Count.ShouldBe(4);
        combined.Branches.ShouldAllBe(branch => branch.Skip(1).Select(match => match.Text)
            .SequenceEqual(new[] { "health", "a", "team x" }));
        combined.Branches.ShouldAllBe(branch => branch.Skip(1).All(match => match.PropertyName == "CATEGORIES"));
        categoriesOnly.Branches.ShouldHaveSingleItem()
            .ShouldBe([new CalendarTextPropertyMatch("CATEGORIES", "health")]);
        ineligible.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void BindingIdentifiesTheFoldedCriteria()
    {
        var first = Create(new CalendarTextFilter("Dentist  Visit", ["Health"])).EncodeBinding();
        var equivalent = Create(new CalendarTextFilter("dentist visit", ["HEALTH"])).EncodeBinding();
        var other = Create(new CalendarTextFilter("dentist", ["health"])).EncodeBinding();

        Encoding.UTF8.GetString(first).ShouldBe("[[\"dentist\",\"visit\"],[\"health\"]]");
        equivalent.ShouldBe(first);
        other.ShouldNotBe(first);
    }

    private static bool Matches(CalendarContentDocument document, string text) =>
        Create(new CalendarTextFilter(text)).MatchesAnyComponent(document, CalendarEntityKind.Event);

    private static bool MatchesCategories(CalendarContentDocument document, params string[] categories) =>
        Create(new CalendarTextFilter(Categories: categories)).MatchesAnyComponent(document, CalendarEntityKind.Event);

    private static CalendarTextCriteria Create(CalendarTextFilter filter)
    {
        CalendarTextCriteria.TryCreate(filter, out var criteria).ShouldBeTrue();
        return criteria.ShouldNotBeNull();
    }

    private static CalendarTemporalValue Utc(string value) => new(CalendarTemporalKind.UtcDateTime, value);

    private static CalendarContentDocument Document(string content) =>
        CalendarContentDocument.Parse(Encoding.UTF8.GetBytes(content));

    private static string Event(string lines) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Text//EN\r\nBEGIN:VEVENT\r\nUID:text\r\n"
        + $"DTSTAMP:20260815T120000Z\r\nDTSTART:20261101T090000Z\r\n{lines}END:VEVENT\r\nEND:VCALENDAR\r\n";

    private static string RecurringEvent(string master, string overrideLines) =>
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Text//EN\r\n"
        + "BEGIN:VEVENT\r\nUID:series\r\nDTSTAMP:20260815T120000Z\r\nDTSTART:20261101T090000Z\r\n"
        + $"DURATION:PT1H\r\nRRULE:FREQ=DAILY;COUNT=5\r\n{master}END:VEVENT\r\n"
        + "BEGIN:VEVENT\r\nUID:series\r\nDTSTAMP:20260815T120000Z\r\nRECURRENCE-ID:20261103T090000Z\r\n"
        + $"DTSTART:20261103T150000Z\r\nDURATION:PT1H\r\n{overrideLines}END:VEVENT\r\nEND:VCALENDAR\r\n";
}
