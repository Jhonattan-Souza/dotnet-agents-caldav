using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Mcp.Hosting;

internal static class CalendarTelemetryFacts
{
    internal static CalendarStructuredErrorFacts FromInputGuard(bool payloadTooLarge) => new(
        payloadTooLarge ? CalendarTelemetryErrorCode.PayloadTooLarge : CalendarTelemetryErrorCode.InvalidInput,
        payloadTooLarge
            ? CalendarTelemetryErrorCategory.LimitsAndAdmission
            : CalendarTelemetryErrorCategory.Input,
        payloadTooLarge
            ? CalendarTelemetryErrorPhase.AdmissionAndPayload
            : CalendarTelemetryErrorPhase.SchemaLexicalDiscriminator,
        false);

    internal static CalendarStructuredErrorFacts From(QueryFailure failure) => new(
        ErrorCode(failure.Code),
        ErrorCategory(failure.Category),
        ErrorPhase(failure.Phase),
        failure.Retryable);

    internal static CalendarStructuredErrorFacts From(CalendarEntityPatchResult result) => new(
        ErrorCode(result.Code),
        ErrorCategory(result.Code),
        ErrorPhase(result.Phase),
        result.Retryable);

    internal static CalendarStructuredErrorFacts From(CalendarResourceMoveResult result) => new(
        ErrorCode(result.Code),
        ErrorCategory(result.Code),
        ErrorPhase(result),
        result.Retryable);

    internal static CalendarStructuredErrorFacts From(CalendarResourceDeleteResult result) => new(
        ErrorCode(result.Code),
        ErrorCategory(result.Code),
        ErrorPhase(result),
        result.Retryable);

    internal static CalendarStructuredErrorFacts From(CalendarEntityCreateResult result) => new(
        ErrorCode(result.Code),
        ErrorCategory(result.Code),
        ErrorPhase(result),
        result.Code == CalendarEntityCreateCode.UpstreamRateLimited
            && result.MutationState == CalendarMutationState.NotCommitted);

    internal static CalendarStructuredErrorFacts From(CalendarExactResourceResult result) => new(
        ErrorCode(result.Code),
        ErrorCategory(result.Code),
        ErrorPhase(result.Phase),
        result.Retryable);

    internal static CalendarStructuredErrorFacts From(CalendarCollectionCreateResult result) => new(
        ErrorCode(result),
        ErrorCategory(result),
        ErrorPhase(result),
        result.Retryable);

    internal static CalendarStructuredErrorFacts From(CalendarCollectionDeleteResult result) => new(
        ErrorCode(result),
        ErrorCategory(result),
        ErrorPhase(result),
        result.Retryable);

    internal static CalendarStructuredErrorFacts From(CalendarResourceReadCode code) => code switch
    {
        CalendarResourceReadCode.InvalidInput => new(
            CalendarTelemetryErrorCode.InvalidInput,
            CalendarTelemetryErrorCategory.Input,
            CalendarTelemetryErrorPhase.OriginScopeAuthorization,
            false),
        CalendarResourceReadCode.OutsideScope => new(
            CalendarTelemetryErrorCode.OutsideScope,
            CalendarTelemetryErrorCategory.Selection,
            CalendarTelemetryErrorPhase.OriginScopeAuthorization,
            false),
        CalendarResourceReadCode.NotFound => new(
            CalendarTelemetryErrorCode.NotFound,
            CalendarTelemetryErrorCategory.Selection,
            CalendarTelemetryErrorPhase.TargetRevision,
            false),
        CalendarResourceReadCode.ConcurrencyUnavailable => new(
            CalendarTelemetryErrorCode.ConcurrencyUnavailable,
            CalendarTelemetryErrorCategory.State,
            CalendarTelemetryErrorPhase.TargetRevision,
            false),
        CalendarResourceReadCode.PayloadTooLarge => new(
            CalendarTelemetryErrorCode.PayloadTooLarge,
            CalendarTelemetryErrorCategory.LimitsAndAdmission,
            CalendarTelemetryErrorPhase.AdmissionAndPayload,
            false),
        CalendarResourceReadCode.UnsupportedCapability => new(
            CalendarTelemetryErrorCode.UnsupportedCapability,
            CalendarTelemetryErrorCategory.CapabilityAndProjection,
            CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
            false),
        CalendarResourceReadCode.UpstreamProtocolError => new(
            CalendarTelemetryErrorCode.UpstreamProtocolError,
            CalendarTelemetryErrorCategory.Upstream,
            CalendarTelemetryErrorPhase.Execution,
            false),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null)
    };

    private static CalendarTelemetryErrorCode ErrorCode(CalendarCollectionCreateResult result) =>
        result.Code switch
        {
            CalendarCollectionCreateCode.InvalidInput => CalendarTelemetryErrorCode.InvalidInput,
            CalendarCollectionCreateCode.OutsideScope => CalendarTelemetryErrorCode.OutsideScope,
            CalendarCollectionCreateCode.Conflict => CalendarTelemetryErrorCode.Conflict,
            CalendarCollectionCreateCode.DestinationConflict => CalendarTelemetryErrorCode.DestinationConflict,
            CalendarCollectionCreateCode.UnsupportedCapability => CalendarTelemetryErrorCode.UnsupportedCapability,
            CalendarCollectionCreateCode.PayloadTooLarge => CalendarTelemetryErrorCode.PayloadTooLarge,
            CalendarCollectionCreateCode.UpstreamUnauthorized => CalendarTelemetryErrorCode.UpstreamUnauthorized,
            CalendarCollectionCreateCode.UpstreamForbidden => CalendarTelemetryErrorCode.UpstreamForbidden,
            CalendarCollectionCreateCode.UpstreamRateLimited => CalendarTelemetryErrorCode.UpstreamRateLimited,
            CalendarCollectionCreateCode.UpstreamUnavailable => CalendarTelemetryErrorCode.UpstreamUnavailable,
            CalendarCollectionCreateCode.UpstreamProtocolError => CalendarTelemetryErrorCode.UpstreamProtocolError,
            CalendarCollectionCreateCode.CommittedButUnverified => CalendarTelemetryErrorCode.CommittedButUnverified,
            CalendarCollectionCreateCode.Indeterminate or CalendarCollectionCreateCode.Success =>
                CalendarTelemetryErrorCode.Indeterminate,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Code, null)
        };

    private static CalendarTelemetryErrorCategory ErrorCategory(CalendarCollectionCreateResult result) =>
        result.Code switch
        {
            CalendarCollectionCreateCode.InvalidInput => CalendarTelemetryErrorCategory.Input,
            CalendarCollectionCreateCode.OutsideScope => CalendarTelemetryErrorCategory.Selection,
            CalendarCollectionCreateCode.Conflict or CalendarCollectionCreateCode.DestinationConflict =>
                CalendarTelemetryErrorCategory.State,
            CalendarCollectionCreateCode.UnsupportedCapability =>
                CalendarTelemetryErrorCategory.CapabilityAndProjection,
            CalendarCollectionCreateCode.PayloadTooLarge => CalendarTelemetryErrorCategory.LimitsAndAdmission,
            CalendarCollectionCreateCode.CommittedButUnverified or CalendarCollectionCreateCode.Indeterminate
                or CalendarCollectionCreateCode.Success => CalendarTelemetryErrorCategory.PostWriteTruth,
            _ => CalendarTelemetryErrorCategory.Upstream
        };

    private static CalendarTelemetryErrorPhase ErrorPhase(CalendarCollectionCreateResult result) =>
        result.Code switch
        {
            CalendarCollectionCreateCode.InvalidInput => CalendarTelemetryErrorPhase.SchemaLexicalDiscriminator,
            CalendarCollectionCreateCode.OutsideScope => CalendarTelemetryErrorPhase.OriginScopeAuthorization,
            CalendarCollectionCreateCode.Conflict => CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
            CalendarCollectionCreateCode.PayloadTooLarge => CalendarTelemetryErrorPhase.AdmissionAndPayload,
            CalendarCollectionCreateCode.CommittedButUnverified or CalendarCollectionCreateCode.Indeterminate
                or CalendarCollectionCreateCode.Success =>
                CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation,
            _ => CalendarTelemetryErrorPhase.Execution
        };

    private static CalendarTelemetryErrorCode ErrorCode(CalendarCollectionDeleteResult result) =>
        result.Code switch
        {
            CalendarCollectionDeleteCode.InvalidInput => CalendarTelemetryErrorCode.InvalidInput,
            CalendarCollectionDeleteCode.NotFound => CalendarTelemetryErrorCode.NotFound,
            CalendarCollectionDeleteCode.OutsideScope => CalendarTelemetryErrorCode.OutsideScope,
            CalendarCollectionDeleteCode.Conflict => CalendarTelemetryErrorCode.Conflict,
            CalendarCollectionDeleteCode.ConfirmationMismatch => CalendarTelemetryErrorCode.ConfirmationMismatch,
            CalendarCollectionDeleteCode.UnsupportedCapability => CalendarTelemetryErrorCode.UnsupportedCapability,
            CalendarCollectionDeleteCode.PayloadTooLarge => CalendarTelemetryErrorCode.PayloadTooLarge,
            CalendarCollectionDeleteCode.UpstreamUnauthorized => CalendarTelemetryErrorCode.UpstreamUnauthorized,
            CalendarCollectionDeleteCode.UpstreamForbidden => CalendarTelemetryErrorCode.UpstreamForbidden,
            CalendarCollectionDeleteCode.UpstreamRateLimited => CalendarTelemetryErrorCode.UpstreamRateLimited,
            CalendarCollectionDeleteCode.UpstreamUnavailable => CalendarTelemetryErrorCode.UpstreamUnavailable,
            CalendarCollectionDeleteCode.UpstreamProtocolError => CalendarTelemetryErrorCode.UpstreamProtocolError,
            CalendarCollectionDeleteCode.CommittedButUnverified => CalendarTelemetryErrorCode.CommittedButUnverified,
            CalendarCollectionDeleteCode.Indeterminate or CalendarCollectionDeleteCode.Success =>
                CalendarTelemetryErrorCode.Indeterminate,
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Code, null)
        };

    private static CalendarTelemetryErrorCategory ErrorCategory(CalendarCollectionDeleteResult result) =>
        result.Code switch
        {
            CalendarCollectionDeleteCode.InvalidInput => CalendarTelemetryErrorCategory.Input,
            CalendarCollectionDeleteCode.NotFound or CalendarCollectionDeleteCode.OutsideScope =>
                CalendarTelemetryErrorCategory.Selection,
            CalendarCollectionDeleteCode.Conflict => CalendarTelemetryErrorCategory.State,
            CalendarCollectionDeleteCode.ConfirmationMismatch => CalendarTelemetryErrorCategory.Confirmation,
            CalendarCollectionDeleteCode.UnsupportedCapability =>
                CalendarTelemetryErrorCategory.CapabilityAndProjection,
            CalendarCollectionDeleteCode.PayloadTooLarge => CalendarTelemetryErrorCategory.LimitsAndAdmission,
            CalendarCollectionDeleteCode.CommittedButUnverified or CalendarCollectionDeleteCode.Indeterminate
                or CalendarCollectionDeleteCode.Success => CalendarTelemetryErrorCategory.PostWriteTruth,
            _ => CalendarTelemetryErrorCategory.Upstream
        };

    private static CalendarTelemetryErrorPhase ErrorPhase(CalendarCollectionDeleteResult result) =>
        result.Code switch
        {
            CalendarCollectionDeleteCode.InvalidInput => CalendarTelemetryErrorPhase.SchemaLexicalDiscriminator,
            CalendarCollectionDeleteCode.NotFound => CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
            CalendarCollectionDeleteCode.OutsideScope => CalendarTelemetryErrorPhase.OriginScopeAuthorization,
            CalendarCollectionDeleteCode.Conflict => CalendarTelemetryErrorPhase.TargetRevision,
            CalendarCollectionDeleteCode.ConfirmationMismatch => CalendarTelemetryErrorPhase.Mrtr,
            CalendarCollectionDeleteCode.PayloadTooLarge => CalendarTelemetryErrorPhase.AdmissionAndPayload,
            CalendarCollectionDeleteCode.CommittedButUnverified or CalendarCollectionDeleteCode.Indeterminate
                or CalendarCollectionDeleteCode.Success =>
                CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation,
            _ => CalendarTelemetryErrorPhase.Execution
        };

    private static CalendarTelemetryErrorCode ErrorCode(QueryFailureCode code) => code switch
    {
        QueryFailureCode.InvalidInput => CalendarTelemetryErrorCode.InvalidInput,
        QueryFailureCode.CursorExpired => CalendarTelemetryErrorCode.CursorExpired,
        QueryFailureCode.LimitExhausted => CalendarTelemetryErrorCode.LimitExhausted,
        QueryFailureCode.Busy => CalendarTelemetryErrorCode.Busy,
        QueryFailureCode.PayloadTooLarge => CalendarTelemetryErrorCode.PayloadTooLarge,
        QueryFailureCode.UpstreamProtocolError => CalendarTelemetryErrorCode.UpstreamProtocolError,
        QueryFailureCode.UnsupportedCapability => CalendarTelemetryErrorCode.UnsupportedCapability,
        QueryFailureCode.ConcurrencyUnavailable => CalendarTelemetryErrorCode.ConcurrencyUnavailable,
        QueryFailureCode.TemporalUnresolved => CalendarTelemetryErrorCode.TemporalUnresolved,
        QueryFailureCode.RecurrenceUnevaluable => CalendarTelemetryErrorCode.RecurrenceUnevaluable,
        QueryFailureCode.UpstreamUnavailable => CalendarTelemetryErrorCode.UpstreamUnavailable,
        QueryFailureCode.UpstreamUnauthorized => CalendarTelemetryErrorCode.UpstreamUnauthorized,
        QueryFailureCode.UpstreamForbidden => CalendarTelemetryErrorCode.UpstreamForbidden,
        QueryFailureCode.UpstreamRateLimited => CalendarTelemetryErrorCode.UpstreamRateLimited,
        QueryFailureCode.NotFound => CalendarTelemetryErrorCode.NotFound,
        QueryFailureCode.Ambiguous => CalendarTelemetryErrorCode.Ambiguous,
        QueryFailureCode.OutsideScope => CalendarTelemetryErrorCode.OutsideScope,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null)
    };

    private static CalendarTelemetryErrorCategory ErrorCategory(QueryFailureCategory category) => category switch
    {
        QueryFailureCategory.Input => CalendarTelemetryErrorCategory.Input,
        QueryFailureCategory.State => CalendarTelemetryErrorCategory.State,
        QueryFailureCategory.LimitsAndAdmission => CalendarTelemetryErrorCategory.LimitsAndAdmission,
        QueryFailureCategory.Upstream => CalendarTelemetryErrorCategory.Upstream,
        QueryFailureCategory.CapabilityAndProjection => CalendarTelemetryErrorCategory.CapabilityAndProjection,
        QueryFailureCategory.Selection => CalendarTelemetryErrorCategory.Selection,
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null)
    };

    private static CalendarTelemetryErrorPhase ErrorPhase(QueryFailurePhase phase) => phase switch
    {
        QueryFailurePhase.SchemaLexicalDiscriminator => CalendarTelemetryErrorPhase.SchemaLexicalDiscriminator,
        QueryFailurePhase.Pagination => CalendarTelemetryErrorPhase.Pagination,
        QueryFailurePhase.Execution => CalendarTelemetryErrorPhase.Execution,
        QueryFailurePhase.AdmissionAndPayload => CalendarTelemetryErrorPhase.AdmissionAndPayload,
        QueryFailurePhase.SelectionDiscoveryCapability =>
            CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
        QueryFailurePhase.TargetRevision => CalendarTelemetryErrorPhase.TargetRevision,
        QueryFailurePhase.CompleteResourceSemantics => CalendarTelemetryErrorPhase.CompleteResourceSemantics,
        QueryFailurePhase.OriginScopeAuthorization => CalendarTelemetryErrorPhase.OriginScopeAuthorization,
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, null)
    };

    private static CalendarTelemetryErrorCode ErrorCode(CalendarEntityPatchCode code) => code switch
    {
        CalendarEntityPatchCode.NoChange or CalendarEntityPatchCode.Success =>
            throw new ArgumentOutOfRangeException(nameof(code), code, null),
        CalendarEntityPatchCode.InvalidInput => CalendarTelemetryErrorCode.InvalidInput,
        CalendarEntityPatchCode.InvalidCalendarData => CalendarTelemetryErrorCode.InvalidCalendarData,
        CalendarEntityPatchCode.NotFound or CalendarEntityPatchCode.RemovalNotFound =>
            CalendarTelemetryErrorCode.NotFound,
        CalendarEntityPatchCode.RemovalAmbiguous => CalendarTelemetryErrorCode.Ambiguous,
        CalendarEntityPatchCode.OutsideScope => CalendarTelemetryErrorCode.OutsideScope,
        CalendarEntityPatchCode.EntityKindMismatch => CalendarTelemetryErrorCode.EntityKindMismatch,
        CalendarEntityPatchCode.OpaqueResource => CalendarTelemetryErrorCode.OpaqueResource,
        CalendarEntityPatchCode.RecurrenceUnevaluable => CalendarTelemetryErrorCode.RecurrenceUnevaluable,
        CalendarEntityPatchCode.Conflict => CalendarTelemetryErrorCode.Conflict,
        CalendarEntityPatchCode.ConcurrencyUnavailable => CalendarTelemetryErrorCode.ConcurrencyUnavailable,
        CalendarEntityPatchCode.UnsupportedCapability => CalendarTelemetryErrorCode.UnsupportedCapability,
        CalendarEntityPatchCode.PayloadTooLarge => CalendarTelemetryErrorCode.PayloadTooLarge,
        CalendarEntityPatchCode.LimitExhausted => CalendarTelemetryErrorCode.LimitExhausted,
        CalendarEntityPatchCode.UpstreamUnauthorized => CalendarTelemetryErrorCode.UpstreamUnauthorized,
        CalendarEntityPatchCode.UpstreamForbidden => CalendarTelemetryErrorCode.UpstreamForbidden,
        CalendarEntityPatchCode.UpstreamRateLimited => CalendarTelemetryErrorCode.UpstreamRateLimited,
        CalendarEntityPatchCode.UpstreamUnavailable => CalendarTelemetryErrorCode.UpstreamUnavailable,
        CalendarEntityPatchCode.UpstreamProtocolError => CalendarTelemetryErrorCode.UpstreamProtocolError,
        CalendarEntityPatchCode.FidelityFailure => CalendarTelemetryErrorCode.FidelityFailure,
        CalendarEntityPatchCode.CommittedButUnverified => CalendarTelemetryErrorCode.CommittedButUnverified,
        CalendarEntityPatchCode.CommittedButConcurrencyUnavailable =>
            CalendarTelemetryErrorCode.CommittedButConcurrencyUnavailable,
        CalendarEntityPatchCode.Indeterminate => CalendarTelemetryErrorCode.Indeterminate,
        CalendarEntityPatchCode.TemporalUnresolved => CalendarTelemetryErrorCode.TemporalUnresolved,
        CalendarEntityPatchCode.CompletionStateConflict => CalendarTelemetryErrorCode.CompletionStateConflict,
        _ => CalendarTelemetryErrorCode.UpstreamProtocolError
    };

    private static CalendarTelemetryErrorCategory ErrorCategory(CalendarEntityPatchCode code) => code switch
    {
        CalendarEntityPatchCode.InvalidInput or CalendarEntityPatchCode.InvalidCalendarData =>
            CalendarTelemetryErrorCategory.Input,
        CalendarEntityPatchCode.NotFound or CalendarEntityPatchCode.RemovalNotFound
            or CalendarEntityPatchCode.RemovalAmbiguous or CalendarEntityPatchCode.OutsideScope
            or CalendarEntityPatchCode.EntityKindMismatch => CalendarTelemetryErrorCategory.Selection,
        CalendarEntityPatchCode.OpaqueResource or CalendarEntityPatchCode.TemporalUnresolved
            or CalendarEntityPatchCode.RecurrenceUnevaluable or CalendarEntityPatchCode.UnsupportedCapability =>
            CalendarTelemetryErrorCategory.CapabilityAndProjection,
        CalendarEntityPatchCode.Conflict or CalendarEntityPatchCode.ConcurrencyUnavailable
            or CalendarEntityPatchCode.CompletionStateConflict => CalendarTelemetryErrorCategory.State,
        CalendarEntityPatchCode.PayloadTooLarge or CalendarEntityPatchCode.LimitExhausted =>
            CalendarTelemetryErrorCategory.LimitsAndAdmission,
        CalendarEntityPatchCode.FidelityFailure or CalendarEntityPatchCode.CommittedButUnverified
            or CalendarEntityPatchCode.CommittedButConcurrencyUnavailable or CalendarEntityPatchCode.Indeterminate =>
            CalendarTelemetryErrorCategory.PostWriteTruth,
        _ => CalendarTelemetryErrorCategory.Upstream
    };

    private static CalendarTelemetryErrorPhase ErrorPhase(CalendarEntityPatchPhase phase) => phase switch
    {
        CalendarEntityPatchPhase.SchemaLexicalDiscriminator =>
            CalendarTelemetryErrorPhase.SchemaLexicalDiscriminator,
        CalendarEntityPatchPhase.SelectionDiscoveryCapability =>
            CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
        CalendarEntityPatchPhase.OriginScopeAuthorization => CalendarTelemetryErrorPhase.OriginScopeAuthorization,
        CalendarEntityPatchPhase.TargetRevision => CalendarTelemetryErrorPhase.TargetRevision,
        CalendarEntityPatchPhase.CompleteResourceSemantics =>
            CalendarTelemetryErrorPhase.CompleteResourceSemantics,
        CalendarEntityPatchPhase.AdmissionAndPayload => CalendarTelemetryErrorPhase.AdmissionAndPayload,
        CalendarEntityPatchPhase.PostWriteVerificationOrReconciliation =>
            CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation,
        _ => CalendarTelemetryErrorPhase.Execution
    };

    private static CalendarTelemetryErrorCode ErrorCode(CalendarResourceMoveCode code) => code switch
    {
        CalendarResourceMoveCode.Success => throw new ArgumentOutOfRangeException(nameof(code), code, null),
        CalendarResourceMoveCode.InvalidInput => CalendarTelemetryErrorCode.InvalidInput,
        CalendarResourceMoveCode.NotFound => CalendarTelemetryErrorCode.NotFound,
        CalendarResourceMoveCode.Ambiguous => CalendarTelemetryErrorCode.Ambiguous,
        CalendarResourceMoveCode.OutsideScope => CalendarTelemetryErrorCode.OutsideScope,
        CalendarResourceMoveCode.EntityKindMismatch => CalendarTelemetryErrorCode.EntityKindMismatch,
        CalendarResourceMoveCode.UnsupportedCapability => CalendarTelemetryErrorCode.UnsupportedCapability,
        CalendarResourceMoveCode.OpaqueResource => CalendarTelemetryErrorCode.OpaqueResource,
        CalendarResourceMoveCode.Conflict => CalendarTelemetryErrorCode.Conflict,
        CalendarResourceMoveCode.DestinationConflict => CalendarTelemetryErrorCode.DestinationConflict,
        CalendarResourceMoveCode.ConcurrencyUnavailable => CalendarTelemetryErrorCode.ConcurrencyUnavailable,
        CalendarResourceMoveCode.LimitExhausted => CalendarTelemetryErrorCode.LimitExhausted,
        CalendarResourceMoveCode.PayloadTooLarge => CalendarTelemetryErrorCode.PayloadTooLarge,
        CalendarResourceMoveCode.UpstreamUnauthorized => CalendarTelemetryErrorCode.UpstreamUnauthorized,
        CalendarResourceMoveCode.UpstreamForbidden => CalendarTelemetryErrorCode.UpstreamForbidden,
        CalendarResourceMoveCode.UpstreamRateLimited => CalendarTelemetryErrorCode.UpstreamRateLimited,
        CalendarResourceMoveCode.UpstreamUnavailable => CalendarTelemetryErrorCode.UpstreamUnavailable,
        CalendarResourceMoveCode.UpstreamProtocolError => CalendarTelemetryErrorCode.UpstreamProtocolError,
        CalendarResourceMoveCode.FidelityFailure => CalendarTelemetryErrorCode.FidelityFailure,
        CalendarResourceMoveCode.CommittedButUnverified => CalendarTelemetryErrorCode.CommittedButUnverified,
        CalendarResourceMoveCode.CommittedButConcurrencyUnavailable =>
            CalendarTelemetryErrorCode.CommittedButConcurrencyUnavailable,
        CalendarResourceMoveCode.Indeterminate => CalendarTelemetryErrorCode.Indeterminate,
        _ => CalendarTelemetryErrorCode.UpstreamProtocolError
    };

    private static CalendarTelemetryErrorCode ErrorCode(CalendarResourceDeleteCode code) => code switch
    {
        CalendarResourceDeleteCode.Success => throw new ArgumentOutOfRangeException(nameof(code), code, null),
        CalendarResourceDeleteCode.InvalidInput => CalendarTelemetryErrorCode.InvalidInput,
        CalendarResourceDeleteCode.NotFound => CalendarTelemetryErrorCode.NotFound,
        CalendarResourceDeleteCode.OutsideScope => CalendarTelemetryErrorCode.OutsideScope,
        CalendarResourceDeleteCode.EntityKindMismatch => CalendarTelemetryErrorCode.EntityKindMismatch,
        CalendarResourceDeleteCode.OpaqueResource => CalendarTelemetryErrorCode.OpaqueResource,
        CalendarResourceDeleteCode.Conflict => CalendarTelemetryErrorCode.Conflict,
        CalendarResourceDeleteCode.ConcurrencyUnavailable => CalendarTelemetryErrorCode.ConcurrencyUnavailable,
        CalendarResourceDeleteCode.UnsupportedCapability => CalendarTelemetryErrorCode.UnsupportedCapability,
        CalendarResourceDeleteCode.PayloadTooLarge => CalendarTelemetryErrorCode.PayloadTooLarge,
        CalendarResourceDeleteCode.UpstreamUnauthorized => CalendarTelemetryErrorCode.UpstreamUnauthorized,
        CalendarResourceDeleteCode.UpstreamForbidden => CalendarTelemetryErrorCode.UpstreamForbidden,
        CalendarResourceDeleteCode.UpstreamRateLimited => CalendarTelemetryErrorCode.UpstreamRateLimited,
        CalendarResourceDeleteCode.UpstreamUnavailable => CalendarTelemetryErrorCode.UpstreamUnavailable,
        CalendarResourceDeleteCode.UpstreamProtocolError => CalendarTelemetryErrorCode.UpstreamProtocolError,
        CalendarResourceDeleteCode.CommittedButUnverified => CalendarTelemetryErrorCode.CommittedButUnverified,
        CalendarResourceDeleteCode.Indeterminate => CalendarTelemetryErrorCode.Indeterminate,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, null)
    };

    private static CalendarTelemetryErrorCategory ErrorCategory(CalendarResourceDeleteCode code) => code switch
    {
        CalendarResourceDeleteCode.InvalidInput => CalendarTelemetryErrorCategory.Input,
        CalendarResourceDeleteCode.NotFound or CalendarResourceDeleteCode.OutsideScope =>
            CalendarTelemetryErrorCategory.Selection,
        CalendarResourceDeleteCode.EntityKindMismatch or CalendarResourceDeleteCode.Conflict
            or CalendarResourceDeleteCode.ConcurrencyUnavailable => CalendarTelemetryErrorCategory.State,
        CalendarResourceDeleteCode.OpaqueResource or CalendarResourceDeleteCode.UnsupportedCapability =>
            CalendarTelemetryErrorCategory.CapabilityAndProjection,
        CalendarResourceDeleteCode.PayloadTooLarge => CalendarTelemetryErrorCategory.LimitsAndAdmission,
        CalendarResourceDeleteCode.CommittedButUnverified or CalendarResourceDeleteCode.Indeterminate =>
            CalendarTelemetryErrorCategory.PostWriteTruth,
        _ => CalendarTelemetryErrorCategory.Upstream
    };

    private static CalendarTelemetryErrorPhase ErrorPhase(CalendarResourceDeleteResult result)
    {
        if (result.MutationState is CalendarMutationState.Committed or CalendarMutationState.Unknown)
            return CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation;
        if (result.MutationState == CalendarMutationState.NotCommitted)
        {
            return result.Code == CalendarResourceDeleteCode.UpstreamUnavailable
                && result.CurrentSnapshot is not null
                    ? CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation
                    : CalendarTelemetryErrorPhase.Execution;
        }
        return result.Code switch
        {
            CalendarResourceDeleteCode.InvalidInput or CalendarResourceDeleteCode.OutsideScope =>
                CalendarTelemetryErrorPhase.OriginScopeAuthorization,
            CalendarResourceDeleteCode.NotFound or CalendarResourceDeleteCode.EntityKindMismatch
                or CalendarResourceDeleteCode.OpaqueResource or CalendarResourceDeleteCode.Conflict
                or CalendarResourceDeleteCode.ConcurrencyUnavailable => CalendarTelemetryErrorPhase.TargetRevision,
            CalendarResourceDeleteCode.UnsupportedCapability =>
                CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
            CalendarResourceDeleteCode.PayloadTooLarge => CalendarTelemetryErrorPhase.AdmissionAndPayload,
            CalendarResourceDeleteCode.UpstreamUnavailable => CalendarTelemetryErrorPhase.TargetRevision,
            CalendarResourceDeleteCode.CommittedButUnverified or CalendarResourceDeleteCode.Indeterminate =>
                CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation,
            _ => CalendarTelemetryErrorPhase.Execution
        };
    }

    private static CalendarTelemetryErrorCategory ErrorCategory(CalendarResourceMoveCode code) => code switch
    {
        CalendarResourceMoveCode.InvalidInput => CalendarTelemetryErrorCategory.Input,
        CalendarResourceMoveCode.NotFound or CalendarResourceMoveCode.Ambiguous
            or CalendarResourceMoveCode.OutsideScope => CalendarTelemetryErrorCategory.Selection,
        CalendarResourceMoveCode.EntityKindMismatch or CalendarResourceMoveCode.Conflict
            or CalendarResourceMoveCode.DestinationConflict or CalendarResourceMoveCode.ConcurrencyUnavailable =>
            CalendarTelemetryErrorCategory.State,
        CalendarResourceMoveCode.UnsupportedCapability or CalendarResourceMoveCode.OpaqueResource =>
            CalendarTelemetryErrorCategory.CapabilityAndProjection,
        CalendarResourceMoveCode.LimitExhausted or CalendarResourceMoveCode.PayloadTooLarge =>
            CalendarTelemetryErrorCategory.LimitsAndAdmission,
        CalendarResourceMoveCode.FidelityFailure or CalendarResourceMoveCode.CommittedButUnverified
            or CalendarResourceMoveCode.CommittedButConcurrencyUnavailable or CalendarResourceMoveCode.Indeterminate =>
            CalendarTelemetryErrorCategory.PostWriteTruth,
        _ => CalendarTelemetryErrorCategory.Upstream
    };

    private static CalendarTelemetryErrorPhase ErrorPhase(CalendarResourceMoveResult result)
    {
        if (result.MutationState is CalendarMutationState.Committed or CalendarMutationState.Unknown)
            return CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation;
        if (result.Phase is not null)
            return ErrorPhase(result.Phase.Value);
        if (result.MutationState == CalendarMutationState.NotCommitted)
            return CalendarTelemetryErrorPhase.Execution;
        return result.Code switch
        {
            CalendarResourceMoveCode.InvalidInput => CalendarTelemetryErrorPhase.SchemaLexicalDiscriminator,
            CalendarResourceMoveCode.NotFound or CalendarResourceMoveCode.Ambiguous
                or CalendarResourceMoveCode.UnsupportedCapability =>
                CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
            CalendarResourceMoveCode.OutsideScope => CalendarTelemetryErrorPhase.OriginScopeAuthorization,
            CalendarResourceMoveCode.EntityKindMismatch or CalendarResourceMoveCode.Conflict
                or CalendarResourceMoveCode.ConcurrencyUnavailable => CalendarTelemetryErrorPhase.TargetRevision,
            CalendarResourceMoveCode.OpaqueResource => CalendarTelemetryErrorPhase.CompleteResourceSemantics,
            CalendarResourceMoveCode.PayloadTooLarge => CalendarTelemetryErrorPhase.AdmissionAndPayload,
            CalendarResourceMoveCode.FidelityFailure or CalendarResourceMoveCode.CommittedButUnverified
                or CalendarResourceMoveCode.CommittedButConcurrencyUnavailable
                or CalendarResourceMoveCode.Indeterminate =>
                CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation,
            _ => CalendarTelemetryErrorPhase.Execution
        };
    }

    private static CalendarTelemetryErrorPhase ErrorPhase(CalendarResourceMovePhase phase) => phase switch
    {
        CalendarResourceMovePhase.SchemaLexicalDiscriminator =>
            CalendarTelemetryErrorPhase.SchemaLexicalDiscriminator,
        CalendarResourceMovePhase.OriginScopeAuthorization => CalendarTelemetryErrorPhase.OriginScopeAuthorization,
        CalendarResourceMovePhase.SelectionDiscoveryCapability =>
            CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
        CalendarResourceMovePhase.TargetRevision => CalendarTelemetryErrorPhase.TargetRevision,
        CalendarResourceMovePhase.CompleteResourceSemantics =>
            CalendarTelemetryErrorPhase.CompleteResourceSemantics,
        CalendarResourceMovePhase.AdmissionAndPayload => CalendarTelemetryErrorPhase.AdmissionAndPayload,
        CalendarResourceMovePhase.PostWriteVerificationOrReconciliation =>
            CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation,
        _ => CalendarTelemetryErrorPhase.Execution
    };

    private static CalendarTelemetryErrorCode ErrorCode(CalendarEntityCreateCode code) => code switch
    {
        CalendarEntityCreateCode.Success => throw new ArgumentOutOfRangeException(nameof(code), code, null),
        CalendarEntityCreateCode.InvalidInput => CalendarTelemetryErrorCode.InvalidInput,
        CalendarEntityCreateCode.InvalidCalendarData => CalendarTelemetryErrorCode.InvalidCalendarData,
        CalendarEntityCreateCode.NotFound => CalendarTelemetryErrorCode.NotFound,
        CalendarEntityCreateCode.Ambiguous => CalendarTelemetryErrorCode.Ambiguous,
        CalendarEntityCreateCode.OutsideScope => CalendarTelemetryErrorCode.OutsideScope,
        CalendarEntityCreateCode.UnsupportedCapability => CalendarTelemetryErrorCode.UnsupportedCapability,
        CalendarEntityCreateCode.RecurrenceUnevaluable => CalendarTelemetryErrorCode.RecurrenceUnevaluable,
        CalendarEntityCreateCode.OpaqueResource => CalendarTelemetryErrorCode.OpaqueResource,
        CalendarEntityCreateCode.ConcurrencyUnavailable => CalendarTelemetryErrorCode.ConcurrencyUnavailable,
        CalendarEntityCreateCode.DestinationConflict => CalendarTelemetryErrorCode.DestinationConflict,
        CalendarEntityCreateCode.Conflict => CalendarTelemetryErrorCode.Conflict,
        CalendarEntityCreateCode.LimitExhausted => CalendarTelemetryErrorCode.LimitExhausted,
        CalendarEntityCreateCode.PayloadTooLarge => CalendarTelemetryErrorCode.PayloadTooLarge,
        CalendarEntityCreateCode.UpstreamUnauthorized => CalendarTelemetryErrorCode.UpstreamUnauthorized,
        CalendarEntityCreateCode.UpstreamForbidden => CalendarTelemetryErrorCode.UpstreamForbidden,
        CalendarEntityCreateCode.UpstreamRateLimited => CalendarTelemetryErrorCode.UpstreamRateLimited,
        CalendarEntityCreateCode.FidelityFailure => CalendarTelemetryErrorCode.FidelityFailure,
        CalendarEntityCreateCode.CommittedButUnverified => CalendarTelemetryErrorCode.CommittedButUnverified,
        CalendarEntityCreateCode.CommittedButConcurrencyUnavailable =>
            CalendarTelemetryErrorCode.CommittedButConcurrencyUnavailable,
        CalendarEntityCreateCode.Indeterminate => CalendarTelemetryErrorCode.Indeterminate,
        CalendarEntityCreateCode.UpstreamUnavailable => CalendarTelemetryErrorCode.UpstreamUnavailable,
        CalendarEntityCreateCode.UpstreamProtocolError => CalendarTelemetryErrorCode.UpstreamProtocolError,
        _ => CalendarTelemetryErrorCode.Indeterminate
    };

    private static CalendarTelemetryErrorCategory ErrorCategory(CalendarEntityCreateCode code) => code switch
    {
        CalendarEntityCreateCode.InvalidInput or CalendarEntityCreateCode.InvalidCalendarData =>
            CalendarTelemetryErrorCategory.Input,
        CalendarEntityCreateCode.NotFound or CalendarEntityCreateCode.Ambiguous
            or CalendarEntityCreateCode.OutsideScope => CalendarTelemetryErrorCategory.Selection,
        CalendarEntityCreateCode.UnsupportedCapability or CalendarEntityCreateCode.RecurrenceUnevaluable
            or CalendarEntityCreateCode.OpaqueResource => CalendarTelemetryErrorCategory.CapabilityAndProjection,
        CalendarEntityCreateCode.ConcurrencyUnavailable or CalendarEntityCreateCode.DestinationConflict
            or CalendarEntityCreateCode.Conflict => CalendarTelemetryErrorCategory.State,
        CalendarEntityCreateCode.LimitExhausted or CalendarEntityCreateCode.PayloadTooLarge =>
            CalendarTelemetryErrorCategory.LimitsAndAdmission,
        CalendarEntityCreateCode.FidelityFailure or CalendarEntityCreateCode.CommittedButUnverified
            or CalendarEntityCreateCode.CommittedButConcurrencyUnavailable or CalendarEntityCreateCode.Indeterminate =>
            CalendarTelemetryErrorCategory.PostWriteTruth,
        _ => CalendarTelemetryErrorCategory.Upstream
    };

    private static CalendarTelemetryErrorPhase ErrorPhase(CalendarEntityCreateResult result)
    {
        if (result.Code == CalendarEntityCreateCode.NotFound
            && result.MutationState == CalendarMutationState.NotCommitted)
        {
            return CalendarTelemetryErrorPhase.Execution;
        }
        if (result.MutationState == CalendarMutationState.NotAttempted
            && result.Code is CalendarEntityCreateCode.UpstreamUnavailable
                or CalendarEntityCreateCode.UpstreamProtocolError)
        {
            return CalendarTelemetryErrorPhase.SelectionDiscoveryCapability;
        }
        return result.Code switch
        {
            CalendarEntityCreateCode.InvalidInput => CalendarTelemetryErrorPhase.SchemaLexicalDiscriminator,
            CalendarEntityCreateCode.InvalidCalendarData or CalendarEntityCreateCode.RecurrenceUnevaluable =>
                CalendarTelemetryErrorPhase.CompleteResourceSemantics,
            CalendarEntityCreateCode.NotFound or CalendarEntityCreateCode.Ambiguous
                or CalendarEntityCreateCode.UnsupportedCapability =>
                CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
            CalendarEntityCreateCode.OutsideScope => CalendarTelemetryErrorPhase.OriginScopeAuthorization,
            CalendarEntityCreateCode.OpaqueResource or CalendarEntityCreateCode.ConcurrencyUnavailable =>
                CalendarTelemetryErrorPhase.TargetRevision,
            CalendarEntityCreateCode.PayloadTooLarge => CalendarTelemetryErrorPhase.AdmissionAndPayload,
            CalendarEntityCreateCode.FidelityFailure or CalendarEntityCreateCode.CommittedButUnverified
                or CalendarEntityCreateCode.CommittedButConcurrencyUnavailable
                or CalendarEntityCreateCode.Indeterminate =>
                CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation,
            _ => CalendarTelemetryErrorPhase.Execution
        };
    }

    private static CalendarTelemetryErrorCode ErrorCode(CalendarExactResourceCode code) => code switch
    {
        CalendarExactResourceCode.Success or CalendarExactResourceCode.NoChange =>
            throw new ArgumentOutOfRangeException(nameof(code), code, null),
        CalendarExactResourceCode.InvalidInput => CalendarTelemetryErrorCode.InvalidInput,
        CalendarExactResourceCode.InvalidCalendarData => CalendarTelemetryErrorCode.InvalidCalendarData,
        CalendarExactResourceCode.NotFound => CalendarTelemetryErrorCode.NotFound,
        CalendarExactResourceCode.OutsideScope => CalendarTelemetryErrorCode.OutsideScope,
        CalendarExactResourceCode.EntityKindMismatch => CalendarTelemetryErrorCode.EntityKindMismatch,
        CalendarExactResourceCode.UnsupportedCapability => CalendarTelemetryErrorCode.UnsupportedCapability,
        CalendarExactResourceCode.Conflict => CalendarTelemetryErrorCode.Conflict,
        CalendarExactResourceCode.DestinationConflict => CalendarTelemetryErrorCode.DestinationConflict,
        CalendarExactResourceCode.ConcurrencyUnavailable => CalendarTelemetryErrorCode.ConcurrencyUnavailable,
        CalendarExactResourceCode.LimitExhausted => CalendarTelemetryErrorCode.LimitExhausted,
        CalendarExactResourceCode.PayloadTooLarge => CalendarTelemetryErrorCode.PayloadTooLarge,
        CalendarExactResourceCode.UpstreamUnauthorized => CalendarTelemetryErrorCode.UpstreamUnauthorized,
        CalendarExactResourceCode.UpstreamForbidden => CalendarTelemetryErrorCode.UpstreamForbidden,
        CalendarExactResourceCode.UpstreamRateLimited => CalendarTelemetryErrorCode.UpstreamRateLimited,
        CalendarExactResourceCode.UpstreamUnavailable => CalendarTelemetryErrorCode.UpstreamUnavailable,
        CalendarExactResourceCode.UpstreamProtocolError => CalendarTelemetryErrorCode.UpstreamProtocolError,
        CalendarExactResourceCode.FidelityFailure => CalendarTelemetryErrorCode.FidelityFailure,
        CalendarExactResourceCode.CommittedButUnverified => CalendarTelemetryErrorCode.CommittedButUnverified,
        CalendarExactResourceCode.CommittedButConcurrencyUnavailable =>
            CalendarTelemetryErrorCode.CommittedButConcurrencyUnavailable,
        CalendarExactResourceCode.ConfirmationMismatch => CalendarTelemetryErrorCode.ConfirmationMismatch,
        CalendarExactResourceCode.Indeterminate => CalendarTelemetryErrorCode.Indeterminate,
        _ => CalendarTelemetryErrorCode.Indeterminate
    };

    private static CalendarTelemetryErrorCategory ErrorCategory(CalendarExactResourceCode code) => code switch
    {
        CalendarExactResourceCode.InvalidInput or CalendarExactResourceCode.InvalidCalendarData =>
            CalendarTelemetryErrorCategory.Input,
        CalendarExactResourceCode.NotFound or CalendarExactResourceCode.OutsideScope =>
            CalendarTelemetryErrorCategory.Selection,
        CalendarExactResourceCode.EntityKindMismatch or CalendarExactResourceCode.Conflict
            or CalendarExactResourceCode.DestinationConflict or CalendarExactResourceCode.ConcurrencyUnavailable =>
            CalendarTelemetryErrorCategory.State,
        CalendarExactResourceCode.UnsupportedCapability =>
            CalendarTelemetryErrorCategory.CapabilityAndProjection,
        CalendarExactResourceCode.LimitExhausted or CalendarExactResourceCode.PayloadTooLarge =>
            CalendarTelemetryErrorCategory.LimitsAndAdmission,
        CalendarExactResourceCode.FidelityFailure or CalendarExactResourceCode.CommittedButUnverified
            or CalendarExactResourceCode.CommittedButConcurrencyUnavailable or CalendarExactResourceCode.Indeterminate =>
            CalendarTelemetryErrorCategory.PostWriteTruth,
        CalendarExactResourceCode.ConfirmationMismatch => CalendarTelemetryErrorCategory.Confirmation,
        _ => CalendarTelemetryErrorCategory.Upstream
    };

    private static CalendarTelemetryErrorPhase ErrorPhase(CalendarExactResourcePhase phase) => phase switch
    {
        CalendarExactResourcePhase.SchemaLexicalDiscriminator =>
            CalendarTelemetryErrorPhase.SchemaLexicalDiscriminator,
        CalendarExactResourcePhase.OriginScopeAuthorization => CalendarTelemetryErrorPhase.OriginScopeAuthorization,
        CalendarExactResourcePhase.SelectionDiscoveryCapability =>
            CalendarTelemetryErrorPhase.SelectionDiscoveryCapability,
        CalendarExactResourcePhase.TargetRevision => CalendarTelemetryErrorPhase.TargetRevision,
        CalendarExactResourcePhase.CompleteResourceSemantics =>
            CalendarTelemetryErrorPhase.CompleteResourceSemantics,
        CalendarExactResourcePhase.Mrtr => CalendarTelemetryErrorPhase.Mrtr,
        CalendarExactResourcePhase.PostWriteVerificationOrReconciliation =>
            CalendarTelemetryErrorPhase.PostWriteVerificationOrReconciliation,
        _ => CalendarTelemetryErrorPhase.Execution
    };
}
