# CalDAV Calendars

This context exposes CalDAV Calendars, Events, and To-dos to conversational and href-based workflows while preserving deterministic targeting and concurrency safety.

## Language

**Calendar**:
A CalDAV collection that contains Calendar Object Resources. Its advertised Entity Kind support is discovery evidence rather than enforcement of its stored contents.
_Avoid_: Task list, event list

**Calendar Name**:
A human-readable Calendar label supplied by the server or derived from its href with explicit provenance; it is not identity and need not be unique.
_Avoid_: Calendar ID, task list name

**Entity Kind Support**:
Observed evidence that a Calendar advertises, does not advertise, or has unknown support for one Entity Kind; it is not inferred from stored Calendar Object Resources.
_Avoid_: Capability boolean, content type

**CalDAV Capability**:
A protocol behavior available to an operation at a defined origin, Calendar, or Calendar Object Resource scope, distinguished as advertised, verified, or unavailable.
_Avoid_: Feature flag, server promise

**Interoperability Profile**:
An evidence-backed statement of CalDAV Capabilities and limitations for one precisely identified server runtime.
_Avoid_: Compatibility mode, generic server support

**Calendar Scope**:
The explicit set of Calendars eligible for an operation, identified by canonical href rather than partial href or inferred Calendar Name matches.
_Avoid_: Task list filter, implicit all-calendars search

**Default Calendar**:
A Calendar selected for one Entity Kind only when an operation has no explicit Calendar selection; Event and To-do defaults are independent.
_Avoid_: Global default list, fallback Calendar

**Calendar Object Resource**:
A resource stored in a Calendar that is the persistence and concurrency boundary for exactly one Calendar Entity and its supporting calendar data.
_Avoid_: Calendar item, event file, task file

**Opaque Calendar Object Resource**:
Complete calendar resource content that cannot be projected as exactly one valid Calendar Entity; it remains inspectable but has no Calendar Entity semantics.
_Avoid_: Unknown entity, generic calendar item

**Calendar Entity**:
Exactly one logical Event or To-do, whether non-recurring or recurring; it is a closed category rather than a generic item with a shared bag of fields.
_Avoid_: Calendar item, generic item

**Entity Kind**:
The fixed classification of a Calendar Entity as exactly Event or To-do.
_Avoid_: Component type, mutable type

**Entity UID**:
The durable logical identity of a Calendar Entity, unchanged by moving or rescheduling it. Within one Calendar it is unique across Entity Kinds; a recurrence master and its overrides share one Entity UID within one Calendar Object Resource.
_Avoid_: Resource address, summary

**Entity Tag**:
The server-issued identity of one Calendar Object Resource revision, used to distinguish it from earlier or later revisions.
_Avoid_: Entity ID, version number

**Calendar Object Resource Snapshot**:
An immutable view of one Calendar Object Resource revision whose href, Entity Tag, authoritative content, and typed projection all describe that same revision.
_Avoid_: Calendar Entity, mutable resource

**Calendar Object Resource Revision Reference**:
The href, Entity UID, Entity Kind, and Entity Tag that together identify the Calendar Object Resource revision an operation expects to affect.
_Avoid_: Resource ID, Entity identity

**Semantic Patch**:
A typed mutation intent that changes explicitly addressed Calendar Entity semantics while preserving every unaddressed part of the authoritative Calendar Object Resource.
_Avoid_: Partial resource, generic update

**Exact Replacement**:
An explicit mutation that supplies the complete intended content of a Calendar Object Resource, with omitted content intentionally removed rather than preserved.
_Avoid_: Patch, merge

**Calendar Object Resource Move**:
An atomic relocation of a Calendar Object Resource that preserves its Entity UID and complete semantics while producing a new resource address and revision.
_Avoid_: Copy and delete, Entity conversion

**Mutation State**:
The evidence-backed classification of whether a requested mutation was attempted and committed: not attempted, not committed, committed, or unknown.
_Avoid_: Success flag, HTTP status

**Fidelity Failure**:
A committed Calendar Object Resource revision whose observed semantics differ from the mutation that produced it.
_Avoid_: Rejected write, transport failure

**Execution Budget**:
The declared finite allowance for completing one operation across elapsed time, evaluated work, result count, and transferred data.
_Avoid_: Timeout, page size, server capacity

**Limit Exhaustion**:
The failure to produce a complete truthful operation result because one dimension of its Execution Budget was consumed.
_Avoid_: Partial success, empty result, timeout

**Query Result Snapshot**:
An immutable, authorization-bound view of one completely evaluated query result from which every result page is derived without repeating remote or semantic work.
_Avoid_: CalDAV cache, live result set, offset page

**Continuation Cursor**:
An opaque authenticated position in one Query Result Snapshot whose validity cannot outlive that snapshot.
_Avoid_: Page number, durable result identifier, CalDAV sync token

**Event**:
A Calendar Entity representing a scheduled activity or state over a date or time interval.

**To-do**:
A Calendar Entity representing actionable work with a completion lifecycle.
_Avoid_: Task, calendar item

**Recurrence Set**:
The optional recurrence definition of a Calendar Entity, including all of its inclusion and exclusion rules and Recurrence Overrides.
_Avoid_: Repeated item, recurrence instances

**Unevaluable Recurrence Set**:
A preserved Recurrence Set whose definition does not determine Occurrences unambiguously, without making its Calendar Entity opaque.
_Avoid_: Invalid entity, opaque resource

**Recurrence Override**:
A persisted exception that changes or cancels one Recurrence Identity within a Recurrence Set while retaining the Entity UID of its master.
_Avoid_: Edited occurrence, exception event

**Range Override**:
A Recurrence Override whose addressed changes apply from one Recurrence Identity through the later identities in the Recurrence Set.
_Avoid_: Future exception, recurring patch

**Occurrence Exclusion**:
The suppression of one Recurrence Identity by an exclusion rule without persisted replacement content for that Occurrence.
_Avoid_: Cancellation, deletion

**Occurrence Cancellation**:
A persisted Recurrence Override that retains one Recurrence Identity and its content while marking that Occurrence cancelled.
_Avoid_: Exclusion, deletion

**Occurrence**:
A derived realization of a Calendar Entity at a particular recurrence identity; it is not an independently persisted Calendar Entity.
_Avoid_: Instance, recurring item

**Recurrence Identity**:
The original date or date-time that identifies one recurrence within a Recurrence Set, remaining stable when that recurrence is moved.
_Avoid_: Current start, occurrence index

**Temporal Value**:
A date or date-time that preserves whether it is date-only, floating, UTC, or associated with a named time zone.
_Avoid_: Timestamp, normalized date-time

**Temporal Evaluation Context**:
The explicit IANA time zone used to compare or expand floating and date-only Temporal Values without changing their preserved temporal kind. A caller context is distinct from a validated deployment configuration context; neither permits inference from Calendar, server, operating-system, process, host, locale, or location state.
_Avoid_: Default time zone, host time zone

**Effective Temporal Span**:
The derived interval between a Calendar Entity's effective start and end, retained when it is rescheduled unless the mutation explicitly changes its end or duration.
_Avoid_: Stored duration, fixed end time

**Mutation Scope**:
The explicit recurrence boundary targeted by a mutation: one Recurrence Identity, that identity and its future, or the entire Recurrence Set.
_Avoid_: Edit mode, inferred occurrence range

**Calendar Property**:
A preserved iCalendar content value with its name, value type, parameters, and multiplicity when it is not represented by a first-class domain field.
_Avoid_: Property bag, custom field

**First-class Calendar Field**:
A typed Event or To-do semantic value that a Semantic Patch can address directly under explicit domain invariants.
_Avoid_: Convenience property, raw iCalendar field

**Structured Calendar Data**:
A typed nested or repeatable calendar value whose complete standard structure and unmodeled Calendar Properties remain distinguishable.
_Avoid_: Scalar field, flattened collection

**Effective Calendar Value**:
The interpreted value after applying a standard default while retaining whether and how the source value was explicitly represented.
_Avoid_: Normalized value, rewritten default

**Derived Calendar Data**:
A calendar value explicitly marked as derived from other data but without an assumed or inferred source relationship.
_Avoid_: Computed field, synchronized copy

**Storage-only Scheduling Data**:
Organizer, Attendee, Participant, and related participation data preserved and explicitly mutable without invitation, reply, delivery, authority, or propagation behavior.
_Avoid_: Scheduling workflow, meeting invitation

**Inert External Reference**:
A URI-bearing Calendar Entity value that is preserved and exposed as data but is never automatically fetched, opened, joined, or executed.
_Avoid_: Integration, active link

**Calendar Alarm**:
An inert alert definition stored within a Calendar Entity; its presence does not imply notification delivery or execution by this system.
_Avoid_: Notification job, reminder execution

**To-do Completion**:
A transition that marks a non-recurring To-do or exactly one Occurrence of a recurring To-do as completed at a specific time while retaining its other data.
_Avoid_: Done flag, finish operation, task completion

**To-do Completion State**:
The server-owned interpretation of a To-do's completion evidence as `open`, `completed`, `cancelled`, or `indeterminate`, derived conservatively from STATUS, COMPLETED, and PERCENT-COMPLETE without rewriting the authoritative Calendar Object Resource.
_Avoid_: Boolean done flag, client-side status guess, cancellation as deletion
