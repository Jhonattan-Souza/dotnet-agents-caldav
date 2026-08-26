using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Options;

namespace DotnetAgents.CalDav.Core.Services;

/// <summary>Owns collection identity, scope, protocol orchestration, and post-write truth.</summary>
internal sealed class CalendarCollectionModule(
    ICalendarCollectionTransport transport,
    IOptions<CalDavOptions> options) : ICalendarCollectionModule
{
    private const int MaximumDisplayNameCharacters = 256;
    private const int MaximumCalendars = 256;

    public async Task<CalendarCollectionCreateResult> CreateAsync(
        CalendarCollectionCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryValidateCreateInput(request, out var normalizedKinds))
            return new(CalendarCollectionCreateCode.InvalidInput, CalendarMutationState.NotAttempted);

        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        // Href remains the collection identity; creation rejects a duplicate display name only
        // so the existing name-based resource selection remains deterministic.
        if (ApplyScope(discovery.Items).Any(item => string.Equals(
                item.DisplayName?.Trim(),
                request.DisplayName.Trim(),
                StringComparison.OrdinalIgnoreCase)))
        {
            return new(CalendarCollectionCreateCode.Conflict, CalendarMutationState.NotAttempted);
        }

        var target = ResolveCreateTarget(request.DestinationHref, discovery.HomeSetHref);
        if (target is null)
        {
            return new(
                string.IsNullOrWhiteSpace(request.DestinationHref)
                    ? CalendarCollectionCreateCode.OutsideScope
                    : CalendarCollectionCreateCode.InvalidInput,
                CalendarMutationState.NotAttempted);
        }

        CalendarCollectionDispatchResult dispatch;
        try
        {
            dispatch = await transport.CreateAsync(
                new CalendarCollectionCreateDispatchRequest(target, request.DisplayName.Trim(), normalizedKinds),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsAmbiguousDispatchFailure(exception))
        {
            return new(
                CalendarCollectionCreateCode.Indeterminate,
                CalendarMutationState.Unknown,
                Retryable: IsRetryable(exception));
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return new(CalendarCollectionCreateCode.UnsupportedCapability, CalendarMutationState.NotCommitted);
        }
        catch (Exception exception) when (IsDefinitiveDispatchFailure(exception))
        {
            return new(CalendarCollectionCreateCode.UpstreamProtocolError, CalendarMutationState.NotCommitted);
        }
        var mapped = MapCreateDispatch(dispatch);
        if (mapped is not null)
            return mapped;

        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Reconcile);
        CalendarCollectionDiscoverySnapshot after;
        try
        {
            after = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsReconciliationFailure(exception))
        {
            return new(
                dispatch.Code == CalendarCollectionDispatchCode.PossiblyDispatched
                    ? CalendarCollectionCreateCode.Indeterminate
                    : CalendarCollectionCreateCode.CommittedButUnverified,
                dispatch.Code == CalendarCollectionDispatchCode.PossiblyDispatched
                    ? CalendarMutationState.Unknown
                    : CalendarMutationState.Committed,
                Retryable: dispatch.Code == CalendarCollectionDispatchCode.PossiblyDispatched
                    || IsRetryable(exception));
        }
        var created = after.Items.SingleOrDefault(item => string.Equals(item.Href, target, StringComparison.Ordinal));
        if (created is null)
        {
            return new(
                dispatch.Code == CalendarCollectionDispatchCode.PossiblyDispatched
                    ? CalendarCollectionCreateCode.Indeterminate
                    : CalendarCollectionCreateCode.CommittedButUnverified,
                dispatch.Code == CalendarCollectionDispatchCode.PossiblyDispatched
                    ? CalendarMutationState.Unknown
                    : CalendarMutationState.Committed,
                Retryable: dispatch.Code == CalendarCollectionDispatchCode.PossiblyDispatched);
        }

        if (!string.Equals(created.DisplayName?.Trim(), request.DisplayName.Trim(), StringComparison.OrdinalIgnoreCase)
            || !SupportsAll(created, normalizedKinds))
        {
            return new(
                CalendarCollectionCreateCode.CommittedButUnverified,
                CalendarMutationState.Committed,
                created);
        }

        return CalendarCollectionCreateResult.Success(created);
    }

    public async Task<CalendarCollectionDeleteReviewResult> ReviewDeleteAsync(
        CalendarCollectionDeleteRequest request,
        CancellationToken cancellationToken)
    {
        if (!TryCanonicalHref(request.Href, out var href))
        {
            return new(
                FailureDelete(CalendarCollectionDeleteCode.InvalidInput),
                null,
                null);
        }

        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var raw = discovery.Items.SingleOrDefault(item => string.Equals(item.Href, href, StringComparison.Ordinal));
        if (raw is null)
        {
            var configuredScope = CalendarDiscoveryPolicy.ParseScope(options.Value.CalendarHrefs);
            return new(
                configuredScope.Count > 0
                    && !configuredScope.Contains(href, StringComparer.Ordinal)
                    ? FailureDelete(CalendarCollectionDeleteCode.OutsideScope)
                    : FailureDelete(CalendarCollectionDeleteCode.NotFound),
                null,
                null);
        }

        if (!ApplyScope(discovery.Items).Any(item => string.Equals(item.Href, href, StringComparison.Ordinal)))
            return new(FailureDelete(CalendarCollectionDeleteCode.OutsideScope), null, null);

        var binding = new CalendarCollectionDeleteReviewBinding(href, DescriptorDigest(raw));
        return new(null, binding, raw);
    }

    public async Task<CalendarCollectionDeleteResult> ExecuteConfirmedDeleteAsync(
        CalendarCollectionDeleteRequest request,
        CalendarCollectionDeleteReviewBinding priorBinding,
        CancellationToken cancellationToken)
    {
        if (!TryCanonicalHref(request.Href, out var href)
            || !string.Equals(href, priorBinding.Href, StringComparison.Ordinal))
        {
            return FailureDelete(CalendarCollectionDeleteCode.ConfirmationMismatch);
        }

        var discovery = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        var current = discovery.Items.SingleOrDefault(item => string.Equals(item.Href, href, StringComparison.Ordinal));
        if (current is null)
            return FailureDelete(CalendarCollectionDeleteCode.NotFound);
        if (!ApplyScope(discovery.Items).Any(item => string.Equals(item.Href, href, StringComparison.Ordinal)))
            return FailureDelete(CalendarCollectionDeleteCode.OutsideScope);
        if (!string.Equals(DescriptorDigest(current), priorBinding.DescriptorDigest, StringComparison.Ordinal))
            return new(CalendarCollectionDeleteCode.ConfirmationMismatch, CalendarMutationState.NotAttempted, current);

        CalendarCollectionDispatchResult dispatch;
        try
        {
            dispatch = await transport.DeleteAsync(href, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsAmbiguousDispatchFailure(exception))
        {
            return new(
                CalendarCollectionDeleteCode.Indeterminate,
                CalendarMutationState.Unknown,
                current,
                Retryable: IsRetryable(exception));
        }
        catch (CalendarDiscoveryUnsupportedCapabilityException)
        {
            return new(CalendarCollectionDeleteCode.UnsupportedCapability, CalendarMutationState.NotCommitted, current);
        }
        catch (Exception exception) when (IsDefinitiveDispatchFailure(exception))
        {
            return new(CalendarCollectionDeleteCode.UpstreamProtocolError, CalendarMutationState.NotCommitted, current);
        }
        if (dispatch.Code is not CalendarCollectionDispatchCode.Dispatched
            and not CalendarCollectionDispatchCode.PossiblyDispatched)
            return MapDeleteDispatch(dispatch);

        CalendarOperationProgress.SetPhase(CalendarOperationPhase.Reconcile);
        CalendarCollectionDiscoverySnapshot after;
        try
        {
            after = await DiscoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsReconciliationFailure(exception))
        {
            return new(
                dispatch.Code == CalendarCollectionDispatchCode.PossiblyDispatched
                    ? CalendarCollectionDeleteCode.Indeterminate
                    : CalendarCollectionDeleteCode.CommittedButUnverified,
                dispatch.Code == CalendarCollectionDispatchCode.PossiblyDispatched
                    ? CalendarMutationState.Unknown
                    : CalendarMutationState.Committed,
                current,
                Retryable: dispatch.Code == CalendarCollectionDispatchCode.PossiblyDispatched
                    || IsRetryable(exception));
        }
        var remaining = after.Items.Any(item => string.Equals(item.Href, href, StringComparison.Ordinal));
        if (!remaining)
            return CalendarCollectionDeleteResult.Success(current);

        return new(
            dispatch.Code == CalendarCollectionDispatchCode.PossiblyDispatched
                ? CalendarCollectionDeleteCode.Indeterminate
                : CalendarCollectionDeleteCode.CommittedButUnverified,
            dispatch.Code == CalendarCollectionDispatchCode.PossiblyDispatched
                ? CalendarMutationState.Unknown
                : CalendarMutationState.Committed,
            current,
            Retryable: dispatch.Code == CalendarCollectionDispatchCode.PossiblyDispatched);
    }

    private async Task<CalendarCollectionDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken)
    {
        var discovery = await transport.DiscoverAsync(cancellationToken).ConfigureAwait(false);
        if (discovery.Items.Count > MaximumCalendars)
            throw new CalendarDiscoveryLimitException(discovery.Items.Count);
        return discovery;
    }

    private IReadOnlyList<CalendarDescriptor> ApplyScope(IReadOnlyList<CalendarDescriptor> calendars)
    {
        var scope = CalendarDiscoveryPolicy.ParseScope(options.Value.CalendarHrefs);
        return scope.Count == 0
            ? calendars
            : calendars.Where(calendar => scope.Contains(calendar.Href, StringComparer.Ordinal)).ToArray();
    }

    private string? ResolveCreateTarget(string? requested, string homeSetHref)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            if (CalendarDiscoveryPolicy.ParseScope(options.Value.CalendarHrefs).Count > 0)
                return null;
            return $"{homeSetHref.TrimEnd('/')}/{Guid.NewGuid():N}/";
        }

        if (!TryCanonicalHref(requested, out var target)
            || !IsDirectChild(homeSetHref, target))
            return null;
        var scope = CalendarDiscoveryPolicy.ParseScope(options.Value.CalendarHrefs);
        return scope.Count == 0 || scope.Contains(target, StringComparer.Ordinal) ? target : null;
    }

    private bool TryValidateCreateInput(
        CalendarCollectionCreateRequest request,
        out IReadOnlyList<CalendarEntityKind> normalizedKinds)
    {
        normalizedKinds = [];
        if (string.IsNullOrWhiteSpace(request.DisplayName)
            || request.DisplayName.Trim().Length > MaximumDisplayNameCharacters
            || request.EntityKinds is null
            || request.EntityKinds.Count is < 1 or > 2)
            return false;

        var kinds = request.EntityKinds.Distinct().ToArray();
        if (kinds.Length != request.EntityKinds.Count
            || kinds.Any(kind => kind is not (CalendarEntityKind.Event or CalendarEntityKind.Todo)))
            return false;
        normalizedKinds = kinds.OrderBy(kind => kind).ToArray();
        return true;
    }

    private bool TryCanonicalHref(string href, out string canonical)
    {
        canonical = string.Empty;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)
            || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath.Contains("%2e", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("%2f", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.Contains("%5c", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.AbsoluteUri, href, StringComparison.Ordinal)
            || !uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
            return false;

        var origin = new Uri(options.Value.BaseUrl, UriKind.Absolute);
        if (!string.Equals(uri.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != origin.Port)
            return false;
        canonical = uri.AbsoluteUri;
        return true;
    }

    private static bool IsDirectChild(string homeSetHref, string targetHref)
    {
        if (!Uri.TryCreate(homeSetHref, UriKind.Absolute, out var home)
            || !Uri.TryCreate(targetHref, UriKind.Absolute, out var target)
            || !string.Equals(home.GetLeftPart(UriPartial.Authority), target.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            return false;
        var prefix = home.AbsolutePath.EndsWith('/') ? home.AbsolutePath : home.AbsolutePath + "/";
        if (!target.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var relative = target.AbsolutePath[prefix.Length..].TrimEnd('/');
        return relative.Length > 0 && !relative.Contains('/');
    }

    private static bool SupportsAll(CalendarDescriptor descriptor, IReadOnlyList<CalendarEntityKind> kinds) =>
        kinds.All(kind => CalendarDiscoveryPolicy.SupportsEntityKind(descriptor, kind));

    private static string DescriptorDigest(CalendarDescriptor descriptor)
    {
        var payload = JsonSerializer.Serialize(new
        {
            descriptor.Href,
            descriptor.DisplayName,
            descriptor.DisplayNameProvenance,
            descriptor.EventSupport,
            descriptor.TodoSupport,
            descriptor.EventEvidence,
            descriptor.TodoEvidence
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static CalendarCollectionCreateResult? MapCreateDispatch(CalendarCollectionDispatchResult result) => result.Code switch
    {
        CalendarCollectionDispatchCode.Dispatched or CalendarCollectionDispatchCode.PossiblyDispatched => null,
        CalendarCollectionDispatchCode.Conflict => new(CalendarCollectionCreateCode.DestinationConflict, CalendarMutationState.NotCommitted),
        CalendarCollectionDispatchCode.UnsupportedCapability => new(CalendarCollectionCreateCode.UnsupportedCapability, CalendarMutationState.NotCommitted),
        CalendarCollectionDispatchCode.PayloadTooLarge => new(CalendarCollectionCreateCode.PayloadTooLarge, CalendarMutationState.NotCommitted),
        CalendarCollectionDispatchCode.UpstreamUnauthorized => new(CalendarCollectionCreateCode.UpstreamUnauthorized, CalendarMutationState.NotCommitted),
        CalendarCollectionDispatchCode.UpstreamForbidden => new(CalendarCollectionCreateCode.UpstreamForbidden, CalendarMutationState.NotCommitted),
        CalendarCollectionDispatchCode.UpstreamRateLimited => new(CalendarCollectionCreateCode.UpstreamRateLimited, CalendarMutationState.NotCommitted, Retryable: true, RetryAfterMilliseconds: result.RetryAfterMilliseconds),
        CalendarCollectionDispatchCode.UpstreamUnavailable => new(CalendarCollectionCreateCode.UpstreamUnavailable, CalendarMutationState.Unknown, Retryable: true),
        CalendarCollectionDispatchCode.NotFound => new(CalendarCollectionCreateCode.UpstreamProtocolError, CalendarMutationState.NotCommitted),
        CalendarCollectionDispatchCode.ProtocolError => new(CalendarCollectionCreateCode.UpstreamProtocolError, CalendarMutationState.NotCommitted),
        _ => new(CalendarCollectionCreateCode.UpstreamProtocolError, CalendarMutationState.Unknown)
    };

    private static CalendarCollectionDeleteResult MapDeleteDispatch(CalendarCollectionDispatchResult result) => result.Code switch
    {
        CalendarCollectionDispatchCode.Conflict => RejectedDelete(CalendarCollectionDeleteCode.Conflict),
        CalendarCollectionDispatchCode.UnsupportedCapability => RejectedDelete(CalendarCollectionDeleteCode.UnsupportedCapability),
        CalendarCollectionDispatchCode.PayloadTooLarge => RejectedDelete(CalendarCollectionDeleteCode.PayloadTooLarge),
        CalendarCollectionDispatchCode.UpstreamUnauthorized => RejectedDelete(CalendarCollectionDeleteCode.UpstreamUnauthorized),
        CalendarCollectionDispatchCode.UpstreamForbidden => RejectedDelete(CalendarCollectionDeleteCode.UpstreamForbidden),
        CalendarCollectionDispatchCode.UpstreamRateLimited => new(CalendarCollectionDeleteCode.UpstreamRateLimited, CalendarMutationState.NotCommitted, Retryable: true, RetryAfterMilliseconds: result.RetryAfterMilliseconds),
        CalendarCollectionDispatchCode.UpstreamUnavailable => new(CalendarCollectionDeleteCode.UpstreamUnavailable, CalendarMutationState.Unknown, Retryable: true),
        CalendarCollectionDispatchCode.NotFound => RejectedDelete(CalendarCollectionDeleteCode.NotFound),
        CalendarCollectionDispatchCode.ProtocolError => RejectedDelete(CalendarCollectionDeleteCode.UpstreamProtocolError),
        _ => new(CalendarCollectionDeleteCode.UpstreamProtocolError, CalendarMutationState.Unknown)
    };

    private static CalendarCollectionDeleteResult FailureDelete(CalendarCollectionDeleteCode code) =>
        new(code, CalendarMutationState.NotAttempted);

    private static CalendarCollectionDeleteResult RejectedDelete(CalendarCollectionDeleteCode code) =>
        new(code, CalendarMutationState.NotCommitted);

    private static bool IsReconciliationFailure(Exception exception) => exception is
        HttpRequestException or
        IOException or
        TimeoutException or
        System.Xml.XmlException or
        CalendarDiscoveryProtocolException or
        CalendarDiscoveryUnsupportedCapabilityException or
        CalendarDiscoveryLimitException or
        OperationCanceledException;

    private static bool IsRetryable(Exception exception) => exception is
        HttpRequestException or
        IOException or
        TimeoutException or
        OperationCanceledException;

    private static bool IsAmbiguousDispatchFailure(Exception exception) => exception is
        HttpRequestException or
        IOException or
        TimeoutException or
        OperationCanceledException;

    private static bool IsDefinitiveDispatchFailure(Exception exception) => exception is
        System.Xml.XmlException or
        CalendarDiscoveryProtocolException;
}
