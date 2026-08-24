# Migrating from contract 0.2.2 to 0.2.3

Contract 0.2.3 deliberately replaces query replay and Move preflight behavior.
Tool names and the 17-tool default catalog remain unchanged, but the three query
input contracts, their continuation tokens, Temporal Evaluation Context rules,
and Move interoperability requirements change.

## Query migration

- Treat `calendar_entities.query`, `calendar_occurrences.query`, and
  `todos.query` as strict Start-or-Continue unions.
- Send query criteria only on Start. Send only the Continuation Cursor and
  optional page size on Continue; do not repeat Start criteria.
- Discard every 0.2.2 query cursor during upgrade. A 0.2.3 Continue addresses a
  process-local Query Result Snapshot and is not portable across versions or
  server restarts.
- Expect a fixed ten-minute snapshot expiry from the first page. Continuing a
  snapshot does not extend its lifetime and performs no CalDAV, parsing,
  filtering, projection, or recurrence work.
- Provide `evaluationTimeZone` or configure
  `CALDAV_EVALUATION_TIME_ZONE` for every Occurrence or To-do Start and every
  bounded Calendar Entity Start. Use an exact IANA time-zone identifier.
- Do not send an unused caller time-zone override for an unbounded Calendar
  Entity Start; it is rejected before discovery.

Normal retrieval continues to use `calendar-multiget`. Direct GET Compatibility
Mode is automatic only after verified HTTP 405, HTTP 501, or explicit DAV
unsupported evidence. Other multiget failures return a typed
`upstream_protocol_error` without partial query results.

## Move migration

Set `CALDAV_INTEROPERABILITY_PROFILE=radicale-3.7.8` only when the server is the
digest-pinned verified profile. Semantic and Exact Move fail closed with
`unsupported_capability` when that profile is absent. No legacy collection-scan
fallback is available.

Move now delegates destination and UID collision truth to one conditional MOVE
and reconciles source and destination with bounded probes. Clients should treat
`destination_conflict` as exact destination occupancy and other `conflict`
results as deliberately non-disclosing. Exact Move still requires MRTR, but its
opaque request state now binds a protected one-use execution plan. Discard
pending 0.2.2 Move confirmations and request a new review after upgrade.

## Telemetry configuration

Telemetry remains disabled by default. A non-empty
`OTEL_EXPORTER_OTLP_ENDPOINT` opts into the bounded, privacy-allowlisted OTLP
pipeline unless `OTEL_SDK_DISABLED=true`. Review endpoint, protocol, headers,
and service-name configuration before enabling export; no CalDAV identifiers,
credentials, payloads, URLs, or exception details are intended to leave the
process.

## Deployment and rollback

Before switching clients, configure the Temporal Evaluation Context and, when
applicable, the verified interoperability profile. Restart the MCP process,
rediscover the catalog, and start fresh query and MRTR flows. No CalDAV data
migration or rewrite is performed.

Rollback by pinning `dotnet-agents-caldav@0.2.2`, removing any configuration
used only by 0.2.3 if desired, restarting the MCP process, and rediscovering the
0.2.2 catalog. Discard all 0.2.3 Continuation Cursors and MRTR request states;
they are not portable to the older contract.
