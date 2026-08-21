# Compatibility matrix: authoritative Create contract 0.2.2

| Surface | 0.2.1 | 0.2.2 |
| --- | --- | --- |
| `events.create`, `todos.create` collision decision | Exhaustive UID preflight followed by conditional PUT | One authoritative conditional PUT; no collection enumeration |
| `calendar_resources.exact_create` review | Destination absence represented as a synthetic `"*"` revision | Dedicated create binding: destination href, UID, kind, intent digest, and policy version |
| Destination href collision | `conflict` | `destination_conflict`, `not_committed` |
| Entity UID collision | `conflict` | `conflict`, `not_committed` |
| Generated UID collision | Bounded retry after preflight or PUT conflict | At most three conditional PUT attempts with fresh UIDs after definite not-committed conflicts |
| Caller-supplied UID | Fixed | Fixed; exactly one conditional PUT attempt |
| Create execution deadline | `limit_exhausted` without a guaranteed Core dimension | `limit_exhausted` with `limits.dimension: elapsed_time` |
| Default semantic catalog | 17 tools | 17 tools |
| Opt-in exact catalog | 4 additional tools | 4 additional tools |

The 0.2.2 wire schemas remain closed. This version deliberately changes Create
collision behavior while preserving tool names and input shapes. The historical
0.2.0 and 0.2.1 artifacts remain immutable records of their contracts.
