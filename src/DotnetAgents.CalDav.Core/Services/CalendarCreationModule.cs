using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using System.Security.Cryptography;
using System.Xml;

namespace DotnetAgents.CalDav.Core.Services;

internal sealed class CalendarCreationModule(
    ICalendarCreateTransport transport,
    CalDavOptions options,
    TimeProvider timeProvider,
    ICalendarEntityIdentityGenerator identityGenerator,
    Func<IReadOnlyList<CalendarDescriptor>, CalendarDiscoveryResult> applyScope,
    Func<CalendarEntityKind, IReadOnlyList<CalendarDescriptor>, IReadOnlyList<CalendarDescriptor>, CalendarSelectionResult>
        resolveDefaultCalendar)
{
    private const int MaximumAttempts = 3;
    private const int MaximumDiagnostics = 32;
    private const int MaximumCalendarResourceBytes = 4 * 1024 * 1024;
    private const string ExactCreatePolicyVersion = "1";
    private static readonly TimeSpan PreDispatchBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconciliationBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MutationExecutionBudget = TimeSpan.FromSeconds(60);

    public async Task<CalendarCreationOutcome> CreateAsync(
        CalendarCreationCommand command,
        CancellationToken cancellationToken) => command switch
        {
            CalendarCreationCommand.Event create => new CalendarCreationOutcome.Semantic(
                await CreateEventAsync(create.Request, cancellationToken)),
            CalendarCreationCommand.Todo create => new CalendarCreationOutcome.Semantic(
                await CreateTodoAsync(create.Request, cancellationToken)),
            CalendarCreationCommand.Exact create => new CalendarCreationOutcome.Exact(
                await CreateExactAsync(create.ReviewedCreate, cancellationToken)),
            _ => throw new System.Diagnostics.UnreachableException()
        };

    public async Task<CalendarExactCreateReviewResult> ReviewExactAsync(
        ExactCreateIntent intent,
        CancellationToken cancellationToken)
    {
        var request = intent.Request;
        var shapeFailure = ValidateExactCreateShape(request);
        if (shapeFailure is not null)
            return FailedExactReview(shapeFailure);
        try
        {
            var preparation = await PrepareExactCreateAsync(request, cancellationToken);
            if (preparation.Failure is not null)
                return FailedExactReview(preparation.Failure);
            return SuccessfulExactReview(request, preparation);
        }
        catch (Exception exception) when (IsExactPhaseFailure(exception, cancellationToken))
        {
            return FailedExactReview(FromExactPhaseFailure(
                exception,
                CalendarExactResourcePhase.SelectionDiscoveryCapability));
        }
    }

    private Task<CalendarEntityCreateResult> CreateEventAsync(
        CalendarEventCreateRequest request,
        CancellationToken cancellationToken) => ExecuteWithinPreDispatchDeadlineAsync(
        (startedTimestamp, deadlineToken) => CreateEventCoreAsync(request, startedTimestamp, deadlineToken),
        cancellationToken);

    private Task<CalendarEntityCreateResult> CreateTodoAsync(
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
        var prevalidation = PrevalidateEventRequest(request);
        if (prevalidation is not null)
            return prevalidation;

        var selection = await SelectCalendarAsync(request.Destination, CalendarEntityKind.Event, cancellationToken);
        if (selection.Code != CalendarSelectionCode.Success)
            return SelectionFailure(selection);
        if (selection.Calendar!.EventSupport != EntityKindSupport.Advertised)
            return Failure(CalendarEntityCreateCode.UnsupportedCapability, selection.Candidates);
        var contentValidation = PrevalidateEventContent(request);
        if (contentValidation is not null)
            return contentValidation;
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
        var prevalidation = PrevalidateTodoRequest(request);
        if (prevalidation is not null)
            return prevalidation;

        var selection = await SelectCalendarAsync(request.Destination, CalendarEntityKind.Todo, cancellationToken);
        if (selection.Code != CalendarSelectionCode.Success)
            return SelectionFailure(selection);
        if (selection.Calendar!.TodoSupport != EntityKindSupport.Advertised)
            return Failure(CalendarEntityCreateCode.UnsupportedCapability, selection.Candidates);
        var contentValidation = PrevalidateTodoContent(request);
        if (contentValidation is not null)
            return contentValidation;
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
            return Failure(
                CalendarEntityCreateCode.LimitExhausted,
                limits: new CalendarEntityCreateExecutionLimits(
                    Dimension: CalendarEntityCreateLimitDimension.ElapsedTime));
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
            if (!IsDefiniteIdentityConflict(result.Code)
                || callerSuppliedUid
                || attempt == MaximumAttempts - 1)
                return result;
        }

        throw new System.Diagnostics.UnreachableException();
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
            if (!IsDefiniteIdentityConflict(result.Code)
                || callerSuppliedUid
                || attempt == MaximumAttempts - 1)
                return result;
        }

        throw new System.Diagnostics.UnreachableException();
    }

    private async Task<CalendarEntityCreateResult> TryCreateEventIdentityAsync(
        CalendarDescriptor calendar,
        CalendarEventCreateFields fields,
        string uid,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        if (!TrySerializeEvent(uid, fields, out var authoritativeUtf8))
            return Failure(CalendarEntityCreateCode.InvalidCalendarData);
        var dispatch = await transport.CreateCalendarResourceAsync(
            new CalendarResourceCreateRequest(
                calendar.Href,
                CalendarResourceCreateProtocol.BuildResourceHref(calendar.Href, uid),
                authoritativeUtf8),
            cancellationToken);
        var conflict = CreateConflict(dispatch.Code);
        if (conflict is not null)
            return conflict;
        return await VerifyCreateAsync(
            calendar.Href,
            uid,
            CalendarResourceProjectionKind.Event,
            authoritativeUtf8,
            dispatch,
            startedTimestamp);
    }

    private async Task<CalendarEntityCreateResult> TryCreateTodoIdentityAsync(
        CalendarDescriptor calendar,
        CalendarTodoCreateFields fields,
        string uid,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        if (!TrySerializeTodo(uid, fields, out var authoritativeUtf8))
            return Failure(CalendarEntityCreateCode.InvalidCalendarData);
        var dispatch = await transport.CreateCalendarResourceAsync(
            new CalendarResourceCreateRequest(
                calendar.Href,
                CalendarResourceCreateProtocol.BuildResourceHref(calendar.Href, uid),
                authoritativeUtf8),
            cancellationToken);
        var conflict = CreateConflict(dispatch.Code);
        if (conflict is not null)
            return conflict;
        return await VerifyCreateAsync(
            calendar.Href,
            uid,
            CalendarResourceProjectionKind.Todo,
            authoritativeUtf8,
            dispatch,
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
        CalendarResourceCreateResult dispatch,
        long startedTimestamp)
    {
        var possiblyDispatched = dispatch.Code == CalendarResourceCreateCode.PossiblyDispatched;
        if (dispatch.Code != CalendarResourceCreateCode.Dispatched && !possiblyDispatched)
        {
            return new CreateVerificationRead(
                null,
                TransportFailure(dispatch.Code));
        }

        var overallRemaining = MutationExecutionBudget - timeProvider.GetElapsedTime(startedTimestamp);
        var remaining = overallRemaining < ReconciliationBudget ? overallRemaining : ReconciliationBudget;
        if (remaining <= TimeSpan.Zero)
            return VerificationDeadlineFailure(possiblyDispatched);

        using var reconciliationDeadline = new CancellationTokenSource(remaining, timeProvider);
        try
        {
            var observed = await transport.GetCalendarResourceAsync(
                dispatch.ResourceHref,
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
        var discovered = await transport.GetCalendarsAsync(cancellationToken);
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

    private CalendarEntityCreateResult? PrevalidateEventRequest(CalendarEventCreateRequest request) =>
        PrevalidateCreateRequest(request.Destination, request.Fields.RecurrenceSet?.Rule)
        ?? PrevalidateRecurrence(
            () => CalendarEntityCreateValidator.ValidateEventRecurrencePreNetwork(request.Fields))
        ?? PrevalidateRecurrenceDates(request.Fields.RecurrenceSet?.RecurrenceDates);

    private CalendarEntityCreateResult? PrevalidateTodoRequest(CalendarTodoCreateRequest request) =>
        PrevalidateCreateRequest(request.Destination, request.Fields.RecurrenceSet?.Rule)
        ?? PrevalidateRecurrence(
            () => CalendarEntityCreateValidator.ValidateTodoRecurrencePreNetwork(request.Fields))
        ?? PrevalidateRecurrenceDates(request.Fields.RecurrenceSet?.RecurrenceDates);

    private static CalendarEntityCreateResult? PrevalidateRecurrenceDates(
        IReadOnlyList<CalendarRecurrenceDateCreate>? recurrenceDates) =>
        HasRecurrencePeriod(recurrenceDates)
            ? Failure(CalendarEntityCreateCode.UnsupportedCapability)
            : null;

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

    private static CalendarEntityCreateResult? PrevalidateRecurrence(Action validate)
    {
        try
        {
            validate();
            return null;
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

    private static CalendarEntityCreateResult? CreateConflict(CalendarResourceCreateCode code) => code switch
    {
        CalendarResourceCreateCode.DestinationConflict => Failure(
            CalendarEntityCreateCode.DestinationConflict,
            mutationState: CalendarMutationState.NotCommitted),
        CalendarResourceCreateCode.UidConflict or CalendarResourceCreateCode.Conflict => Failure(
            CalendarEntityCreateCode.Conflict,
            mutationState: CalendarMutationState.NotCommitted),
        _ => null
    };

    private static bool IsDefiniteIdentityConflict(CalendarEntityCreateCode code) => code is
        CalendarEntityCreateCode.DestinationConflict or CalendarEntityCreateCode.Conflict;

    private async Task<CalendarExactResourceResult> CreateExactAsync(
        CalendarReviewedExactCreate reviewedCreate,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(PreDispatchBudget, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await CreateExactCoreAsync(reviewedCreate, linked.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return ExactFailure(
                CalendarExactResourceCode.LimitExhausted,
                limits: new CalendarEntityCreateExecutionLimits(
                    Dimension: CalendarEntityCreateLimitDimension.ElapsedTime));
        }
        catch (Exception exception) when (IsExactPhaseFailure(exception, cancellationToken))
        {
            return FromExactPhaseFailure(exception, CalendarExactResourcePhase.Execution);
        }
    }

    private async Task<CalendarExactResourceResult> CreateExactCoreAsync(
        CalendarReviewedExactCreate reviewedCreate,
        CancellationToken cancellationToken)
    {
        var integrityFailure = ValidateReviewedExactCreate(reviewedCreate);
        if (integrityFailure is not null)
            return integrityFailure;
        var dispatch = await transport.CreateCalendarResourceAsync(
            new CalendarResourceCreateRequest(
                reviewedCreate.CalendarHref,
                reviewedCreate.Binding.DestinationHref,
                reviewedCreate.AuthoritativeUtf8),
            cancellationToken);
        return await VerifyExactCreateAsync(reviewedCreate, dispatch);
    }

    private CalendarExactResourceResult? ValidateReviewedExactCreate(CalendarReviewedExactCreate reviewedCreate)
    {
        var request = new CalendarExactCreateRequest(
            reviewedCreate.Binding.DestinationHref,
            reviewedCreate.AuthoritativeUtf8);
        var shapeFailure = ValidateExactCreateShape(request);
        if (shapeFailure is not null)
            return shapeFailure;
        if (!string.Equals(reviewedCreate.Binding.PolicyVersion, ExactCreatePolicyVersion, StringComparison.Ordinal)
            || !IsDirectResourceOf(reviewedCreate.Binding.DestinationHref, reviewedCreate.CalendarHref)
            || !HasExactIntentDigest(reviewedCreate)
            || !HasExactIdentity(reviewedCreate))
        {
            return ExactFailure(
                CalendarExactResourceCode.InvalidCalendarData,
                CalendarExactResourcePhase.CompleteResourceSemantics);
        }
        return null;
    }

    private static bool HasExactIntentDigest(CalendarReviewedExactCreate reviewedCreate)
    {
        var observed = SHA256.HashData(reviewedCreate.AuthoritativeUtf8.Span);
        return CryptographicOperations.FixedTimeEquals(observed, reviewedCreate.Binding.IntentDigest.Span);
    }

    private static bool HasExactIdentity(CalendarReviewedExactCreate reviewedCreate) =>
        CalendarExactResourceValidator.TryValidate(
            reviewedCreate.AuthoritativeUtf8.Span,
            out var identity)
        && string.Equals(identity.EntityUid, reviewedCreate.Binding.EntityUid, StringComparison.Ordinal)
        && identity.EntityKind == reviewedCreate.Binding.EntityKind;

    private async Task<CalendarExactResourceResult> VerifyExactCreateAsync(
        CalendarReviewedExactCreate reviewedCreate,
        CalendarResourceCreateResult dispatch)
    {
        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Reconcile);
        if (dispatch.Code is not (CalendarResourceCreateCode.Dispatched or CalendarResourceCreateCode.PossiblyDispatched))
            return FromExactCreateFailure(dispatch);
        var observed = await ObserveExactAsync(reviewedCreate.Binding.DestinationHref);
        if (observed is null)
            return MissingExactObservation(dispatch.Code);
        return ClassifyExactObservation(reviewedCreate, dispatch.Code, observed);
    }

    private async Task<CalendarResourceRead?> ObserveExactAsync(string href)
    {
        using var verification = new CancellationTokenSource(ReconciliationBudget, timeProvider);
        try
        {
            return await transport.GetCalendarResourceAsync(href, verification.Token);
        }
        catch (Exception exception) when (exception is HttpRequestException
            or IOException
            or TimeoutException
            or OperationCanceledException)
        {
            return null;
        }
    }

    private static CalendarExactResourceResult ClassifyExactObservation(
        CalendarReviewedExactCreate reviewedCreate,
        CalendarResourceCreateCode dispatchCode,
        CalendarResourceRead observed)
    {
        var contentMatches = CalendarEntityCreateFidelity.IsExactEquivalent(
            reviewedCreate.AuthoritativeUtf8.Span,
            observed.AuthoritativeUtf8.Span);
        if (observed.Code == CalendarResourceReadCode.ConcurrencyUnavailable)
            return ClassifyWeakExactObservation(contentMatches, dispatchCode);
        if (observed.Code != CalendarResourceReadCode.Success)
            return MissingExactObservation(dispatchCode);

        var snapshot = CalendarResourceProjector.AttachSnapshot(reviewedCreate.CalendarHref, observed).Snapshot!;
        var sameIdentity = CalendarExactResourceValidator.TryValidate(
            snapshot.AuthoritativeUtf8.Span,
            out var observedIdentity)
            && string.Equals(observedIdentity.EntityUid, reviewedCreate.Binding.EntityUid, StringComparison.Ordinal)
            && observedIdentity.EntityKind == reviewedCreate.Binding.EntityKind;
        if (sameIdentity && contentMatches)
            return CalendarExactResourceResult.Success(snapshot);
        return dispatchCode == CalendarResourceCreateCode.PossiblyDispatched
            ? ExactUnknown(snapshot)
            : ExactPostWrite(CalendarExactResourceCode.FidelityFailure, snapshot);
    }

    private static CalendarExactResourceResult ClassifyWeakExactObservation(
        bool contentMatches,
        CalendarResourceCreateCode dispatchCode)
    {
        if (contentMatches)
            return ExactPostWrite(CalendarExactResourceCode.CommittedButConcurrencyUnavailable);
        return dispatchCode == CalendarResourceCreateCode.PossiblyDispatched
            ? ExactUnknown()
            : ExactPostWrite(CalendarExactResourceCode.FidelityFailure);
    }

    private async Task<ExactPreparedCreate> PrepareExactCreateAsync(
        CalendarExactCreateRequest request,
        CancellationToken cancellationToken)
    {
        var discovery = await DiscoverExactCalendarsAsync(cancellationToken);
        if (discovery.Failure is not null)
            return new ExactPreparedCreate(null, null, discovery.Failure);
        var destination = discovery.Calendars!.SingleOrDefault(calendar =>
            IsDirectResourceOf(request.DestinationHref, calendar.Href));
        if (destination is null)
        {
            return new ExactPreparedCreate(
                null,
                null,
                ExactFailure(
                    CalendarExactResourceCode.OutsideScope,
                    CalendarExactResourcePhase.SelectionDiscoveryCapability));
        }
        if (!CalendarExactResourceValidator.TryReadIdentity(request.AuthoritativeUtf8.Span, out var identity))
        {
            return new ExactPreparedCreate(
                null,
                null,
                ExactFailure(
                    CalendarExactResourceCode.InvalidCalendarData,
                    CalendarExactResourcePhase.CompleteResourceSemantics));
        }
        if (!Advertises(destination, identity.EntityKind))
        {
            return new ExactPreparedCreate(
                null,
                null,
                ExactFailure(
                    CalendarExactResourceCode.UnsupportedCapability,
                    CalendarExactResourcePhase.SelectionDiscoveryCapability));
        }
        return await ReadAndValidateExactDestinationAsync(request, destination, identity, cancellationToken);
    }

    private async Task<ExactPreparedCreate> ReadAndValidateExactDestinationAsync(
        CalendarExactCreateRequest request,
        CalendarDescriptor destination,
        CalendarExactResourceIdentity identity,
        CancellationToken cancellationToken)
    {
        var target = await ReadExactTargetAsync(request.DestinationHref, cancellationToken);
        if (target.Failure is not null)
            return new ExactPreparedCreate(null, null, target.Failure);
        if (target.Read!.Code != CalendarResourceReadCode.NotFound)
        {
            return new ExactPreparedCreate(
                null,
                null,
                ExistingExactDestinationFailure(target.Read.Code));
        }
        if (!CalendarExactResourceValidator.TryValidate(request.AuthoritativeUtf8.Span, out identity))
        {
            return new ExactPreparedCreate(
                null,
                null,
                ExactFailure(
                    CalendarExactResourceCode.InvalidCalendarData,
                    CalendarExactResourcePhase.CompleteResourceSemantics));
        }
        return new ExactPreparedCreate(destination, identity, null);
    }

    private async Task<ExactDiscoveryAttempt> DiscoverExactCalendarsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return new ExactDiscoveryAttempt(
                applyScope(await transport.GetCalendarsAsync(cancellationToken)).Items,
                null);
        }
        catch (Exception exception) when (IsExactPhaseFailure(exception, cancellationToken))
        {
            return new ExactDiscoveryAttempt(
                null,
                FromExactPhaseFailure(
                    exception,
                    CalendarExactResourcePhase.SelectionDiscoveryCapability));
        }
    }

    private async Task<ExactReadAttempt> ReadExactTargetAsync(string href, CancellationToken cancellationToken)
    {
        try
        {
            return new ExactReadAttempt(
                await transport.GetCalendarResourceAsync(href, cancellationToken),
                null);
        }
        catch (Exception exception) when (IsExactPhaseFailure(exception, cancellationToken))
        {
            return new ExactReadAttempt(
                null,
                FromExactPhaseFailure(exception, CalendarExactResourcePhase.TargetRevision));
        }
    }

    private static CalendarExactCreateReviewResult SuccessfulExactReview(
        CalendarExactCreateRequest request,
        ExactPreparedCreate preparation)
    {
        var authoritativeUtf8 = request.AuthoritativeUtf8.ToArray();
        var identity = preparation.Identity!;
        var binding = new CalendarExactCreateReviewBinding(
            request.DestinationHref,
            identity.EntityUid,
            identity.EntityKind,
            SHA256.HashData(authoritativeUtf8),
            ExactCreatePolicyVersion);
        return new CalendarExactCreateReviewResult(
            null,
            binding,
            new CalendarReviewedExactCreate(
                preparation.Calendar!.Href,
                binding,
                authoritativeUtf8));
    }

    private CalendarExactResourceResult? ValidateExactCreateShape(CalendarExactCreateRequest request)
    {
        if (request.AuthoritativeUtf8.IsEmpty)
        {
            return ExactFailure(
                CalendarExactResourceCode.InvalidInput,
                CalendarExactResourcePhase.SchemaLexicalDiscriminator);
        }
        if (request.AuthoritativeUtf8.Length > MaximumCalendarResourceBytes)
        {
            return ExactFailure(
                CalendarExactResourceCode.PayloadTooLarge,
                CalendarExactResourcePhase.SchemaLexicalDiscriminator);
        }
        if (!TryValidateExactResourceHref(request.DestinationHref))
        {
            return ExactFailure(
                CalendarExactResourceCode.InvalidInput,
                CalendarExactResourcePhase.OriginScopeAuthorization);
        }
        return ValidateExactOriginAndScope(request.DestinationHref);
    }

    private CalendarExactResourceResult? ValidateExactOriginAndScope(string href)
    {
        var resource = new Uri(href, UriKind.Absolute);
        if (!HasSameOrigin(new Uri(options.BaseUrl, UriKind.Absolute), resource))
        {
            return ExactFailure(
                CalendarExactResourceCode.InvalidInput,
                CalendarExactResourcePhase.OriginScopeAuthorization);
        }
        var configuredScope = ParseScope(options.CalendarHrefs);
        return configuredScope.Count == 0
            || configuredScope.Any(calendarHref => IsDirectResourceOf(href, calendarHref))
            ? null
            : ExactFailure(
                CalendarExactResourceCode.OutsideScope,
                CalendarExactResourcePhase.OriginScopeAuthorization);
    }

    private static bool TryValidateExactResourceHref(string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var resource)
        && HasSafeExactHref(resource, href);

    private static bool HasSafeExactHref(Uri uri, string original) =>
        (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment)
        && !uri.AbsolutePath.Contains("%2F", StringComparison.OrdinalIgnoreCase)
        && !uri.AbsolutePath.Contains("%5C", StringComparison.OrdinalIgnoreCase)
        && !original.Contains("%2e", StringComparison.OrdinalIgnoreCase)
        && !original.Contains('\\')
        && string.Equals(uri.AbsoluteUri, original, StringComparison.Ordinal);

    private static bool IsDirectResourceOf(string resourceHref, string calendarHref)
    {
        if (!Uri.TryCreate(resourceHref, UriKind.Absolute, out var resource)
            || !Uri.TryCreate(calendarHref, UriKind.Absolute, out var calendar)
            || !HasSameOrigin(resource, calendar))
        {
            return false;
        }
        var calendarPath = calendar.AbsolutePath.EndsWith('/') ? calendar.AbsolutePath : calendar.AbsolutePath + '/';
        if (!resource.AbsolutePath.StartsWith(calendarPath, StringComparison.Ordinal))
            return false;
        var relative = resource.AbsolutePath[calendarPath.Length..];
        return relative.Length > 0 && !relative.Contains('/');
    }

    private static bool Advertises(CalendarDescriptor calendar, CalendarEntityKind kind) => kind switch
    {
        CalendarEntityKind.Event => calendar.EventSupport == EntityKindSupport.Advertised,
        CalendarEntityKind.Todo => calendar.TodoSupport == EntityKindSupport.Advertised,
        _ => false
    };

    private static bool IsExactPhaseFailure(Exception exception, CancellationToken cancellationToken) => exception is
        HttpRequestException or IOException or TimeoutException or XmlException or CalendarDiscoveryProtocolException
        || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private static CalendarExactResourceResult FromExactPhaseFailure(
        Exception exception,
        CalendarExactResourcePhase phase) => exception switch
        {
            HttpRequestException http => FromExactHttpFailure(http.StatusCode, phase),
            OperationCanceledException or IOException or TimeoutException =>
                ExactFailure(CalendarExactResourceCode.UpstreamUnavailable, phase, retryable: true),
            XmlException or CalendarDiscoveryProtocolException =>
                ExactFailure(CalendarExactResourceCode.UpstreamProtocolError, phase),
            _ => throw new InvalidOperationException("The exception is not a supported Exact Create failure.", exception)
        };

    private static CalendarExactResourceResult FromExactHttpFailure(
        System.Net.HttpStatusCode? statusCode,
        CalendarExactResourcePhase phase) => ExactFailure(
        statusCode switch
        {
            System.Net.HttpStatusCode.Unauthorized => CalendarExactResourceCode.UpstreamUnauthorized,
            System.Net.HttpStatusCode.Forbidden => CalendarExactResourceCode.UpstreamForbidden,
            System.Net.HttpStatusCode.TooManyRequests => CalendarExactResourceCode.UpstreamRateLimited,
            >= System.Net.HttpStatusCode.InternalServerError => CalendarExactResourceCode.UpstreamUnavailable,
            _ => CalendarExactResourceCode.UpstreamProtocolError
        },
        phase,
        statusCode is System.Net.HttpStatusCode.TooManyRequests or >= System.Net.HttpStatusCode.InternalServerError);

    private static CalendarExactResourceResult ExistingExactDestinationFailure(CalendarResourceReadCode code) => code switch
    {
        CalendarResourceReadCode.Success or CalendarResourceReadCode.ConcurrencyUnavailable =>
            ExactFailure(CalendarExactResourceCode.DestinationConflict),
        _ => ExactReadFailure(code)
    };

    private static CalendarExactResourceResult ExactReadFailure(CalendarResourceReadCode code) => ExactFailure(
        code switch
        {
            CalendarResourceReadCode.InvalidInput => CalendarExactResourceCode.InvalidInput,
            CalendarResourceReadCode.NotFound => CalendarExactResourceCode.NotFound,
            CalendarResourceReadCode.OutsideScope => CalendarExactResourceCode.OutsideScope,
            CalendarResourceReadCode.ConcurrencyUnavailable => CalendarExactResourceCode.ConcurrencyUnavailable,
            CalendarResourceReadCode.PayloadTooLarge => CalendarExactResourceCode.PayloadTooLarge,
            CalendarResourceReadCode.UnsupportedCapability => CalendarExactResourceCode.UnsupportedCapability,
            _ => CalendarExactResourceCode.UpstreamProtocolError
        },
        CalendarExactResourcePhase.TargetRevision);

    private static CalendarExactResourceResult FromExactCreateFailure(CalendarResourceCreateResult dispatch) =>
        ExactRejected(
            dispatch.Code switch
            {
                CalendarResourceCreateCode.DestinationConflict => CalendarExactResourceCode.DestinationConflict,
                CalendarResourceCreateCode.UidConflict or CalendarResourceCreateCode.Conflict =>
                    CalendarExactResourceCode.Conflict,
                CalendarResourceCreateCode.UnsupportedCapability => CalendarExactResourceCode.UnsupportedCapability,
                CalendarResourceCreateCode.PayloadTooLarge => CalendarExactResourceCode.PayloadTooLarge,
                CalendarResourceCreateCode.NotFound => CalendarExactResourceCode.NotFound,
                CalendarResourceCreateCode.UpstreamUnauthorized => CalendarExactResourceCode.UpstreamUnauthorized,
                CalendarResourceCreateCode.UpstreamForbidden => CalendarExactResourceCode.UpstreamForbidden,
                CalendarResourceCreateCode.UpstreamRateLimited => CalendarExactResourceCode.UpstreamRateLimited,
                CalendarResourceCreateCode.UpstreamUnavailable => CalendarExactResourceCode.UpstreamUnavailable,
                _ => CalendarExactResourceCode.UpstreamProtocolError
            },
            dispatch.Code == CalendarResourceCreateCode.UpstreamRateLimited);

    private static CalendarExactResourceResult MissingExactObservation(CalendarResourceCreateCode dispatchCode) =>
        dispatchCode == CalendarResourceCreateCode.PossiblyDispatched
            ? ExactUnknown()
            : ExactPostWrite(CalendarExactResourceCode.CommittedButUnverified);

    private static CalendarExactResourceResult ExactFailure(
        CalendarExactResourceCode code,
        CalendarExactResourcePhase phase = CalendarExactResourcePhase.Execution,
        bool retryable = false,
        CalendarEntityCreateExecutionLimits? limits = null) => new(
            code,
            CalendarMutationState.NotAttempted,
            Retryable: retryable,
            Phase: phase,
            Limits: limits);

    private static CalendarExactResourceResult ExactRejected(
        CalendarExactResourceCode code,
        bool retryable = false,
        int? retryAfterMilliseconds = null) => new(
            code,
            CalendarMutationState.NotCommitted,
            Retryable: retryable,
            RetryAfterMilliseconds: retryAfterMilliseconds);

    private static CalendarExactResourceResult ExactPostWrite(
        CalendarExactResourceCode code,
        CalendarResourceSnapshot? snapshot = null) => new(
            code,
            CalendarMutationState.Committed,
            snapshot,
            Phase: CalendarExactResourcePhase.PostWriteVerificationOrReconciliation);

    private static CalendarExactResourceResult ExactUnknown(CalendarResourceSnapshot? snapshot = null) => new(
        CalendarExactResourceCode.Indeterminate,
        CalendarMutationState.Unknown,
        snapshot,
        Phase: CalendarExactResourcePhase.PostWriteVerificationOrReconciliation);

    private static CalendarExactCreateReviewResult FailedExactReview(CalendarExactResourceResult failure) =>
        new(failure, null, null);

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

    private sealed record ExactPreparedCreate(
        CalendarDescriptor? Calendar,
        CalendarExactResourceIdentity? Identity,
        CalendarExactResourceResult? Failure);

    private sealed record ExactDiscoveryAttempt(
        IReadOnlyList<CalendarDescriptor>? Calendars,
        CalendarExactResourceResult? Failure);

    private sealed record ExactReadAttempt(
        CalendarResourceRead? Read,
        CalendarExactResourceResult? Failure);

}
