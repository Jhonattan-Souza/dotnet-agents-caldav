# Compatibility matrix: unified Calendar contract 0.2.0

Classes are deliberately independent. `preserved but unevaluable` is not semantic support.
`planned` means the behavior is frozen by the contract but is not implemented by the 0.1.x codebase.

| Capability | Project contract | Ical.Net 5.2.3 | Radicale 3.7.8 profile | Evidence / required outcome |
| --- | --- | --- | --- | --- |
| Event and To-do resource projection | planned | supported parser projection | pinned-profile-only | `CAL-MODEL-001`, corpus plus live mixed-calendar cases |
| Exact server-returned resource authority | planned | unsafe through Ical.Net | supported | `CAL-RESOURCE-001`; only server GET bytes are authoritative |
| Unknown registered and extension content on unrelated patch | planned | unsafe through Ical.Net | preserved but unevaluable | `CAL-RESOURCE-002` corpus oracle; regenerated Ical.Net output is forbidden |
| Resource-local VTIMEZONE evaluation | required typed rejection when unresolved | preserved but unevaluable | pinned-profile-only | `CAL-TIME-003`; no host-zone fallback |
| Bounded RRULE evaluation | planned | supported | pinned-profile-only | `CAL-RECUR-001`; client owns final evaluation |
| Multiple RRULE resources | preserved but unevaluable | preserved but unevaluable | pinned-profile-only | `CAL-MODEL-005`; return `recurrence_unevaluable` |
| RDATE PERIOD semantic write | required typed rejection | preserved but unevaluable | required typed rejection | `CAL-RECUR-002`; reject before PUT |
| THISANDFUTURE mutation | planned | unsafe through Ical.Net | pinned-profile-only | `CAL-BASE-004`; service owns semantics |
| Full-resource GET and REPORT candidate reduction | planned | not applicable | supported | `CAL-DISC-006`, `CAL-DAV-001`; final filter is local |
| Strong ETag conditional mutation | planned | not applicable | supported | `CAL-RESOURCE-009`; no weak-tag bypass |
| Strict preconditions mode | planned | not applicable | pinned-profile-only | `strict-preconditions` fixture variant |
| Calendar alarms and URI values | planned inert storage | preserved but unevaluable | preserved but unevaluable | `CAL-EVENT-006`; never execute or dereference |
| Exact replacement | planned | unsafe through Ical.Net | pinned-profile-only | `CAL-RESOURCE-008`; caller UTF-8 is sent unchanged |
| Other CalDAV servers | unverified profile | not applicable | not applicable | `CAL-BASE-002`; no interoperability claim |

## Classification vocabulary

- `supported`: the component may provide the required semantics at the named boundary.
- `required typed rejection`: the contract must fail closed with the named structured outcome.
- `preserved but unevaluable`: data can remain intact, but no semantic evaluation or mutation claim follows.
- `pinned-profile-only`: behavior is evidenced only for the digest-pinned Radicale profile.
- `unsafe through Ical.Net`: Ical.Net must not be used as the authority for that operation.
