# Compatibility matrix: compact To-do query contract 0.2.1

| Surface | 0.2.0 | 0.2.1 |
| --- | --- | --- |
| `calendar_entities.query` | Full revision-coherent snapshots | Unchanged and backward compatible |
| `todos.query` | Not present | Default semantic tool; explicit Calendar Scope; compact typed output |
| Completion interpretation | Mutation-only | Read normalization: open, completed, cancelled, indeterminate |
| `todos.complete` completion evidence | Primarily `STATUS:COMPLETED` | Normalized completion evidence; cancelled and contradictory evidence are typed rejections |
| Default semantic catalog | 16 tools | 17 tools |
| Opt-in exact catalog | 4 additional tools | 4 additional tools |

The 0.2.1 query is additive. Clients that require full Calendar Object
Resource snapshots continue to use `calendar_entities.query`.
