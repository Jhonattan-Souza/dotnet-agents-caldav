# Compatibility matrix: unified Calendar contract 0.2.0

Every component cell uses exactly one closed class. `preserved but unevaluable` is not semantic support. Implementation maturity is recorded separately and never changes the component classification.

| Capability | Project contract | Ical.Net 5.2.3 | Radicale 3.7.8 | Implementation state | Evidence / required outcome |
| --- | --- | --- | --- | --- | --- |
| Event and To-do resource projection | supported | supported | pinned-profile-only | planned | `CAL-MODEL-001`, corpus plus live mixed-calendar cases |
| Exact server-returned resource authority | supported | unsafe through Ical.Net | supported | planned | `CAL-RESOURCE-001`; only server GET bytes are authoritative |
| Unknown registered and extension content on unrelated patch | supported | unsafe through Ical.Net | preserved but unevaluable | planned | `CAL-RESOURCE-002` corpus oracle; regenerated Ical.Net output is forbidden |
| Resource-local VTIMEZONE evaluation | required typed rejection | preserved but unevaluable | pinned-profile-only | planned | `CAL-TIME-003`; no host-zone fallback |
| Bounded RRULE evaluation | supported | supported | pinned-profile-only | planned | `CAL-RECUR-001`; client owns final evaluation |
| Multiple RRULE resources | preserved but unevaluable | preserved but unevaluable | pinned-profile-only | planned | `CAL-MODEL-005`; return `recurrence_unevaluable` |
| RDATE PERIOD semantic write | required typed rejection | preserved but unevaluable | required typed rejection | planned | `CAL-RECUR-002`; reject before PUT |
| THISANDFUTURE mutation | supported | unsafe through Ical.Net | pinned-profile-only | planned | `CAL-BASE-004`; service owns semantics |
| Full-resource GET and REPORT candidate reduction | supported | required typed rejection | supported | planned | `CAL-DISC-006`, `CAL-DAV-001`; final filter is local |
| Strong ETag conditional mutation | supported | required typed rejection | supported | planned | `CAL-RESOURCE-009`; no weak-tag bypass |
| Strict preconditions mode | supported | required typed rejection | pinned-profile-only | implemented | `strict-preconditions` fixture variant |
| Calendar alarms and URI values | supported | preserved but unevaluable | preserved but unevaluable | planned | `CAL-EVENT-006`; never execute or dereference |
| Exact replacement | supported | unsafe through Ical.Net | pinned-profile-only | planned | `CAL-RESOURCE-008`; caller UTF-8 is sent unchanged |
| Other CalDAV servers | pinned-profile-only | required typed rejection | pinned-profile-only | planned | `CAL-BASE-002`; capability negotiation may operate, but no interoperability claim |

## Classification vocabulary

- `supported`: the component may provide the required semantics at the named boundary.
- `required typed rejection`: the contract must fail closed with the named structured outcome.
- `preserved but unevaluable`: data can remain intact, but no semantic evaluation or mutation claim follows.
- `pinned-profile-only`: behavior is evidenced only for the digest-pinned Radicale profile.
- `unsafe through Ical.Net`: Ical.Net must not be used as the authority for that operation.
