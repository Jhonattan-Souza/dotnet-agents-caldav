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
- Objective oracle: Inspect the pinned profile or semantic corpus and assert this observable result: RFC 5545, RFC 6868, verified errata, RFC 4791, RFC 4918/current HTTP conditionals, and RFC 8996 define the unconditional standards baseline. Valid registered and unknown Event data from applicable extensions, including RFCs 7986, 9073, 9074, and 9253, must be preserved. RFC 7529 and RFC 7809 behavior is capability-gated. Owner: [Establish the normative RFC baseline for Event support](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/16). A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-BASE-002

- Normative statement: The first verified Interoperability Profile is Radicale 3.7.8. Standards define the product contract; Radicale accommodations are allowed only when standards-correct, semantically lossless, and explicitly classified. Other servers remain unverified profiles even when runtime capability negotiation allows them to operate. Owner: [Characterize the Radicale Event interoperability envelope](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/17).
- Source and owning decision: issue #17, Characterize the Radicale Event interoperability envelope; normative source is the Radicale 3.7.8 pinned profile.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: Radicale 3.7.8 pinned profile; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: live pinned-Radicale integration.
- Named scenario or fixture: `0.2.0/base/cal-base-002`.
- Objective oracle: Inspect the pinned profile or semantic corpus and assert this observable result: The first verified Interoperability Profile is Radicale 3.7.8. Standards define the product contract; Radicale accommodations are allowed only when standards-correct, semantically lossless, and explicitly classified. Other servers remain unverified profiles even when runtime capability negotiation allows them to operate. Owner: [Characterize the Radicale Event interoperability envelope](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/17). A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-BASE-003

- Normative statement: Pin the official `ghcr.io/kozea/radicale` OCI index digest `sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80`, with the resolved platform manifest recorded by every run. The verified baseline is Radicale 3.7.8, CPython 3.14.7, vobject 0.9.9, `TZ=UTC`, and `strict_preconditions=false`; required variants use `strict_preconditions=true` and `TZ=America/New_York`. Owner: [Pin the Radicale conformance runtime](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/28).
- Source and owning decision: issue #28, Pin the Radicale conformance runtime; normative source is the official ghcr.io/kozea/radicale OCI index and its platform manifests.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: Radicale 3.7.8 pinned profile; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: live pinned-Radicale integration.
- Named scenario or fixture: `0.2.0/base/cal-base-003`.
- Objective oracle: `RadicaleConformanceHarnessTests.Pinned_profile_records_the_runtime_and_selected_variant` asserts the exact index digest, amd64 and arm64 manifest digests, Radicale 3.7.8, CPython 3.14.7, vobject 0.9.9, selected `TZ`, and selected strict-precondition flag.
- Implementation status: implemented by the digest-pinned fixture and profile record.
- Evidence status: focused live harness test is required in each CI matrix variant.

## CAL-BASE-004

- Normative statement: Use Ical.Net 5.2.3 as a typed parser/editor and bounded recurrence helper only. It must not be the lossless persistence authority, own `THISANDFUTURE` semantics, resolve unproven resource-local time zones, or regenerate unrelated content. Owner: [Establish Ical.Net Event fidelity limits](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/21).
- Source and owning decision: issue #21, Establish Ical.Net Event fidelity limits; normative source is the Ical.Net 5.2.3 compatibility boundary.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/base/cal-base-004`.
- Objective oracle: Inspect the pinned profile or semantic corpus and assert this observable result: Use Ical.Net 5.2.3 as a typed parser/editor and bounded recurrence helper only. It must not be the lossless persistence authority, own `THISANDFUTURE` semantics, resolve unproven resource-local time zones, or regenerate unrelated content. Owner: [Establish Ical.Net Event fidelity limits](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/21). A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MODEL-001

- Normative statement: A Calendar Object Resource is the immutable persistence and concurrency aggregate for exactly one logical Calendar Entity, and Calendar Entity is a closed union of Event or To-do. There is no generic calendar item abstraction. Owner: [Choose the unified Calendar Entity domain model](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/19).
- Source and owning decision: Owner: issue #19, Choose the unified Calendar Entity domain model; normative source: issue #35 domain-model decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/model/cal-model-001`.
- Objective oracle: Construct the named resource fixture and assert this observable result: A Calendar Object Resource is the immutable persistence and concurrency aggregate for exactly one logical Calendar Entity, and Calendar Entity is a closed union of Event or To-do. There is no generic calendar item abstraction. Owner: [Choose the unified Calendar Entity domain model](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/19). A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MODEL-002

- Normative statement: A projectable resource contains exactly one master Event or To-do, zero or more same-kind and same-UID Recurrence Overrides, and supporting calendar data such as VTIMEZONE. A resource outside that invariant is an Opaque Calendar Object Resource with diagnostics and no semantic mutation surface.
- Source and owning decision: Owner: issue #19, Choose the unified Calendar Entity domain model; normative source: issue #35 domain-model decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/model/cal-model-002`.
- Objective oracle: Construct the named resource fixture and assert this observable result: A projectable resource contains exactly one master Event or To-do, zero or more same-kind and same-UID Recurrence Overrides, and supporting calendar data such as VTIMEZONE. A resource outside that invariant is an Opaque Calendar Object Resource with diagnostics and no semantic mutation surface. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MODEL-003

- Normative statement: Keep identity layers distinct: Calendar and resource use canonical absolute hrefs; Entity UID is durable logical identity; Entity Tag identifies one resource revision; Recurrence Identity is the original recurrence value; names, summaries, current starts, and positions are never identities.
- Source and owning decision: Owner: issue #19, Choose the unified Calendar Entity domain model; normative source: issue #35 domain-model decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/model/cal-model-003`.
- Objective oracle: Construct the named resource fixture and assert this observable result: Keep identity layers distinct: Calendar and resource use canonical absolute hrefs; Entity UID is durable logical identity; Entity Tag identifies one resource revision; Recurrence Identity is the original recurrence value; names, summaries, current starts, and positions are never identities. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MODEL-004

- Normative statement: Event and To-do share only Entity UID and Entity Kind. Entity Kind is immutable; conversion requires an explicit delete and create. Only To-do has To-do Completion.
- Source and owning decision: Owner: issue #19, Choose the unified Calendar Entity domain model; normative source: issue #35 domain-model decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/model/cal-model-004`.
- Objective oracle: Construct the named resource fixture and assert this observable result: Event and To-do share only Entity UID and Entity Kind. Entity Kind is immutable; conversion requires an explicit delete and create. Only To-do has To-do Completion. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MODEL-005

- Normative statement: Recurrence Set retains one typed RRULE at most for semantic creation or mutation, all RDATE and EXDATE values, and complete Recurrence Overrides. Standards-valid multiple RRULE resources are preserved but are Unevaluable Recurrence Sets.
- Source and owning decision: Owner: issue #19, Choose the unified Calendar Entity domain model; normative source: issue #35 domain-model decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/model/cal-model-005`.
- Objective oracle: Construct the named resource fixture and assert this observable result: Recurrence Set retains one typed RRULE at most for semantic creation or mutation, all RDATE and EXDATE values, and complete Recurrence Overrides. Standards-valid multiple RRULE resources are preserved but are Unevaluable Recurrence Sets. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MODEL-006

- Normative statement: An Occurrence is a derived, immutable, read-only projection. It is never persisted or written back as a Calendar Object Resource.
- Source and owning decision: Owner: issue #19, Choose the unified Calendar Entity domain model; normative source: issue #35 domain-model decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/model/cal-model-006`.
- Objective oracle: Construct the named resource fixture and assert this observable result: An Occurrence is a derived, immutable, read-only projection. It is never persisted or written back as a Calendar Object Resource. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MODEL-007

- Normative statement: Public contracts expose domain values, not Ical.Net, WebDAV, or HTTP implementation types. The Calendar Service replaces TaskItem, TaskList, ITaskService, and task-specific aliases in `0.2.0`.
- Source and owning decision: Owner: issue #19, Choose the unified Calendar Entity domain model; normative source: issue #35 domain-model decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/model/cal-model-007`.
- Objective oracle: Construct the named resource fixture and assert this observable result: Public contracts expose domain values, not Ical.Net, WebDAV, or HTTP implementation types. The Calendar Service replaces TaskItem, TaskList, ITaskService, and task-specific aliases in `0.2.0`. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RESOURCE-001

- Normative statement: A direct read returns a Calendar Object Resource Snapshot whose canonical href, exact strong Entity Tag, server-returned UTF-8 bytes, lossless content-line representation, diagnostics, and typed projection all describe one revision. Owner: [Define lossless resource mutation semantics](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/18).
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-001`.
- Objective oracle: Run the named deterministic snapshot or mutation exchange and assert this observable result: A direct read returns a Calendar Object Resource Snapshot whose canonical href, exact strong Entity Tag, server-returned UTF-8 bytes, lossless content-line representation, diagnostics, and typed projection all describe one revision. Owner: [Define lossless resource mutation semantics](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/18). A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RESOURCE-002

- Normative statement: The lossless representation retains component hierarchy, every property and parameter occurrence, value type, raw encoded value, and original slices for untouched content. Semantic mutation replaces only addressed semantics and replays untouched slices. Owner: [Probe semantic iCalendar round-trip fidelity](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/30).
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-002`.
- Objective oracle: Run the named deterministic snapshot or mutation exchange and assert this observable result: The lossless representation retains component hierarchy, every property and parameter occurrence, value type, raw encoded value, and original slices for untouched content. Semantic mutation replaces only addressed semantics and replays untouched slices. Owner: [Probe semantic iCalendar round-trip fidelity](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/30). A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RESOURCE-003

- Normative statement: Query and expanded projections are read-only. Semantic Patch requires the complete direct snapshot as its base; Exact Replacement, Move, and Delete require a Calendar Object Resource Revision Reference containing href, Entity UID, Entity Kind, and Entity Tag.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-003`.
- Objective oracle: Run the named deterministic snapshot or mutation exchange and assert this observable result: Query and expanded projections are read-only. Semantic Patch requires the complete direct snapshot as its base; Exact Replacement, Move, and Delete require a Calendar Object Resource Revision Reference containing href, Entity UID, Entity Kind, and Entity Tag. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RESOURCE-004

- Normative statement: Semantic Create builds one complete typed Event or To-do resource and generates UID when omitted. Exact Create accepts one complete UTF-8 resource with an existing UID. Both validate one master, consistent kind and UID, valid supporting components, and destination support before writing.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-004`.
- Objective oracle: Run the named deterministic snapshot or mutation exchange and assert this observable result: Semantic Create builds one complete typed Event or To-do resource and generates UID when omitted. Exact Create accepts one complete UTF-8 resource with an existing UID. Both validate one master, consistent kind and UID, valid supporting components, and destination support before writing. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RESOURCE-005

- Normative statement: Create uses `If-None-Match: *`. Generated identity may retry for collision within the execution bound; caller-supplied UID or href returns conflict without changing identity. Success returns a verified server-read snapshot.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-005`.
- Objective oracle: Run the named deterministic snapshot or mutation exchange and assert this observable result: Create uses `If-None-Match: *`. Generated identity may retry for collision within the execution bound; caller-supplied UID or href returns conflict without changing identity. Success returns a verified server-read snapshot. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RESOURCE-006

- Normative statement: Semantic Patch uses explicit preserve, set, and clear for scalars and add/remove or destructive replace-all for collections. Removal must be unambiguous. Apply and validate the whole intent in memory; any failure prevents all writes. A semantically unchanged result returns `no_change` without a new revision.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-006`.
- Objective oracle: Run the named deterministic snapshot or mutation exchange and assert this observable result: Semantic Patch uses explicit preserve, set, and clear for scalars and add/remove or destructive replace-all for collections. Removal must be unambiguous. Apply and validate the whole intent in memory; any failure prevents all writes. A semantically unchanged result returns `no_change` without a new revision. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RESOURCE-007

- Normative statement: Semantic Patch may change only modeled first-class or structured registered data. It preserves unknown, unsupported, and unaddressed content. Exact Replacement is the only way to intentionally change the complete payload or unsupported properties.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-007`.
- Objective oracle: Run the named deterministic snapshot or mutation exchange and assert this observable result: Semantic Patch may change only modeled first-class or structured registered data. It preserves unknown, unsupported, and unaddressed content. Exact Replacement is the only way to intentionally change the complete payload or unsupported properties. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RESOURCE-008

- Normative statement: Exact Replacement requires the current strong Entity Tag, the same Entity UID and Entity Kind, one valid master, consistent overrides, and a complete payload. Send the caller's UTF-8 payload without Ical.Net regeneration. Only a byte-identical payload skips the write.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-008`.
- Objective oracle: Run the named deterministic snapshot or mutation exchange and assert this observable result: Exact Replacement requires the current strong Entity Tag, the same Entity UID and Entity Kind, one valid master, consistent overrides, and a complete payload. Send the caller's UTF-8 payload without Ical.Net regeneration. Only a byte-identical payload skips the write. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RESOURCE-009

- Normative statement: Every update, replacement, move, and delete uses the exact current strong Entity Tag. Missing or weak tags return `concurrency_unavailable`; stale tags return `conflict`, cause no write, and include the current authorized snapshot when available. There is no unsafe bypass or automatic merge.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-009`.
- Objective oracle: Run the named deterministic snapshot or mutation exchange and assert this observable result: Every update, replacement, move, and delete uses the exact current strong Entity Tag. Missing or weak tags return `concurrency_unavailable`; stale tags return `conflict`, cause no write, and include the current authorized snapshot when available. There is no unsafe bypass or automatic merge. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RESOURCE-010

- Normative statement: Never blindly retry a possibly dispatched mutation. Reconcile with reads and classify the result as committed, unchanged, or indeterminate. Every mutation reports Mutation State as `not_attempted`, `not_committed`, `committed`, or `unknown`.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-010`.
- Objective oracle: Run the named deterministic snapshot or mutation exchange and assert this observable result: Never blindly retry a possibly dispatched mutation. Reconcile with reads and classify the result as committed, unchanged, or indeterminate. Every mutation reports Mutation State as `not_attempted`, `not_committed`, `committed`, or `unknown`. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RESOURCE-011

- Normative statement: Create, patch, replacement, and move succeed only after validating the observed post-write snapshot; delete succeeds only after verified absence. Semantic difference after commit is `fidelity_failure`; missing verification is `committed_but_unverified`; committed semantics without a usable strong tag is `committed_but_concurrency_unavailable`.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-011`.
- Objective oracle: Run the named deterministic snapshot or mutation exchange and assert this observable result: Create, patch, replacement, and move succeed only after validating the observed post-write snapshot; delete succeeds only after verified absence. Semantic difference after commit is `fidelity_failure`; missing verification is `committed_but_unverified`; committed semantics without a usable strong tag is `committed_but_concurrency_unavailable`. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RESOURCE-012

- Normative statement: Move is atomic, preserves UID and complete semantics, refuses overwrite, verifies destination and source absence, and never degrades to copy-then-delete. Normal Move selects a destination Calendar; explicit href and same-Calendar rename are exact/raw only.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-012`.
- Objective oracle: Run the named deterministic snapshot or mutation exchange and assert this observable result: Move is atomic, preserves UID and complete semantics, refuses overwrite, verifies destination and source absence, and never degrades to copy-then-delete. Normal Move selects a destination Calendar; explicit href and same-Calendar rename are exact/raw only. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RESOURCE-013

- Normative statement: Delete removes the entire resource, not one recurrence. It requires a revision reference, MRTR confirmation, and verified absence, and returns a deletion receipt with href, Entity UID, Entity Kind, and consumed Entity Tag.
- Source and owning decision: Owner: issue #18, Define lossless resource mutation semantics, and issue #30 for replay fidelity; normative source: RFC 5545 plus HTTP conditional semantics.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/resource/cal-resource-013`.
- Objective oracle: Run the named deterministic snapshot or mutation exchange and assert this observable result: Delete removes the entire resource, not one recurrence. It requires a revision reference, MRTR confirmation, and verified absence, and returns a deletion receipt with href, Entity UID, Entity Kind, and consumed Entity Tag. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DISC-001

- Normative statement: Discover every Calendar in configured scope and expose Event and To-do Entity Kind Support independently as `advertised`, `not_advertised`, or `unknown`, including raw component evidence and provenance. Advertisement is policy evidence, not enforcement or inventory. Owner: [Define Calendar discovery and query semantics](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/26).
- Source and owning decision: Owner: issue #26, Define Calendar discovery and query semantics; normative source: RFC 4791.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/disc/cal-disc-001`.
- Objective oracle: Run the named discovery/query exchange and assert this observable result: Discover every Calendar in configured scope and expose Event and To-do Entity Kind Support independently as `advertised`, `not_advertised`, or `unknown`, including raw component evidence and provenance. Advertisement is policy evidence, not enforcement or inventory. Owner: [Define Calendar discovery and query semantics](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/26). A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DISC-002

- Normative statement: Calendar canonical href is identity. Calendar Name comes from displayname or a provenance-marked href derivation. Name selection uses trimmed case-insensitive exact equality: zero is `not_found`, one resolves, and multiple are `ambiguous` with authorized candidates.
- Source and owning decision: Owner: issue #26, Define Calendar discovery and query semantics; normative source: RFC 4791.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/disc/cal-disc-002`.
- Objective oracle: Run the named discovery/query exchange and assert this observable result: Calendar canonical href is identity. Calendar Name comes from displayname or a provenance-marked href derivation. Name selection uses trimmed case-insensitive exact equality: zero is `not_found`, one resolves, and multiple are `ambiguous` with authorized candidates. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DISC-003

- Normative statement: Configured Calendar Scope is an exact canonical-href allowlist. Without an allowlist, all discovered Calendars are in scope. Missing or duplicate configured hrefs are explicit diagnostics.
- Source and owning decision: Owner: issue #26, Define Calendar discovery and query semantics; normative source: RFC 4791.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/disc/cal-disc-003`.
- Objective oracle: Run the named discovery/query exchange and assert this observable result: Configured Calendar Scope is an exact canonical-href allowlist. Without an allowlist, all discovered Calendars are in scope. Missing or duplicate configured hrefs are explicit diagnostics. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DISC-004

- Normative statement: Event and To-do defaults are independent and apply only when no selection is supplied. Explicit missing, ambiguous, out-of-scope, or incompatible selection never falls back. Searching all Calendars is always explicit.
- Source and owning decision: Owner: issue #26, Define Calendar discovery and query semantics; normative source: RFC 4791.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/disc/cal-disc-004`.
- Objective oracle: Run the named discovery/query exchange and assert this observable result: Event and To-do defaults are independent and apply only when no selection is supplied. Explicit missing, ambiguous, out-of-scope, or incompatible selection never falls back. Searching all Calendars is always explicit. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DISC-005

- Normative statement: Semantic entity queries declare one or both Entity Kinds and explicit Calendar Scope, return persisted snapshots, classify actual resource content locally, and report opaque resources and diagnostics separately. Occurrence queries are a separate read-only contract.
- Source and owning decision: Owner: issue #26, Define Calendar discovery and query semantics; normative source: RFC 4791.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/disc/cal-disc-005`.
- Objective oracle: Run the named discovery/query exchange and assert this observable result: Semantic entity queries declare one or both Entity Kinds and explicit Calendar Scope, return persisted snapshots, classify actual resource content locally, and report opaque resources and diagnostics separately. Occurrence queries are a separate read-only contract. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DISC-006

- Normative statement: Server REPORT filters reduce candidates only. Retrieve complete unexpanded resources, perform final semantic filtering and recurrence evaluation locally, and never mutate an expanded or projected REPORT representation. Owner: [Probe Radicale discovery filtering and concurrency](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/31).
- Source and owning decision: Owner: issue #26, Define Calendar discovery and query semantics; normative source: RFC 4791.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/disc/cal-disc-006`.
- Objective oracle: Run the named discovery/query exchange and assert this observable result: Server REPORT filters reduce candidates only. Retrieve complete unexpanded resources, perform final semantic filtering and recurrence evaluation locally, and never mutate an expanded or projected REPORT representation. Owner: [Probe Radicale discovery filtering and concurrency](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/31). A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DAV-001

- Normative statement: The writable capability floor is CalDAV/WebDAV discovery and collection PROPFIND, minimal component-filter calendar-query, calendar-multiget, full-resource GET, strong Entity Tags, and conditional create/update/delete. Missing mandatory REPORT support has no Depth-1 crawl fallback. Owner: [Choose the CalDAV capability and fallback policy](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/25).
- Source and owning decision: Owner: issue #25, Choose the CalDAV capability and fallback policy; normative sources: RFC 4791 and RFC 4918.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/dav/cal-dav-001`.
- Objective oracle: Run the named WebDAV exchange and assert this observable result: The writable capability floor is CalDAV/WebDAV discovery and collection PROPFIND, minimal component-filter calendar-query, calendar-multiget, full-resource GET, strong Entity Tags, and conditional create/update/delete. Missing mandatory REPORT support has no Depth-1 crawl fallback. Owner: [Choose the CalDAV capability and fallback policy](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/25). A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DAV-002

- Normative statement: Use configured Calendar Home when provided and validated; otherwise follow well-known, principal, and calendar-home-set discovery. Validate transport and discovery initially, verify query capabilities on first use, and never probe with artificial writes.
- Source and owning decision: Owner: issue #25, Choose the CalDAV capability and fallback policy; normative sources: RFC 4791 and RFC 4918.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/dav/cal-dav-002`.
- Objective oracle: Run the named WebDAV exchange and assert this observable result: Use configured Calendar Home when provided and validated; otherwise follow well-known, principal, and calendar-home-set discovery. Validate transport and discovery initially, verify query capabilities on first use, and never probe with artificial writes. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DAV-003

- Normative statement: CalDAV Capability is scoped by origin, Calendar, resource, and operation and classified as advertised, verified, or unavailable. Process-lifetime capability state may be explicitly rediscovered and is invalidated by origin, credentials, or relevant configuration changes, not by transient failures or conflicts.
- Source and owning decision: Owner: issue #25, Choose the CalDAV capability and fallback policy; normative sources: RFC 4791 and RFC 4918.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/dav/cal-dav-003`.
- Objective oracle: Run the named WebDAV exchange and assert this observable result: CalDAV Capability is scoped by origin, Calendar, resource, and operation and classified as advertised, verified, or unavailable. Process-lifetime capability state may be explicitly rediscovered and is invalidated by origin, credentials, or relevant configuration changes, not by transient failures or conflicts. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DAV-004

- Normative statement: Explicit omission of an Entity Kind blocks create and move for that kind but does not hide existing resources. Unknown support permits reads and blocks writes until verified. Actual content always controls resource classification.
- Source and owning decision: Owner: issue #25, Choose the CalDAV capability and fallback policy; normative sources: RFC 4791 and RFC 4918.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/dav/cal-dav-004`.
- Objective oracle: Run the named WebDAV exchange and assert this observable result: Explicit omission of an Entity Kind blocks create and move for that kind but does not hide existing resources. Unknown support permits reads and blocks writes until verified. Actual content always controls resource classification. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DAV-005

- Normative statement: Only semantics-preserving fallbacks are permitted. Optional filters may fall back to a minimal kind query plus local filtering. Missing safe preconditions degrades the affected mutation capability to read-only.
- Source and owning decision: Owner: issue #25, Choose the CalDAV capability and fallback policy; normative sources: RFC 4791 and RFC 4918.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/dav/cal-dav-005`.
- Objective oracle: Run the named WebDAV exchange and assert this observable result: Only semantics-preserving fallbacks are permitted. Optional filters may fall back to a minimal kind query plus local filtering. Missing safe preconditions degrades the affected mutation capability to read-only. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-DAV-006

- Normative statement: Reads may follow bounded same-origin 301, 302, 307, and 308 redirects while preserving method and body. Mutations may follow only same-origin 307 and 308. Cross-origin redirects require operator authorization and never receive credentials implicitly; 303 is rejected.
- Source and owning decision: Owner: issue #25, Choose the CalDAV capability and fallback policy; normative sources: RFC 4791 and RFC 4918.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/dav/cal-dav-006`.
- Objective oracle: Run the named WebDAV exchange and assert this observable result: Reads may follow bounded same-origin 301, 302, 307, and 308 redirects while preserving method and body. Mutations may follow only same-origin 307 and 308. Cross-origin redirects require operator authorization and never receive credentials implicitly; 303 is rejected. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVENT-001

- Normative statement: Event content has three layers: First-class Calendar Fields for common typed semantics, Structured Calendar Data for complete rich or repeatable standard values, and preserved Calendar Properties for everything valid not modeled. Owner: [Define Event content and scheduling-property policy](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/22).
- Source and owning decision: Owner: issue #22, Define Event content and scheduling-property policy; normative sources: RFC 5545 and applicable registered extensions.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/event/cal-event-001`.
- Objective oracle: Run the named semantic calendar corpus fixture and assert this observable result: Event content has three layers: First-class Calendar Fields for common typed semantics, Structured Calendar Data for complete rich or repeatable standard values, and preserved Calendar Properties for everything valid not modeled. Owner: [Define Event content and scheduling-property policy](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/22). A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVENT-002

- Normative statement: First-class Event fields include optional SUMMARY, DESCRIPTION, start/end/duration, LOCATION, GEO, STATUS, TRANSP, CLASS, PRIORITY, CATEGORIES, URL, and Recurrence Set. Empty TEXT, clear, and omission remain distinct and no trimming is performed.
- Source and owning decision: Owner: issue #22, Define Event content and scheduling-property policy; normative sources: RFC 5545 and applicable registered extensions.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/event/cal-event-002`.
- Objective oracle: Run the named semantic calendar corpus fixture and assert this observable result: First-class Event fields include optional SUMMARY, DESCRIPTION, start/end/duration, LOCATION, GEO, STATUS, TRANSP, CLASS, PRIORITY, CATEGORIES, URL, and Recurrence Set. Empty TEXT, clear, and omission remain distinct and no trimming is performed. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVENT-003

- Normative statement: Semantic Create generates UID, DTSTAMP, CREATED, and LAST-MODIFIED when omitted. Semantic Patch updates LAST-MODIFIED and preserves UID, DTSTAMP, CREATED, and SEQUENCE; scheduling is excluded, so it never auto-increments SEQUENCE.
- Source and owning decision: Owner: issue #22, Define Event content and scheduling-property policy; normative sources: RFC 5545 and applicable registered extensions.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/event/cal-event-003`.
- Objective oracle: Run the named semantic calendar corpus fixture and assert this observable result: Semantic Create generates UID, DTSTAMP, CREATED, and LAST-MODIFIED when omitted. Semantic Patch updates LAST-MODIFIED and preserves UID, DTSTAMP, CREATED, and SEQUENCE; scheduling is excluded, so it never auto-increments SEQUENCE. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVENT-004

- Normative statement: Structured data includes Organizer, Attendees, Participants, Alarms, Attachments, Comments, Contacts, Resources, Related-To, Request-Status, Styled Descriptions, Images, Conferences, Links, Concepts, Structured Data, VLOCATION, and VRESOURCE while retaining full parameters, multiplicity, meaningful ordering, and unmodeled properties.
- Source and owning decision: Owner: issue #22, Define Event content and scheduling-property policy; normative sources: RFC 5545 and applicable registered extensions.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/event/cal-event-004`.
- Objective oracle: Run the named semantic calendar corpus fixture and assert this observable result: Structured data includes Organizer, Attendees, Participants, Alarms, Attachments, Comments, Contacts, Resources, Related-To, Request-Status, Styled Descriptions, Images, Conferences, Links, Concepts, Structured Data, VLOCATION, and VRESOURCE while retaining full parameters, multiplicity, meaningful ordering, and unmodeled properties. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVENT-005

- Normative statement: Organizer, Attendee, Participant, and related values are Storage-only Scheduling Data. Preserve every syntactically valid CAL-ADDRESS and explicit parameter; never restrict to mailto, deduplicate, infer identity, send invitations, access scheduling inboxes/outboxes, or propagate changes.
- Source and owning decision: Owner: issue #22, Define Event content and scheduling-property policy; normative sources: RFC 5545 and applicable registered extensions.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/event/cal-event-005`.
- Objective oracle: Run the named semantic calendar corpus fixture and assert this observable result: Organizer, Attendee, Participant, and related values are Storage-only Scheduling Data. Preserve every syntactically valid CAL-ADDRESS and explicit parameter; never restrict to mailto, deduplicate, infer identity, send invitations, access scheduling inboxes/outboxes, or propagate changes. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVENT-006

- Normative statement: Calendar Alarms and URI-bearing values are inert. Store typed supported forms only when explicitly requested, preserve existing valid forms on unrelated patches, never dereference or execute them, and require Exact Replacement for unsupported value grammars or inline binary typed mutation.
- Source and owning decision: Owner: issue #22, Define Event content and scheduling-property policy; normative sources: RFC 5545 and applicable registered extensions.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/event/cal-event-006`.
- Objective oracle: Run the named semantic calendar corpus fixture and assert this observable result: Calendar Alarms and URI-bearing values are inert. Store typed supported forms only when explicitly requested, preserve existing valid forms on unrelated patches, never dereference or execute them, and require Exact Replacement for unsupported value grammars or inline binary typed mutation. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVENT-007

- Normative statement: Open enumerations preserve recognized cases or `Other(rawValue)`. Semantic Patch may create recognized values and valid `X-` extensions; changing other unknown values requires Exact Replacement. Derived Calendar Data is read-only to Semantic Patch.
- Source and owning decision: Owner: issue #22, Define Event content and scheduling-property policy; normative sources: RFC 5545 and applicable registered extensions.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/event/cal-event-007`.
- Objective oracle: Run the named semantic calendar corpus fixture and assert this observable result: Open enumerations preserve recognized cases or `Other(rawValue)`. Semantic Patch may create recognized values and valid `X-` extensions; changing other unknown values requires Exact Replacement. Derived Calendar Data is read-only to Semantic Patch. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-TIME-001

- Normative statement: Temporal Value is a closed union of date-only, floating date-time, UTC date-time, or named-time-zone date-time retaining the original TZID. These forms are never collapsed or silently converted. Owner: [Define temporal and recurrence semantics](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/27).
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/time/cal-time-001`.
- Objective oracle: Run the named temporal corpus fixture and assert this observable result: Temporal Value is a closed union of date-only, floating date-time, UTC date-time, or named-time-zone date-time retaining the original TZID. These forms are never collapsed or silently converted. Owner: [Define temporal and recurrence semantics](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/27). A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-TIME-002

- Normative statement: Instant comparison and expansion for floating or date-only values requires a request-supplied IANA Temporal Evaluation Context. No Calendar, server, process, or host time zone is an implicit fallback.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/time/cal-time-002`.
- Objective oracle: Run the named temporal corpus fixture and assert this observable result: Instant comparison and expansion for floating or date-only values requires a request-supplied IANA Temporal Evaluation Context. No Calendar, server, process, or host time zone is an implicit fallback. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-TIME-003

- Normative statement: Resolve named zones first from one unambiguous resource-local VTIMEZONE and then from a recognized IANA TZID. Unknown or conflicting definitions are unresolved: preserve and diagnose them, permit unrelated semantic changes, and reject evaluation-dependent operations.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/time/cal-time-003`.
- Objective oracle: Run the named temporal corpus fixture and assert this observable result: Resolve named zones first from one unambiguous resource-local VTIMEZONE and then from a recognized IANA TZID. Unknown or conflicting definitions are unresolved: preserve and diagnose them, permit unrelated semantic changes, and reject evaluation-dependent operations. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-TIME-004

- Normative statement: Preserve RFC effective-span rules: DTEND and DURATION are exclusive; date-only Event without end lasts one day; date-time Event without end has zero duration; To-do DUE and DURATION are exclusive; and DURATION requires DTSTART. Rescheduling start preserves Effective Temporal Span unless end, due, or duration is explicitly changed.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/time/cal-time-004`.
- Objective oracle: Run the named temporal corpus fixture and assert this observable result: Preserve RFC effective-span rules: DTEND and DURATION are exclusive; date-only Event without end lasts one day; date-time Event without end has zero duration; To-do DUE and DURATION are exclusive; and DURATION requires DTSTART. Rescheduling start preserves Effective Temporal Span unless end, due, or duration is explicitly changed. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RECUR-001

- Normative statement: Occurrence queries use non-empty half-open UTC windows `[from,to)`. Positive-duration Occurrences match overlap; zero-duration Occurrences match start. Moved Occurrences match effective span. Non-recurring To-dos follow their available DTSTART/DUE/DURATION semantics and may yield no temporal Occurrence.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/recur/cal-recur-001`.
- Objective oracle: Run the named recurrence corpus fixture and assert this observable result: Occurrence queries use non-empty half-open UTC windows `[from,to)`. Positive-duration Occurrences match overlap; zero-duration Occurrences match start. Moved Occurrences match effective span. Non-recurring To-dos follow their available DTSTART/DUE/DURATION semantics and may yield no temporal Occurrence. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RECUR-002

- Normative statement: Build recurrence from DTSTART, one typed RRULE at most, every RDATE, every EXDATE, and overrides; collapse duplicate identities and apply EXDATE precedence. Standards-valid RDATE PERIOD is preserved, but Radicale 3.7.8 writes reject it before PUT.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/recur/cal-recur-002`.
- Objective oracle: Run the named recurrence corpus fixture and assert this observable result: Build recurrence from DTSTART, one typed RRULE at most, every RDATE, every EXDATE, and overrides; collapse duplicate identities and apply EXDATE precedence. Standards-valid RDATE PERIOD is preserved, but Radicale 3.7.8 writes reject it before PUT. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RECUR-003

- Normative statement: Recurrence Identity retains the master's temporal family and original value when an Occurrence moves. Individual overrides win over the nearest applicable Range Override; a later Range Override supersedes an earlier range for later identities.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/recur/cal-recur-003`.
- Objective oracle: Run the named recurrence corpus fixture and assert this observable result: Recurrence Identity retains the master's temporal family and original value when an Occurrence moves. Individual overrides win over the nearest applicable Range Override; a later Range Override supersedes an earlier range for later identities. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RECUR-004

- Normative statement: `one-occurrence` creates or updates one complete individual override. `this-and-future` applies addressed changes from its anchor while preserving relative exception offsets and unrelated properties. `entire-set` applies addressed changes to master and all overrides with the same preservation rules.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/recur/cal-recur-004`.
- Objective oracle: Run the named recurrence corpus fixture and assert this observable result: `one-occurrence` creates or updates one complete individual override. `this-and-future` applies addressed changes from its anchor while preserving relative exception offsets and unrelated properties. `entire-set` applies addressed changes to master and all overrides with the same preservation rules. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RECUR-005

- Normative statement: Recurrence-definition changes require every exclusion and override to remain valid or be explicitly reconciled in the same intent. Temporal-family changes for recurring entities require Exact Replacement.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/recur/cal-recur-005`.
- Objective oracle: Run the named recurrence corpus fixture and assert this observable result: Recurrence-definition changes require every exclusion and override to remain valid or be explicitly reconciled in the same intent. Temporal-family changes for recurring entities require Exact Replacement. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RECUR-006

- Normative statement: Exclusion adds EXDATE; cancellation creates or updates a cancelled complete override; restoration removes only the exclusion or cancelled status. EXDATE suppresses but does not delete an override. Adding a nonexistent identity is an explicit RDATE operation.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/recur/cal-recur-006`.
- Objective oracle: Run the named recurrence corpus fixture and assert this observable result: Exclusion adds EXDATE; cancellation creates or updates a cancelled complete override; restoration removes only the exclusion or cancelled status. EXDATE suppresses but does not delete an override. Adding a nonexistent identity is an explicit RDATE operation. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RECUR-007

- Normative statement: To-do Completion may target a non-recurring To-do or exactly one identified recurring Occurrence and records its completion instant. `this-and-future` and `entire-set` are invalid completion scopes.
- Source and owning decision: Owner: issue #27, Define temporal and recurrence semantics; normative source: RFC 5545.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: semantic corpus.
- Named scenario or fixture: `0.2.0/recur/cal-recur-007`.
- Objective oracle: Run the named recurrence corpus fixture and assert this observable result: To-do Completion may target a non-recurring To-do or exactly one identified recurring Occurrence and records its completion instant. `this-and-future` and `entire-set` are invalid completion scopes. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MCP-001

- Normative statement: Target stable MCP 2026-07-28 or a later stable revision verified at implementation time. Implement `server/discover`; requests are stateless and self-contained. Do not implement removed initialization/session lifecycle, sticky sessions, legacy SSE resumability, or proprietary protocol substitutes. Owner: [Choose the MCP calendar tool and safety model](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/20).
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-001`.
- Objective oracle: Invoke the named real-MCP-client scenario and assert this observable result: Target stable MCP 2026-07-28 or a later stable revision verified at implementation time. Implement `server/discover`; requests are stateless and self-contained. Do not implement removed initialization/session lifecycle, sticky sessions, legacy SSE resumability, or proprietary protocol substitutes. Owner: [Choose the MCP calendar tool and safety model](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/20). A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MCP-002

- Normative statement: The default semantic catalog, in deterministic discovery/read/write order, is: `calendars.list`, `calendar_entities.query`, `calendar_occurrences.query`, `calendar_resources.get`, `events.create`, `events.patch`, `todos.create`, `todos.patch`, `todos.complete`, `calendar_occurrences.add`, `calendar_occurrences.exclude`, `calendar_occurrences.restore_exclusion`, `calendar_occurrences.cancel`, `calendar_occurrences.restore_cancellation`, `calendar_resources.move`, and `calendar_resources.delete`.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-002`.
- Objective oracle: Invoke the named real-MCP-client scenario and assert this observable result: The default semantic catalog, in deterministic discovery/read/write order, is: `calendars.list`, `calendar_entities.query`, `calendar_occurrences.query`, `calendar_resources.get`, `events.create`, `events.patch`, `todos.create`, `todos.patch`, `todos.complete`, `calendar_occurrences.add`, `calendar_occurrences.exclude`, `calendar_occurrences.restore_exclusion`, `calendar_occurrences.cancel`, `calendar_occurrences.restore_cancellation`, `calendar_resources.move`, and `calendar_resources.delete`. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MCP-003

- Normative statement: The opt-in exact catalog is: `calendar_resources.exact_get`, `calendar_resources.exact_create`, `calendar_resources.exact_replace`, and `calendar_resources.exact_move`. Configuration exposure and authorization are independent gates. Normal results do not embed raw iCalendar; exact reads use protected MCP resource links and do not enumerate the CalDAV store through resources/list.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-003`.
- Objective oracle: Invoke the named real-MCP-client scenario and assert this observable result: The opt-in exact catalog is: `calendar_resources.exact_get`, `calendar_resources.exact_create`, `calendar_resources.exact_replace`, and `calendar_resources.exact_move`. Configuration exposure and authorization are independent gates. Normal results do not embed raw iCalendar; exact reads use protected MCP resource links and do not enumerate the CalDAV store through resources/list. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MCP-004

- Normative statement: Inputs are strict JSON Schema 2020-12 closed camel-case objects with discriminated unions, explicit required values, and duplicate/unknown-property rejection. Every tool defines and validates an output schema and returns authoritative `structuredContent` plus concise compatible text content.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-004`.
- Objective oracle: Invoke the named real-MCP-client scenario and assert this observable result: Inputs are strict JSON Schema 2020-12 closed camel-case objects with discriminated unions, explicit required values, and duplicate/unknown-property rejection. Every tool defines and validates an output schema and returns authoritative `structuredContent` plus concise compatible text content. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MCP-005

- Normative statement: Calendar Reference selects exactly one Calendar by exact name or canonical href. Calendar Scope is `default`, `selected`, or explicit `all`. Existing-resource mutations always require a Calendar Object Resource Revision Reference and refetch the current revision before writing.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-005`.
- Objective oracle: Invoke the named real-MCP-client scenario and assert this observable result: Calendar Reference selects exactly one Calendar by exact name or canonical href. Calendar Scope is `default`, `selected`, or explicit `all`. Existing-resource mutations always require a Calendar Object Resource Revision Reference and refetch the current revision before writing. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MCP-006

- Normative statement: Scalar patch operations are set or clear; collection operations are addRemove or replaceAll. `replaceAll` and recurrence-definition changes are high-impact. A patch explicitly targets master or original Recurrence Identity plus Mutation Scope.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-006`.
- Objective oracle: Invoke the named real-MCP-client scenario and assert this observable result: Scalar patch operations are set or clear; collection operations are addRemove or replaceAll. `replaceAll` and recurrence-definition changes are high-impact. A patch explicitly targets master or original Recurrence Identity plus Mutation Scope. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MCP-007

- Normative statement: Query envelopes contain items, diagnostics, pagination mode, and nextCursor. Entity ordering is canonical Calendar href then resource href; Occurrence ordering is effective start, Calendar href, Entity UID, then Recurrence Identity. Pagination is explicitly non-snapshot.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-007`.
- Objective oracle: Invoke the named real-MCP-client scenario and assert this observable result: Query envelopes contain items, diagnostics, pagination mode, and nextCursor. Entity ordering is canonical Calendar href then resource href; Occurrence ordering is effective start, Calendar href, Entity UID, then Recurrence Identity. Pagination is explicitly non-snapshot. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MCP-008

- Normative statement: Expected input, domain, capability, limit, concurrency, CalDAV, and execution failures remain schema-valid tool results with MCP `isError: true`. Invalid protocol messages, unknown methods/tools, incompatible versions, and MCP transport authentication/authorization use their protocol or HTTP channels. `no_change` and declined confirmation use `isError: false`. Unexpected failures are sanitized.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-008`.
- Objective oracle: Invoke the named real-MCP-client scenario and assert this observable result: Expected input, domain, capability, limit, concurrency, CalDAV, and execution failures remain schema-valid tool results with MCP `isError: true`. Invalid protocol messages, unknown methods/tools, incompatible versions, and MCP transport authentication/authorization use their protocol or HTTP channels. `no_change` and declined confirmation use `isError: false`. Unexpected failures are sanitized. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MCP-009

- Normative statement: Use MCP Multi Round-Trip Requests for delete, all exact writes, replaceAll, recurrence-definition changes, this-and-future, entire-set, and any future multi-resource mutation. Preview resolves and validates read-only, binds opaque ten-minute requestState to normalized arguments, principal or credential context, fixed identity/destination, and Entity Tag, and revalidates everything before write.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-009`.
- Objective oracle: Invoke the named real-MCP-client scenario and assert this observable result: Use MCP Multi Round-Trip Requests for delete, all exact writes, replaceAll, recurrence-definition changes, this-and-future, entire-set, and any future multi-resource mutation. Preview resolves and validates read-only, binds opaque ten-minute requestState to normalized arguments, principal or credential context, fixed identity/destination, and Entity Tag, and revalidates everything before write. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MCP-010

- Normative statement: Decline, expiry, mismatch, changed arguments, changed revision, or invalid ownership writes nothing. A direct explicit Semantic Create, scalar single-resource patch, one-Occurrence mutation, or To-do Completion may execute without extra server-requested confirmation.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-010`.
- Objective oracle: Invoke the named real-MCP-client scenario and assert this observable result: Decline, expiry, mismatch, changed arguments, changed revision, or invalid ownership writes nothing. A direct explicit Semantic Create, scalar single-resource patch, one-Occurrence mutation, or To-do Completion may execute without extra server-requested confirmation. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MCP-011

- Normative statement: One tool call mutates at most one Calendar Object Resource. There is no bulk mutation, implicit repeated mutation, search-then-destroy tool, or generic action/update tool.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-011`.
- Objective oracle: Invoke the named real-MCP-client scenario and assert this observable result: One tool call mutates at most one Calendar Object Resource. There is no bulk mutation, implicit repeated mutation, search-then-destroy tool, or generic action/update tool. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MCP-012

- Normative statement: Cache hints are private: Calendars list uses 30 seconds, semantic queries 5 seconds, and direct snapshots and mutations 0. The fixed catalog does not advertise list-change notifications. Tool annotations describe behavior but never enforce policy.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-012`.
- Objective oracle: Invoke the named real-MCP-client scenario and assert this observable result: Cache hints are private: Calendars list uses 30 seconds, semantic queries 5 seconds, and direct snapshots and mutations 0. The fixed catalog does not advertise list-change notifications. Tool annotations describe behavior but never enforce policy. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-MCP-013

- Normative statement: The initial server does not implement the MCP Tasks extension because operations are synchronous, bounded, and cancellable. Any future durable operation must use the officially negotiated extension rather than an application-specific async protocol.
- Source and owning decision: Owner: issue #20, Choose the MCP calendar tool and safety model; normative source: stable MCP 2026-07-28.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: real MCP client over stdio.
- Named scenario or fixture: `0.2.0/mcp/cal-mcp-013`.
- Objective oracle: Invoke the named real-MCP-client scenario and assert this observable result: The initial server does not implement the MCP Tasks extension because operations are synchronous, bounded, and cancellable. Any future durable operation must use the officially negotiated extension rather than an application-specific async protocol. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-BOUND-001

- Normative statement: Validate in this order: transport authorization; admission and payload size; schema/lexical/discriminator; origin/scope/caller authorization; selection/discovery/capability; target revision; complete resource semantics; MRTR; execution; post-write verification or reconciliation. Return the earliest failing phase and at most 32 safe violations ordered by JSON Pointer. Owner: [Choose validation errors and execution bounds](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/23).
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative source: issue #35 safety decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/bound/cal-bound-001`.
- Objective oracle: Run the named boundary fixture and assert this observable result: Validate in this order: transport authorization; admission and payload size; schema/lexical/discriminator; origin/scope/caller authorization; selection/discovery/capability; target revision; complete resource semantics; MRTR; execution; post-write verification or reconciliation. Return the earliest failing phase and at most 32 safe violations ordered by JSON Pointer. Owner: [Choose validation errors and execution bounds](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/23). A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-BOUND-002

- Normative statement: Occurrence and entity temporal windows are non-empty and at most 366 days. pageSize defaults to 50 and is capped at 200. One query inspects at most 5,000 resources across 256 Calendars.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative source: issue #35 safety decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/bound/cal-bound-002`.
- Objective oracle: Run the named boundary fixture and assert this observable result: Occurrence and entity temporal windows are non-empty and at most 366 days. pageSize defaults to 50 and is capped at 200. One query inspects at most 5,000 resources across 256 Calendars. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-BOUND-003

- Normative statement: Expansion derives at most 2,000 Occurrences per Calendar Entity, 5,000 per query, and 10,000 unmatched increments per Recurrence Set. Limit Exhaustion returns no partial items.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative source: issue #35 safety decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/bound/cal-bound-003`.
- Objective oracle: Run the named boundary fixture and assert this observable result: Expansion derives at most 2,000 Occurrences per Calendar Entity, 5,000 per query, and 10,000 unmatched increments per Recurrence Set. Limit Exhaustion returns no partial items. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-BOUND-004

- Normative statement: Normal semantic arguments are at most 256 KiB; one authoritative resource or exact payload is at most 4 MiB; one structured page is at most 4 MiB; human-readable text plus diagnostics is at most 64 KiB. Measure final UTF-8 JSON, resource UTF-8 bytes, and decompressed HTTP bodies with streaming limit-plus-one enforcement.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative source: issue #35 safety decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/bound/cal-bound-004`.
- Objective oracle: Run the named boundary fixture and assert this observable result: Normal semantic arguments are at most 256 KiB; one authoritative resource or exact payload is at most 4 MiB; one structured page is at most 4 MiB; human-readable text plus diagnostics is at most 64 KiB. Measure final UTF-8 JSON, resource UTF-8 bytes, and decompressed HTTP bodies with streaming limit-plus-one enforcement. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-BOUND-005

- Normative statement: One HTTP attempt is 10 seconds; a read is 30 seconds; mutation before dispatch is 30 seconds; reconciliation may use another 30 seconds within a 60-second total. Reads have at most three transient attempts; mutations have none; generated-UID create has at most three collision attempts.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative source: issue #35 safety decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/bound/cal-bound-005`.
- Objective oracle: Run the named boundary fixture and assert this observable result: One HTTP attempt is 10 seconds; a read is 30 seconds; mutation before dispatch is 30 seconds; reconciliation may use another 30 seconds within a 60-second total. Reads have at most three transient attempts; mutations have none; generated-UID create has at most three collision attempts. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-BOUND-006

- Normative statement: Per origin, admit at most four operations and one mutation, queue at most 16 FIFO calls, and wait at most two seconds before `busy`. Requested progress begins after 500 ms, emits at most four notifications per second, uses aggregate phases, and never reveals names, hrefs, or content.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative source: issue #35 safety decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/bound/cal-bound-006`.
- Objective oracle: Run the named boundary fixture and assert this observable result: Per origin, admit at most four operations and one mutation, queue at most 16 FIFO calls, and wait at most two seconds before `busy`. Requested progress begins after 500 ms, emits at most four notifications per second, uses aggregate phases, and never reveals names, hrefs, or content. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-BOUND-007

- Normative statement: Cancellation stops reads and pre-dispatch mutations promptly. After possible dispatch, bounded reconciliation continues despite caller cancellation so Mutation State remains truthful.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative source: issue #35 safety decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/bound/cal-bound-007`.
- Objective oracle: Run the named boundary fixture and assert this observable result: Cancellation stops reads and pre-dispatch mutations promptly. After possible dispatch, bounded reconciliation continues despite caller cancellation so Mutation State remains truthful. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-BOUND-008

- Normative statement: Cursors and MRTR requestState are authenticated, encrypted, at most 2 KiB, expire after ten minutes, bind normalized inputs and credential context, and are invalidated by key rotation. Replay remains non-duplicating through fixed identity plus conditional writes and full revalidation.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative source: issue #35 safety decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/bound/cal-bound-008`.
- Objective oracle: Run the named boundary fixture and assert this observable result: Cursors and MRTR requestState are authenticated, encrypted, at most 2 KiB, expire after ten minutes, bind normalized inputs and credential context, and are invalidated by key rotation. Replay remains non-duplicating through fixed identity plus conditional writes and full revalidation. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-ERROR-001

- Normative statement: Every typed error includes code, category, safe message, retryable, and phase, with optional capped violations, limits, retryAfterMs, authorized candidates, or current conflict snapshot. It never includes rejected raw values, complete arguments, resource content, credentials, HTTP bodies, cursors, requestState, or stack traces.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative sources: RFC 4791, RFC 4918, and HTTP conditionals.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/error/cal-error-001`.
- Objective oracle: Run the named failure fixture and assert this observable result: Every typed error includes code, category, safe message, retryable, and phase, with optional capped violations, limits, retryAfterMs, authorized candidates, or current conflict snapshot. It never includes rejected raw values, complete arguments, resource content, credentials, HTTP bodies, cursors, requestState, or stack traces. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-ERROR-002

- Normative statement: Closed codes are: `invalid_input`, `invalid_calendar_data`, `not_found`, `ambiguous`, `outside_scope`, `entity_kind_mismatch`, `unsupported_capability`, `opaque_resource`, `temporal_unresolved`, `recurrence_unevaluable`, `conflict`, `destination_conflict`, `concurrency_unavailable`, `limit_exhausted`, `payload_too_large`, `busy`, `upstream_unauthorized`, `upstream_forbidden`, `upstream_rate_limited`, `upstream_unavailable`, `upstream_protocol_error`, `confirmation_expired`, `confirmation_mismatch`, `fidelity_failure`, `committed_but_unverified`, `committed_but_concurrency_unavailable`, and `indeterminate`. `no_change` and declined confirmation are successful non-mutating results; MRTR `input_required` is a protocol result type.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative sources: RFC 4791, RFC 4918, and HTTP conditionals.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/error/cal-error-002`.
- Objective oracle: Run the named failure fixture and assert this observable result: Closed codes are: `invalid_input`, `invalid_calendar_data`, `not_found`, `ambiguous`, `outside_scope`, `entity_kind_mismatch`, `unsupported_capability`, `opaque_resource`, `temporal_unresolved`, `recurrence_unevaluable`, `conflict`, `destination_conflict`, `concurrency_unavailable`, `limit_exhausted`, `payload_too_large`, `busy`, `upstream_unauthorized`, `upstream_forbidden`, `upstream_rate_limited`, `upstream_unavailable`, `upstream_protocol_error`, `confirmation_expired`, `confirmation_mismatch`, `fidelity_failure`, `committed_but_unverified`, `committed_but_concurrency_unavailable`, and `indeterminate`. `no_change` and declined confirmation are successful non-mutating results; MRTR `input_required` is a protocol result type. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-ERROR-003

- Normative statement: Map CalDAV and HTTP outcomes deterministically: 401/403 to upstream authorization errors; direct-target 404 to `not_found`; discovery 404 and invalid successful responses to `upstream_protocol_error`; 409/412 to conflict; 413 to `payload_too_large`; 429 to `upstream_rate_limited`; 405/501 or explicit DAV capability errors to `unsupported_capability`; exhausted 5xx, timeouts, and transport failures to `upstream_unavailable`; and 507 to non-retryable `upstream_unavailable`.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative sources: RFC 4791, RFC 4918, and HTTP conditionals.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/error/cal-error-003`.
- Objective oracle: Run the named failure fixture and assert this observable result: Map CalDAV and HTTP outcomes deterministically: 401/403 to upstream authorization errors; direct-target 404 to `not_found`; discovery 404 and invalid successful responses to `upstream_protocol_error`; 409/412 to conflict; 413 to `payload_too_large`; 429 to `upstream_rate_limited`; 405/501 or explicit DAV capability errors to `unsupported_capability`; exhausted 5xx, timeouts, and transport failures to `upstream_unavailable`; and 507 to non-retryable `upstream_unavailable`. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-SEC-001

- Normative statement: Accept only canonical absolute resource hrefs without userinfo or fragments, validate configured origin and Calendar Scope before network access, and never construct a host from an agent-supplied href.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative sources: RFC 4918 and RFC 8996.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/sec/cal-sec-001`.
- Objective oracle: Run the named hardening fixture and assert this observable result: Accept only canonical absolute resource hrefs without userinfo or fragments, validate configured origin and Calendar Scope before network access, and never construct a host from an agent-supplied href. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-SEC-002

- Normative statement: Disable XML DTDs and external entities, cap XML depth and characters, keep every calendar URI inert, and never expose out-of-scope existence through ambiguity or authorization diagnostics.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative sources: RFC 4918 and RFC 8996.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/sec/cal-sec-002`.
- Objective oracle: Run the named hardening fixture and assert this observable result: Disable XML DTDs and external entities, cap XML depth and characters, keep every calendar URI inert, and never expose out-of-scope existence through ambiguity or authorization diagnostics. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-SEC-003

- Normative statement: Logs contain only safe codes, phases, durations, and correlation identifiers. Stdout remains the JSON-RPC transport; valid runs leave stderr clean. Credentials, raw requests/responses, complete arguments, calendar content, cursors, and requestState are never logged.
- Source and owning decision: Owner: issue #23, Choose validation errors and execution bounds; normative sources: RFC 4918 and RFC 8996.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: deterministic WebDAV contract.
- Named scenario or fixture: `0.2.0/sec/cal-sec-003`.
- Objective oracle: Run the named hardening fixture and assert this observable result: Logs contain only safe codes, phases, durations, and correlation identifiers. Stdout remains the JSON-RPC transport; valid runs leave stderr clean. Credentials, raw requests/responses, complete arguments, calendar content, cursors, and requestState are never logged. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RELEASE-001

- Normative statement: Release the contract as `0.2.0` under the unchanged NuGet package and MCP server identities. Provide no legacy mode, compatibility aliases, parallel abstractions, or automatic Calendar Object Resource migration. Owner: [Define the breaking release and migration contract](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/32).
- Source and owning decision: Owner: issue #32, Define the breaking release and migration contract; normative source: issue #35 release decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: packed-artifact check.
- Named scenario or fixture: `0.2.0/release/cal-release-001`.
- Objective oracle: Inspect the named packed-artifact fixture and assert this observable result: Release the contract as `0.2.0` under the unchanged NuGet package and MCP server identities. Provide no legacy mode, compatibility aliases, parallel abstractions, or automatic Calendar Object Resource migration. Owner: [Define the breaking release and migration contract](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/32). A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RELEASE-002

- Normative statement: Remove all twelve `0.1.x` tools. Migration is: list task lists to `calendars.list`; show/find/list tasks to To-do `calendar_entities.query`; get task to `calendar_resources.get`; add/create task to `todos.create`; update task to `todos.patch`; complete task to `todos.complete`; complete-by-summary to query then revision-bound completion; delete task to revision-bound confirmed `calendar_resources.delete`; delete-by-summary to query then revision-bound confirmed delete.
- Source and owning decision: Owner: issue #32, Define the breaking release and migration contract; normative source: issue #35 release decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: packed-artifact check.
- Named scenario or fixture: `0.2.0/release/cal-release-002`.
- Objective oracle: Inspect the named packed-artifact fixture and assert this observable result: Remove all twelve `0.1.x` tools. Migration is: list task lists to `calendars.list`; show/find/list tasks to To-do `calendar_entities.query`; get task to `calendar_resources.get`; add/create task to `todos.create`; update task to `todos.patch`; complete task to `todos.complete`; complete-by-summary to query then revision-bound completion; delete task to revision-bound confirmed `calendar_resources.delete`; delete-by-summary to query then revision-bound confirmed delete. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RELEASE-003

- Normative statement: Keep `CALDAV_URL`, `CALDAV_USERNAME`, and `CALDAV_PASSWORD`. Replace task list allowlisting with `CALDAV_CALENDAR_HREFS`, the task default with `CALDAV_DEFAULT_TODO_CALENDAR_NAME`, add `CALDAV_DEFAULT_EVENT_CALENDAR_NAME`, and replace the advanced gate with `CALDAV_EXPOSE_EXACT_TOOLS`. Old names are not interpreted.
- Source and owning decision: Owner: issue #32, Define the breaking release and migration contract; normative source: issue #35 release decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: packed-artifact check.
- Named scenario or fixture: `0.2.0/release/cal-release-003`.
- Objective oracle: Inspect the named packed-artifact fixture and assert this observable result: Keep `CALDAV_URL`, `CALDAV_USERNAME`, and `CALDAV_PASSWORD`. Replace task list allowlisting with `CALDAV_CALENDAR_HREFS`, the task default with `CALDAV_DEFAULT_TODO_CALENDAR_NAME`, add `CALDAV_DEFAULT_EVENT_CALENDAR_NAME`, and replace the advanced gate with `CALDAV_EXPOSE_EXACT_TOOLS`. Old names are not interpreted. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RELEASE-004

- Normative statement: Packaged metadata describes Calendars, Events, and To-dos, declares only the new environment settings and actual protocol capabilities, and retains source server/package versions at `0.0.0` for tag substitution.
- Source and owning decision: Owner: issue #32, Define the breaking release and migration contract; normative source: issue #35 release decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: packed-artifact check.
- Named scenario or fixture: `0.2.0/release/cal-release-004`.
- Objective oracle: Inspect the named packed-artifact fixture and assert this observable result: Packaged metadata describes Calendars, Events, and To-dos, declares only the new environment settings and actual protocol capabilities, and retains source server/package versions at `0.0.0` for tag substitution. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-RELEASE-005

- Normative statement: Migration documentation includes before/after configuration, the complete tool mapping, recipes for To-do reads and writes, revision references, structured outcomes, MRTR, deployment verification, and rollback to pinned `0.1.4`. It states that no CalDAV data migration occurs.
- Source and owning decision: Owner: issue #32, Define the breaking release and migration contract; normative source: issue #35 release decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: packed-artifact check.
- Named scenario or fixture: `0.2.0/release/cal-release-005`.
- Objective oracle: Inspect the named packed-artifact fixture and assert this observable result: Migration documentation includes before/after configuration, the complete tool mapping, recipes for To-do reads and writes, revision references, structured outcomes, MRTR, deployment verification, and rollback to pinned `0.1.4`. It states that no CalDAV data migration occurs. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVIDENCE-001

- Normative statement: Maintain a permanent requirement-to-evidence catalog keyed by these `CAL-<AREA>-NNN` identifiers. Every row records normative statement, owning decision and standards source, applicable Interoperability Profile and compatibility class, primary evidence layer, named scenario/fixture, objective oracle, and implementation/evidence status. IDs are never reused or renumbered. Owner: [Choose the final implementation handoff structure](https://github.com/Jhonattan-Souza/dotnet-agents-caldav/issues/33).
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-001`.
- Objective oracle: `ContractCatalogTests.Evidence_catalog_has_one_complete_row_for_every_normative_requirement` requires exactly 96 unique `## CAL-*` rows, including `CAL-BASE-003` and `CAL-EVIDENCE-010`, and requires every row field mandated by this catalog.
- Implementation status: implemented by this versioned catalog and verifier.
- Evidence status: focused catalog verifier passes locally and is included in the CI test run.

## CAL-EVIDENCE-002

- Normative statement: Versioned semantic fixtures cover discovery/scope/defaults, snapshot coherence, strict schemas, patch operations, temporal kinds, recurrence and overrides, exclusions/cancellations/restoration, Event structured data, inert content, opaque resources, concurrency, post-write truth, limits, errors, and MRTR. Use equivalence partitions and pairwise coverage and explicitly cross recurrence with temporal kind, override with Mutation Scope, patch with opaque content, conditionals with ambiguous outcomes, MRTR with revision change, and limits with pagination.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-002`.
- Objective oracle: Run the catalog verifier and assert this observable result: Versioned semantic fixtures cover discovery/scope/defaults, snapshot coherence, strict schemas, patch operations, temporal kinds, recurrence and overrides, exclusions/cancellations/restoration, Event structured data, inert content, opaque resources, concurrency, post-write truth, limits, errors, and MRTR. Use equivalence partitions and pairwise coverage and explicitly cross recurrence with temporal kind, override with Mutation Scope, patch with opaque content, conditionals with ambiguous outcomes, MRTR with revision change, and limits with pagination. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVIDENCE-003

- Normative statement: Semantic corpus tests prove lossless parsing/replay, domain invariants, recurrence, temporal evaluation, patch atomicity, error ordering, limits, reconciliation, and hardening. Existing mapper, recurrence, XML, and service tests are prior art, but regenerated Ical.Net output or snapshots are never the oracle for losslessness.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-003`.
- Objective oracle: Run the catalog verifier and assert this observable result: Semantic corpus tests prove lossless parsing/replay, domain invariants, recurrence, temporal evaluation, patch atomicity, error ordering, limits, reconciliation, and hardening. Existing mapper, recurrence, XML, and service tests are prior art, but regenerated Ical.Net output or snapshots are never the oracle for losslessness. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVIDENCE-004

- Normative statement: Deterministic WebDAV contract tests prove discovery, REPORT candidate behavior, full-resource reads, conditional mutations, redirects, status mapping, XML safety, origin restrictions, limits, and redaction. Existing CalDAV client request/response tests are the preferred seam to extend.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-004`.
- Objective oracle: Run the catalog verifier and assert this observable result: Deterministic WebDAV contract tests prove discovery, REPORT candidate behavior, full-resource reads, conditional mutations, redirects, status mapping, XML safety, origin restrictions, limits, and redaction. Existing CalDAV client request/response tests are the preferred seam to extend. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVIDENCE-005

- Normative statement: Live integration uses the official digest-pinned Radicale fixture and records platform manifest, Python/vobject versions, `TZ`, and strict-precondition mode. It covers Event-only, To-do-only, mixed, unknown-support, advertisement-violation, and opaque cases; full and expanded REPORT behavior; strong Entity Tag rotation; current/stale/missing/wildcard preconditions; create/update/delete/move; recurrence/time zones; fidelity; server ceilings; and post-write refetch.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-005`.
- Objective oracle: `RadicaleConformanceHarnessTests.Pinned_profile_records_the_runtime_and_selected_variant` reads the running official image and compares its runtime facts with the committed profile; the CI matrix invokes it for baseline, strict-preconditions, and alternate-time-zone. The later behavioral cases remain owned by their named downstream scenarios.
- Implementation status: runtime harness implemented; behavioral coverage planned by downstream tickets.
- Evidence status: the harness runs in the CI matrix; downstream behavioral cases are not represented by task fixtures.

## CAL-EVIDENCE-006

- Normative statement: Packed-artifact tests inspect the final NuGet package, MCP metadata/schema, README, bundled skill, migration guide, CHANGELOG, and release notes. Existing MCP metadata tests are prior art and must be expanded with every environment or metadata change.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-006`.
- Objective oracle: Run the catalog verifier and assert this observable result: Packed-artifact tests inspect the final NuGet package, MCP metadata/schema, README, bundled skill, migration guide, CHANGELOG, and release notes. Existing MCP metadata tests are prior art and must be expanded with every environment or metadata change. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVIDENCE-007

- Normative statement: Every compatibility-matrix entry is independently classified against the project contract, Ical.Net 5.2.3, and Radicale 3.7.8 as supported, required typed rejection, preserved but unevaluable, pinned-profile-only, or unsafe through Ical.Net. A limitation passes only when observed behavior matches its declared class without silent loss.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-007`.
- Objective oracle: `ContractCatalogTests.Compatibility_matrix_uses_independent_component_classes` requires every matrix row to provide a project, Ical.Net 5.2.3, and Radicale 3.7.8 classification and rejects a matrix that equates preservation with support.
- Implementation status: matrix established; behavior tests are planned by downstream tickets.
- Evidence status: focused matrix verifier is included in the CI test run.

## CAL-EVIDENCE-008

- Normative statement: Boundary-sensitive limits are tested below, at, and above each boundary. Limit exhaustion never passes through partial results. Expected output is fixed; test runs never rewrite fixtures or snapshots.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-008`.
- Objective oracle: Run the catalog verifier and assert this observable result: Boundary-sensitive limits are tested below, at, and above each boundary. Limit exhaustion never passes through partial results. Expected output is fixed; test runs never rewrite fixtures or snapshots. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVIDENCE-009

- Normative statement: Pull-request CI and the release workflow run every normative row. Missing, skipped, quarantined, or flaky normative evidence fails. Release build with warnings as errors, method complexity at most 10, at least 90% line and 85% branch coverage, all unit/integration tests, Slopwatch with no warnings, clean stdio, schema-valid metadata, and correct packed source-version substitution are mandatory.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-009`.
- Objective oracle: Run the catalog verifier and assert this observable result: Pull-request CI and the release workflow run every normative row. Missing, skipped, quarantined, or flaky normative evidence fails. Release build with warnings as errors, method complexity at most 10, at least 90% line and 85% branch coverage, all unit/integration tests, Slopwatch with no warnings, clean stdio, schema-valid metadata, and correct packed source-version substitution are mandatory. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.

## CAL-EVIDENCE-010

- Normative statement: Before implementation acceptance, reverify MCP behavior against the selected stable specification, changelog, official feature documentation, and matching official C# SDK. Stable normative text wins over drafts, deprecated samples, and third-party examples.
- Source and owning decision: Owner: issue #33, Choose the final implementation handoff structure; normative source: issue #35 testing decision.
- Normative strength: MUST; this is a versioned 0.2.0 contract requirement.
- Interoperability profile and compatibility class: standards baseline; Radicale 3.7.8 where applicable; see [compatibility matrix](compatibility-matrix.md).
- Primary evidence layer: catalog verifier.
- Named scenario or fixture: `0.2.0/evidence/cal-evidence-010`.
- Objective oracle: Run the catalog verifier and assert this observable result: Before implementation acceptance, reverify MCP behavior against the selected stable specification, changelog, official feature documentation, and matching official C# SDK. Stable normative text wins over drafts, deprecated samples, and third-party examples. A skipped, quarantined, or non-matching result fails this row.
- Implementation status: planned.
- Evidence status: planned until its named scenario is green in pull-request and release CI.
