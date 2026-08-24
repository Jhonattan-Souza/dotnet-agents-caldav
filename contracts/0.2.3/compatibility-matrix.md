# Compatibility matrix: query, Move, and telemetry contract 0.2.3

| Surface | 0.2.2 | 0.2.3 |
| --- | --- | --- |
| `calendar_entities.query` pagination | Query-bound cursor re-executes the query | Start creates a ten-minute Query Result Snapshot; Continue performs no CalDAV or semantic work |
| `calendar_occurrences.query` pagination | Cursor-bound query replay | Start creates one immutable snapshot; Continue reads projected snapshot bytes only |
| `todos.query` pagination | Query-bound cursor replay | Start evaluates one VTODO-only corpus once; Continue reads the immutable snapshot |
| Query request shape | Query fields plus an optional cursor | Strict Start-or-Continue union; Continue accepts only the Continuation Cursor and page size |
| Continuation expiry | Query cursor policy | Fixed snapshot expiry ten minutes after Start; replay never extends it |
| Bounded Calendar Entity time zone | Implicit host context possible | Explicit caller or configured IANA Temporal Evaluation Context required before I/O |
| Occurrence and To-do time zone | Caller query context | Explicit caller or configured IANA Temporal Evaluation Context required for every Start |
| Successful query retrieval | `calendar-multiget` batches | 50-resource multiget batches with zero GETs; malformed or incomplete batches fail atomically |
| Direct GET compatibility | No closed activation and work contract | Activates only after HTTP 405, HTTP 501, or explicit DAV unsupported evidence; bounded four-wide waves |
| Semantic Move | Client-side destination and UID discovery | One conditional server-authoritative MOVE plus bounded bilateral reconciliation |
| Exact Move MRTR | Repeated preflight work around confirmation | Fresh review plus one internal one-use execution plan; no third preflight or UID scan |
| Move interoperability | Generic capability behavior | Fail closed unless the verified Radicale 3.7.8 profile is configured |
| Destination collision disclosure | May derive collision details from scans | `destination_conflict` only for exact occupancy; other conflicts remain non-disclosing |
| Discovery lifetime | Repeated discovery consumers inside one call | One immutable authorization-bound discovery result reused per MCP tool call |
| Telemetry | MCP SDK defaults without product OTLP pipeline | Opt-in allowlisted OTLP traces, metrics, and correlated logs with bounded export |
| Default semantic catalog | 17 tools | 17 tools; no legacy aliases or compatibility paths |
| Opt-in exact catalog | 4 additional tools | 4 additional tools |

The 0.2.3 wire schemas remain closed. Query input and continuation contracts
are deliberately breaking; pending 0.2.2 cursors and MRTR request states must
be discarded during upgrade. Historical contract directories remain immutable.
