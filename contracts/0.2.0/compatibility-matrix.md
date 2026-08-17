# Compatibility matrix: unified Calendar contract 0.2.0

Every component cell uses exactly one closed class. `preserved but unevaluable` is not semantic support. Implementation maturity is recorded separately and never changes the component classification.

| Capability | Project contract | Ical.Net 5.2.3 | Radicale 3.7.8 | Implementation state | Evidence / required outcome |
| --- | --- | --- | --- | --- | --- |
| Event and To-do resource projection | supported | supported | pinned-profile-only | planned | `CAL-MODEL-001`, corpus plus live mixed-calendar cases |
| Exact server-returned resource authority | supported | unsafe through Ical.Net | supported | implemented for Event and To-do patch | `CAL-RESOURCE-001`; patch edits authoritative server GET slices and verifies by refetch |
| Unknown registered and extension content on unrelated patch | supported | unsafe through Ical.Net | preserved but unevaluable | implemented for Event and To-do patch | `CAL-RESOURCE-002`; outbound unaddressed slices are byte-exact and refetch requires semantic/lossless equivalence |
| Resource-local VTIMEZONE evaluation | supported | preserved but unevaluable | pinned-profile-only | implemented for occurrence queries | `CAL-TIME-003`; one unambiguous resource-local definition wins, while unknown or conflicting definitions fail with a typed outcome and no host-zone fallback |
| Bounded RRULE evaluation | supported | supported | pinned-profile-only | implemented for occurrence queries | `CAL-RECUR-001`; client owns final evaluation from authoritative GET bytes |
| Multiple RRULE resources | preserved but unevaluable | preserved but unevaluable | pinned-profile-only | implemented for occurrence queries | `CAL-MODEL-005`; preserve the resource and return `recurrence_unevaluable` with no partial items |
| RDATE PERIOD semantic write | required typed rejection | preserved but unevaluable | required typed rejection | planned | `CAL-RECUR-002`; reject before PUT |
| THISANDFUTURE mutation | supported | unsafe through Ical.Net | pinned-profile-only | planned | `CAL-BASE-004`; service owns semantics |
| Full-resource GET and REPORT candidate reduction | supported | required typed rejection | supported | implemented for entity and occurrence queries | `CAL-DISC-006`, `CAL-DAV-001`; REPORT is candidate reduction only and final filtering uses authoritative GET bytes locally |
| Strong ETag conditional mutation | supported | required typed rejection | supported | implemented for Event and To-do patch | `CAL-RESOURCE-009`; exact If-Match, no weak-tag bypass, no blind retry |
| Strict preconditions mode | supported | required typed rejection | pinned-profile-only | implemented | `strict-preconditions` fixture variant |
| Calendar alarms and URI values | supported | preserved but unevaluable | preserved but unevaluable | implemented for Event and To-do patch | `CAL-EVENT-006`; typed edits remain inert and unaddressed forms are preserved losslessly |
| Exact replacement | supported | unsafe through Ical.Net | pinned-profile-only | planned | `CAL-RESOURCE-008`; caller UTF-8 is sent unchanged |
| Other CalDAV servers | pinned-profile-only | required typed rejection | pinned-profile-only | planned | `CAL-BASE-002`; capability negotiation may operate, but no interoperability claim |

## Classification vocabulary

- `supported`: the component may provide the required semantics at the named boundary.
- `required typed rejection`: the contract must fail closed with the named structured outcome.
- `preserved but unevaluable`: data can remain intact, but no semantic evaluation or mutation claim follows.
- `pinned-profile-only`: behavior is evidenced only for the digest-pinned Radicale profile.
- `unsafe through Ical.Net`: Ical.Net must not be used as the authority for that operation.
