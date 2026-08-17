using System.Globalization;
using DotnetAgents.CalDav.Core.Models;
using Ical.Net.DataTypes;
using Ical.Net.Evaluation;

namespace DotnetAgents.CalDav.Core.Internal.Ical;

internal sealed record CalendarOccurrencePatchTarget(
    CalendarContentDocument? Document,
    CalendarContentComponent? Component,
    CalendarEntityPatchResult? Failure);

internal sealed record CalendarOccurrenceAdditionValidation(
    bool ShouldAdd,
    CalendarEntityPatchResult? Failure);

internal sealed record CalendarOccurrenceMembershipInspection(
    CalendarContentComponent? Master,
    CalendarContentComponent? Individual,
    CalendarContentComponent? Range,
    bool Exists,
    bool IsExcluded,
    CalendarEntityPatchResult? Failure);

/// <summary>Materializes the complete individual override addressed by one original Recurrence Identity.</summary>
internal static class CalendarOccurrencePatchBuilder
{
    private const int MaximumRecurrenceWork = 10_000;
    private const int MaximumEntityOccurrences = 2_000;
    private static readonly IReadOnlySet<string> TemporalValueParameters =
        new HashSet<string>(["VALUE", "TZID"], StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> RecurrenceIdentityValueParameters =
        new HashSet<string>(["VALUE", "TZID", "RANGE"], StringComparer.OrdinalIgnoreCase);
    private static readonly IReadOnlySet<string> RemovedMembershipProperties =
        new HashSet<string>(["RRULE", "RDATE", "EXDATE", "RECURRENCE-ID"], StringComparer.OrdinalIgnoreCase);

    public static CalendarOccurrenceAdditionValidation ValidateAddition(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        CalendarTemporalValue identity,
        CalendarEntityKind kind,
        CancellationToken cancellationToken)
    {
        var inspection = InspectMembership(snapshot, document, identity, kind, cancellationToken);
        if (inspection.Failure is not null)
            return new(false, inspection.Failure);
        return inspection.IsExcluded
            ? AdditionFailure(CalendarEntityPatchCode.InvalidInput, snapshot)
            : new(!inspection.Exists, null);
    }

    public static CalendarOccurrenceMembershipInspection InspectMembership(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        CalendarTemporalValue identity,
        CalendarEntityKind kind,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!HasValidIdentityLexicalForm(identity))
                return InspectionFailure(CalendarEntityPatchCode.InvalidInput, snapshot);
            if (CalendarOccurrenceEvaluator.HasUnevaluableRecurrenceStructure(snapshot))
                return InspectionFailure(CalendarEntityPatchCode.RecurrenceUnevaluable, snapshot);
            var master = document.GetMasterComponent(kind);
            var masterStart = GetProperty(document, master, "DTSTART");
            if (!HasValidIdentityFamily(masterStart, identity))
                return InspectionFailure(CalendarEntityPatchCode.InvalidInput, snapshot);
            if (IsTemporallyUnresolved(snapshot, identity))
                return InspectionFailure(CalendarEntityPatchCode.TemporalUnresolved, snapshot);
            if (HasRecurrencePeriodDate(document, master))
                return InspectionFailure(CalendarEntityPatchCode.UnsupportedCapability, snapshot);

            var overrides = GetOverrides(document, kind);
            EnsureKnownIdentityLimit(document, master, overrides);
            var key = identity.GetCanonicalSortKey();
            var individual = FindIndividual(overrides, key);
            var range = FindRange(overrides, key);
            var exists = CalendarPatchValueSerializer.ParseTemporal(masterStart!).GetCanonicalSortKey() == key
                || overrides.Any(item => item.Key == key)
                || IsRecurrenceDate(document, master, key)
                || IsGeneratedByRule(document, master, identity, overrides, cancellationToken);
            return new(master, individual, range, exists, IsExcluded(document, master, key), null);
        }
        catch (EvaluationLimitExceededException)
        {
            return InspectionFailure(CalendarEntityPatchCode.LimitExhausted, snapshot);
        }
        catch (Exception exception) when (exception is FormatException
            or ArgumentException
            or InvalidOperationException
            or EvaluationException
            or OverflowException)
        {
            return InspectionFailure(CalendarEntityPatchCode.InvalidCalendarData, snapshot);
        }
    }

    public static CalendarOccurrencePatchTarget MaterializeIndividual(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        CalendarOccurrenceMembershipInspection inspection,
        CalendarTemporalValue identity,
        CalendarEntityKind kind)
    {
        try
        {
            if (!inspection.Exists || inspection.Master is null)
                return Failed(CalendarEntityPatchCode.NotFound, snapshot);
            if (inspection.Individual is not null)
            {
                return CompleteSupportedIndividual(
                    snapshot,
                    document,
                    inspection.Master,
                    inspection.Individual,
                    inspection.Range,
                    identity,
                    kind);
            }

            var materialized = MaterializeOverride(
                snapshot,
                document,
                inspection.Master,
                inspection.Range,
                identity,
                kind);
            return new(materialized.Document, materialized.Component, null);
        }
        catch (CalendarTemporalUnresolvedException)
        {
            return Failed(CalendarEntityPatchCode.TemporalUnresolved, snapshot);
        }
        catch (Exception exception) when (exception is FormatException
            or ArgumentException
            or InvalidOperationException
            or OverflowException)
        {
            return Failed(CalendarEntityPatchCode.InvalidCalendarData, snapshot);
        }
    }

    private static CalendarOccurrenceAdditionValidation AdditionFailure(
        CalendarEntityPatchCode code,
        CalendarResourceSnapshot snapshot) => new(false, Failed(code, snapshot).Failure);

    private static CalendarOccurrenceMembershipInspection InspectionFailure(
        CalendarEntityPatchCode code,
        CalendarResourceSnapshot snapshot) => new(null, null, null, false, false, Failed(code, snapshot).Failure);

    private static bool IsTemporallyUnresolved(
        CalendarResourceSnapshot snapshot,
        CalendarTemporalValue identity) => identity.Kind == CalendarTemporalKind.ZonedDateTime
        && new CalendarTemporalResolver(snapshot.CalendarProperties, snapshot.AuthoritativeUtf8.Span)
            .Resolve(ToCalDateTime(identity)).Unresolved;

    public static CalendarOccurrencePatchTarget SelectTarget(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        CalendarMutationTarget target,
        CalendarEntityKind kind,
        CancellationToken cancellationToken)
    {
        if (target.Scope == "master")
            return new(document, document.GetMasterComponent(kind), null);
        try
        {
            var identity = target.RecurrenceIdentity!;
            if (!HasValidIdentityLexicalForm(identity))
                return Failed(CalendarEntityPatchCode.InvalidInput, snapshot);
            if (CalendarOccurrenceEvaluator.HasUnevaluableRecurrenceStructure(snapshot))
                return Failed(CalendarEntityPatchCode.RecurrenceUnevaluable, snapshot);
            var master = document.GetMasterComponent(kind);
            var membership = ResolveMembership(document, master, identity, kind, cancellationToken);
            if (membership.Code is { } failure)
                return Failed(failure, snapshot);
            if (membership.Individual is not null)
                return CompleteSupportedIndividual(
                    snapshot,
                    document,
                    master,
                    membership.Individual,
                    membership.Range,
                    identity,
                    kind);
            var materialized = MaterializeOverride(snapshot, document, master, membership.Range, identity, kind);
            return new(materialized.Document, materialized.Component, null);
        }
        catch (EvaluationLimitExceededException)
        {
            return Failed(CalendarEntityPatchCode.LimitExhausted, snapshot);
        }
        catch (Exception exception) when (exception is FormatException
            or ArgumentException
            or InvalidOperationException
            or EvaluationException
            or OverflowException)
        {
            return Failed(CalendarEntityPatchCode.InvalidCalendarData, snapshot);
        }
    }

    private static CalendarOccurrencePatchTarget Failed(
        CalendarEntityPatchCode code,
        CalendarResourceSnapshot snapshot) => new(
        null,
        null,
        new CalendarEntityPatchResult(
            code,
            CalendarMutationState.NotAttempted,
            snapshot,
            Phase: CalendarEntityPatchPhase.CompleteResourceSemantics));

    private static OccurrenceMembership ResolveMembership(
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarTemporalValue identity,
        CalendarEntityKind kind,
        CancellationToken cancellationToken)
    {
        var masterStart = GetProperty(document, master, "DTSTART");
        if (!HasValidIdentityFamily(masterStart, identity))
            return OccurrenceMembership.Failure(CalendarEntityPatchCode.InvalidInput);
        if (HasRecurrencePeriodDate(document, master))
            return OccurrenceMembership.Failure(CalendarEntityPatchCode.UnsupportedCapability);
        var overrides = GetOverrides(document, kind);
        EnsureKnownIdentityLimit(document, master, overrides);
        var key = identity.GetCanonicalSortKey();
        var individual = FindIndividual(overrides, key);
        var range = FindRange(overrides, key);
        if (IsExcluded(document, master, key))
            return OccurrenceMembership.Failure(CalendarEntityPatchCode.InvalidInput);
        if (IsRecurrencePeriodDate(document, master, key))
            return OccurrenceMembership.Failure(CalendarEntityPatchCode.UnsupportedCapability);
        if (individual is not null)
            return new(individual, range, null);
        return Exists(document, master, masterStart!, identity, overrides, key, cancellationToken)
            ? new(null, range, null)
            : OccurrenceMembership.Failure(CalendarEntityPatchCode.NotFound);
    }

    private static bool HasValidIdentityLexicalForm(CalendarTemporalValue value) => value.Kind switch
    {
        CalendarTemporalKind.Date => value.TimeZoneId is null && DateOnly.TryParseExact(
            value.Value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _),
        CalendarTemporalKind.UtcDateTime => value.TimeZoneId is null
            && value.Value.EndsWith('Z')
            && TryDateTime(value.Value[..^1]),
        CalendarTemporalKind.FloatingDateTime => value.TimeZoneId is null && TryDateTime(value.Value),
        CalendarTemporalKind.ZonedDateTime => !string.IsNullOrEmpty(value.TimeZoneId) && TryDateTime(value.Value),
        _ => false
    };

    private static bool TryDateTime(string value) => DateTime.TryParseExact(
        value,
        "yyyy-MM-dd'T'HH:mm:ss",
        CultureInfo.InvariantCulture,
        DateTimeStyles.None,
        out _);

    private static bool HasValidIdentityFamily(
        CalendarContentProperty? masterStart,
        CalendarTemporalValue identity) => masterStart is not null
        && HasSameIdentityFamily(CalendarPatchValueSerializer.ParseTemporal(masterStart), identity);

    private static CalendarContentComponent? FindIndividual(
        IEnumerable<CalendarOverrideComponent> overrides,
        string key) => overrides.FirstOrDefault(item => !item.IsRange && item.Key == key)?.Component;

    private static CalendarContentComponent? FindRange(
        IEnumerable<CalendarOverrideComponent> overrides,
        string key) => overrides.Where(item => item.IsRange && string.CompareOrdinal(item.Key, key) <= 0)
        .OrderBy(item => item.Key, StringComparer.Ordinal)
        .LastOrDefault()?.Component;

    private static bool Exists(
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarContentProperty masterStart,
        CalendarTemporalValue identity,
        IReadOnlyList<CalendarOverrideComponent> overrides,
        string key,
        CancellationToken cancellationToken) => overrides.Any(item => item.Key == key)
        || HasRecurrenceSet(document, master, overrides)
            && CalendarPatchValueSerializer.ParseTemporal(masterStart).GetCanonicalSortKey() == key
        || IsRecurrenceDate(document, master, key)
        || IsGeneratedByRule(document, master, identity, overrides, cancellationToken);

    private static bool HasRecurrenceSet(
        CalendarContentDocument document,
        CalendarContentComponent master,
        IReadOnlyCollection<CalendarOverrideComponent> overrides) => overrides.Count > 0
        || document.Properties.Any(property => property.ComponentPath.SequenceEqual(master.Path)
            && IsRecurrenceSetProperty(property.Name));

    private static bool IsRecurrenceSetProperty(string name) => name.Equals("RRULE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("RDATE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("EXDATE", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<CalendarOverrideComponent> GetOverrides(
        CalendarContentDocument document,
        CalendarEntityKind kind)
    {
        var componentName = kind == CalendarEntityKind.Event ? "VEVENT" : "VTODO";
        return document.Components.Where(component => component.Path.Count == 2
                && component.Path[^1].Name.Equals(componentName, StringComparison.OrdinalIgnoreCase))
            .Select(component => CreateOverride(document, component))
            .Where(item => item is not null)
            .Cast<CalendarOverrideComponent>()
            .ToArray();
    }

    private static CalendarOverrideComponent? CreateOverride(
        CalendarContentDocument document,
        CalendarContentComponent component)
    {
        var property = GetProperty(document, component, "RECURRENCE-ID");
        if (property is null)
            return null;
        var identity = CalendarPatchValueSerializer.ParseTemporal(property);
        var range = property.Parameters.Where(parameter => parameter.Name.Equals("RANGE", StringComparison.OrdinalIgnoreCase))
            .SelectMany(parameter => parameter.Values)
            .SingleOrDefault();
        if (range is not null && !range.Equals("THISANDFUTURE", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("The recurrence range is unsupported.");
        return new(component, identity.GetCanonicalSortKey(), range is not null);
    }

    private static bool IsExcluded(
        CalendarContentDocument document,
        CalendarContentComponent master,
        string identityKey) => DirectProperties(document, master, "EXDATE")
        .SelectMany(property => property.RawEncodedValue.Split(',', StringSplitOptions.None)
            .Select(value => ParseTemporalToken(property, value).GetCanonicalSortKey()))
        .Contains(identityKey, StringComparer.Ordinal);

    private static bool IsRecurrenceDate(
        CalendarContentDocument document,
        CalendarContentComponent master,
        string identityKey) => DirectProperties(document, master, "RDATE")
        .Where(property => property.ValueType != CalendarPropertyValueType.Period)
        .SelectMany(property => property.RawEncodedValue.Split(',', StringSplitOptions.None)
            .Select(value => value.Split('/', 2, StringSplitOptions.None)[0])
            .Select(value => ParseTemporalToken(property, value).GetCanonicalSortKey()))
        .Contains(identityKey, StringComparer.Ordinal);

    private static bool IsRecurrencePeriodDate(
        CalendarContentDocument document,
        CalendarContentComponent master,
        string identityKey) => DirectProperties(document, master, "RDATE")
        .Where(property => property.ValueType == CalendarPropertyValueType.Period)
        .SelectMany(property => property.RawEncodedValue.Split(',', StringSplitOptions.None)
            .Select(value => value.Split('/', 2, StringSplitOptions.None)[0])
            .Select(value => ParseTemporalToken(property, value).GetCanonicalSortKey()))
        .Contains(identityKey, StringComparer.Ordinal);

    private static bool HasRecurrencePeriodDate(
        CalendarContentDocument document,
        CalendarContentComponent master) => DirectProperties(document, master, "RDATE")
        .Any(property => property.ValueType == CalendarPropertyValueType.Period);

    private static bool IsGeneratedByRule(
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarTemporalValue identity,
        IReadOnlyList<CalendarOverrideComponent> overrides,
        CancellationToken cancellationToken)
    {
        var rules = DirectProperties(document, master, "RRULE").ToArray();
        if (rules.Length == 0)
            return false;
        if (rules.Length > 1)
            throw new FormatException("A typed recurrence set cannot contain multiple RRULE values.");
        var masterStart = CalendarPatchValueSerializer.ParseTemporal(GetProperty(document, master, "DTSTART")!);
        var nominalStart = ToCalDateTime(masterStart);
        var target = ToCalDateTime(identity).Value;
        if (target < nominalStart.Value)
            return false;
        var identities = GetKnownIdentityKeys(document, master, overrides);
        var observed = 0;
        foreach (var period in new RecurrencePatternEvaluator(new RecurrenceRule(rules[0].RawEncodedValue)).Evaluate(
                     nominalStart,
                     nominalStart,
                     new EvaluationOptions { MaxUnmatchedIncrementsLimit = MaximumRecurrenceWork }))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++observed > MaximumRecurrenceWork)
                throw new EvaluationLimitExceededException();
            identities.Add(WithLocalValue(masterStart, period.StartTime.Value).GetCanonicalSortKey());
            if (identities.Count > MaximumEntityOccurrences)
                throw new EvaluationLimitExceededException();
            if (period.StartTime.Value == target)
                return true;
            if (period.StartTime.Value > target)
                return false;
        }
        return false;
    }

    private static void EnsureKnownIdentityLimit(
        CalendarContentDocument document,
        CalendarContentComponent master,
        IReadOnlyList<CalendarOverrideComponent> overrides)
    {
        if (GetKnownIdentityKeys(document, master, overrides).Count > MaximumEntityOccurrences)
            throw new EvaluationLimitExceededException();
    }

    private static HashSet<string> GetKnownIdentityKeys(
        CalendarContentDocument document,
        CalendarContentComponent master,
        IReadOnlyList<CalendarOverrideComponent> overrides)
    {
        var identities = overrides.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var masterStart = GetProperty(document, master, "DTSTART");
        if (masterStart is not null && HasRecurrenceSet(document, master, overrides))
            identities.Add(CalendarPatchValueSerializer.ParseTemporal(masterStart).GetCanonicalSortKey());
        foreach (var property in DirectProperties(document, master, "RDATE"))
        {
            foreach (var value in property.RawEncodedValue.Split(',', StringSplitOptions.None))
            {
                identities.Add(ParseTemporalToken(
                    property,
                    value.Split('/', 2, StringSplitOptions.None)[0]).GetCanonicalSortKey());
            }
        }
        return identities;
    }

    private static CalendarTemporalValue WithLocalValue(CalendarTemporalValue template, DateTime value)
    {
        var format = template.Kind == CalendarTemporalKind.Date ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss";
        var lexical = value.ToString(format, CultureInfo.InvariantCulture);
        return template with { Value = template.Kind == CalendarTemporalKind.UtcDateTime ? lexical + "Z" : lexical };
    }

    private static MaterializedOverride MaterializeOverride(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarContentComponent? range,
        CalendarTemporalValue identity,
        CalendarEntityKind kind)
    {
        var source = range ?? master;
        var sourceDocument = document;
        var root = document.GetComponent(master.Path.Take(1).ToArray());
        var clone = document.GetComponentOccurrence(source.Path).OriginalSlice;
        document = CalendarContentDocument.Parse(document.EditOccurrences(root.Path, [], [clone]));
        var componentName = kind == CalendarEntityKind.Event ? "VEVENT" : "VTODO";
        var added = document.Components.Where(component => component.Path.Count == 2
                && component.Path[^1].Name.Equals(componentName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(component => component.Path[^1].Occurrence)
            .Last();
        document = RemoveMembershipAndSetIdentity(document, added, identity);
        added = document.GetComponent(added.Path);
        document = SetInheritedTiming(
            snapshot,
            sourceDocument,
            source,
            document,
            added,
            range is not null,
            identity,
            kind);
        return new(document, document.GetComponent(added.Path));
    }

    private static CalendarOccurrencePatchTarget CompleteSupportedIndividual(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument document,
        CalendarContentComponent master,
        CalendarContentComponent individual,
        CalendarContentComponent? range,
        CalendarTemporalValue identity,
        CalendarEntityKind kind)
    {
        if (kind != CalendarEntityKind.Event || GetProperty(document, individual, "DTSTART") is not null)
            return new(document, individual, null);
        if (!IsCancelled(document, individual))
            return Failed(CalendarEntityPatchCode.InvalidCalendarData, snapshot);
        var source = range ?? master;
        var additions = document.Properties.Where(property => property.ComponentPath.SequenceEqual(source.Path))
            .Where(property => !RemovedMembershipProperties.Contains(property.Name)
                && property.Name is not ("UID" or "DTSTART" or "DTEND" or "DUE")
                && GetProperty(document, individual, property.Name) is null)
            .Select(property => property.OriginalSlice)
            .ToArray();
        if (additions.Length > 0)
        {
            document = CalendarContentDocument.Parse(document.EditOccurrences(individual.Path, [], additions));
            individual = document.GetComponent(individual.Path);
        }
        document = SetInheritedTiming(
            snapshot,
            document,
            source,
            document,
            individual,
            range is not null,
            identity,
            kind);
        return new(document, document.GetComponent(individual.Path), null);
    }

    private static bool IsCancelled(
        CalendarContentDocument document,
        CalendarContentComponent component) => GetProperty(document, component, "STATUS")?.RawEncodedValue
        .Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) == true;

    private static CalendarContentDocument RemoveMembershipAndSetIdentity(
        CalendarContentDocument document,
        CalendarContentComponent component,
        CalendarTemporalValue identity)
    {
        var removals = document.Properties.Where(property => property.ComponentPath.SequenceEqual(component.Path)
                && RemovedMembershipProperties.Contains(property.Name)
                && !property.Name.Equals("RECURRENCE-ID", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(property => property, _ => (string?)null);
        document = CalendarContentDocument.Parse(document.EditProperties(component.Path, removals, []));
        component = document.GetComponent(component.Path);
        var slice = CalendarPatchValueSerializer.Temporal("RECURRENCE-ID", identity);
        var edited = GetProperty(document, component, "RECURRENCE-ID") is null
            ? document.SetOrClearSinglePropertySlice(component.Path, "RECURRENCE-ID", slice)
            : document.SetSinglePropertySlicePreservingParameters(
                component.Path,
                "RECURRENCE-ID",
                slice,
                RecurrenceIdentityValueParameters);
        return CalendarContentDocument.Parse(edited);
    }

    private static CalendarContentDocument SetInheritedTiming(
        CalendarResourceSnapshot snapshot,
        CalendarContentDocument sourceDocument,
        CalendarContentComponent sourceComponent,
        CalendarContentDocument document,
        CalendarContentComponent component,
        bool sourceIsRange,
        CalendarTemporalValue identity,
        CalendarEntityKind kind)
    {
        var sourceStartProperty = GetProperty(sourceDocument, sourceComponent, "DTSTART")
            ?? throw new FormatException("A recurring component requires DTSTART.");
        var sourceStart = CalendarPatchValueSerializer.ParseTemporal(sourceStartProperty);
        var effectiveStart = !sourceIsRange
            ? identity
            : AddNominalOffset(identity, GetRangeOffset(sourceDocument, sourceComponent, sourceStart));
        var endName = kind == CalendarEntityKind.Event ? "DTEND" : "DUE";
        var sourceEndProperty = GetProperty(sourceDocument, sourceComponent, endName);
        var effectiveEnd = ResolveInheritedEnd(snapshot, sourceStart, sourceEndProperty, effectiveStart);
        document = SetTemporalProperty(document, component, "DTSTART", effectiveStart);
        if (effectiveEnd is null)
            return document;
        component = document.GetComponent(component.Path);
        return SetTemporalProperty(document, component, endName, effectiveEnd);
    }

    private static CalendarTemporalValue? ResolveInheritedEnd(
        CalendarResourceSnapshot snapshot,
        CalendarTemporalValue sourceStart,
        CalendarContentProperty? sourceEndProperty,
        CalendarTemporalValue effectiveStart)
    {
        if (sourceEndProperty is null)
            return null;
        var shifted = CalendarDurationArithmetic.ShiftExplicitEndResolution(
            sourceStart,
            CalendarPatchValueSerializer.ParseTemporal(sourceEndProperty),
            effectiveStart,
            new CalendarTemporalResolver(snapshot.CalendarProperties, snapshot.AuthoritativeUtf8.Span));
        if (shifted.Value is not null)
            return shifted.Value;
        if (shifted.Instant.Unresolved || shifted.Instant.Skipped)
            throw new CalendarTemporalUnresolvedException();
        throw new FormatException("The inherited component span must be strictly positive.");
    }

    private static CalendarContentDocument SetTemporalProperty(
        CalendarContentDocument document,
        CalendarContentComponent component,
        string name,
        CalendarTemporalValue value)
    {
        var slice = CalendarPatchValueSerializer.Temporal(name, value);
        var edited = GetProperty(document, component, name) is null
            ? document.SetOrClearSinglePropertySlice(component.Path, name, slice)
            : document.SetSinglePropertySlicePreservingParameters(
                component.Path,
                name,
                slice,
                TemporalValueParameters);
        return CalendarContentDocument.Parse(edited);
    }

    private static TimeSpan GetRangeOffset(
        CalendarContentDocument document,
        CalendarContentComponent component,
        CalendarTemporalValue start)
    {
        var sourceIdentity = CalendarPatchValueSerializer.ParseTemporal(
            GetProperty(document, component, "RECURRENCE-ID")
            ?? throw new FormatException("A range override requires RECURRENCE-ID."));
        return ParseLocal(start) - ParseLocal(sourceIdentity);
    }

    private static CalendarTemporalValue AddNominalOffset(CalendarTemporalValue value, TimeSpan offset)
    {
        var format = value.Kind == CalendarTemporalKind.Date ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss";
        var shifted = ParseLocal(value).Add(offset).ToString(format, CultureInfo.InvariantCulture);
        return value with { Value = value.Kind == CalendarTemporalKind.UtcDateTime ? shifted + "Z" : shifted };
    }

    private static DateTime ParseLocal(CalendarTemporalValue value)
    {
        var format = value.Kind == CalendarTemporalKind.Date ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss";
        return DateTime.ParseExact(value.Value.TrimEnd('Z'), format, CultureInfo.InvariantCulture);
    }

    private static CalendarTemporalValue ParseTemporalToken(CalendarContentProperty property, string value) =>
        CalendarPatchValueSerializer.ParseTemporal(property with { RawEncodedValue = value });

    private static bool HasSameIdentityFamily(CalendarTemporalValue left, CalendarTemporalValue right) =>
        left.Kind == right.Kind && string.Equals(left.TimeZoneId, right.TimeZoneId, StringComparison.Ordinal);

    private static CalDateTime ToCalDateTime(CalendarTemporalValue value)
    {
        var local = ParseLocal(value);
        return value.Kind switch
        {
            CalendarTemporalKind.Date => new CalDateTime(local.Year, local.Month, local.Day),
            CalendarTemporalKind.UtcDateTime => new CalDateTime(DateTime.SpecifyKind(local, DateTimeKind.Utc)),
            CalendarTemporalKind.ZonedDateTime => new CalDateTime(local, value.TimeZoneId),
            _ => new CalDateTime(local)
        };
    }

    private static CalendarContentProperty? GetProperty(
        CalendarContentDocument document,
        CalendarContentComponent component,
        string name) => DirectProperties(document, component, name).FirstOrDefault();

    private static IEnumerable<CalendarContentProperty> DirectProperties(
        CalendarContentDocument document,
        CalendarContentComponent component,
        string name) => document.Properties.Where(property => property.ComponentPath.SequenceEqual(component.Path)
            && property.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private sealed record CalendarOverrideComponent(
        CalendarContentComponent Component,
        string Key,
        bool IsRange);

    private sealed record OccurrenceMembership(
        CalendarContentComponent? Individual,
        CalendarContentComponent? Range,
        CalendarEntityPatchCode? Code)
    {
        public static OccurrenceMembership Failure(CalendarEntityPatchCode code) => new(null, null, code);
    }

    private sealed record MaterializedOverride(
        CalendarContentDocument Document,
        CalendarContentComponent Component);

    private sealed class CalendarTemporalUnresolvedException : FormatException
    {
    }
}
