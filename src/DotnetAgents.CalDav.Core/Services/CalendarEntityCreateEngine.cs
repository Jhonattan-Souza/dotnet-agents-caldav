using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using System.Xml;

namespace DotnetAgents.CalDav.Core.Services;

internal sealed class CalendarEntityCreateEngine(
    ICalendarClient calendarClient,
    CalDavOptions options,
    TimeProvider timeProvider,
    ICalendarEntityIdentityGenerator identityGenerator,
    Func<IReadOnlyList<CalendarDescriptor>, CalendarDiscoveryResult> applyScope,
    Func<CalendarEntityKind, IReadOnlyList<CalendarDescriptor>, IReadOnlyList<CalendarDescriptor>, CalendarSelectionResult>
        resolveDefaultCalendar)
{
    private const int MaximumAttempts = 3;
    private const int MaximumDiagnostics = 32;
    private const int MaximumInspectedResources = 5_000;
    private const int MaximumCalendarResourceBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan PreDispatchBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconciliationBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MutationExecutionBudget = TimeSpan.FromSeconds(60);

    public Task<CalendarEntityCreateResult> CreateEventAsync(
        CalendarEventCreateRequest request,
        CancellationToken cancellationToken) => ExecuteWithinPreDispatchDeadlineAsync(
        (startedTimestamp, deadlineToken) => CreateEventCoreAsync(request, startedTimestamp, deadlineToken),
        cancellationToken);

    public Task<CalendarEntityCreateResult> CreateTodoAsync(
        CalendarTodoCreateRequest request,
        CancellationToken cancellationToken) => ExecuteWithinPreDispatchDeadlineAsync(
        (startedTimestamp, deadlineToken) => CreateTodoCoreAsync(request, startedTimestamp, deadlineToken),
        cancellationToken);

    private async Task<CalendarEntityCreateResult> CreateEventCoreAsync(
        CalendarEventCreateRequest request,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        if (!IsValidEventCreateRequest(request))
            return Failure(CalendarEntityCreateCode.InvalidInput);
        var prevalidation = PrevalidateCreateRequest(
            request.Destination,
            request.Fields.RecurrenceSet?.Rule);
        if (prevalidation is not null)
            return prevalidation;
        var contentValidation = PrevalidateEventContent(request);
        if (contentValidation is not null)
            return contentValidation;
        if (HasRecurrencePeriod(request.Fields.RecurrenceSet?.RecurrenceDates))
            return Failure(CalendarEntityCreateCode.UnsupportedCapability);

        var selection = await SelectCalendarAsync(request.Destination, CalendarEntityKind.Event, cancellationToken);
        if (selection.Code != CalendarSelectionCode.Success)
            return SelectionFailure(selection);
        if (selection.Calendar!.EventSupport != EntityKindSupport.Advertised)
            return Failure(CalendarEntityCreateCode.UnsupportedCapability, selection.Candidates);
        return await CreateEventInCalendarAsync(selection.Calendar, request, startedTimestamp, cancellationToken);
    }

    private async Task<CalendarEntityCreateResult> CreateTodoCoreAsync(
        CalendarTodoCreateRequest request,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        if (!HasValidDestinationShape(request.Destination)
            || !HasValidUidShape(request.Uid))
        {
            return Failure(CalendarEntityCreateCode.InvalidInput);
        }
        var prevalidation = PrevalidateCreateRequest(
            request.Destination,
            request.Fields.RecurrenceSet?.Rule);
        if (prevalidation is not null)
            return prevalidation;
        var contentValidation = PrevalidateTodoContent(request);
        if (contentValidation is not null)
            return contentValidation;
        if (HasRecurrencePeriod(request.Fields.RecurrenceSet?.RecurrenceDates))
            return Failure(CalendarEntityCreateCode.UnsupportedCapability);

        var selection = await SelectCalendarAsync(request.Destination, CalendarEntityKind.Todo, cancellationToken);
        if (selection.Code != CalendarSelectionCode.Success)
            return SelectionFailure(selection);
        if (selection.Calendar!.TodoSupport != EntityKindSupport.Advertised)
            return Failure(CalendarEntityCreateCode.UnsupportedCapability, selection.Candidates);
        return await CreateTodoInCalendarAsync(selection.Calendar, request, startedTimestamp, cancellationToken);
    }

    private async Task<CalendarEntityCreateResult> ExecuteWithinPreDispatchDeadlineAsync(
        Func<long, CancellationToken, Task<CalendarEntityCreateResult>> execute,
        CancellationToken callerCancellationToken)
    {
        var startedTimestamp = timeProvider.GetTimestamp();
        using var deadline = new CancellationTokenSource(PreDispatchBudget, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerCancellationToken, deadline.Token);
        try
        {
            return await execute(startedTimestamp, linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !callerCancellationToken.IsCancellationRequested)
        {
            return Failure(CalendarEntityCreateCode.LimitExhausted);
        }
        catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
        {
            return Failure(CalendarEntityCreateCode.UpstreamUnavailable);
        }
        catch (Exception exception) when (exception is CalendarDiscoveryProtocolException or XmlException)
        {
            return Failure(CalendarEntityCreateCode.UpstreamProtocolError);
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return Failure(CalendarEntityCreateCode.UnsupportedCapability);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TimeoutException)
        {
            return Failure(CalendarEntityCreateCode.UpstreamUnavailable);
        }
    }

    private async Task<CalendarEntityCreateResult> CreateEventInCalendarAsync(
        CalendarDescriptor calendar,
        CalendarEventCreateRequest request,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        var callerSuppliedUid = request.Uid is not null;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var uid = callerSuppliedUid ? request.Uid! : identityGenerator.CreateUid();
            var result = await TryCreateEventIdentityAsync(
                calendar,
                request.Fields,
                uid,
                startedTimestamp,
                cancellationToken);
            if (result.Code != CalendarEntityCreateCode.Conflict || callerSuppliedUid)
                return result;
        }

        return Failure(CalendarEntityCreateCode.Conflict, mutationState: CalendarMutationState.NotCommitted);
    }

    private async Task<CalendarEntityCreateResult> CreateTodoInCalendarAsync(
        CalendarDescriptor calendar,
        CalendarTodoCreateRequest request,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        var callerSuppliedUid = request.Uid is not null;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            var uid = callerSuppliedUid ? request.Uid! : identityGenerator.CreateUid();
            var result = await TryCreateTodoIdentityAsync(
                calendar,
                request.Fields,
                uid,
                startedTimestamp,
                cancellationToken);
            if (result.Code != CalendarEntityCreateCode.Conflict || callerSuppliedUid)
                return result;
        }

        return Failure(CalendarEntityCreateCode.Conflict, mutationState: CalendarMutationState.NotCommitted);
    }

    private async Task<CalendarEntityCreateResult> TryCreateEventIdentityAsync(
        CalendarDescriptor calendar,
        CalendarEventCreateFields fields,
        string uid,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        var uidAvailability = await CheckEntityUidAsync(
            calendar,
            uid,
            CalendarEntityKind.Event,
            cancellationToken);
        if (uidAvailability != EntityUidAvailability.Available)
            return UidAvailabilityFailure(uidAvailability);
        if (!TrySerializeEvent(uid, fields, out var authoritativeUtf8))
            return Failure(CalendarEntityCreateCode.InvalidCalendarData);
        var transport = await calendarClient.CreateCalendarResourceAsync(
            new CalendarResourceCreateRequest(
                calendar.Href,
                CalendarResourceCreateProtocol.BuildResourceHref(calendar.Href, uid),
                authoritativeUtf8),
            cancellationToken);
        if (transport.Code == CalendarResourceCreateCode.Conflict)
            return Failure(CalendarEntityCreateCode.Conflict, mutationState: CalendarMutationState.NotCommitted);
        return await VerifyCreateAsync(
            calendar.Href,
            uid,
            CalendarResourceProjectionKind.Event,
            authoritativeUtf8,
            transport,
            startedTimestamp);
    }

    private async Task<CalendarEntityCreateResult> TryCreateTodoIdentityAsync(
        CalendarDescriptor calendar,
        CalendarTodoCreateFields fields,
        string uid,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        var uidAvailability = await CheckEntityUidAsync(
            calendar,
            uid,
            CalendarEntityKind.Todo,
            cancellationToken);
        if (uidAvailability != EntityUidAvailability.Available)
            return UidAvailabilityFailure(uidAvailability);
        if (!TrySerializeTodo(uid, fields, out var authoritativeUtf8))
            return Failure(CalendarEntityCreateCode.InvalidCalendarData);
        var transport = await calendarClient.CreateCalendarResourceAsync(
            new CalendarResourceCreateRequest(
                calendar.Href,
                CalendarResourceCreateProtocol.BuildResourceHref(calendar.Href, uid),
                authoritativeUtf8),
            cancellationToken);
        if (transport.Code == CalendarResourceCreateCode.Conflict)
            return Failure(CalendarEntityCreateCode.Conflict, mutationState: CalendarMutationState.NotCommitted);
        return await VerifyCreateAsync(
            calendar.Href,
            uid,
            CalendarResourceProjectionKind.Todo,
            authoritativeUtf8,
            transport,
            startedTimestamp);
    }

    private async Task<CalendarEntityCreateResult> VerifyCreateAsync(
        string calendarHref,
        string uid,
        CalendarResourceProjectionKind kind,
        ReadOnlyMemory<byte> submittedUtf8,
        CalendarResourceCreateResult transport,
        long startedTimestamp)
    {
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Reconcile);
        var observedResult = await ReadVerificationAsync(transport, startedTimestamp);
        if (observedResult.Failure is not null)
            return observedResult.Failure;
        var snapshot = CalendarResourceProjector.AttachSnapshot(calendarHref, observedResult.Read!).Snapshot!;
        if (!CalendarEntityCreateFidelity.IsEquivalent(submittedUtf8.Span, snapshot.AuthoritativeUtf8.Span))
            return PostWriteDifference(transport.Code, snapshot);
        if (snapshot.Projection.Kind == kind
            && string.Equals(snapshot.Projection.EntityUid, uid, StringComparison.Ordinal))
        {
            return CalendarEntityCreateResult.Success(snapshot);
        }
        return new CalendarEntityCreateResult(
            CalendarEntityCreateCode.CommittedButUnverified,
            CalendarMutationState.Committed,
            snapshot);
    }

    private static CalendarEntityCreateResult PostWriteDifference(
        CalendarResourceCreateCode transportCode,
        CalendarResourceSnapshot snapshot) => new(
        transportCode == CalendarResourceCreateCode.PossiblyDispatched
            ? CalendarEntityCreateCode.Indeterminate
            : CalendarEntityCreateCode.FidelityFailure,
        transportCode == CalendarResourceCreateCode.PossiblyDispatched
            ? CalendarMutationState.Unknown
            : CalendarMutationState.Committed,
        snapshot);

    private async Task<CreateVerificationRead> ReadVerificationAsync(
        CalendarResourceCreateResult transport,
        long startedTimestamp)
    {
        var possiblyDispatched = transport.Code == CalendarResourceCreateCode.PossiblyDispatched;
        if (transport.Code != CalendarResourceCreateCode.Dispatched && !possiblyDispatched)
        {
            return new CreateVerificationRead(
                null,
                TransportFailure(transport.Code));
        }

        var overallRemaining = MutationExecutionBudget - timeProvider.GetElapsedTime(startedTimestamp);
        var remaining = overallRemaining < ReconciliationBudget ? overallRemaining : ReconciliationBudget;
        if (remaining <= TimeSpan.Zero)
            return VerificationDeadlineFailure(possiblyDispatched);

        using var reconciliationDeadline = new CancellationTokenSource(remaining, timeProvider);
        try
        {
            var observed = await calendarClient.GetCalendarResourceAsync(
                transport.ResourceHref,
                reconciliationDeadline.Token);
            return new CreateVerificationRead(observed, VerificationFailure(possiblyDispatched, observed.Code));
        }
        catch (OperationCanceledException) when (reconciliationDeadline.IsCancellationRequested)
        {
            return VerificationDeadlineFailure(possiblyDispatched);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or TimeoutException
            or CalendarDiscoveryProtocolException
            or OperationCanceledException)
        {
            return VerificationDeadlineFailure(possiblyDispatched);
        }
    }

    private static CreateVerificationRead VerificationDeadlineFailure(bool possiblyDispatched) => new(
        null,
        Failure(
            possiblyDispatched
                ? CalendarEntityCreateCode.Indeterminate
                : CalendarEntityCreateCode.CommittedButUnverified,
            mutationState: possiblyDispatched
                ? CalendarMutationState.Unknown
                : CalendarMutationState.Committed));

    private async Task<CalendarSelectionResult> SelectCalendarAsync(
        CalendarCreateDestination destination,
        CalendarEntityKind entityKind,
        CancellationToken cancellationToken)
    {
        var discovered = await calendarClient.GetCalendarsAsync(cancellationToken);
        var scoped = applyScope(discovered).Items;
        if (destination.Mode == CalendarEntityScopeMode.Default)
            return resolveDefaultCalendar(entityKind, discovered, scoped);

        var matches = FindCalendarMatches(scoped, destination.Calendar!);
        if (matches.Length == 0)
            return CalendarSelectionResult.Failure(CalendarSelectionCode.NotFound, scoped.Take(MaximumDiagnostics).ToArray());
        return matches.Length == 1
            ? CalendarSelectionResult.Success(matches[0])
            : CalendarSelectionResult.Failure(CalendarSelectionCode.Ambiguous, matches.Take(MaximumDiagnostics).ToArray());
    }

    private CalendarEntityCreateResult? PrevalidateDestination(CalendarCreateDestination destination)
    {
        var href = destination.Mode == CalendarEntityScopeMode.Selected ? destination.Calendar?.Href : null;
        if (href is null)
            return null;
        if (!TryValidateCalendarHref(href))
            return Failure(CalendarEntityCreateCode.InvalidInput);
        var configuredScope = ParseScope(options.CalendarHrefs);
        return configuredScope.Count > 0 && !configuredScope.Contains(href, StringComparer.Ordinal)
            ? Failure(CalendarEntityCreateCode.OutsideScope)
            : null;
    }

    private bool TryValidateCalendarHref(string href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var candidate)
            || !string.Equals(candidate.AbsoluteUri, href, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(candidate.UserInfo)
            || !string.IsNullOrEmpty(candidate.Fragment)
            || !string.IsNullOrEmpty(candidate.Query)
            || candidate.AbsolutePath.Contains("%2F", StringComparison.OrdinalIgnoreCase)
            || candidate.AbsolutePath.Contains("%5C", StringComparison.OrdinalIgnoreCase)
            || href.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            || href.Contains('\\'))
        {
            return false;
        }

        return HasSameOrigin(new Uri(options.BaseUrl, UriKind.Absolute), candidate);
    }

    private bool TrySerializeEvent(string uid, CalendarEventCreateFields fields, out byte[] authoritativeUtf8) =>
        TrySerialize(() => CalendarEntityCreateSerializer.SerializeEvent(uid, fields, timeProvider.GetUtcNow()), out authoritativeUtf8);

    private bool TrySerializeTodo(string uid, CalendarTodoCreateFields fields, out byte[] authoritativeUtf8) =>
        TrySerialize(() => CalendarEntityCreateSerializer.SerializeTodo(uid, fields, timeProvider.GetUtcNow()), out authoritativeUtf8);

    private static bool TrySerialize(Func<byte[]> serialize, out byte[] authoritativeUtf8)
    {
        try
        {
            authoritativeUtf8 = serialize();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            authoritativeUtf8 = [];
            return false;
        }
    }

    private CalendarEntityCreateResult? PrevalidateEventContent(CalendarEventCreateRequest request) =>
        PrevalidateContent(() => CalendarEntityCreateSerializer.SerializeEvent(
            request.Uid ?? "generated-uid",
            request.Fields,
            timeProvider.GetUtcNow()));

    private static CalendarEntityCreateResult? PrevalidateRecurrenceCapability(string? rule) =>
        CalendarEntityCreateValidator.RequiresUnsupportedRecurrenceScale(rule)
            ? Failure(CalendarEntityCreateCode.UnsupportedCapability)
            : null;

    private CalendarEntityCreateResult? PrevalidateCreateRequest(
        CalendarCreateDestination destination,
        string? recurrenceRule) => PrevalidateDestination(destination)
            ?? PrevalidateRecurrenceCapability(recurrenceRule);

    private CalendarEntityCreateResult? PrevalidateTodoContent(CalendarTodoCreateRequest request) =>
        PrevalidateContent(() => CalendarEntityCreateSerializer.SerializeTodo(
            request.Uid ?? "generated-uid",
            request.Fields,
            timeProvider.GetUtcNow()));

    private static CalendarEntityCreateResult? PrevalidateContent(Func<byte[]> serialize)
    {
        try
        {
            return serialize().Length > MaximumCalendarResourceBytes
                ? Failure(CalendarEntityCreateCode.PayloadTooLarge)
                : null;
        }
        catch (CalendarRecurrenceUnevaluableException)
        {
            return Failure(CalendarEntityCreateCode.RecurrenceUnevaluable);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Failure(CalendarEntityCreateCode.InvalidCalendarData);
        }
    }

    private async Task<EntityUidAvailability> CheckEntityUidAsync(
        CalendarDescriptor calendar,
        string uid,
        CalendarEntityKind entityKind,
        CancellationToken cancellationToken)
    {
        var hrefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in GetUidQueryKinds(entityKind))
        {
            hrefs.UnionWith(await calendarClient.QueryCalendarResourceHrefsAsync(
                calendar.Href,
                kind,
                null,
                null,
                cancellationToken));
            if (hrefs.Count > MaximumInspectedResources)
                return EntityUidAvailability.LimitExhausted;
        }
        foreach (var href in hrefs)
        {
            var read = await calendarClient.GetCalendarResourceAsync(href, cancellationToken);
            if (read.Code == CalendarResourceReadCode.NotFound)
                continue;
            if (read.Code != CalendarResourceReadCode.Success)
                return read.Code switch
                {
                    CalendarResourceReadCode.ConcurrencyUnavailable => EntityUidAvailability.ConcurrencyUnavailable,
                    CalendarResourceReadCode.PayloadTooLarge => EntityUidAvailability.PayloadTooLarge,
                    _ => EntityUidAvailability.UpstreamProtocolError
                };
            var projected = CalendarResourceProjector.Project(read.AuthoritativeUtf8.Span);
            if (CalendarResourceProjector.ContainsEntityUid(projected, uid))
                return EntityUidAvailability.Exists;
            if (projected.Projection.Kind == CalendarResourceProjectionKind.Opaque)
                return EntityUidAvailability.OpaqueResource;
        }

        return EntityUidAvailability.Available;
    }

    private static IEnumerable<CalendarEntityKind> GetUidQueryKinds(CalendarEntityKind requestedKind)
    {
        yield return requestedKind;
        yield return requestedKind == CalendarEntityKind.Event
            ? CalendarEntityKind.Todo
            : CalendarEntityKind.Event;
    }

    private static CalendarEntityCreateResult UidAvailabilityFailure(EntityUidAvailability availability) => availability switch
    {
        EntityUidAvailability.Exists => Failure(
            CalendarEntityCreateCode.Conflict,
            mutationState: CalendarMutationState.NotCommitted),
        EntityUidAvailability.ConcurrencyUnavailable => Failure(CalendarEntityCreateCode.ConcurrencyUnavailable),
        EntityUidAvailability.OpaqueResource => Failure(CalendarEntityCreateCode.OpaqueResource),
        EntityUidAvailability.LimitExhausted => Failure(
            CalendarEntityCreateCode.LimitExhausted,
            limits: new CalendarEntityCreateExecutionLimits(
                ResourcesInspected: MaximumInspectedResources + 1)),
        EntityUidAvailability.PayloadTooLarge => Failure(CalendarEntityCreateCode.PayloadTooLarge),
        _ => Failure(CalendarEntityCreateCode.UpstreamProtocolError)
    };

    private static CalendarEntityCreateResult? VerificationFailure(
        bool possiblyDispatched,
        CalendarResourceReadCode code) => (possiblyDispatched, code) switch
        {
            (_, CalendarResourceReadCode.Success) => null,
            (true, CalendarResourceReadCode.NotFound) =>
                Failure(CalendarEntityCreateCode.Indeterminate, mutationState: CalendarMutationState.Unknown),
            (false, CalendarResourceReadCode.ConcurrencyUnavailable) =>
                Failure(CalendarEntityCreateCode.CommittedButConcurrencyUnavailable, mutationState: CalendarMutationState.Committed),
            (true, _) => Failure(CalendarEntityCreateCode.Indeterminate, mutationState: CalendarMutationState.Unknown),
            _ => Failure(CalendarEntityCreateCode.CommittedButUnverified, mutationState: CalendarMutationState.Committed)
        };

    private static CalendarEntityCreateResult TransportFailure(CalendarResourceCreateCode code) => Failure(
        code switch
        {
            CalendarResourceCreateCode.InvalidInput => CalendarEntityCreateCode.InvalidInput,
            CalendarResourceCreateCode.UnsupportedCapability => CalendarEntityCreateCode.UnsupportedCapability,
            CalendarResourceCreateCode.PayloadTooLarge => CalendarEntityCreateCode.PayloadTooLarge,
            CalendarResourceCreateCode.NotFound => CalendarEntityCreateCode.NotFound,
            CalendarResourceCreateCode.UpstreamUnauthorized => CalendarEntityCreateCode.UpstreamUnauthorized,
            CalendarResourceCreateCode.UpstreamForbidden => CalendarEntityCreateCode.UpstreamForbidden,
            CalendarResourceCreateCode.UpstreamRateLimited => CalendarEntityCreateCode.UpstreamRateLimited,
            CalendarResourceCreateCode.UpstreamProtocolError => CalendarEntityCreateCode.UpstreamProtocolError,
            _ => CalendarEntityCreateCode.UpstreamUnavailable
        },
        mutationState: code == CalendarResourceCreateCode.InvalidInput
            ? CalendarMutationState.NotAttempted
            : CalendarMutationState.NotCommitted);

    private static bool IsValidEventCreateRequest(CalendarEventCreateRequest request) =>
        HasValidDestinationShape(request.Destination)
        && HasValidUidShape(request.Uid);

    private static bool HasValidUidShape(string? uid) => uid is null
        || !string.IsNullOrWhiteSpace(uid) && !uid.Any(char.IsControl);

    private static bool HasValidDestinationShape(CalendarCreateDestination destination) => destination.Mode switch
    {
        CalendarEntityScopeMode.Default => destination.Calendar is null,
        CalendarEntityScopeMode.Selected => HasExactlyOneSelector(destination.Calendar),
        _ => false
    };

    private static bool HasExactlyOneSelector(CalendarReference? reference)
    {
        if (reference is null)
            return false;
        var hasName = !string.IsNullOrWhiteSpace(reference.Name);
        var hasHref = !string.IsNullOrWhiteSpace(reference.Href);
        return hasName != hasHref
            && (!hasName || string.Equals(reference.Name, reference.Name!.Trim(), StringComparison.Ordinal));
    }

    private static CalendarDescriptor[] FindCalendarMatches(
        IReadOnlyList<CalendarDescriptor> calendars,
        CalendarReference reference) => calendars.Where(calendar => reference.Name is not null
            ? string.Equals(calendar.DisplayName?.Trim(), reference.Name, StringComparison.OrdinalIgnoreCase)
            : string.Equals(calendar.Href, reference.Href, StringComparison.Ordinal)).ToArray();

    private static CalendarEntityCreateResult SelectionFailure(CalendarSelectionResult selection) =>
        Failure(selection.Code switch
        {
            CalendarSelectionCode.NotFound => CalendarEntityCreateCode.NotFound,
            CalendarSelectionCode.Ambiguous => CalendarEntityCreateCode.Ambiguous,
            CalendarSelectionCode.OutsideScope => CalendarEntityCreateCode.OutsideScope,
            _ => CalendarEntityCreateCode.UnsupportedCapability
        }, selection.Candidates);

    private static bool HasRecurrencePeriod(IReadOnlyList<CalendarRecurrenceDateCreate>? recurrenceDates) =>
        recurrenceDates?.Any(date => date.Period is not null) == true;

    private static CalendarEntityCreateResult Failure(
        CalendarEntityCreateCode code,
        IReadOnlyList<CalendarDescriptor>? candidates = null,
        CalendarMutationState mutationState = CalendarMutationState.NotAttempted,
        CalendarEntityCreateExecutionLimits? limits = null) =>
        new(code, mutationState, AuthorizedCandidates: candidates ?? [], Limits: limits);

    private static bool HasSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    private static IReadOnlyList<string> ParseScope(string? calendarHrefs) => calendarHrefs is null
        ? []
        : calendarHrefs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record CreateVerificationRead(
        CalendarResourceRead? Read,
        CalendarEntityCreateResult? Failure);

    private enum EntityUidAvailability
    {
        Available,
        Exists,
        ConcurrencyUnavailable,
        OpaqueResource,
        LimitExhausted,
        PayloadTooLarge,
        UpstreamProtocolError
    }
}
