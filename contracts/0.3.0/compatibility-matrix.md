# Compatibility matrix: slim output schema and cached tool list contract 0.3.0

| Surface | 0.2.3 | 0.3.0 |
| --- | --- | --- |
| `errorOutcome` | Optional `currentSnapshot` Calendar Snapshot | No `currentSnapshot`; read tools never returned one |
| `mutationErrorOutcome` | Optional `currentSnapshot` Calendar Snapshot | Unchanged; entity mutations keep their conflict snapshot |
| `calendars.create`, `calendars.delete` failure | Not present | `calendarCollectionMutationErrorOutcome`: the mutation failure shape without `currentSnapshot` |
| `calendars.patch` failure | Not present | `calendarMetadataPatchErrorOutcome` without `currentSnapshot` |
| Read output schemas | Every tool embedded the Calendar Snapshot projection through its failure branch | `calendars.list`, `calendars.inspect`, `calendars.free_busy`, `calendar_resources.changes`, `calendar_resources.exact_get`, and the collection tools no longer embed it |
| `todos.query` `recurrence` | Shared `recurrenceSet`; overrides may declare Event or To-do fields | `todoQueryRecurrenceSet`; overrides are `entityKind: todo` with To-do fields only, as VTODO overrides always were |
| `tools/list` caching hint | SDK default `ttlMs: 0`, `cacheScope: private` | `ttlMs: 3600000`, `cacheScope: private` from `transport.toolsListCache` |
| `server/discover` caching hint | SDK default `ttlMs: 0`, `cacheScope: private` | Unchanged |
| Per-tool cache metadata | Tool-specific hints under `_meta.cache` | Same hints under `_meta["io.github.jhonattan-souza/cache"]` |
| Tool titles | Absent | Short human-readable titles for all 27 tools |
| Server instructions | Absent | Catalog routing guidance returned in discovery and initialization |
| `CALDAV_EVALUATION_TIME_ZONE` | Optional | Required for installations; a caller `evaluationTimeZone` still wins |
| Default semantic catalog | 17 tools | 23 tools: adds `calendars.create`, `calendars.delete`, `calendars.inspect`, `calendars.patch`, `calendars.free_busy`, and `calendar_resources.changes` |
| Opt-in exact catalog | 4 additional tools | 4 additional tools |
| MRTR `tools/call` parameters | 21 closed branches | 27 closed branches |

The 0.3.0 wire schemas remain closed. Removing `currentSnapshot` from
`errorOutcome` narrows a property that no read path ever populated, so every
failure a 0.2.3 read tool emitted also validates against 0.3.0. The native
collection, metadata, report, and sync tools shipped after 0.2.3 without a
contract directory; 0.3.0 is the first versioned record of that catalog.
Historical contract directories remain immutable.
