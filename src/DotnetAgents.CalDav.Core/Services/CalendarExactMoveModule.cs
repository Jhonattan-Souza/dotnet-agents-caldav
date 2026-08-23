using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Xml;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Services;

/// <summary>Owns constant-work Exact Move review, confirmation, and plan consumption.</summary>
internal sealed class CalendarExactMoveModule(
    ICalendarMoveTransport transport,
    CalDavOptions options,
    TimeProvider timeProvider)
{
    internal const string PolicyVersion = "server-authoritative-exact-move/1";
    private static readonly TimeSpan PreDispatchTimeout = TimeSpan.FromSeconds(30);

    public async Task<CalendarExactMoveReviewResult> ReviewAsync(
        CalendarExactMoveRequest request,
        CancellationToken cancellationToken)
    {
        InitializeMoveProgress();
        var preparation = await PrepareWithinBudgetAsync(request, cancellationToken).ConfigureAwait(false);
        return new CalendarExactMoveReviewResult(preparation.Outcome, preparation.Binding);
    }

    public async Task<CalendarExactResourceResult> ExecuteConfirmedAsync(
        CalendarExactMoveRequest request,
        CalendarExactMoveReviewBinding priorBinding,
        CancellationToken cancellationToken)
    {
        InitializeMoveProgress();
        var inputFailure = ValidateInput(request);
        if (inputFailure is not null)
            return inputFailure;
        using var deadline = new CancellationTokenSource(PreDispatchTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var preparation = await PrepareAsync(request, linked.Token).ConfigureAwait(false);
            if (preparation.Outcome is not null)
                return preparation.Outcome;
            if (!Matches(priorBinding, preparation.Binding!))
                return Failure(
                    CalendarExactResourceCode.ConfirmationMismatch,
                    CalendarExactResourcePhase.Mrtr);

            var plan = new CalendarReviewedMovePlan(new CalendarReviewedMovePreparation(
                request.Revision,
                preparation.Source!,
                preparation.Source!.CalendarHref,
                preparation.DestinationCalendarHref!,
                request.DestinationHref,
                CalendarMoveFidelityMode.Exact));
            var result = await new CalendarMoveDispatcher(transport, timeProvider)
                .DispatchAsync(plan, linked.Token)
                .ConfigureAwait(false);
            return FromSharedResult(result);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return Failure(CalendarExactResourceCode.LimitExhausted);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(CalendarExactResourceCode.UpstreamUnavailable, retryable: true);
        }
        catch (HttpRequestException exception)
        {
            return FromHttpFailure(exception.StatusCode);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return Failure(CalendarExactResourceCode.UpstreamUnavailable, retryable: true);
        }
        catch (Exception exception) when (exception is XmlException or CalendarDiscoveryProtocolException)
        {
            return Failure(CalendarExactResourceCode.UpstreamProtocolError);
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return Failure(CalendarExactResourceCode.UnsupportedCapability);
        }
        catch (CalendarDiscoveryLimitException)
        {
            return Failure(CalendarExactResourceCode.LimitExhausted);
        }
    }

    private async Task<ExactMovePreparation> PrepareWithinBudgetAsync(
        CalendarExactMoveRequest request,
        CancellationToken cancellationToken)
    {
        var inputFailure = ValidateInput(request);
        if (inputFailure is not null)
            return ExactMovePreparation.Failed(inputFailure);
        using var deadline = new CancellationTokenSource(PreDispatchTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await PrepareAsync(request, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return ExactMovePreparation.Failed(Failure(CalendarExactResourceCode.LimitExhausted));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ExactMovePreparation.Failed(Failure(
                CalendarExactResourceCode.UpstreamUnavailable,
                retryable: true));
        }
        catch (HttpRequestException exception)
        {
            return ExactMovePreparation.Failed(FromHttpFailure(exception.StatusCode));
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return ExactMovePreparation.Failed(Failure(
                CalendarExactResourceCode.UpstreamUnavailable,
                retryable: true));
        }
        catch (Exception exception) when (exception is XmlException or CalendarDiscoveryProtocolException)
        {
            return ExactMovePreparation.Failed(Failure(CalendarExactResourceCode.UpstreamProtocolError));
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return ExactMovePreparation.Failed(Failure(CalendarExactResourceCode.UnsupportedCapability));
        }
        catch (CalendarDiscoveryLimitException)
        {
            return ExactMovePreparation.Failed(Failure(CalendarExactResourceCode.LimitExhausted));
        }
    }

    private async Task<ExactMovePreparation> PrepareAsync(
        CalendarExactMoveRequest request,
        CancellationToken cancellationToken)
    {
        var discovery = await transport.DiscoverCalendarsAsync(cancellationToken).ConfigureAwait(false);
        var calendars = discovery.ScopedDiscovery.Items;
        var sourceCalendar = FindCalendar(request.Revision.Href, calendars);
        var destinationCalendar = FindCalendar(request.DestinationHref, calendars);
        if (sourceCalendar is null || destinationCalendar is null)
        {
            return ExactMovePreparation.Failed(Failure(
                CalendarExactResourceCode.OutsideScope,
                CalendarExactResourcePhase.OriginScopeAuthorization));
        }
        if (!SupportsExactMove(destinationCalendar, request.Revision.EntityKind))
        {
            return ExactMovePreparation.Failed(Failure(
                CalendarExactResourceCode.UnsupportedCapability,
                CalendarExactResourcePhase.SelectionDiscoveryCapability));
        }
        if (EntityTagHeaderValue.Parse(request.Revision.EntityTag).IsWeak)
        {
            return ExactMovePreparation.Failed(Failure(
                CalendarExactResourceCode.ConcurrencyUnavailable,
                CalendarExactResourcePhase.TargetRevision));
        }

        var sourceAttempt = await ReadSourceAsync(
            sourceCalendar.Href,
            request.Revision.Href,
            cancellationToken).ConfigureAwait(false);
        if (sourceAttempt.Failure is not null)
            return ExactMovePreparation.Failed(sourceAttempt.Failure);
        var sourceRead = sourceAttempt.Read!;
        if (sourceRead.Code != CalendarResourceReadCode.Success || sourceRead.Snapshot is null)
            return ExactMovePreparation.Failed(FromReadFailure(sourceRead.Code));
        var revisionFailure = ValidateCurrentRevision(request.Revision, sourceRead.Snapshot);
        if (revisionFailure is not null)
            return ExactMovePreparation.Failed(RecordRevisionFailure(revisionFailure));

        var destinationRead = await transport
            .ProbeDestinationPresenceAsync(
                destinationCalendar.Href,
                request.DestinationHref,
                cancellationToken)
            .ConfigureAwait(false);
        if (destinationRead.Code != CalendarResourceReadCode.NotFound)
            return ExactMovePreparation.Failed(RecordDestinationFailure(destinationRead.Code));
        CalendarOperationProgress.SetMoveCollision(CalendarMoveCollisionClassification.None);

        var binding = new CalendarExactMoveReviewBinding(
            request.Revision,
            request.DestinationHref,
            BindSourceIntent(sourceRead.Snapshot.AuthoritativeUtf8.Span, request.DestinationHref),
            PolicyVersion);
        return new ExactMovePreparation(
            null,
            binding,
            sourceRead.Snapshot,
            destinationCalendar.Href);
    }

    private CalendarExactResourceResult? ValidateInput(CalendarExactMoveRequest request)
    {
        if (!CalendarMoveHrefPolicy.TryParseSafeResourceHref(request.Revision.Href, out var source)
            || !CalendarMoveHrefPolicy.TryParseSafeResourceHref(request.DestinationHref, out var destination)
            || string.Equals(request.Revision.Href, request.DestinationHref, StringComparison.Ordinal)
            || !HasValidRevisionShape(request.Revision))
        {
            return Failure(
                CalendarExactResourceCode.InvalidInput,
                CalendarExactResourcePhase.SchemaLexicalDiscriminator);
        }
        var origin = new Uri(options.BaseUrl, UriKind.Absolute);
        if (!CalendarMoveHrefPolicy.HasSameOrigin(origin, source)
            || !CalendarMoveHrefPolicy.HasSameOrigin(origin, destination))
        {
            return Failure(
                CalendarExactResourceCode.InvalidInput,
                CalendarExactResourcePhase.OriginScopeAuthorization);
        }
        var scope = ParseScope(options.CalendarHrefs);
        return scope.Count > 0
            && (!scope.Any(href => CalendarMoveHrefPolicy.IsDirectResourceOf(source, href))
                || !scope.Any(href => CalendarMoveHrefPolicy.IsDirectResourceOf(destination, href)))
            ? Failure(
                CalendarExactResourceCode.OutsideScope,
                CalendarExactResourcePhase.OriginScopeAuthorization)
            : null;
    }

    private static bool HasValidRevisionShape(CalendarResourceRevisionReference revision)
    {
        if (string.IsNullOrWhiteSpace(revision.EntityUid)
            || !Enum.IsDefined(revision.EntityKind)
            || !EntityTagHeaderValue.TryParse(revision.EntityTag, out var entityTag)
            || entityTag is null)
        {
            return false;
        }
        return entityTag != EntityTagHeaderValue.Any
            && string.Equals(entityTag.ToString(), revision.EntityTag, StringComparison.Ordinal);
    }

    private static CalendarExactResourceResult? ValidateCurrentRevision(
        CalendarResourceRevisionReference revision,
        CalendarResourceSnapshot source)
    {
        if (!string.Equals(source.EntityTag, revision.EntityTag, StringComparison.Ordinal))
        {
            return new CalendarExactResourceResult(
                CalendarExactResourceCode.Conflict,
                CalendarMutationState.NotAttempted,
                source,
                Phase: CalendarExactResourcePhase.TargetRevision);
        }
        if (!CalendarExactResourceValidator.TryValidate(source.AuthoritativeUtf8.Span, out var identity))
        {
            return new CalendarExactResourceResult(
                CalendarExactResourceCode.InvalidCalendarData,
                CalendarMutationState.NotAttempted,
                source,
                Phase: CalendarExactResourcePhase.TargetRevision);
        }
        if (identity.EntityKind != revision.EntityKind)
        {
            return new CalendarExactResourceResult(
                CalendarExactResourceCode.EntityKindMismatch,
                CalendarMutationState.NotAttempted,
                source,
                Phase: CalendarExactResourcePhase.TargetRevision);
        }
        return string.Equals(identity.EntityUid, revision.EntityUid, StringComparison.Ordinal)
            ? null
            : new CalendarExactResourceResult(
                CalendarExactResourceCode.Conflict,
                CalendarMutationState.NotAttempted,
                source,
                Phase: CalendarExactResourcePhase.TargetRevision);
    }

    private static bool Matches(
        CalendarExactMoveReviewBinding expected,
        CalendarExactMoveReviewBinding actual) =>
        expected.Revision == actual.Revision
        && string.Equals(expected.DestinationHref, actual.DestinationHref, StringComparison.Ordinal)
        && string.Equals(expected.PolicyVersion, actual.PolicyVersion, StringComparison.Ordinal)
        && expected.SourceIntentDigest.Length == SHA256.HashSizeInBytes
        && actual.SourceIntentDigest.Length == SHA256.HashSizeInBytes
        && CryptographicOperations.FixedTimeEquals(
            expected.SourceIntentDigest.Span,
            actual.SourceIntentDigest.Span);

    private static byte[] BindSourceIntent(ReadOnlySpan<byte> authoritativeUtf8, string destinationHref)
    {
        var destination = System.Text.Encoding.UTF8.GetBytes(destinationHref);
        var value = new byte[authoritativeUtf8.Length + 1 + destination.Length];
        authoritativeUtf8.CopyTo(value);
        destination.CopyTo(value.AsSpan(authoritativeUtf8.Length + 1));
        return SHA256.HashData(value);
    }

    private CalendarDescriptor? FindCalendar(
        string resourceHref,
        IReadOnlyList<CalendarDescriptor> calendars)
    {
        var resource = new Uri(resourceHref, UriKind.Absolute);
        return calendars
            .Where(calendar => CalendarMoveHrefPolicy.IsSafeCalendarHref(calendar.Href, options.BaseUrl)
                && CalendarMoveHrefPolicy.IsDirectResourceOf(resource, calendar.Href))
            .OrderByDescending(calendar => calendar.Href.Length)
            .FirstOrDefault();
    }

    private static bool Advertises(CalendarDescriptor calendar, CalendarEntityKind kind) => kind switch
    {
        CalendarEntityKind.Event => calendar.EventSupport == EntityKindSupport.Advertised,
        CalendarEntityKind.Todo => calendar.TodoSupport == EntityKindSupport.Advertised,
        _ => false
    };

    private bool SupportsExactMove(CalendarDescriptor calendar, CalendarEntityKind kind) =>
        string.Equals(
            options.InteroperabilityProfile,
            CalDavInteroperabilityProfiles.Radicale_3_7_8,
            StringComparison.Ordinal)
        && Advertises(calendar, kind);

    private static CalendarResourceRead Attach(string calendarHref, CalendarResourceRead read) =>
        read.Code == CalendarResourceReadCode.Success
            ? CalendarResourceProjector.AttachSnapshot(calendarHref, read)
            : read;

    private async Task<ExactMoveSourceRead> ReadSourceAsync(
        string calendarHref,
        string resourceHref,
        CancellationToken cancellationToken)
    {
        try
        {
            var read = await transport.ReadSourceAsync(
                calendarHref,
                resourceHref,
                cancellationToken).ConfigureAwait(false);
            return new ExactMoveSourceRead(Attach(calendarHref, read), null);
        }
        catch (HttpRequestException exception)
        {
            return ExactMoveSourceRead.Failed(FromHttpFailure(
                exception.StatusCode,
                CalendarExactResourcePhase.TargetRevision));
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return ExactMoveSourceRead.Failed(Failure(
                CalendarExactResourceCode.UpstreamUnavailable,
                CalendarExactResourcePhase.TargetRevision,
                retryable: true));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ExactMoveSourceRead.Failed(Failure(
                CalendarExactResourceCode.UpstreamUnavailable,
                CalendarExactResourcePhase.TargetRevision,
                retryable: true));
        }
        catch (Exception exception) when (exception is XmlException or CalendarDiscoveryProtocolException)
        {
            return ExactMoveSourceRead.Failed(Failure(
                CalendarExactResourceCode.UpstreamProtocolError,
                CalendarExactResourcePhase.TargetRevision));
        }
    }

    private static CalendarExactResourceResult DestinationFailure(CalendarResourceReadCode code) => code switch
    {
        CalendarResourceReadCode.Success
            or CalendarResourceReadCode.ConcurrencyUnavailable
            or CalendarResourceReadCode.PayloadTooLarge => Failure(
                CalendarExactResourceCode.DestinationConflict,
                CalendarExactResourcePhase.SelectionDiscoveryCapability),
        CalendarResourceReadCode.UnsupportedCapability => Failure(
            CalendarExactResourceCode.UnsupportedCapability,
            CalendarExactResourcePhase.SelectionDiscoveryCapability),
        _ => Failure(CalendarExactResourceCode.UpstreamProtocolError)
    };

    private static CalendarExactResourceResult RecordRevisionFailure(CalendarExactResourceResult failure)
    {
        if (failure.Code == CalendarExactResourceCode.Conflict)
            CalendarOperationProgress.SetMoveCollision(CalendarMoveCollisionClassification.SourceRevision);
        return failure;
    }

    private static CalendarExactResourceResult RecordDestinationFailure(CalendarResourceReadCode code)
    {
        if (code is CalendarResourceReadCode.Success
            or CalendarResourceReadCode.ConcurrencyUnavailable
            or CalendarResourceReadCode.PayloadTooLarge)
        {
            CalendarOperationProgress.SetMoveCollision(CalendarMoveCollisionClassification.DestinationHref);
        }
        return DestinationFailure(code);
    }

    private static void InitializeMoveProgress()
    {
        CalendarOperationProgress.SetMoveDispatch(CalendarMoveDispatchClassification.NotAttempted);
        CalendarOperationProgress.SetMoveCollision(CalendarMoveCollisionClassification.Unspecified);
        CalendarOperationProgress.SetMoveReconciliation(CalendarMoveReconciliationClassification.NotRun);
    }

    private static CalendarExactResourceResult FromReadFailure(CalendarResourceReadCode code) => code switch
    {
        CalendarResourceReadCode.NotFound => Failure(
            CalendarExactResourceCode.NotFound,
            CalendarExactResourcePhase.TargetRevision),
        CalendarResourceReadCode.ConcurrencyUnavailable => Failure(
            CalendarExactResourceCode.ConcurrencyUnavailable,
            CalendarExactResourcePhase.TargetRevision),
        CalendarResourceReadCode.PayloadTooLarge => Failure(
            CalendarExactResourceCode.PayloadTooLarge,
            CalendarExactResourcePhase.TargetRevision),
        CalendarResourceReadCode.UnsupportedCapability => Failure(
            CalendarExactResourceCode.UnsupportedCapability,
            CalendarExactResourcePhase.SelectionDiscoveryCapability),
        _ => Failure(CalendarExactResourceCode.UpstreamProtocolError)
    };

    private static CalendarExactResourceResult FromSharedResult(CalendarResourceMoveResult result) => new(
        result.Code switch
        {
            CalendarResourceMoveCode.Success => CalendarExactResourceCode.Success,
            CalendarResourceMoveCode.InvalidInput => CalendarExactResourceCode.InvalidInput,
            CalendarResourceMoveCode.NotFound => CalendarExactResourceCode.NotFound,
            CalendarResourceMoveCode.OutsideScope => CalendarExactResourceCode.OutsideScope,
            CalendarResourceMoveCode.EntityKindMismatch => CalendarExactResourceCode.EntityKindMismatch,
            CalendarResourceMoveCode.UnsupportedCapability => CalendarExactResourceCode.UnsupportedCapability,
            CalendarResourceMoveCode.Conflict => CalendarExactResourceCode.Conflict,
            CalendarResourceMoveCode.DestinationConflict => CalendarExactResourceCode.DestinationConflict,
            CalendarResourceMoveCode.ConcurrencyUnavailable => CalendarExactResourceCode.ConcurrencyUnavailable,
            CalendarResourceMoveCode.LimitExhausted => CalendarExactResourceCode.LimitExhausted,
            CalendarResourceMoveCode.PayloadTooLarge => CalendarExactResourceCode.PayloadTooLarge,
            CalendarResourceMoveCode.UpstreamUnauthorized => CalendarExactResourceCode.UpstreamUnauthorized,
            CalendarResourceMoveCode.UpstreamForbidden => CalendarExactResourceCode.UpstreamForbidden,
            CalendarResourceMoveCode.UpstreamRateLimited => CalendarExactResourceCode.UpstreamRateLimited,
            CalendarResourceMoveCode.UpstreamUnavailable => CalendarExactResourceCode.UpstreamUnavailable,
            CalendarResourceMoveCode.UpstreamProtocolError => CalendarExactResourceCode.UpstreamProtocolError,
            CalendarResourceMoveCode.FidelityFailure => CalendarExactResourceCode.FidelityFailure,
            CalendarResourceMoveCode.CommittedButUnverified => CalendarExactResourceCode.CommittedButUnverified,
            CalendarResourceMoveCode.CommittedButConcurrencyUnavailable =>
                CalendarExactResourceCode.CommittedButConcurrencyUnavailable,
            _ => CalendarExactResourceCode.Indeterminate
        },
        result.MutationState,
        result.Snapshot,
        result.Retryable,
        result.RetryAfterMilliseconds,
        result.Phase switch
        {
            CalendarResourceMovePhase.PostWriteVerificationOrReconciliation =>
                CalendarExactResourcePhase.PostWriteVerificationOrReconciliation,
            CalendarResourceMovePhase.Execution => CalendarExactResourcePhase.Execution,
            _ => CalendarExactResourcePhase.Execution
        });

    private static CalendarExactResourceResult FromHttpFailure(
        System.Net.HttpStatusCode? statusCode,
        CalendarExactResourcePhase phase = CalendarExactResourcePhase.SelectionDiscoveryCapability) => statusCode switch
    {
        System.Net.HttpStatusCode.Unauthorized => Failure(CalendarExactResourceCode.UpstreamUnauthorized, phase),
        System.Net.HttpStatusCode.Forbidden => Failure(CalendarExactResourceCode.UpstreamForbidden, phase),
        System.Net.HttpStatusCode.RequestEntityTooLarge => Failure(CalendarExactResourceCode.PayloadTooLarge, phase),
        System.Net.HttpStatusCode.TooManyRequests => Failure(
            CalendarExactResourceCode.UpstreamRateLimited,
            phase,
            retryable: true),
        System.Net.HttpStatusCode.MethodNotAllowed or System.Net.HttpStatusCode.NotImplemented =>
            Failure(CalendarExactResourceCode.UnsupportedCapability, phase),
        >= System.Net.HttpStatusCode.InternalServerError => Failure(
            CalendarExactResourceCode.UpstreamUnavailable,
            phase,
            retryable: true),
        _ => Failure(CalendarExactResourceCode.UpstreamProtocolError, phase)
    };

    private static CalendarExactResourceResult Failure(
        CalendarExactResourceCode code,
        CalendarExactResourcePhase phase = CalendarExactResourcePhase.SelectionDiscoveryCapability,
        bool retryable = false) => new(
            code,
            CalendarMutationState.NotAttempted,
            Retryable: retryable,
            Phase: phase);

    private sealed record ExactMoveSourceRead(
        CalendarResourceRead? Read,
        CalendarExactResourceResult? Failure)
    {
        public static ExactMoveSourceRead Failed(CalendarExactResourceResult failure) => new(null, failure);
    }

    private static IReadOnlyList<string> ParseScope(string? calendarHrefs) => calendarHrefs is null
        ? []
        : calendarHrefs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record ExactMovePreparation(
        CalendarExactResourceResult? Outcome,
        CalendarExactMoveReviewBinding? Binding,
        CalendarResourceSnapshot? Source,
        string? DestinationCalendarHref)
    {
        public static ExactMovePreparation Failed(CalendarExactResourceResult outcome) =>
            new(outcome, null, null, null);
    }
}
