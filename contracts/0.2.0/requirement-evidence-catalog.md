# Requirement-to-evidence catalog: unified Calendar contract 0.2.0
This catalog freezes the 96 normative identifiers from issue #35. Each row is independently addressable, has a primary evidence layer, and remains a failing acceptance obligation until evidence is green. The current task establishes the catalog and the pinned Radicale harness; it does not represent task-specific 0.1.x fixtures as Calendar-contract evidence.
## Row schema
Every requirement row contains the normative statement, source, interoperability profile and class, primary evidence layer, named scenario or fixture, objective oracle, implementation status, and evidence status. IDs are never renumbered or reused.
## CAL-BASE-001
- Normative statement: RFC 5545, RFC 6868, verified errata, RFC 4791, RFC 4918/current HTTP conditionals, and RFC 8996 define the unconditional standards baseline. Valid registered and unknown Event data from applicable extensions, including RFCs 7986, 9073, 9074, and 9253, must be preserved. RFC 7529 and RFC 7809 behavior is capability-gated. Owner: [Establish the normative RFC baseline for Event support](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/16).
- Source and owning decision: issue #16, Establish the normative RFC baseline for Event support; normative sources are RFC 5545, RFC 6868, RFC 4791, RFC 4918, and RFC 8996.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/base/cal-base-001`.
- Objective oracle: Given `base-001.ics` with `COLOR:#112233`, `IMAGE:https://e/x.png`, `CONFERENCE:https://e/c`, `LOCATION-TYPE:office`, and `X-KEEP:1`, when a SUMMARY patch is applied, then the five untouched original slices are byte-equal and RFC7529/RFC7809 calls return `unsupported_capability` before any PUT.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.
## CAL-BASE-002

- Normative statement: The first verified Interoperability Profile is Radicale 3.7.8. Standards define the product contract; Radicale accommodations are allowed only when standards-correct, semantically lossless, and explicitly classified. Other servers remain unverified profiles even when runtime capability negotiation allows them to operate. Owner: [Characterize the Radicale Event interoperability envelope](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/17).
- Source and owning decision: issue #17, Characterize the Radicale Event interoperability envelope; normative source is the Radicale 3.7.8 pinned profile.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: Radicale 3.7.8 pinned profile; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: live pinned-Radicale integration.
- Named scenario or fixture: `0.2.0/base/cal-base-002`.
- Objective oracle: Given Radicale 3.7.8 and an unverified server capability transcript, when the profile selector runs, then only `ghcr.io/kozea/radicale@sha256:3a008...5c80` is classified `pinned-profile-only`; the other transcript remains operable but carries no verified-profile claim.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-BASE-003

- Normative statement: Pin the official `ghcr.io/kozea/radicale` OCI index digest `sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80`, with the resolved platform manifest recorded by every run. The verified baseline is Radicale 3.7.8, CPython 3.14.7, vobject 0.9.9, `TZ=UTC`, and `strict_preconditions=false`; required variants use `strict_preconditions=true` and `TZ=America/New_York`. Owner: [Pin the Radicale conformance runtime](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/28).
- Source and owning decision: issue #28, Pin the Radicale conformance runtime; normative source is the official ghcr.io/kozea/radicale OCI index and its platform manifests.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: Radicale 3.7.8 pinned profile; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: live pinned-Radicale integration.
- Named scenario or fixture: `0.2.0/base/cal-base-003`.
- Objective oracle: Given each matrix container variant, when runtime evidence is emitted, then its TRX JSON contains the exact index digest, selected amd64/arm64 manifest, Radicale `3.7.8`, Python `3.14.7`, vobject `0.9.9`, requested TZ, and observed strict-precondition result.
- Implementation status: implemented and passing: the digest-pinned fixture records the selected manifest from the running container architecture and observes the configured strict-precondition behavior.
- Evidence status: passing locally in baseline, strict-preconditions, and alternate-time-zone; required in the same three CI matrix variants.

## CAL-BASE-004

- Normative statement: Use Ical.Net 5.2.3 as a typed parser/editor and bounded recurrence helper only. It must not be the lossless persistence authority, own `THISANDFUTURE` semantics, resolve unproven resource-local time zones, or regenerate unrelated content. Owner: [Establish Ical.Net Event fidelity limits](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/21).
- Source and owning decision: issue #21, Establish Ical.Net Event fidelity limits; normative source is the Ical.Net 5.2.3 compatibility boundary.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/base/cal-base-004`.
- Objective oracle: Given a resource with `RANGE=THISANDFUTURE`, a local VTIMEZONE, and an unrelated X property, when Ical.Net projection is requested, then the service either preserves the raw slices or returns `recurrence_unevaluable`; it never PUTs regenerated Ical.Net text.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MODEL-001

- Normative statement: A Calendar Object Resource is the immutable persistence and concurrency aggregate for exactly one logical Calendar Entity, and Calendar Entity is a closed union of Event or To-do. There is no generic calendar item abstraction. Owner: [Choose the unified Calendar Entity domain model](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/19).
- Source and owning decision: Owner: issue #19, Choose the unified Calendar Entity domain model; normative source: issue #35 domain-model decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/model/cal-model-001`.
- Objective oracle: Given one VEVENT resource, one VTODO resource, and a resource containing both masters, when projected, then the first two have `projection.kind` exactly `event`/`todo` and the mixed resource has `projection.kind=opaque`, one diagnostic, and no entity revision.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MODEL-002

- Normative statement: A projectable resource contains exactly one master Event or To-do, zero or more same-kind and same-UID Recurrence Overrides, and supporting calendar data such as VTIMEZONE. A resource outside that invariant is an Opaque Calendar Object Resource with diagnostics and no semantic mutation surface.
- Source and owning decision: Owner: issue #19, Choose the unified Calendar Entity domain model; normative source: issue #35 domain-model decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/model/cal-model-002`.
- Objective oracle: Given a resource with one UID `u1` master plus same-UID override and VTIMEZONE, and a second fixture with two masters, when read, then the first exposes one projectable aggregate and the second is opaque with a cardinality diagnostic and zero semantic mutation routes.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MODEL-003

- Normative statement: Keep identity layers distinct: Calendar and resource use canonical absolute hrefs; Entity UID is durable logical identity; Entity Tag identifies one resource revision; Recurrence Identity is the original recurrence value; names, summaries, current starts, and positions are never identities.
- Source and owning decision: Owner: issue #19, Choose the unified Calendar Entity domain model; normative source: issue #35 domain-model decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/model/cal-model-003`.
- Objective oracle: Given href `https://cal.example/a.ics`, UID `u-1`, ETag `"e1"`, recurrence ID `2026-01-02T09:00:00`, duplicate name `Work`, and summary `Work`, when serialized, then href/UID/ETag/recurrence ID occupy distinct fields and name/summary never appear in mutation references.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MODEL-004

- Normative statement: Event and To-do share only Entity UID and Entity Kind. Entity Kind is immutable; conversion requires an explicit delete and create. Only To-do has To-do Completion.
- Source and owning decision: Owner: issue #19, Choose the unified Calendar Entity domain model; normative source: issue #35 domain-model decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/model/cal-model-004`.
- Objective oracle: Given a VEVENT and VTODO with UID `same`, when a conversion patch is attempted, then it returns `entity_kind_mismatch` with `category=selection`, `phase=completeResourceSemantics`, `mutationState=not_attempted`, and zero writes.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MODEL-005

- Normative statement: Recurrence Set retains one typed RRULE at most for semantic creation or mutation, all RDATE and EXDATE values, and complete Recurrence Overrides. Standards-valid multiple RRULE resources are preserved but are Unevaluable Recurrence Sets.
- Source and owning decision: Owner: issue #19, Choose the unified Calendar Entity domain model; normative source: issue #35 domain-model decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/model/cal-model-005`; issue #40 targets `CalendarServiceTests.QueryOccurrencesAsync_MultipleRrulesAreTypedRecurrenceUnevaluableWithNoPartialItems` and `RadicaleConformanceHarnessTests.Pinned_profile_preserves_occurrence_boundary_dst_leap_range_and_typed_failures`; issue #44 targets `CalendarEntityCreateServiceTests.CreateEventAsync_InvalidCompleteCalendarDataFailsBeforeDiscoveryOrPut` and `CalendarMcpRawStdioTests.EventCreate_DuplicateRruleReturnsTypedInvalidInputBeforeNetwork`.
- Objective oracle: Given DTSTART, two RRULE lines, two RDATEs, one EXDATE, and two overrides, when read, then `rrules` has count 2, `evaluationState=unevaluable`, every RDATE/EXDATE/override remains ordered, and recurrence expansion returns `recurrence_unevaluable`.
- Implementation status: multiple RRULE resources remain projectable but recurrence evaluation fails closed with `recurrence_unevaluable` and zero partial items; semantic create admits at most one typed RRULE and rejects duplicate raw properties or injected additional content before PUT. Recurrence mutation remains planned.
- Evidence status: focused Core and raw-stdio semantic-create evidence passes locally; digest-pinned Radicale recurrence evidence remains part of the integration matrix.

## CAL-MODEL-006

- Normative statement: An Occurrence is a derived, immutable, read-only projection. It is never persisted or written back as a Calendar Object Resource.
- Source and owning decision: Owner: issue #19, Choose the unified Calendar Entity domain model; normative source: issue #35 domain-model decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/model/cal-model-006`; committed issue #40 target: occurrence snapshot-shape assertions in `CalendarOccurrenceToolsTests.QueryAsync_EmitsExactOccurrenceShapeAndPaginatesByFrozenContinuationTuple`; direct Occurrence-object mutation rejection remains a planned mutation-tool target.
- Objective oracle: Given a recurring event expanded into three occurrences, when occurrence query returns them, then every item has `timing`, original recurrence identity, and an `occurrenceSnapshot.calendarSnapshot` carrying the authoritative source href and ETag. The derived Occurrence object is not accepted directly as mutation input and is never serialized or written back. Any mutation uses its own input contract, an extracted or supplied Revision Reference, explicit original identity and intent, and an authoritative refetch; a direct Occurrence-object write is rejected as `invalid_input` once that mutation tool exists.
- Implementation status: occurrence query returns the frozen derived shape with authoritative source revision; direct Occurrence-object mutation admission remains planned with the mutation tools.
- Evidence status: focused occurrence snapshot-shape evidence passes locally; the direct-write rejection target remains planned and is not claimed as implemented evidence.

## CAL-MODEL-007

- Normative statement: Public contracts expose domain values, not Ical.Net, WebDAV, or HTTP implementation types. The Calendar Service replaces TaskItem, TaskList, ITaskService, and task-specific aliases in `0.2.0`.
- Source and owning decision: Owner: issue #19, Choose the unified Calendar Entity domain model; normative source: issue #35 domain-model decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/model/cal-model-007`.
- Objective oracle: Given the public service and MCP schemas, when reflection and JSON serialization run, then no public member type namespace starts `Ical.Net`, `System.Net.Http`, or `System.Xml`, and the old `TaskItem`, `TaskList`, and `ITaskService` names are absent.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RESOURCE-001

- Normative statement: A direct read returns a Calendar Object Resource Snapshot whose canonical href, exact strong Entity Tag, server-returned UTF-8 bytes, lossless content-line representation, diagnostics, and typed projection all describe one revision. Owner: [Define lossless resource mutation semantics](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/18).
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-001`.
- Objective oracle: Given GET bytes containing folded `DESCRIPTION`, blank lines, and strong ETag `"r1"`, when `calendar_resources.get` reads it, then base64 payload decodes byte-equal, content-line hierarchy preserves fold/slices, and href/ETag/projection/diagnostics identify that same revision.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RESOURCE-002

- Normative statement: The lossless representation retains component hierarchy, every property and parameter occurrence, value type, raw encoded value, and original slices for untouched content. Semantic mutation replaces only addressed semantics and replays untouched slices. Owner: [Probe semantic iCalendar round-trip fidelity](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/30).
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-002`.
- Objective oracle: Given nested VCALENDAR/VTIMEZONE/STANDARD/VEVENT properties with repeated parameters and an unknown component, when SUMMARY changes, then component path, parameter occurrence order, raw encoded values, and untouched original slices are byte-equal.
- Implementation status: implemented for Semantic Patch: addressed scalar and structured occurrences are edited as lossless source slices, and all unaddressed unknown, unsupported, repeated, ordered, and parameterized content is preserved byte-exact in the outbound body; post-write authority is compared semantically so harmless server normalization is accepted.
- Evidence status: issue #43 targets `CalendarEntityPatchServiceTests.PatchEventAsync_ChangesOnlySummaryAndLastModifiedWithExactReviewedRevision` and `CalendarEntityPatchMatrixTests.Post_write_property_reordering_is_success_but_unknown_drift_is_fidelity_failure` pass locally; the digest-pinned Radicale lossless target is included in the integration run.

## CAL-RESOURCE-003

- Normative statement: Query and expanded projections are read-only. Semantic Patch requires the complete direct snapshot as its base; Exact Replacement, Move, and Delete require a Calendar Object Resource Revision Reference containing href, Entity UID, Entity Kind, and Entity Tag.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-003`.
- Objective oracle: Given a query snapshot with href `https://cal.example/a.ics`, UID `u1`, kind event, ETag `"r1"`, when patch/exact-replace/move/delete are invoked, then patch rejects anything but the complete snapshot and each existing-resource operation sends exactly one `If-Match: "r1"`.
- Implementation status: implemented for Event and To-do Semantic Patch: a coherent direct snapshot and strong resource revision are required; ordinary master fields and one explicitly identified existing occurrence are patchable on recurring resources. One-occurrence mutation materializes or edits a complete individual override while occurrence projections remain read-only; recurrence-anchor/membership changes and other mutation scopes are rejected before write. Exact replacement and move remain planned.
- Evidence status: issue #43 Core and MCP targets plus issue #45 `CalendarEntityPatchServiceTests` one-occurrence corpus, `CalendarResourceUpdateProtocolTests`, native-stdio Radicale, and pinned-profile Radicale targets pass locally and are included in the CI test run.

## CAL-RESOURCE-004

- Normative statement: Semantic Create builds one complete typed Event or To-do resource and generates UID when omitted. Exact Create accepts one complete UTF-8 resource with an existing UID. Both validate one master, consistent kind and UID, valid supporting components, and destination support before writing.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-004`.
- Objective oracle: Given semantic create payloads for UID `u1`, VTODO, and VEVENT plus exact UTF-8 resource UID `u2`, when destination capability lacks the requested kind or contains multiple masters, then creation returns `unsupported_capability` or `invalid_calendar_data` with zero PUT; valid create sends `If-None-Match: *`.
- Implementation status: implemented for Semantic Create: complete Event and To-do masters plus complete same-kind and same-UID recurrence overrides are validated before any Calendar discovery, omitted UID is generated once per attempt and inherited by every override, destination component support is required after content preflight, and every exact replayable UTF-8 write uses `If-None-Match: *`. Exact Create remains planned.
- Evidence status: issue #44 targets `CalendarEntityCreateServiceTests.CreateTodoAsync_CreatesRdateOnlySeriesWithExclusionAndCancelledOverride`, `CreateEventAsync_GeneratedUidCollisionUsesOneFreshIdentityWithinTheThreeAttemptBound`, and the pinned-profile recurring-create target; Exact Create evidence remains planned.

## CAL-RESOURCE-005

- Normative statement: Create uses `If-None-Match: *`. Generated identity may retry for collision within the execution bound; caller-supplied UID or href returns conflict without changing identity. Success returns a verified server-read snapshot.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-005`.
- Objective oracle: Given generated UID `g1` colliding once and caller UID `u1` colliding once, when create runs, then generated create retries once with a new UID and `If-None-Match:*`; caller create returns `conflict` with UID `u1`, zero identity changes, and verified GET snapshot on success.
- Implementation status: implemented for Semantic Create: generated UID collisions retry within the three-attempt bound, caller UID collisions do not change identity, every attempt uses `If-None-Match: *`, and success is based on authoritative GET readback with fidelity comparison. Exact Create remains planned.
- Evidence status: issue #44 targets `CalendarEntityCreateServiceTests.CreateEventAsync_GeneratedUidCollisionUsesOneFreshIdentityWithinTheThreeAttemptBound`, existing Event/To-do collision tests, and the pinned-profile recurring-create target.

## CAL-RESOURCE-006

- Normative statement: Semantic Patch uses explicit preserve, set, and clear for scalars and add/remove or destructive replace-all for collections. Removal must be unambiguous. Apply and validate the whole intent in memory; any failure prevents all writes. A semantically unchanged result returns `no_change` without a new revision.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-006`.
- Objective oracle: Given SUMMARY, ORGANIZER, `CATEGORIES:A,B`, and every repeatable structured field, when preserve/set/clear/addRemove/replaceAll variants run, then each exact typed field count and source order matches; a missing or ambiguous lossless occurrence aborts the whole intent with zero PUT; unchanged intent returns `no_change`, and every replaceAll yields MRTR.
- Implementation status: implemented for Event and To-do Semantic Patch: every patch-owned scalar uses explicit set/clear, every repeatable typed field uses addRemove/replaceAll, removals must identify exactly one lossless occurrence, the entire intent validates atomically, and semantic equality returns `no_change` with zero PUT; To-do `COMPLETED` and `STATUS:COMPLETED` are reserved for `todos.complete`, and every replaceAll is routed through MRTR.
- Evidence status: issue #43 matrix targets cover every scalar family and all 18 field-specific structured collections, unambiguous and ambiguous removals, atomic multi-operation rollback, replaceAll, and zero-write no-change; focused Core and MCP suites pass locally.

## CAL-RESOURCE-007

- Normative statement: Semantic Patch may change only modeled first-class or structured registered data. It preserves unknown, unsupported, and unaddressed content. Exact Replacement is the only way to intentionally change the complete payload or unsupported properties.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-007`.
- Objective oracle: Given modeled SUMMARY plus unmodeled `X-KEEP:1` and unsupported `VLOCATION`, when semantic patch changes SUMMARY, then only SUMMARY bytes differ; X/VLOCATION slices are byte-equal; attempting either unmodeled change is rejected before PUT.
- Implementation status: implemented for Semantic Patch: only frozen typed fields are addressable, arbitrary property bags are absent from the closed schema, and unaddressed original slices remain lossless; complete-payload Exact Replacement remains planned.
- Evidence status: `ContractCatalogTests`, `CalendarEntityPatchMatrixTests`, and the outbound-byte oracle in `CalendarEntityPatchServiceTests` pass locally and are included in the CI test run.

## CAL-RESOURCE-008

- Normative statement: Exact Replacement requires the current strong Entity Tag, the same Entity UID and Entity Kind, one valid master, consistent overrides, and a complete payload. Send the caller's UTF-8 payload without Ical.Net regeneration. Only a byte-identical payload skips the write.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-008`.
- Objective oracle: Given current UID `u1`, kind event, ETag `"r1"`, and exact replacement bytes, when bytes are identical then PUT count is zero; when changed valid bytes retain UID/kind/one master, outbound bytes and If-Match are exact; invalid UID/kind/master returns invalid_calendar_data.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RESOURCE-009

- Normative statement: Every update, replacement, move, and delete uses the exact current strong Entity Tag. Missing or weak tags return `concurrency_unavailable`; stale tags return `conflict`, cause no write, and include the current authorized snapshot when available. There is no unsafe bypass or automatic merge.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-009`.
- Objective oracle: Given missing, weak, current, and stale ETags, when update runs, then missing/weak return `concurrency_unavailable`; stale 412 returns `conflict` plus authorized current snapshot and zero writes; current sends one exact strong If-Match without merge.
- Implementation status: implemented for Semantic Patch: origin and Calendar discovery precede revision validation; missing or weak revisions fail before write, the exact caller strong ETag is sent in one `If-Match`, stale 412 returns conflict with a refreshed authorized snapshot when available, and there is no merge or unsafe bypass; other mutation families retain their existing ticket status.
- Evidence status: `CalendarEntityPatchServiceTests`, `CalendarEntityPatchMatrixTests`, and `CalendarResourceUpdateProtocolTests` cover exact preconditions, stale conflict refresh, status mapping, and zero blind retries and pass locally.

## CAL-RESOURCE-010

- Normative statement: Never blindly retry a possibly dispatched mutation. Reconcile with reads and classify the result as committed, unchanged, or indeterminate. Every mutation reports Mutation State as `not_attempted`, `not_committed`, `committed`, or `unknown`.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-010`.
- Objective oracle: Given a dispatch timeout with subsequent GET states committed, unchanged, and differing, when reconciliation runs, then no retry PUT occurs and results report respectively committed, not_committed, and unknown mutationState.
- Implementation status: implemented for Semantic Patch: a possibly dispatched PUT is never retried, bounded refetch reconciliation classifies committed, unchanged/not-committed, or indeterminate/unknown, and every patch result carries the truthful frozen Mutation State.
- Evidence status: issue #43 reconciliation targets cover committed, unchanged, differing, unavailable, and caller-cancelled post-dispatch observations with exactly one PUT and pass locally.

## CAL-RESOURCE-011

- Normative statement: Create, patch, replacement, and move succeed only after validating the observed post-write snapshot; delete succeeds only after verified absence. Semantic difference after commit is `fidelity_failure`; missing verification is `committed_but_unverified`; committed semantics without a usable strong tag is `committed_but_concurrency_unavailable`.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-011`.
- Objective oracle: Given post-write GET fixtures with matching semantics, missing GET, changed unknown slice, missing strong tag, and delete absence, when verification runs, then outputs are success, committed_but_unverified, fidelity_failure, committed_but_concurrency_unavailable, and deletion receipt only after 404 absence.
- Implementation status: implemented for Semantic Patch: success requires a server-refetched snapshot with the addressed semantics, equivalent unaddressed occurrences/multiplicity/order/parameters, and a usable strong tag; missing verification, missing tag, and genuine drift map to the frozen committed outcomes. Other write families remain scoped to their owning tickets.
- Evidence status: `CalendarEntityPatchMatrixTests` covers server normalization versus semantic/lossless drift and the committed-but-unverified/concurrency-unavailable outcomes; the native stdio and pinned-Radicale patch targets are included in the integration run.

## CAL-RESOURCE-012

- Normative statement: Move is atomic, preserves UID and complete semantics, refuses overwrite, verifies destination and source absence, and never degrades to copy-then-delete. Normal Move selects a destination Calendar; explicit href and same-Calendar rename are exact/raw only.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-012`.
- Objective oracle: Given source `s.ics`, destination `d.ics`, collision and successful MOVE responses, when move runs, then collision never overwrites; success preserves UID/bytes, verifies destination GET and source 404, and request trace contains MOVE rather than copy PUT plus DELETE.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RESOURCE-013

- Normative statement: Delete removes the entire resource, not one recurrence. It requires a revision reference, MRTR confirmation, and verified absence, and returns a deletion receipt with href, Entity UID, Entity Kind, and consumed Entity Tag.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-013`.
- Objective oracle: Given full resource `series.ics`, revision `"r1"`, MRTR accept/decline states, when delete runs, then decline makes zero DELETE; accept deletes the full href with exact If-Match, verifies absence, and returns href/UID/kind/consumed tag receipt.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DISC-001

- Normative statement: Discover every Calendar in configured scope and expose Event and To-do Entity Kind Support independently as `advertised`, `not_advertised`, or `unknown`, including raw component evidence and provenance. Advertisement is policy evidence, not enforcement or inventory. Owner: [Define Calendar discovery and query semantics](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/26).
- Source and owning decision: Owner: issue #26, Define Calendar discovery and query semantics; normative source: RFC 4791.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/disc/cal-disc-001`.
- Objective oracle: Given calendars `/a/`, `/b/`, `/c/` advertising VEVENT, VTODO, and neither, when discovered, then each output has independent event/todo advertised/not_advertised/unknown state and raw `supported-calendar-component-set` evidence.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DISC-002

- Normative statement: Calendar canonical href is identity. Calendar Name comes from displayname or a provenance-marked href derivation. Name selection uses trimmed case-insensitive exact equality: zero is `not_found`, one resolves, and multiple are `ambiguous` with authorized candidates.
- Source and owning decision: Owner: issue #26, Define Calendar discovery and query semantics; normative source: RFC 4791.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/disc/cal-disc-002`.
- Objective oracle: Given `/work/` named ` Work ` (Event advertised, To-do unknown), `/archive/` named `work` (Event not_advertised, To-do advertised), and nameless `/third/`, when selection uses `WORK`, then `ambiguous.authorizedCandidates` has exactly two entries, each with its displayName, canonical `calendar.href`, and both independently populated Entity Kind Support values; `/third/` exposes `displayName=third` with `displayNameProvenance=derived-from-href`; unmatched name returns not_found.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DISC-003

- Normative statement: Configured Calendar Scope is an exact canonical-href allowlist. Without an allowlist, all discovered Calendars are in scope. Missing or duplicate configured hrefs are explicit diagnostics.
- Source and owning decision: Owner: issue #26, Define Calendar discovery and query semantics; normative source: RFC 4791.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/disc/cal-disc-003`.
- Objective oracle: Given allowlist `[/a/,/missing/,/a/]` and discovery `/a/,/b/`, when scope applies, then output contains only `/a/` and diagnostics include one missing and one duplicate canonical href; empty allowlist returns both.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DISC-004

- Normative statement: Event and To-do defaults are independent and apply only when no selection is supplied. Explicit missing, ambiguous, out-of-scope, or incompatible selection never falls back. Searching all Calendars is always explicit.
- Source and owning decision: Owner: issue #26, Define Calendar discovery and query semantics; normative source: RFC 4791.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/disc/cal-disc-004`.
- Objective oracle: Given event default `/events/`, todo default `/todos/`, and explicit missing/out-of-scope selections, when create resolves destinations, then omitted event/todo select their own defaults and every explicit bad selection returns typed failure without fallback.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DISC-005

- Normative statement: Semantic entity queries declare one or both Entity Kinds and explicit Calendar Scope, return persisted snapshots, classify actual resource content locally, and report opaque resources and diagnostics separately. Occurrence queries are a separate read-only contract.
- Source and owning decision: Owner: issue #26, Define Calendar discovery and query semantics; normative source: RFC 4791.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/disc/cal-disc-005`.
- Objective oracle: Given event, todo, mixed, and malformed resources in selected scope, when entity query asks `[event,todo]`, then persisted snapshots are canonical-href ordered, malformed result is opaque with diagnostics, and occurrence endpoint is not invoked.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DISC-006

- Normative statement: Server REPORT filters reduce candidates only. Retrieve complete unexpanded resources, perform final semantic filtering and recurrence evaluation locally, and never mutate an expanded or projected REPORT representation. Owner: [Probe Radicale discovery filtering and concurrency](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/31).
- Source and owning decision: Owner: issue #26, Define Calendar discovery and query semantics; normative source: RFC 4791.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/disc/cal-disc-006`.
- Objective oracle: Given REPORT returns an expanded occurrence missing an unknown line and full GET returns the resource, when query filters recurrence, then filtering uses GET bytes locally, output preserves the unknown line, and no mutation targets the REPORT body.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DAV-001

- Normative statement: The writable capability floor is CalDAV/WebDAV discovery and collection PROPFIND, minimal component-filter calendar-query, calendar-multiget, full-resource GET, strong Entity Tags, and conditional create/update/delete. Missing mandatory REPORT support has no Depth-1 crawl fallback. Owner: [Choose the CalDAV capability and fallback policy](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/25).
- Source and owning decision: Owner: issue #25, Choose the CalDAV capability and fallback policy; normative sources: RFC 4791 and RFC 4918.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/dav/cal-dav-001`.
- Objective oracle: Given servers missing REPORT, missing ETag, and complete DAV capability, when writable discovery runs, then missing mandatory capability returns unsupported_capability with no Depth-1 crawl; complete trace includes PROPFIND, calendar-query, multiget/GET and conditionals.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DAV-002

- Normative statement: Use configured Calendar Home when provided and validated; otherwise follow well-known, principal, and calendar-home-set discovery. Validate transport and discovery initially, verify query capabilities on first use, and never probe with artificial writes.
- Source and owning decision: Owner: issue #25, Choose the CalDAV capability and fallback policy; normative sources: RFC 4791 and RFC 4918.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/dav/cal-dav-002`.
- Objective oracle: Given validated configured home `/cal/` and a no-home response chain well-known→principal→calendar-home-set, when discovery runs, then configured home skips chain; fallback emits exact three request order and makes zero artificial writes.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DAV-003

- Normative statement: CalDAV Capability is scoped by origin, Calendar, resource, and operation and classified as advertised, verified, or unavailable. Process-lifetime capability state may be explicitly rediscovered and is invalidated by origin, credentials, or relevant configuration changes, not by transient failures or conflicts.
- Source and owning decision: Owner: issue #25, Choose the CalDAV capability and fallback policy; normative sources: RFC 4791 and RFC 4918.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/dav/cal-dav-003`.
- Objective oracle: Given same origin/calendar with advertised then verified capability, credential change, and 503, when cache is observed, then credential change invalidates/re-discovers; 503 retains state; keys differ by origin/calendar/resource/operation.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DAV-004

- Normative statement: Explicit omission of an Entity Kind blocks create and move for that kind but does not hide existing resources. Unknown support permits reads and blocks writes until verified. Actual content always controls resource classification.
- Source and owning decision: Owner: issue #25, Choose the CalDAV capability and fallback policy; normative sources: RFC 4791 and RFC 4918.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/dav/cal-dav-004`.
- Objective oracle: Given calendar explicitly omitting VTODO, unknown VEVENT support, and existing VTODO resource, when operations run, then VTODO create/move returns unsupported_capability, unknown VEVENT write blocks, and existing VTODO GET remains visible/classified.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DAV-005

- Normative statement: Only semantics-preserving fallbacks are permitted. Optional filters may fall back to a minimal kind query plus local filtering. Missing safe preconditions degrades the affected mutation capability to read-only.
- Source and owning decision: Owner: issue #25, Choose the CalDAV capability and fallback policy; normative sources: RFC 4791 and RFC 4918.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/dav/cal-dav-005`.
- Objective oracle: Given optional filter REPORT failure and absent precondition support, when query/mutation run, then query uses minimal kind REPORT plus local filter preserving result set; mutation is read-only unsupported_capability with zero PUT.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DAV-006

- Normative statement: Reads may follow bounded same-origin 301, 302, 307, and 308 redirects while preserving method and body. Mutations may follow only same-origin 307 and 308. Cross-origin redirects require operator authorization and never receive credentials implicitly; 303 is rejected.
- Source and owning decision: Owner: issue #25, Choose the CalDAV capability and fallback policy; normative sources: RFC 4791 and RFC 4918.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/dav/cal-dav-006`.
- Objective oracle: Given same-origin 301/302/307/308, same-origin mutation 307/308, a 303, and cross-origin redirects, when requests run, then reads preserve method/body only on same-origin 301/302/307/308; mutations follow only 307/308; 303 is rejected; cross-origin sends no implicit credentials unless authorized.
- Implementation status: the mutation half is implemented for conditional patch PUT: only canonical same-origin 307/308 redirects are followed, method/body/If-Match are preserved for at most three hops, and 301/302/303/cross-origin/invalid locations fail closed. Remaining read behavior keeps its existing status.
- Evidence status: `CalendarResourceUpdateProtocolTests` covers each allowed and forbidden redirect class, the exact ceiling, preserved request bytes/precondition, and zero cross-origin credential forwarding and passes locally.

## CAL-EVENT-001

- Normative statement: Event content has three layers: First-class Calendar Fields for common typed semantics, Structured Calendar Data for complete rich or repeatable standard values, and preserved Calendar Properties for everything valid not modeled. Owner: [Define Event content and scheduling-property policy](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/22).
- Source and owning decision: Owner: issue #22, Define Event content and scheduling-property policy; normative sources: RFC 5545 and applicable registered extensions.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/event/cal-event-001`.
- Objective oracle: Given VEVENT fields split into first-class SUMMARY/DTSTART, singleton ORGANIZER, repeatable ATTENDEE, and unknown `X-KEEP`, when scalar and collection patches run, then only the addressed typed occurrence changes, attendee parameter/order stays equal unless addressed, and every unaddressed or unknown original slice is byte-equal.
- Implementation status: implemented for Event Semantic Patch: first-class scalars, singleton Organizer, exact typed structured collections, and preserved source slices remain separate layers throughout validation and editing; create and downstream recurrence mutation keep their owning-ticket status.
- Evidence status: issue #43 Core matrix and strict catalog-schema targets pass locally, including addressed ATTENDEE edits beside byte-exact unknown and unaddressed slices.

## CAL-EVENT-002

- Normative statement: First-class Event fields include optional SUMMARY, DESCRIPTION, start/end/duration, LOCATION, GEO, STATUS, TRANSP, CLASS, PRIORITY, CATEGORIES, URL, and Recurrence Set. Empty TEXT, clear, and omission remain distinct and no trimming is performed.
- Source and owning decision: Owner: issue #22, Define Event content and scheduling-property policy; normative sources: RFC 5545 and applicable registered extensions.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/event/cal-event-002`.
- Objective oracle: Given DTSTART/DTEND/DURATION, SUMMARY, DESCRIPTION, LOCATION, GEO, STATUS, TRANSP, CLASS, PRIORITY, CATEGORIES and URL with empty, clear, omitted, and padded values, when create/patch validates, then empty/clear/omit produce distinct fields, padded input is not trimmed, and invalid end/duration combinations make zero PUT.
- Implementation status: implemented for Event patch scalars and categories, including recurring-master fields that do not change recurrence membership, distinct omission/set-empty/clear intent, and atomic temporal-combination validation; recurrence-anchor and recurrence-definition changes are rejected before write for downstream issues #46/#47.
- Evidence status: `CalendarEntityPatchMatrixTests` exercises set and clear for every Event scalar family, preserves untrimmed text, and proves invalid DTSTART/DTEND/DURATION combinations produce zero PUT.

## CAL-EVENT-003

- Normative statement: Semantic Create generates UID, DTSTAMP, CREATED, and LAST-MODIFIED when omitted. Semantic Patch updates LAST-MODIFIED and preserves UID, DTSTAMP, CREATED, and SEQUENCE; scheduling is excluded, so it never auto-increments SEQUENCE.
- Source and owning decision: Owner: issue #22, Define Event content and scheduling-property policy; normative sources: RFC 5545 and applicable registered extensions.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/event/cal-event-003`.
- Objective oracle: Given create without UID/DTSTAMP/CREATED/LAST-MODIFIED and a patch changing SUMMARY, when persisted, then create generates the four missing values; patch preserves UID, DTSTAMP, CREATED and SEQUENCE byte-for-byte and updates LAST-MODIFIED only.
- Implementation status: the Semantic Patch half is implemented: a real change updates LAST-MODIFIED while UID, DTSTAMP, CREATED, and SEQUENCE remain byte-preserved; no-change writes nothing. Semantic Create generation retains its existing ticket status.
- Evidence status: issue #43 lossless editor targets verify the patch timestamp and immutable identity/scheduling slices and pass locally.

## CAL-EVENT-004

- Normative statement: Structured data includes independent Organizer, ATTENDEE properties, RFC 9073 PARTICIPANT components, Alarms, Attachments, Comments, Contacts, Resources, Related-To, Request-Status, Styled Descriptions, Images, Conferences, Links, Concepts, URI-valued STRUCTURED-DATA, VLOCATION, and VRESOURCE while retaining full parameters, multiplicity, meaningful ordering, and unmodeled properties. ATTENDEE and PARTICIPANT are never synthesized from each other.
- Source and owning decision: Owner: issue #22, Define Event content and scheduling-property policy; normative sources: RFC 5545 and applicable registered extensions.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/event/cal-event-004`.
- Objective oracle: Given distinct ATTENDEE and PARTICIPANT values, URI STRUCTURED-DATA, action-valid DISPLAY/AUDIO/EMAIL alarms, and the remaining named structured collections, when create and field-specific patch run, then every named collection preserves multiplicity/order/parameters, addRemove resolves exactly one lossless occurrence per removal, replaceAll requires MRTR, no ATTENDEE/PARTICIPANT value is synthesized, and every URI remains inert. Given a stored X property, when an unrelated patch runs, then its original slice is byte-equal; semantic create and patch expose no arbitrary property bag for unmodeled content.
- Implementation status: implemented for field-specific Semantic Patch across every named repeatable structured collection: addRemove preserves order/multiplicity and requires exactly one lossless removal match, replaceAll is explicitly destructive and MRTR-gated, and ATTENDEE/PARTICIPANT remain independent. Arbitrary unmodeled mutation remains unavailable.
- Evidence status: `CalendarEntityPatchMatrixTests.Every_structured_collection_maps_to_its_exact_property_or_component` and strict catalog/parser coverage pass locally for all 17 structured fields plus categories.

## CAL-EVENT-005

- Normative statement: Organizer, Attendee, Participant, and related values are Storage-only Scheduling Data. Preserve every syntactically valid CAL-ADDRESS and explicit parameter; never restrict to mailto, deduplicate, infer identity, send invitations, access scheduling inboxes/outboxes, or propagate changes.
- Source and owning decision: Owner: issue #22, Define Event content and scheduling-property policy; normative sources: RFC 5545 and applicable registered extensions.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/event/cal-event-005`.
- Objective oracle: Given ORGANIZER/ATTENDEE `mailto:` and non-mailto CAL-ADDRESS values, when stored or edited, then URI values and parameters round-trip unchanged, mail send/iTIP/network dispatch count is zero, and no mailto-only validation rejects the non-mailto address.
- Implementation status: implemented for Semantic Patch storage: Organizer, Attendee, Participant, and related address values retain explicit URI/parameter data, accept non-mailto CAL-ADDRESS forms through the frozen typed model, and cause no scheduling dispatch.
- Evidence status: issue #43 structured-value serializer/editor targets pass locally; the runtime path contains only CalDAV GET/PUT/refetch operations and no mail or iTIP transport.

## CAL-EVENT-006

- Normative statement: Calendar Alarms and URI-bearing values are inert. Store typed supported forms only when explicitly requested, preserve existing valid forms on unrelated patches, never dereference or execute them, and require Exact Replacement for unsupported value grammars or inline binary typed mutation.
- Source and owning decision: Owner: issue #22, Define Event content and scheduling-property policy; normative sources: RFC 5545 and applicable registered extensions.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/event/cal-event-006`.
- Objective oracle: Given DISPLAY with DESCRIPTION, AUDIO with at most one ATTACH, EMAIL with DESCRIPTION/SUMMARY/one-or-more ATTENDEE plus optional ATTACH, and other URI values, when semantic create validates them, then each action-specific shape is stored inertly with zero dereference/execution; missing required or forbidden fields cause invalid_calendar_data and zero PUT.
- Implementation status: implemented for Semantic Patch: supported typed alarms and URI-bearing values validate through the shared create serializer, remain inert, and unaddressed forms are preserved losslessly; unsupported typed mutation is rejected before PUT.
- Evidence status: the all-structured-field patch matrix includes alarms, attachments, images, conferences, links, structured-data/location/resource URIs and validates zero dereference/execution behavior through the isolated CalDAV client boundary.

## CAL-EVENT-007

- Normative statement: Open enumerations preserve recognized cases or `Other(rawValue)`. Semantic Patch may create recognized values and valid `X-` extensions; changing other unknown values requires Exact Replacement. Derived Calendar Data is read-only to Semantic Patch.
- Source and owning decision: Owner: issue #22, Define Event content and scheduling-property policy; normative sources: RFC 5545 and applicable registered extensions.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/event/cal-event-007`.
- Objective oracle: Given recognized STATUS, unrecognized registered enum, `X-FOO`, and derived occurrence fields, when read/patch runs, then recognized enum is typed, unrecognized/Other/X values preserve raw slice and require exact replacement to change, and derived occurrence field write returns invalid_input.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-TIME-001

- Normative statement: Temporal Value is a closed union of date-only, floating date-time, UTC date-time, or named-time-zone date-time retaining the original TZID. These forms are never collapsed or silently converted. Owner: [Define temporal and recurrence semantics](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/27).
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/time/cal-time-001`.
- Objective oracle: Given date `2026-02-03`, floating `2026-02-03T09:10:11`, UTC `2026-02-03T09:10:11Z`, and zoned `America/New_York`, when serialize/parse runs, then each kind/value/timeZoneId round-trips exactly with no offset normalization.
- Implementation status: implemented for Semantic Create: every closed temporal family is preserved, including complete overrides, and zoned values retain their recognized IANA TZID. Exact Create and the remaining read/mutation surfaces retain their owning-ticket status.
- Evidence status: issue #44 targets `CalendarEntityCreateServiceTests.CreateEventAsync_EmitsOneSupportingVtimezonePerDistinctIanaZone`, `CalendarCreateTimeZoneSerializerTests.SerializeEvent_EmitsDeterministicZoneThatResolvesPastAndFarFutureDst`, and the pinned-profile bounded/unbounded DST recurring-create target.

## CAL-TIME-002

- Normative statement: Instant comparison and expansion for floating or date-only values requires a request-supplied IANA Temporal Evaluation Context. No Calendar, server, process, or host time zone is an implicit fallback.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/time/cal-time-002`; committed issue #40 targets: `CalendarServiceTests.QueryOccurrencesAsync_FloatingComparisonRequiresExplicitEvaluationTimeZoneOnlyWhenEncountered`, `QueryOccurrencesAsync_ExplicitFloatingGapUsesPriorEvaluationZoneOffset`, `QueryOccurrencesAsync_FloatingRruleSkipsEvaluationZoneGapWithoutConsumingCount`, `QueryOccurrencesAsync_DateOnlyEventDefaultsToOneLocalDay`, and `QueryOccurrencesAsync_DateOnlyTodoStartAndDueAreLocalPoints`.
- Objective oracle: Given date/floating values and explicit request IANA `America/Sao_Paulo`, when comparison/expansion runs, then that context is recorded and used; omitted context returns temporal_unresolved and never reads calendar/server/process/host timezone.
- Implementation status: implemented for occurrence comparison and expansion; the request IANA zone is required only when a floating or date-only value is actually evaluated, and no implicit host/server zone is used.
- Evidence status: focused Core and MCP occurrence-query targets pass locally and are included in the CI test run.

## CAL-TIME-003

- Normative statement: Resolve named zones first from one unambiguous resource-local VTIMEZONE and then from a recognized IANA TZID. Unknown or conflicting definitions are unresolved: preserve and diagnose them, permit unrelated semantic changes, and reject evaluation-dependent operations.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/time/cal-time-003`; committed issue #40 targets: `CalendarServiceTests.QueryOccurrencesAsync_UnambiguousResourceLocalTimeZonePrecedesIanaDefinition`, `QueryOccurrencesAsync_ResourceLocalRecurringZoneIsEvaluatedWithoutIanaFallback`, `QueryOccurrencesAsync_ExplicitIanaGapUsesPriorOffsetAndOverlapUsesFirstOccurrence`, `QueryOccurrencesAsync_IanaRruleSkipsGapWithoutConsumingCount`, `QueryOccurrencesAsync_ExplicitResourceLocalGapAndOverlapFollowRfc5545`, `QueryOccurrencesAsync_ResourceLocalRruleSkipsGapWithoutConsumingCount`, `QueryOccurrencesAsync_RruleOverlapUsesFirstOccurrenceExactlyOnce`, `QueryOccurrencesAsync_ExdateSuppressedUnknownZoneOverrideDoesNotRequireResolution`, `QueryOccurrencesAsync_CancelledUnknownZoneOverrideDoesNotRequireResolution`, `QueryOccurrencesAsync_ActiveUnknownZoneOverrideRemainsTypedTemporalUnresolved`, `QueryOccurrencesAsync_UnknownNamedZoneIsTypedTemporalUnresolved`, and `QueryOccurrencesAsync_ConflictingResourceLocalZonesAreTypedTemporalUnresolved`.
- Objective oracle: Given unambiguous resource VTIMEZONE, recognized IANA, explicit and RRULE-generated local values in gaps/overlaps, unknown `Mars/Base`, and conflicting VTIMEZONE, when evaluated, then the resource zone wins before IANA; explicit gap uses the pre-gap offset, overlap uses the first occurrence, generated gap instances are skipped without consuming COUNT, and unknown/conflicting values return temporal_unresolved.
- Implementation status: occurrence queries resolve one resource-local definition before recognized IANA and fail unknown/conflicting definitions as `temporal_unresolved`. Semantic Create accepts only unambiguous recognized IANA local values, rejects unknown/gap/overlap input before Calendar discovery, and emits one deterministic supporting VTIMEZONE per distinct TZID. The serializer uses exact NodaTime TZDB intervals: a baseline at the earliest referenced local plus stable STANDARD/DAYLIGHT offset-signature groups with ordered RDATE transitions through the latest bounded effective end, including nominal and accurate DURATION-derived ends and accurately shifted explicit DTEND/DUE for the last generated occurrence, plus complete-override ends, or through 9999-12-31 for an unbounded rule. It does not consult the wall clock; custom/resource-local definitions remain Exact Create territory. Other mutation behavior remains planned.
- Evidence status: focused Core occurrence evidence and issue #44 `CalendarEntityCreateServiceTests.CreateEventAsync_EmitsOneSupportingVtimezonePerDistinctIanaZone` pass locally; DST create/readback is covered by the pinned-profile target.

## CAL-TIME-004

- Normative statement: Preserve RFC effective-span rules: DTEND and DURATION are exclusive; date-only Event without end lasts one day; date-time Event without end has zero duration; To-do DUE and DURATION are exclusive; and DURATION requires DTSTART. Rescheduling start preserves Effective Temporal Span unless end, due, or duration is explicitly changed.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/time/cal-time-004`; committed issue #40 targets: `CalendarServiceTests.QueryOccurrencesAsync_DateOnlyEventDefaultsToOneLocalDay`, `QueryOccurrencesAsync_DateEventIndividualOverrideDefaultsToOneDay`, `QueryOccurrencesAsync_DateEventRangeOverrideDefaultsEachOccurrenceToOneDay`, `QueryOccurrencesAsync_DateEventOverrideDefaultsToNextLocalDayAcrossDst`, `QueryOccurrencesAsync_IanaMasterDistinguishesNominalDayFromAccurateHours`, `QueryOccurrencesAsync_IanaOverrideDistinguishesNominalDayFromAccurateHours`, `QueryOccurrencesAsync_DetachedOverrideRetainsAccurateMasterSourceSpanAcrossDst`, `QueryOccurrencesAsync_ResourceLocalMasterUsesTheSameDurationArithmetic`, `QueryOccurrencesAsync_WeekDurationRemainsNominalAcrossDst`, `CalendarDurationParser_RejectsNonRfcGrammar`, `CalendarDurationParser_PreservesNominalAndAccurateComponents`, `QueryOccurrencesAsync_RangeDurationUsesPerInstanceNominalThenAccurateArithmetic`, `QueryOccurrencesAsync_RecurringExplicitEndPropagatesExactDurationAcrossDst`, `QueryOccurrencesAsync_RangeExplicitEndPropagatesExactDurationAfterAnchor`, `QueryOccurrencesAsync_RecurringTodoDuePropagatesExactDurationAcrossDst`, `QueryOccurrencesAsync_RecurringDateSearchIncludesTwentyFiveHourFallBackSpan`, `QueryOccurrencesAsync_DateOnlyTodoStartAndDueAreLocalPoints`, and `QueryOccurrencesAsync_SelectedScopeIncludesEventsAndEveryTimedTodoForm`.
- Objective oracle: Given exclusive spans, default one-day event, zero-duration event, VTODO DUE/DURATION variants, missing DTSTART duration, and reschedule, when validated, then bounds/default/span/reschedule results match fixture; zero-duration has effective start=end and appears only when its start lies in range; invalid due/duration/start combinations reject before PUT.
- Implementation status: the occurrence-query effective-span slice is implemented, including exclusive bounds, one-day date Events, zero-duration date-time Events, and available To-do DTSTART/DUE/DURATION forms; reschedule mutation validation remains planned.
- Evidence status: focused Core occurrence-query targets pass locally and are included in the CI test run.

## CAL-RECUR-001

- Normative statement: Occurrence queries use non-empty half-open UTC windows `[from,to)`. Positive-duration Occurrences match overlap; zero-duration Occurrences match start. Moved Occurrences match effective span. Non-recurring To-dos follow their available DTSTART/DUE/DURATION semantics and may yield no temporal Occurrence.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/recur/cal-recur-001`; committed issue #40 targets: `CalendarServiceTests.QueryOccurrencesAsync_SelectedScopeIncludesEventsAndEveryTimedTodoForm`, `QueryOccurrencesAsync_ExdateAndCancellationSuppressWhileMovedTimingRetainsOriginalIdentity`, `CalendarMcpStdioIntegrationTests.CalendarOccurrenceQuery_ProvesBoundaryDstLeapRangeAndTypedFailuresOverRealStdioAndRadicale`, and `RadicaleConformanceHarnessTests.Pinned_profile_preserves_occurrence_boundary_dst_leap_range_and_typed_failures`.
- Objective oracle: Given non-empty UTC `[from,to)` window, overlap event, zero-duration event, moved override, and nonrecurring VTODO fixtures with and without DTSTART/DUE/DURATION, when query runs, then overlap appears, zero-duration matches iff start is in range, moved effective span is returned, todo output follows timing presence, and invalid/empty window rejects.
- Implementation status: implemented for bounded Event and To-do occurrence queries, including half-open overlap/point matching, moved effective spans, every non-recurring To-do timing form, and no occurrence for a To-do without temporal data.
- Evidence status: semantic-corpus targets pass locally; real-stdio and digest-pinned Radicale targets are committed, with live local execution pending because this machine's Docker path could not complete fixture startup.

## CAL-RECUR-002

- Normative statement: Build recurrence from DTSTART, one typed RRULE at most, every RDATE, every EXDATE, and overrides; collapse duplicate identities and apply EXDATE precedence. The RRULE must generate DTSTART as its first occurrence. Radicale 3.7.8 accepts an exact 10,000-occurrence RRULE but rejects a proven 10,001; unbounded RRULE create remains admitted. Standards-valid RDATE PERIOD is preserved, but Radicale 3.7.8 writes reject it before PUT.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/recur/cal-recur-002`; issue #40 targets: `CalendarServiceTests.QueryOccurrencesAsync_RdatePeriodSuppliesOccurrenceSpecificSpan`, `QueryOccurrencesAsync_RdatePeriodDeduplicatesAndOverrideUsesOccurrenceSpecificSourceSpan`, `QueryOccurrencesAsync_RdatePeriodUsesNominalThenAccurateDurationArithmetic`, `QueryOccurrencesAsync_ExdateSuppressesDuplicateRdatePeriodAndItsOverride`, `QueryOccurrencesAsync_DetachedEventOverridesAreEnumeratedWithCancellationAndExdatePrecedence`, `QueryOccurrencesAsync_DetachedTodoOverridesWithoutDtstartRemainObservable`, and `QueryOccurrencesAsync_DueOnlyRecurringTodoIsTypedRecurrenceUnevaluable`; issue #44 targets `CalendarEntityCreateToolsTests.CreateEventRawAsync_CreatesTypedRecurrenceAndReturnsVerifiedSnapshot`, `CreateEventRawAsync_RdatePeriodReturnsUnsupportedBeforeUidLookupOrPut`, and `CalendarEntityCreateServiceTests.CreateTodoAsync_CreatesRdateOnlySeriesWithExclusionAndCancelledOverride`.
- Objective oracle: Given DTSTART, one RRULE, all RDATE/EXDATE/overrides including duplicate and detached identities, VTODO overrides without DTSTART, and RDATE PERIOD, when expansion runs, then duplicates collapse, EXDATE wins even over preserved overrides, detached moved overrides remain observable, cancellations are omitted, PERIOD supplies its own span, and VTODO RRULE without DTSTART is recurrence_unevaluable.
- Implementation status: occurrence query implements RRULE/RDATE/EXDATE/override identity collapse, detached override enumeration, EXDATE precedence, VTODO recurrence validity, and occurrence-specific RDATE PERIOD spans. Semantic Create writes at most one RRULE, every temporal RDATE/EXDATE and complete same-kind override, validates identity families/status consistency/DTSTART inclusion and the pinned 10,000 boundary before discovery, maps nonpositive-count and evaluator-limit failures to `recurrence_unevaluable`, excludes create `THISANDPRIOR`, and returns `unsupported_capability` for a valid PERIOD before discovery or PUT. The strict MCP PERIOD parser reuses the RFC duration grammar, including positive week and explicitly positive day forms; semantically malformed or nonpositive PERIOD input returns `invalid_input` before service invocation.
- Evidence status: focused Core, MCP, contract-schema, raw/native stdio, deterministic WebDAV replay, and digest-pinned Radicale targets cover Event/To-do DTSTART mismatch, COUNT/UNTIL exact and plus-one boundaries, admitted unbounded recurrence, nonpositive and evaluator-limit failures, strict RFC PERIOD input, exclusions, overrides, clock-independent VTIMEZONE readback, and duration-derived DST horizons.

## CAL-RECUR-003

- Normative statement: Recurrence Identity retains the master's temporal family and original value when an Occurrence moves. Individual overrides win over the nearest applicable Range Override; a later Range Override supersedes an earlier range for later identities.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/recur/cal-recur-003`; committed issue #40 targets: `CalendarServiceTests.QueryOccurrencesAsync_NearestRangeReplacesEarlierAndIndividualOverrideWins`, `QueryOccurrencesAsync_MovedRangeMatchesEffectiveWindowAndRetainsOriginalIdentity`, `QueryOccurrencesAsync_CancelledRangeOmitsUntilLaterRangeWhileExactOverrideWins`, `QueryOccurrencesAsync_OldTodoRangeMovedByDueIsIncludedInBoundedSearch`, plus the real-stdio and pinned-Radicale advanced occurrence targets.
- Objective oracle: Given master recurrence ID `2026-01-02T09:00:00`, moved override, and overlapping RANGE overrides, when expanded, then returned identity remains original master family/value and later applicable range override wins deterministically.
- Implementation status: implemented for occurrence queries: original Recurrence Identity is stable, the nearest applicable RANGE transformation is used without double stacking, a later range supersedes an earlier range, and an exact individual override wins.
- Evidence status: focused Core targets pass locally; the moved-identity/range-precedence path is committed to real-stdio and digest-pinned Radicale CI targets.

## CAL-RECUR-004

- Normative statement: `one-occurrence` creates or updates one complete individual override. `this-and-future` applies addressed changes from its anchor while preserving relative exception offsets and unrelated properties. `entire-set` applies addressed changes to master and all overrides with the same preservation rules.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/recur/cal-recur-004`.
- Objective oracle: Given master plus overrides, when one-occurrence, this-and-future, and entire-set patches run, then one creates one complete override, future changes anchor forward while preserving unrelated exceptions, and entire-set changes all same-UID overrides only.
- Implementation status: implemented for all three scopes. `one-occurrence` materializes one complete individual override; `this-and-future` creates or updates the exact original-identity RANGE override and applies addressed scalar or relative temporal changes through later overrides; `entire-set` applies addressed semantics to master and every same-UID override. All scopes preserve unaddressed source slices, individual/later-range precedence, deliberate temporal offsets, and mandatory non-derived LAST-MODIFIED effects on each changed component. Shared recurrence-structure validation returns `recurrence_unevaluable`.
- Evidence status: issues #45 and #47 target `CalendarEntityPatchServiceTests` for complete individual materialization, RANGE creation/update and precedence, sparse and explicit relative timing, Event/To-do entire-set parity, lossless properties, no-change, exact reconciliation, stale revision, and temporal-family rejection; pinned-profile, native-stdio, and raw-stdio targets cover MRTR and one conditional persistence plus verified readback.

## CAL-RECUR-005

- Normative statement: Recurrence-definition changes require every exclusion and override to remain valid or be explicitly reconciled in the same intent. Temporal-family changes for recurring entities require Exact Replacement.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/recur/cal-recur-005`.
- Objective oracle: Given a recurrence-definition edit leaving orphan EXDATE or individual/RANGE override identities and a temporal-family change, when patch runs, then the orphan case returns invalid_calendar_data unless the same recurrence-set scalar supplies an exact one-to-one remove reconciliation for every orphan, missing/extra/duplicate/wrong-kind reconciliation writes nothing, and family change requires Exact Replacement with zero semantic PUT.
- Implementation status: the frozen patch contract now carries an optional closed `orphanReconciliations` array on both recurrence-set `set` and `clear`; each entry identifies an EXDATE or the exact individual/RANGE override at an original Recurrence Identity and authorizes removal only. Retargeting or transforming an orphan remains Exact Replacement.
- Evidence status: issue #47 targets strict catalog/parser parity and Event/To-do semantic corpus cases for exact reconciliation completeness, atomic rejection, lossless non-orphan preservation, MRTR revision binding, and one conditional PUT plus verified readback.

## CAL-RECUR-006

- Normative statement: Exclusion adds EXDATE; cancellation creates or updates a cancelled complete override; restoration removes only the exclusion or cancelled status. EXDATE suppresses but does not delete an override. Adding a nonexistent identity is an explicit RDATE operation.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/recur/cal-recur-006`.
- Objective oracle: Given series with EXDATE, cancelled override, and missing identity, when exclude/cancel/restore/add run, then exclude adds EXDATE without deletion, cancellation creates cancelled override, restorations remove only named construct, and add creates explicit RDATE.
- Implementation status: implemented for Event and To-do: add writes one exact RDATE only for a missing identity; exclude writes one exact EXDATE without deleting an override; cancellation materializes or updates the complete effective individual override; each restoration removes only the addressed EXDATE or cancelled status while preserving the override and other recurrence state.
- Evidence status: `CalendarOccurrenceMutationServiceTests` covers every operation, master/range/individual precedence, both exclusion/cancellation restoration orders, deterministic duplicate/missing/unresolved/unevaluable/PERIOD outcomes, successful DATE/floating/resolved-TZID adds for Event and To-do, exact strong revision handling, one-PUT reconciliation, and verified readback. Native stdio against digest-pinned Radicale covers the complementary full five-operation To-do round trip; raw stdio covers duplicate and oversized input rejection with bounded redacted results.

## CAL-RECUR-007

- Normative statement: To-do Completion may target a non-recurring To-do or exactly one identified recurring Occurrence and records its completion instant. `this-and-future` and `entire-set` are invalid completion scopes.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/recur/cal-recur-007`.
- Objective oracle: Given recurring VTODO identities and completion instant `2026-01-03T10:00:00Z`, when one occurrence completes, then only that override has COMPLETED/STATUS, future identities remain incomplete, and future/entire-set completion scopes return invalid_input.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MCP-001

- Normative statement: Target stable MCP 2026-07-28 or a later stable revision verified at implementation time. Implement `server/discover`; requests are stateless and self-contained. Do not implement removed initialization/session lifecycle, sticky sessions, legacy SSE resumability, or proprietary protocol substitutes. Owner: [Choose the MCP calendar tool and safety model](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/20).
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-001`.
- Objective oracle: Given `server/discover` request and removed initialization/SSE methods, when dispatched, then discover returns negotiated 2026-07-28 metadata, each request is stateless, and removed methods return protocol unknown-method without session state.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MCP-002

- Normative statement: The default semantic catalog, in deterministic discovery/read/write order, is: `calendars.list`, `calendar_entities.query`, `calendar_occurrences.query`, `calendar_resources.get`, `events.create`, `events.patch`, `todos.create`, `todos.patch`, `todos.complete`, `calendar_occurrences.add`, `calendar_occurrences.exclude`, `calendar_occurrences.restore_exclusion`, `calendar_occurrences.cancel`, `calendar_occurrences.restore_cancellation`, `calendar_resources.move`, and `calendar_resources.delete`.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-002`; committed issue #40 targets: `CalDavHostBuilderTests.CreateBuilder_DefaultMode_RegistersCalendarDiscoveryAlongsideChatSafeLegacyTools` and `BuildHost_DefaultMode_ListsToolsInCanonicalWireOrder`.
- Objective oracle: Given tools/list, when catalog is emitted, then the 16 default names exactly equal the committed discovery order and no exact tool appears without opt-in.
- Implementation status: the `calendar_occurrences.query`, `events.patch`, and `todos.patch` catalog positions and default exposure are implemented; remaining semantic tools retain their owning-ticket status.
- Evidence status: focused host discovery/order and strict patch-schema targets pass locally and are included in the CI test run.

## CAL-MCP-003

- Normative statement: The opt-in exact catalog is: `calendar_resources.exact_get`, `calendar_resources.exact_create`, `calendar_resources.exact_replace`, and `calendar_resources.exact_move`. Configuration exposure and authorization are independent gates. Normal results do not embed raw iCalendar; exact reads use protected MCP resource links and do not enumerate the CalDAV store through resources/list.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-003`.
- Objective oracle: Given exact tools disabled/enabled, authorized/unauthorized callers, and a raw resource read, when discovery/call runs, then disabled list omits four names; enabled unauthorized exact call is denied, authorized read returns readable protected resource_link, normal result contains no raw iCalendar, and resources/list is empty.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MCP-004

- Normative statement: Inputs are strict JSON Schema 2020-12 closed camel-case objects with discriminated unions, explicit required values, and duplicate/unknown-property rejection. Every tool defines and validates an output schema and returns authoritative `structuredContent` plus concise compatible text content.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-004`; committed issue #40 targets: `CalendarOccurrenceToolsTests.QueryRawAsync_AcceptsExactFrozenShapeWithoutEntityKindsAndDefaultsAbsentPageSize`, `QueryRawAsync_RejectsMissingNullAndUnknownFrozenShape`, `QueryAsync_EmitsExactOccurrenceShapeAndPaginatesByFrozenContinuationTuple`, and `CalendarMcpRawStdioTests.CalendarOccurrenceQuery_RootDuplicateArgumentsReturnTypedInvalidInputBeforeNetwork`.
- Objective oracle: Given valid closed input, unknown sibling, duplicate JSON member, and typed failure, when tool validates, then valid authoritative structuredContent matches output schema with concise compatible text content, invalid values return schema-valid invalid_input, and parser rejects duplicate/unknown members.
- Implementation status: implemented for `calendar_occurrences.query`, `events.patch`, and `todos.patch`, including closed discriminated inputs, duplicate/unknown rejection, exact field-specific typed unions, schema-valid structured results, and bounded compatible text; later tools remain planned.
- Evidence status: focused MCP parser/tools, catalog closure, and native raw-stdio targets pass locally and are included in the CI test run.

## CAL-MCP-005

- Normative statement: Calendar Reference selects exactly one Calendar by exact name or canonical href. Calendar Scope is `default`, `selected`, or explicit `all`. Existing-resource mutations always require a Calendar Object Resource Revision Reference and refetch the current revision before writing.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-005`; committed issue #40 target: `CalendarOccurrenceToolsTests.QueryAsync_CursorIsNonceRandomTamperEvidentBoundToQueryCredentialsExpiryAndProcessKey` exercises default, selected, and all scope binding through the occurrence query cursor.
- Objective oracle: Given by-name, by-href, default, selected, all scope, and a stale revision, when resolution/mutation runs, then exactly one selector branch is accepted, all is explicit, and mutation refetches expected href/UID/kind/ETag before write.
- Implementation status: the read-only Calendar Scope path is implemented for occurrence queries; Event and To-do patch additionally require the complete direct revision reference and refetch href/UID/kind/ETag before write.
- Evidence status: focused occurrence-query scope plus issue #43 direct-revision, mismatch, and conflict targets pass locally and are included in the CI test run.

## CAL-MCP-006

- Normative statement: Scalar patch operations are set or clear; collection operations are addRemove or replaceAll. `replaceAll` and recurrence-definition changes are high-impact. A patch explicitly targets master or original Recurrence Identity plus Mutation Scope.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-006`.
- Objective oracle: Given first-class and ORGANIZER scalar set/clear plus field-specific categories and structured collection addRemove/replaceAll with master/identity scopes, when patch runs, then exact typed closed schemas admit only the named value type, modeled operations reach only their target, every replaceAll produces MRTR, and wrong scope/identity returns invalid_input with zero PUT.
- Implementation status: implemented for master- and one-occurrence-scoped Event and To-do patch: the exact frozen scalar and field-specific collection unions plus the frozen original-identity wrapper are parsed strictly, scalar/addRemove edits execute directly, and replaceAll always enters MRTR with target/identity-bound preview and clock-stable intent. One-occurrence requires an identity and reserves cancellation/restoration status changes for their dedicated operations; master forbids an identity; `this-and-future`, `entire-set`, and recurrence-definition/membership edits fail before write for downstream tickets.
- Evidence status: `ContractCatalogTests` and `CalendarEntityPatchToolsTests` cover exact schema closure, every typed field mapping, direct versus MRTR routing, strict occurrence-target admission, target/sibling-bound continuation, and wrong-scope zero-write rejection; the native-stdio Radicale target proves real MCP binding and execution.

## CAL-MCP-007

- Normative statement: Query envelopes contain items, diagnostics, pagination mode, and nextCursor. Entity ordering is canonical Calendar href then resource href; Occurrence ordering is effective start, Calendar href, Entity UID, then Recurrence Identity. Pagination is explicitly non-snapshot.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-007`; committed issue #40 targets: `CalendarServiceTests.QueryOccurrencesAsync_OrdersByEffectiveStartCalendarHrefUidThenRecurrenceIdentity` and `CalendarOccurrenceToolsTests.QueryAsync_EmitsExactOccurrenceShapeAndPaginatesByFrozenContinuationTuple`.
- Objective oracle: Given paginated entity and occurrence results with equal timestamps, when query continues, then envelope has items/diagnostics/non_snapshot/nextCursor, entities sort calendar href/resource href and occurrences sort effective start/href/UID/identity.
- Implementation status: implemented for occurrence queries: exact effective-start/Calendar-href/UID/Recurrence-Identity ordering, non-snapshot envelope, and protected continuation tuple pagination; entity ordering was delivered by the preceding query slice.
- Evidence status: focused Core and MCP targets pass locally and are included in the CI test run.

## CAL-MCP-008

- Normative statement: Expected input, domain, capability, limit, concurrency, CalDAV, and execution failures remain schema-valid tool results with MCP `isError: true`. Invalid protocol messages, unknown methods/tools, incompatible versions, and MCP transport authentication/authorization use their protocol or HTTP channels. `no_change` and declined confirmation use `isError: false`. Unexpected failures are sanitized.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-008`; committed issue #40 targets: `CalendarOccurrenceToolsTests.QueryAsync_MapsEveryDomainFailure`, `QueryAsync_PreservesDiscoveryPhaseForDiscovery404`, `QueryAsync_MalformedDiscoveryRetainsSelectionDiscoveryPhase`, and `CalendarMcpRawStdioTests.CalendarOccurrenceQuery_NormalRawCallReachesServiceAndReturnsTypedExecutionFailure`.
- Objective oracle: Given invalid input, conflict, limit, upstream, unexpected exception, no_change, declined confirmation, invalid JSON, unknown method/tool, incompatible version and transport auth failure, when requests run, then tool failures use isError/schema-valid safe fields, protocol cases use their protocol/HTTP channel, and no_change/decline use isError false.
- Implementation status: implemented for occurrence query plus Event and To-do patch failures: invalid input, conflict, concurrency/fidelity/limit/upstream outcomes remain schema-valid and redacted; unexpected failures are sanitized; `no_change` and confirmation decline remain successful MCP results. Other mutation tools retain their owning-ticket status.
- Evidence status: focused MCP unit and native raw-stdio targets cover the patch status/reconciliation matrix, unexpected exceptions, no-change, and decline and pass locally.

## CAL-MCP-009

- Normative statement: Use MCP Multi Round-Trip Requests for delete, all exact writes, replaceAll, recurrence-definition changes, this-and-future, entire-set, and any future multi-resource mutation. Preview resolves and validates read-only, binds opaque ten-minute requestState to normalized arguments, principal or credential context, fixed identity/destination, and Entity Tag, and revalidates everything before write.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-009`.
- Objective oracle: Given delete, exact write, replaceAll, recurrence change and future scope previews, when confirmation is requested/retried, then outer input_required has requestState/inputRequests, retry binds normalized args/principal/identity/ETag for ten minutes and revalidates before one write.
- Implementation status: implemented for patch replaceAll only: preview is read-only and names the exact tool operation, canonical href, UID/kind, ETag, and replaced field counts without values; opaque ten-minute requestState binds normalized arguments, credentials, identity, and revision, then the accepted retry revalidates before one conditional write. Other listed high-impact operations remain planned.
- Evidence status: `CalendarEntityPatchToolsTests` covers accept, decline, mismatch, expiry, credential/argument/revision binding, malformed continuations, re-review, preview redaction, and zero-write failures and passes locally.

## CAL-MCP-010

- Normative statement: Decline, expiry, mismatch, changed arguments, changed revision, or invalid ownership writes nothing. A direct explicit Semantic Create, scalar single-resource patch, one-Occurrence mutation, or To-do Completion may execute without extra server-requested confirmation.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-010`.
- Objective oracle: Given declined, expired, mismatched arguments, changed ETag, invalid owner, direct semantic create, scalar single-resource patch, one-occurrence mutation and todo completion, when called, then first five have no write and typed confirmation failure; the four direct operations execute without MRTR.
- Implementation status: implemented for Event and To-do patch, including one-occurrence: decline, expiry, ownership/argument/revision mismatch write nothing; scalar and unambiguous addRemove edits execute directly for master or one existing occurrence, while destructive replaceAll alone requires MRTR in this slice. Other direct operations retain their owning-ticket status.
- Evidence status: focused MRTR continuation tests prove exact zero-write rejection and direct-versus-review routing; `CalendarEntityPatchToolsTests.PatchEventRawAsync_ParsesOneOccurrenceIdentityAndExecutesDirectly` and the native-stdio Radicale occurrence target prove the explicit one-occurrence direct path.

## CAL-MCP-011

- Normative statement: One tool call mutates at most one Calendar Object Resource. There is no bulk mutation, implicit repeated mutation, search-then-destroy tool, or generic action/update tool.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-011`.
- Objective oracle: Given a request attempting two hrefs or a search-then-delete payload, when schema/service validates, then it returns invalid_input before network and trace has at most one resource mutation.
- Implementation status: implemented for `events.patch` and `todos.patch`: the closed request contains one canonical resource revision and the shared one-mutation admission coordinator permits at most one resource PUT per call; no bulk/search-then-mutate form is exposed.
- Evidence status: issue #43 strict schema, admission, and atomic multi-operation tests pass locally; the global four-operation/progress policy remains explicitly assigned to issue #53.

## CAL-MCP-012

- Normative statement: Cache hints are private: Calendars list uses 30 seconds, semantic queries 5 seconds, and direct snapshots and mutations 0. The fixed catalog does not advertise list-change notifications. Tool annotations describe behavior but never enforce policy.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-012`; committed issue #40 target: `CalDavHostBuilderTests.BuildHost_AdvertisesFrozenOccurrenceQuerySchemasAndPrivateCacheHint`.
- Objective oracle: Given list/query/snapshot/mutation calls, when annotations/cache inspected, then ttlMs are 30000/5000/0, cacheScope private, no list-change notification is advertised, and all annotation booleans match external CalDAV behavior.
- Implementation status: the private five-second cache hint and frozen annotations are implemented for `calendar_occurrences.query`; later catalog entries remain planned by their owning tickets.
- Evidence status: focused host metadata target passes locally and is included in the CI test run.

## CAL-MCP-013

- Normative statement: The initial server does not implement the MCP Tasks extension because operations are synchronous, bounded, and cancellable. Any future durable operation must use the officially negotiated extension rather than an application-specific async protocol.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-013`.
- Objective oracle: Given tasks extension method and bounded synchronous tool call, when dispatched, then task extension is absent/unknown-method and call completes/cancels within its budget without application-specific async result.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-BOUND-001

- Normative statement: Validate in this order: transport authorization; admission and payload size; schema/lexical/discriminator; origin/scope/caller authorization; selection/discovery/capability; target revision; complete resource semantics; MRTR; execution; post-write verification or reconciliation. Semantic Create is the frozen exception: because its complete proposed resource is caller-local, recurrence/temporal/override/zone semantics and profile preflight run after local destination validation but before Calendar discovery; normal selection/capability ordering resumes only after that zero-network preflight. Return the earliest failing phase and at most 32 safe violations ordered by JSON Pointer. Owner: [Choose validation errors and execution bounds](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/23).
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative source: issue #35 safety decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/bound/cal-bound-001`; committed issue #40 targets: `CalendarOccurrenceToolsTests.QueryRawAsync_EnforcesExactArgumentByteBoundary`, `QueryRawAsync_RejectsMissingNullAndUnknownFrozenShape`, `QueryAsync_PreservesDiscoveryPhaseForDiscovery404`, and `QueryAsync_MalformedDiscoveryRetainsSelectionDiscoveryPhase`.
- Objective oracle: Given inputs failing authorization, payload, schema, origin, selection, revision, resource semantics, MRTR, execution and verification in combination, when validated, then only earliest phase is returned and at most 32 JSON-pointer-sorted safe violations appear.
- Implementation status: the occurrence-query validation path is implemented through execution, including payload-before-schema ordering and earliest `selectionDiscoveryCapability` versus `execution` phase mapping; mutation-only phases remain planned.
- Evidence status: focused MCP targets pass locally and are included in the CI test run.

## CAL-BOUND-002

- Normative statement: Occurrence and entity temporal windows are non-empty and at most 366 days. pageSize defaults to 50 and is capped at 200. One query inspects at most 5,000 resources across 256 Calendars.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative source: issue #35 safety decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/bound/cal-bound-002`; committed issue #40 targets: `CalendarOccurrenceToolsTests.QueryRawAsync_AcceptsExactFrozenShapeWithoutEntityKindsAndDefaultsAbsentPageSize` plus bounded-window and page-size cases in `CalendarServiceTests` and `CalendarOccurrenceToolsTests`.
- Objective oracle: Given 0-day, 366-day, 367-day windows; page sizes 0,50,200,201; 256 calendars and 5001 resources, when queried, then valid bounds/default 50/cap 200 apply and excess returns limit_exhausted before partial results.
- Implementation status: implemented for occurrence queries through the shared bounded query engine: non-empty windows up to 366 days, nullable pageSize default 50/cap 200, and shared 5,000-resource/256-Calendar ceilings with zero partial results.
- Evidence status: focused Core and MCP boundary targets pass locally and are included in the CI test run.

## CAL-BOUND-003

- Normative statement: Expansion derives at most 2,000 Occurrences per Calendar Entity, 5,000 per query, and 10,000 unmatched increments per Recurrence Set. Limit Exhaustion returns no partial items.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative source: issue #35 safety decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/bound/cal-bound-003`; committed issue #40 targets: `CalendarServiceTests.QueryOccurrencesAsync_EnforcesExactPerEntityOccurrenceBoundaryWithZeroPartial`, `QueryOccurrencesAsync_PeriodBoundaryCountsUniqueDerivedWorkOutsideWindow`, `QueryOccurrencesAsync_DuplicatePeriodIdentityDoesNotInflateDerivedWork`, `QueryOccurrencesAsync_DetachedOverrideBoundaryCountsUniqueDerivedWorkOutsideWindow`, `QueryOccurrencesAsync_PerEntityBoundaryUnionsRrulePeriodAndDetachedIdentities`, `QueryOccurrencesAsync_EnforcesExactTotalOccurrenceBoundaryWithZeroPartial`, `QueryOccurrencesAsync_EnforcesExactUnmatchedIncrementBoundaryWithoutPartialOrInventedOccurrenceCount`, and `QueryOccurrencesAsync_OldResourceLocalSeriesStartsNearBoundedWindow`.
- Objective oracle: Given recurrence fixtures deriving 2000/2001 unique entity identities in and outside the query window, including RDATE PERIOD and detached overrides; 5000/5001 query occurrences; and 10000/10001 unmatched increments, when expanded, then ceilings pass exactly, duplicate identities neither inflate nor bypass the work count, and plus-one returns limit_exhausted with items count zero.
- Implementation status: implemented for recurrence expansion with exact 2,000/2,001 per-entity, 5,000/5,001 per-query, and 10,000/10,001 unmatched-increment behavior; every exhausted result contains zero partial items.
- Evidence status: exact boundary targets pass locally and are included in the CI test run.

## CAL-BOUND-004

- Normative statement: Normal semantic arguments are at most 256 KiB; one authoritative resource or exact payload is at most 4 MiB; one structured page is at most 4 MiB; human-readable text plus diagnostics is at most 64 KiB. Measure final UTF-8 JSON, resource UTF-8 bytes, and decompressed HTTP bodies with streaming limit-plus-one enforcement.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative source: issue #35 safety decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/bound/cal-bound-004`; committed issue #40 targets: `CalendarOccurrenceToolsTests.QueryRawAsync_EnforcesExactArgumentByteBoundary`, `QueryAsync_RejectsSingleOccurrenceThatCannotFitStructuredBudget`, and `QueryAsync_RejectsHumanAndDiagnosticContentBeyondShared64KiBBudget`.
- Objective oracle: Given UTF-8 JSON at 256KiB±1, resource/exact payload/page at 4MiB±1, text/diagnostics at 64KiB±1, and compressed body expanding over limit, when streamed, then limit-plus-one rejects with payload_too_large and no partial serialization.
- Implementation status: occurrence query and Event/To-do patch implement the 256 KiB argument and 4 MiB structured-result budgets; patch MRTR additionally applies the exact 64 KiB final UTF-8 human-readable preview boundary before review/elicitation, with no partial result.
- Evidence status: focused MCP exact and plus-one boundary targets, including patch preview at 64 KiB and 64 KiB plus one, pass locally and are included in the CI test run.

## CAL-BOUND-005

- Normative statement: One HTTP attempt is 10 seconds; a read is 30 seconds; mutation before dispatch is 30 seconds; reconciliation may use another 30 seconds within a 60-second total. Reads have at most three transient attempts; mutations have none; generated-UID create has at most three collision attempts.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative source: issue #35 safety decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/bound/cal-bound-005`; committed issue #40 target: `CalendarOccurrenceToolsTests.QueryAsync_FinalDeadlineReturnsLimitErrorWithoutSuccessItems`.
- Objective oracle: Given reads with transient failures, mutations before/after dispatch, and fake clock at 10/30/60 seconds, when retries execute, then reads make at most three attempts, mutations no blind retries, and timeout code/phase reflects attempt, operation, or reconciliation bound.
- Implementation status: the occurrence-query read deadline and Event/To-do patch bounds are implemented: patch validation/refetch must dispatch within 30 seconds, PUT is attempted once without retry, and possible dispatch receives bounded reconciliation within the mutation total; generated-UID create remains outside this slice.
- Evidence status: focused fake-time patch pre-dispatch, MRTR preview, direct-call, HTTP no-retry, and reconciliation targets pass locally and are included in the CI test run.

## CAL-BOUND-006

- Normative statement: Per origin, admit at most four operations and one mutation, queue at most 16 FIFO calls, and wait at most two seconds before `busy`. Requested progress begins after 500 ms, emits at most four notifications per second, uses aggregate phases, and never reveals names, hrefs, or content.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative source: issue #35 safety decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/bound/cal-bound-006`.
- Objective oracle: Given five concurrent origin operations, two mutations, queue positions 1..17, and progress clock 499/500ms, when admitted, then max four/one run, seventeenth is busy after two seconds, and progress is <=4/s aggregate with no href/name/content.
- Implementation status: implemented only for the #43 mutation portion by reusing the shared per-origin one-mutation FIFO coordinator, including bounded queue admission; the global maximum four operations and progress notification policy remain assigned to issue #53 and are not claimed here.
- Evidence status: focused patch admission tests prove one active mutation, FIFO handoff, busy rejection, and waiter cancellation without a write and pass locally.

## CAL-BOUND-007

- Normative statement: Cancellation stops reads and pre-dispatch mutations promptly. After possible dispatch, bounded reconciliation continues despite caller cancellation so Mutation State remains truthful.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative source: issue #35 safety decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/bound/cal-bound-007`; committed issue #40 target: `CalendarOccurrenceToolsTests.QueryAsync_CallerCancellationPropagatesWithoutReturningAResult`.
- Objective oracle: Given cancelled read, cancelled pre-dispatch mutation, and cancelled post-dispatch mutation, when tokens fire, then first two stop promptly with zero/one dispatch respectively and post-dispatch continues bounded reconciliation to truthful mutationState.
- Implementation status: prompt caller cancellation is implemented for occurrence reads and pre-dispatch Event/To-do patch; once PUT may have dispatched, patch ignores caller cancellation only for bounded authoritative reconciliation and returns truthful Mutation State.
- Evidence status: focused patch targets cover cancellation before service execution, before PUT, and after possible dispatch with one PUT/no blind retry and pass locally.

## CAL-BOUND-008

- Normative statement: Cursors and MRTR requestState are authenticated, encrypted, at most 2 KiB, expire after ten minutes, bind normalized inputs and credential context, and are invalidated by key rotation. Replay remains non-duplicating through fixed identity plus conditional writes and full revalidation.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative source: issue #35 safety decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/bound/cal-bound-008`; committed issue #40 targets: `CalendarOccurrenceToolsTests.QueryAsync_CursorIsNonceRandomTamperEvidentBoundToQueryCredentialsExpiryAndProcessKey` and `QueryAsync_CursorCredentialBindingRejectsSameKeyUnderDifferentCredentials`.
- Objective oracle: Given cursor/requestState exceeding 2KiB, expired at ten minutes, rotated key, changed arguments/principal/ETag, one-bit tamper, and replay, when consumed, then each invalid handle fails closed; serialized bytes reveal none of normalized args/principal/identity/ETag and valid replay remains nonduplicating through conditional write.
- Implementation status: occurrence cursors and patch replaceAll requestState are implemented with authenticated encryption, random nonce, canonical base64url, ten-minute expiry, process-key rotation invalidation, credential/normalized-input/operation/revision binding, and a 2 KiB cap; accepted replay remains non-duplicating through refetch plus exact If-Match.
- Evidence status: focused cursor and patch requestState tamper/expiry/restart/credential/argument/revision/replay targets pass locally and are included in the CI test run.

## CAL-ERROR-001

- Normative statement: Every typed error includes code, category, safe message, retryable, and phase, with optional capped violations, limits, retryAfterMs, authorized candidates, or current conflict snapshot. It never includes rejected raw values, complete arguments, resource content, credentials, HTTP bodies, cursors, requestState, or stack traces.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative sources: RFC 4791, RFC 4918, and HTTP conditionals.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/error/cal-error-001`.
- Objective oracle: Given each typed failure plus 33 violations, retry delay, candidates and conflict snapshot, when serialized, then required code/category/message/retryable/phase exist, optional fields cap at 32, and raw values/content/credentials/cursors/stack traces are absent.
- Implementation status: implemented for Event and To-do patch outcomes and MRTR: typed results contain only frozen safe fields; parser, preview, service, transport, and unexpected-exception paths redact arguments, content, credentials, ETags/requestState, bodies, and stack traces.
- Evidence status: focused patch error/preview tests inject sensitive markers into rejected input and exceptions and assert they are absent from structured and human-readable output; native stdio evidence is included in the integration run.

## CAL-ERROR-002

- Normative statement: Closed codes are: `invalid_input`, `invalid_calendar_data`, `not_found`, `ambiguous`, `outside_scope`, `entity_kind_mismatch`, `unsupported_capability`, `opaque_resource`, `temporal_unresolved`, `recurrence_unevaluable`, `conflict`, `destination_conflict`, `concurrency_unavailable`, `limit_exhausted`, `payload_too_large`, `busy`, `upstream_unauthorized`, `upstream_forbidden`, `upstream_rate_limited`, `upstream_unavailable`, `upstream_protocol_error`, `confirmation_expired`, `confirmation_mismatch`, `fidelity_failure`, `committed_but_unverified`, `committed_but_concurrency_unavailable`, and `indeterminate`. `no_change` and declined confirmation are successful non-mutating results; MRTR `input_required` is a protocol result type.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative sources: RFC 4791, RFC 4918, and HTTP conditionals.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/error/cal-error-002`.
- Objective oracle: Given one fixture per closed error code plus no_change, confirmation_declined and input_required, when emitted, then code enum is exact, the first two are non-error mutation results, and input_required is outer protocol result not error code.
- Implementation status: implemented for the patch-reachable closed outcomes, including invalid input/calendar data, scope/kind/opaque/concurrency/conflict/limits/upstream/fidelity/verification/indeterminate, plus non-error `no_change`, confirmation decline, and outer MRTR `input_required`; catalog-wide codes for other tools retain their ticket status.
- Evidence status: `CalendarEntityPatchMatrixTests`, `CalendarResourceUpdateProtocolTests`, and `CalendarEntityPatchToolsTests` cover the reachable patch status and protocol-result matrix and pass locally.

## CAL-ERROR-003

- Normative statement: Map CalDAV and HTTP outcomes deterministically: 401/403 to upstream authorization errors; direct-target 404 to `not_found`; discovery 404 and invalid successful responses to `upstream_protocol_error`; 409/412 to conflict; 413 to `payload_too_large`; 429 to `upstream_rate_limited`; 405/501 or explicit DAV capability errors to `unsupported_capability`; exhausted 5xx, timeouts, and transport failures to `upstream_unavailable`; and 507 to non-retryable `upstream_unavailable`.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative sources: RFC 4791, RFC 4918, and HTTP conditionals.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/error/cal-error-003`.
- Objective oracle: Given statuses 401,403,404 direct/discovery,409,412,413,429,405,501,507, malformed success and exhausted 5xx/timeout, when mapped, then each exact code/retryable value follows table and no response body appears in diagnostics.
- Implementation status: deterministic HTTP mapping is implemented for patch PUT and refetch, including 401/403/404/409/412/413/429/405/501/507, malformed success, 5xx, timeout, and transport ambiguity, without exposing response bodies.
- Evidence status: `CalendarResourceUpdateProtocolTests` and Core reconciliation targets cover the patch HTTP/status matrix, retryability/mutation-state truth, and exactly one PUT and pass locally.

## CAL-SEC-001

- Normative statement: Accept only canonical absolute resource hrefs without userinfo or fragments, validate configured origin and Calendar Scope before network access, and never construct a host from an agent-supplied href.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative sources: RFC 4918 and RFC 8996.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/sec/cal-sec-001`.
- Objective oracle: Given absolute hrefs with userinfo, fragment, foreign origin, configured origin and authorized redirect origin, when validated, then invalid inputs make zero DNS/HTTP calls and valid origin request never derives a host from href text.
- Implementation status: implemented for Event and To-do patch: canonical absolute same-origin href validation occurs before network; fragments, userinfo, foreign origins, and unsafe redirect locations are rejected, and requests use the configured client origin.
- Evidence status: strict parser/Core preflight and update-protocol redirect targets assert zero network on invalid hrefs and pass locally.

## CAL-SEC-002

- Normative statement: Disable XML DTDs and external entities, cap XML depth and characters, keep every calendar URI inert, and never expose out-of-scope existence through ambiguity or authorization diagnostics.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative sources: RFC 4918 and RFC 8996.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/sec/cal-sec-002`.
- Objective oracle: Given XML with DTD/entity/deep/oversize nodes, out-of-scope candidate, and URI alarm/attachment, when parsed, then parser rejects unsafe XML within limits, ambiguity exposes no out-of-scope existence, and URI fetch/open count is zero.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-SEC-003

- Normative statement: Logs contain only safe codes, phases, durations, and correlation identifiers. Stdout remains the JSON-RPC transport; valid runs leave stderr clean. Credentials, raw requests/responses, complete arguments, calendar content, cursors, and requestState are never logged.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative sources: RFC 4918 and RFC 8996.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/sec/cal-sec-003`.
- Objective oracle: Given successful stdio run and failures containing credentials, raw request/body, cursor and requestState markers, when logs/streams inspected, then stdout is JSON-RPC only, stderr is clean, and logs retain only code/phase/duration/correlation fields.
- Implementation status: implemented for the native stdio patch slice: results and diagnostics are redacted, stdout remains JSON-RPC transport, and the valid Event/To-do patch run leaves stderr clean; broader logging evidence remains owned by the release-wide scenario.
- Evidence status: `CalendarMcpStdioIntegrationTests.CalendarEntityPatch_PatchesOneEventOverRealStdioAndRadicale` and `CalendarEntityPatch_PatchesOneReviewedTodoOverRealStdioAndRadicale` execute both patch tools and inspect transport cleanliness and schema-valid output; focused redaction targets pass locally.

## CAL-RELEASE-001

- Normative statement: Release the contract as `0.2.0` under the unchanged NuGet package and MCP server identities. Provide no legacy mode, compatibility aliases, parallel abstractions, or automatic Calendar Object Resource migration. Owner: [Define the breaking release and migration contract](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/32).
- Source and owning decision: Owner: issue #32, Define the breaking release and migration contract; normative source: issue #35 release decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: packed-artifact check.
- Named scenario or fixture: `0.2.0/release/cal-release-001`.
- Objective oracle: Given packed NuGet metadata for version 0.2.0 and prior 0.1.x clients, when artifact inspection runs, then package/server identity is unchanged, version is 0.2.0, no legacy mode/alias/parallel abstraction appears, and no CalDAV data migration request is issued.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RELEASE-002

- Normative statement: Remove all twelve `0.1.x` tools. Migration is: list task lists to `calendars.list`; show/find/list tasks to To-do `calendar_entities.query`; get task to `calendar_resources.get`; add/create task to `todos.create`; update task to `todos.patch`; complete task to `todos.complete`; complete-by-summary to query then revision-bound completion; delete task to revision-bound confirmed `calendar_resources.delete`; delete-by-summary to query then revision-bound confirmed delete.
- Source and owning decision: Owner: issue #32, Define the breaking release and migration contract; normative source: issue #35 release decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: packed-artifact check.
- Named scenario or fixture: `0.2.0/release/cal-release-002`.
- Objective oracle: Given the 12 removed 0.1.x tool names and migration table, when packed catalog/docs are inspected, then every old name is absent and each listed task operation maps to the exact calendars/entities/resources/todos 0.2 tool with revision-bound delete.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RELEASE-003

- Normative statement: Keep `CALDAV_URL`, `CALDAV_USERNAME`, and `CALDAV_PASSWORD`. Replace task list allowlisting with `CALDAV_CALENDAR_HREFS`, the task default with `CALDAV_DEFAULT_TODO_CALENDAR_NAME`, add `CALDAV_DEFAULT_EVENT_CALENDAR_NAME`, and replace the advanced gate with `CALDAV_EXPOSE_EXACT_TOOLS`. Old names are not interpreted.
- Source and owning decision: Owner: issue #32, Define the breaking release and migration contract; normative source: issue #35 release decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: packed-artifact check.
- Named scenario or fixture: `0.2.0/release/cal-release-003`.
- Objective oracle: Given environment containing CALDAV_URL/USERNAME/PASSWORD, new calendar/default/exact variables and each old task variable, when startup validates, then required retained values map once, new names map once, old names are ignored/rejected, and metadata lists no old name.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RELEASE-004

- Normative statement: Packaged metadata describes Calendars, Events, and To-dos, declares only the new environment settings and actual protocol capabilities, and retains source server/package versions at `0.0.0` for tag substitution.
- Source and owning decision: Owner: issue #32, Define the breaking release and migration contract; normative source: issue #35 release decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: packed-artifact check.
- Named scenario or fixture: `0.2.0/release/cal-release-004`.
- Objective oracle: Given packed server.json and package manifest, when release substitution simulation runs, then source versions are `0.0.0`, artifact descriptions mention Calendars/Events/To-dos, only declared new environment settings/capabilities appear, and tag substitution changes both versions.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RELEASE-005

- Normative statement: Migration documentation includes before/after configuration, the complete tool mapping, recipes for To-do reads and writes, revision references, structured outcomes, MRTR, deployment verification, and rollback to pinned `0.1.4`. It states that no CalDAV data migration occurs.
- Source and owning decision: Owner: issue #32, Define the breaking release and migration contract; normative source: issue #35 release decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: packed-artifact check.
- Named scenario or fixture: `0.2.0/release/cal-release-005`.
- Objective oracle: Given migration guide and rollback fixture, when documentation assertions run, then before/after config, all tool mappings, To-do recipes, revision/MRTR/outcome/deploy steps exist, rollback pins `0.1.4`, and text explicitly says no CalDAV data migration.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVIDENCE-001

- Normative statement: Maintain a permanent requirement-to-evidence catalog keyed by these `CAL-<AREA>-NNN` identifiers. Every row records normative statement, owning decision and standards source, applicable Interoperability Profile and compatibility class, primary evidence layer, named scenario/fixture, objective oracle, and implementation/evidence status. IDs are never reused or renumbered. Owner: [Choose the final implementation handoff structure](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/33).
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-001`.
- Objective oracle: Given requirement-evidence catalog fixture, when `Evidence_catalog_has_one_complete_row_for_every_normative_requirement` runs, then exactly 96 unique CAL IDs, required row fields, stable heading format, and no reused/renumbered identifier are asserted.
- Implementation status: implemented and passing: this versioned catalog and `ContractCatalogTests.Evidence_catalog_has_one_complete_row_for_every_normative_requirement` verify the exact 96-ID set and required row fields.
- Evidence status: focused catalog verifier passes locally and is included in the CI test run.

## CAL-EVIDENCE-002

- Normative statement: Versioned semantic fixtures cover discovery/scope/defaults, snapshot coherence, strict schemas, patch operations, temporal kinds, recurrence and overrides, exclusions/cancellations/restoration, Event structured data, inert content, opaque resources, concurrency, post-write truth, limits, errors, and MRTR. Use equivalence partitions and pairwise coverage and explicitly cross recurrence with temporal kind, override with Mutation Scope, patch with opaque content, conditionals with ambiguous outcomes, MRTR with revision change, and limits with pagination.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-002`; issue #40 semantic targets are the `CalendarServiceTests.QueryOccurrencesAsync_*` corpus; issue #44 adds `CalendarEntityCreateServiceTests` recurrence creation partitions and strict `CalendarEntityCreateToolsTests`/`CalendarMcpRawStdioTests` inputs for kind-specific recurrence, PERIOD, exclusions, overrides, zones, collisions, and fidelity.
- Objective oracle: Given semantic fixture inventory, when manifest is counted, then it contains named cases for discovery, snapshots, strict schemas, patch, temporal, recurrence, structured/inert/opaque content, concurrency, truth, limits, errors and MRTR plus listed pairwise cross-products.
- Implementation status: occurrence-query equivalence partitions and recurrence-by-temporal, override-precedence, pagination-order, and limit cross-products are implemented; semantic-create partitions now cover Event/To-do RRULE and RDATE-only series, DTSTART inclusion, COUNT/UNTIL 10,000/10,001 and unbounded rules, EXDATE, complete overrides, DST/IANA zones across past/far-future clocks, collision retry, fidelity drift, malformed/valid PERIOD, duplicate RRULE, and pre-discovery validation. The remaining catalog-wide fixture inventory stays planned.
- Evidence status: focused issue #40 and #44 corpus/schema/raw-stdio targets pass locally and are included in the CI test run; this partial slice does not claim completion of the broader inventory.

## CAL-EVIDENCE-003

- Normative statement: Semantic corpus tests prove lossless parsing/replay, domain invariants, recurrence, temporal evaluation, patch atomicity, error ordering, limits, reconciliation, and hardening. Existing mapper, recurrence, XML, and service tests are prior art, but regenerated Ical.Net output or snapshots are never the oracle for losslessness.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-003`; issue #44 targets `CalendarEntityCreateServiceTests.CreateEventAsync_RefetchedRecurrenceMismatchPreservesDispatchTruth`, generated-UID collision and complete-override create cases, and strict MCP/raw-stdio recurrence inputs.
- Objective oracle: Given corpus with lossless lines, invalid aggregates, recurrence/time, patch atomicity, error ordering, limits and reconciliation, when tests run, then byte/semantic assertions use source slices rather than regenerated Ical.Net output or snapshot approval.
- Implementation status: implemented for the Semantic Create slice: recurrence/domain validation, error ordering, collision bounds, reconciliation, and authoritative recurrence-fidelity comparison use semantic corpus assertions; losslessness remains sourced from the authoritative content document rather than regenerated Ical.Net output. Remaining catalog-wide corpus work stays planned.
- Evidence status: focused issue #44 Core/MCP/raw-stdio targets pass locally and are included in the CI test run.

## CAL-EVIDENCE-004

- Normative statement: Deterministic WebDAV contract tests prove discovery, REPORT candidate behavior, full-resource reads, conditional mutations, redirects, status mapping, XML safety, origin restrictions, limits, and redaction. Existing CalDAV client request/response tests are the preferred seam to extend.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-004`.
- Objective oracle: Given deterministic WebDAV request scripts covering discovery, REPORT, GET, conditionals, redirects, statuses, XML, origin, limits and redaction, plus a recurring create body with ordered RDATE/EXDATE, override, non-ASCII text, and an unknown X property, when executed, then exact method/header/body/status assertions pass without live server dependency; create replays identical UTF-8 through one safe method-preserving redirect with `If-None-Match: *`, no normalization, and no retry beyond the redirect dispatch.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVIDENCE-005

- Normative statement: Live integration uses the official digest-pinned Radicale fixture and records platform manifest, Python/vobject versions, `TZ`, and strict-precondition mode. It covers Event-only, To-do-only, mixed, unknown-support, advertisement-violation, and opaque cases; full and expanded REPORT behavior; strong Entity Tag rotation; current/stale/missing/wildcard preconditions; create/update/delete/move; recurrence/time zones; fidelity; server ceilings; and post-write refetch.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-005`; issue #40 target: `RadicaleConformanceHarnessTests.Pinned_profile_preserves_occurrence_boundary_dst_leap_range_and_typed_failures`; issue #44 target: the pinned-profile recurring Event/To-do semantic-create scenario, both executed by the baseline/strict/New_York matrix after PUT followed by authoritative GET.
- Objective oracle: Given `RadicaleConformanceFixture` in baseline/strict/New_York variants, when the runtime and occurrence targets run, then TRX records index/platform/runtime/TZ/strict facts and authoritative post-PUT GET bytes prove half-open boundary behavior, DST, leap recurrence, RANGE precedence with moved original identity, and typed unresolved/unevaluable failures.
- Implementation status: runtime evidence and recurrence/time-zone query fidelity are implemented; issue #44 adds recurring Event/To-do create with RDATE-only, exclusions, complete overrides, deterministic bounded/unbounded supporting VTIMEZONE, authoritative past/far-future readback, exact accepted 10,000 and rejected 10,001 RRULE write boundaries, and pre-discovery PERIOD rejection. Remaining live-server behaviors in this broad row stay planned.
- Evidence status: focused issue #44 evidence passes locally; the recurring-create target runs against the digest-pinned fixture in the baseline/strict/New_York matrix.

## CAL-EVIDENCE-006

- Normative statement: Packed-artifact tests inspect the final NuGet package, MCP metadata/schema, README, bundled skill, migration guide, CHANGELOG, and release notes. Existing MCP metadata tests are prior art and must be expanded with every environment or metadata change.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-006`.
- Objective oracle: Given packed nupkg inspection, when release test runs, then metadata/schema/README/skill/migration guide/CHANGELOG/release notes all exist and each environment/metadata change has a corresponding packed-artifact assertion.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVIDENCE-007

- Normative statement: Every compatibility-matrix entry is independently classified against the project contract, Ical.Net 5.2.3, and Radicale 3.7.8 as supported, required typed rejection, preserved but unevaluable, pinned-profile-only, or unsafe through Ical.Net. A limitation passes only when observed behavior matches its declared class without silent loss.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-007`.
- Objective oracle: Given compatibility matrix rows, when `Compatibility_matrix_uses_independent_component_classes` runs, then project/Ical.Net/Radicale cells each use one of five closed classes and no preserved-but-unevaluable cell is interpreted as supported.
- Implementation status: matrix and independent-class verifier implemented and passing; behavior tests are planned by downstream tickets.
- Evidence status: focused matrix verifier passes locally and is included in the CI test run.

## CAL-EVIDENCE-008

- Normative statement: Boundary-sensitive limits are tested below, at, and above each boundary. Limit exhaustion never passes through partial results. Expected output is fixed; test runs never rewrite fixtures or snapshots.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-008`; committed issue #40 targets: the exact argument, page, text/diagnostics, 2,000/2,001, 5,000/5,001, and 10,000/10,001 boundary theories in `CalendarOccurrenceToolsTests` and `CalendarServiceTests`.
- Objective oracle: Given each numeric boundary at minus-one/exact/plus-one and immutable expected fixture, when tests execute, then only within-bound cases pass, excess has no partial output, and fixture hash remains unchanged after run.
- Implementation status: the occurrence-query boundary inventory is implemented with immutable inline expectations and zero-partial plus-one outcomes; remaining non-occurrence boundaries stay planned.
- Evidence status: focused issue #40 exact-boundary targets pass locally and are included in the CI test run; this partial slice does not claim completion of every catalog boundary.

## CAL-EVIDENCE-009

- Normative statement: Pull-request CI and the release workflow run every normative row. Missing, skipped, quarantined, or flaky normative evidence fails. Release build with warnings as errors, method complexity at most 10, at least 90% line and 85% branch coverage, all unit/integration tests, Slopwatch with no warnings, clean stdio, schema-valid metadata, and correct packed source-version substitution are mandatory.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-009`.
- Objective oracle: Given PR/release workflow definitions and a deliberately skipped normative test, when gate inspection runs, then all 96 rows are required, skipped/quarantined/flaky evidence fails, and build/warnings/complexity/coverage/tests/Slopwatch/stdio/schema/package checks are mandatory.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVIDENCE-010

- Normative statement: Before implementation acceptance, reverify MCP behavior against the selected stable specification, changelog, official feature documentation, and matching official C# SDK. Stable normative text wins over drafts, deprecated samples, and third-party examples.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-010`.
- Objective oracle: Given selected MCP specification/changelog/feature docs/C# SDK revisions, when pre-acceptance audit runs, then stable 2026-07-28 text and matching SDK are recorded, drafts/deprecated samples are excluded, and drift blocks release.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.
