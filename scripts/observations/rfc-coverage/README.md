# RFC coverage live observations

These scripts exercise a published MCP assembly through persistent JSON-RPC
stdio against disposable CalDAV containers. They do not change CI gates or
the configured Hermes installation. Python 3, Docker and .NET 10 are required;
the Hermes runner additionally uses its existing Python environment with
PyYAML and python-dotenv.

Start with a new result directory outside the repository. The fixture owns
only containers listed in its private manifest and labeled `caldav.rfc.owner`.
Published ports bind to loopback. Credentials are generated per fixture;
the result directory is private. `infra-private.json`, the isolated Hermes
home and `run-private.log` must remain local. Only sanitized evidence should
be copied into a report.

```bash
dotnet publish src/DotnetAgents.CalDav.Mcp/DotnetAgents.CalDav.Mcp.csproj \
  -c Release -f net10.0 --artifacts-path /tmp/caldav-rfc-run/build \
  -o /tmp/caldav-rfc-run/candidate
python3 scripts/observations/rfc-coverage/infra.py up /tmp/caldav-rfc-run
python3 scripts/observations/rfc-coverage/infra.py seed /tmp/caldav-rfc-run --count 24
python3 scripts/observations/rfc-coverage/capabilities.py /tmp/caldav-rfc-run
python3 scripts/observations/rfc-coverage/functional.py /tmp/caldav-rfc-run \
  /tmp/caldav-rfc-run/candidate/DotnetAgents.CalDav.Mcp.dll \
  --output /tmp/caldav-rfc-run/candidate-live
python3 scripts/observations/rfc-coverage/telemetry.py /tmp/caldav-rfc-run candidate \
  --resource caldav-rfc-radicale-candidate-live
python3 scripts/observations/rfc-coverage/summarize.py \
  /tmp/caldav-rfc-run/candidate-live /tmp/caldav-rfc-run/candidate-traces.json
```

`functional.py` asserts that every live catalog tool was called. It measures
each call, completes authorized fixture MRTR boolean confirmations, checks
mutations through independent HTTP reads, and restores fixture resource
counts. An added catalog operation needs a corresponding scenario before
this coverage assertion passes. Separate output directories preserve runs
and allow repeated measurements without replacing earlier evidence.

Each record contains wall time, CPU time, resident memory and response size.
Aspire exports are rejected when truncated. `summarize.py` matches one
operation trace to every call and includes HTTP request counts and retries.
It reports sample sizes with timing distributions; individual scenario
samples are observations rather than performance thresholds. The fixture
and model inference are outside the measured MCP round trip.

The new-operation scenarios verify metadata set, omitted-property preservation,
removal and language readback; known busy/tentative periods, cancellation and
transparency; initial sync, no-change, create/update/removal deltas, checkpoint
tampering, small-page limits and rejection after an MCP process restart.
A three-change delta must fail without a checkpoint when a smaller page
cannot be returned completely; retrying the original checkpoint with a larger
page must retain every change, followed by an empty no-change response.
`protocol.py --reports-only` isolates the native report scenarios while
investigating another operation.

For cold/warm measurements, `performance.py` uses three separate MCP processes
per operation and scope, with five warm calls after each cold call. A temporary
loopback observer counts request and response payload bytes without retaining
bodies or headers. Its HTTP attempt count must agree with Aspire before the
summary passes. The observer buffers responses, so its measured durations
include that overhead. Resource counts, process count and scope are recorded.

```bash
python3 scripts/observations/rfc-coverage/performance.py /tmp/caldav-rfc-run \
  /tmp/caldav-rfc-run/candidate/DotnetAgents.CalDav.Mcp.dll \
  --output /tmp/caldav-rfc-run/candidate-performance --resources 100
```

The other compatibility lanes use distinct manifests and pinned images.
They reuse the RFC fixture's Aspire collector. Leave the Move interoperability
profile unset on these lanes: Semantic and Exact Move must return
`unsupported_capability` until a separate verified profile exists.
Collection deletion and participation-bearing writes also fail without an
attempt when OPTIONS advertises automatic scheduling.

```bash
python3 scripts/observations/rfc-coverage/compat.py baikal /tmp/caldav-rfc-baikal \
  --telemetry-root /tmp/caldav-rfc-run
python3 scripts/observations/rfc-coverage/compat.py nextcloud /tmp/caldav-rfc-nextcloud \
  --telemetry-root /tmp/caldav-rfc-run
python3 scripts/observations/rfc-coverage/infra.py seed /tmp/caldav-rfc-baikal --count 24
python3 scripts/observations/rfc-coverage/infra.py seed /tmp/caldav-rfc-nextcloud --count 24
```

The same functional command accepts either lane directory. Telemetry service
names include `baikal` or `nextcloud` instead of `radicale`. Collection deletion
checks the DAV resource type: a server may retain a trash node at an old href
without retaining a CalDAV Calendar. GET alone cannot verify collection
absence because Nextcloud serves a generic WebDAV page at collection URLs.

Nextcloud's trash hierarchy includes a collection whose Depth 1 PROPFIND
returns 501. Unrestricted recursive discovery retains that explicit failure.
Use `functional.py --exact-scope` for the successful Nextcloud lane: it sets
an explicit scope containing the three seeded Calendars and both planned
fixture destinations before starting MCP. This limits traversal by the
configured authorization scope. Use `performance.py --scope explicit` for
the same runtime limitation in performance observations.
The default performance mode counts payload bytes through an HTTP observer.
Its buffering and fresh backend connections add latency. Use `--direct` for
latency measurements without that observer; Aspire still records HTTP attempts.
For example, `--resources 100 --cold-runs 1 --warm-runs 15 --scope explicit
--direct` records one cold call and fifteen warm calls per new operation,
plus fifteen no-change checkpoint calls. Setup and model inference are excluded.
Use `--operation calendar_resources.changes` to repeat only sync measurements
after a checkpoint implementation change.
Telemetry export resolves every process instance of a named service and checks
each capture for truncation before combining spans.
Repeated fixture creation can reach Nextcloud's default ten-calendar-per-user
hourly limit. `compat.py nextcloud NEW_ROOT --fresh-user-from ORIGINAL_ROOT`
creates another disposable user in the owned container without changing
server limits. Seed that new lane before using it; its parent owns container
cleanup.

Radicale 3.7.8 returns nonconforming native free/busy content despite HTTP 200:
multiple VFREEBUSY components contain standalone FBTYPE fields instead of
FREEBUSY periods. The scenario requires `upstream_protocol_error` with no
availability result. Baikal and Nextcloud supply the conformant success lanes.
Radicale also synthesizes a display name after removal; the scenario records
`committed_but_unverified` and verifies description-only removal separately.

For real agent usage, invoke `hermes.py` using the installed Hermes Python.
The script copies the configured model and reasoning setting unchanged into
a separate `HERMES_HOME`, with only this MCP configured. It uses the existing
OpenRouter credential for the run and removes that copied credential on exit.
`--prompt-file` supplies a scenario for added operations. The transparent
proxy preserves MCP bytes and records only sanitized response structure.
Checkpoint lengths and hashes record whether the client copied returned
handles exactly without retaining those handles in the evidence.
Confirm successful writes independently; if the client cannot continue MRTR,
report that limitation and clean only the resulting disposable resource.

```bash
/path/to/hermes/venv/bin/python scripts/observations/rfc-coverage/hermes.py \
  /tmp/caldav-rfc-run /tmp/caldav-rfc-run/candidate/DotnetAgents.CalDav.Mcp.dll \
  --output /tmp/caldav-rfc-run/hermes-candidate
python3 scripts/observations/rfc-coverage/infra.py down /tmp/caldav-rfc-baikal
python3 scripts/observations/rfc-coverage/infra.py down /tmp/caldav-rfc-nextcloud
python3 scripts/observations/rfc-coverage/infra.py down /tmp/caldav-rfc-run
```

Setup references: [Aspire standalone dashboard](https://aspire.dev/dashboard/standalone/)
and [telemetry APIs](https://aspire.dev/dashboard/apis/),
[Nextcloud Docker auto configuration](https://github.com/nextcloud/docker#auto-configuration-via-environment-variables),
[Baikal Docker guidance](https://sabre.io/baikal/docker-install/) and
[the referenced image project](https://github.com/ckulka/baikal-docker).
Protocol gap decisions belong in the RFC implementation plan; these
references explain fixture setup only.
