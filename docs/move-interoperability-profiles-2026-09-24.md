# Move interoperability profiles: live observation, 2026-09-24

Server-authoritative Semantic and Exact Move delegate atomic destination and
UID decisions to one conditional `MOVE` (`If-Match`, `Overwrite: F`, absolute
`Destination`). A server runtime becomes a verified Interoperability Profile
only after its pinned image is observed to enforce the guarantees the digest-
pinned Radicale 3.7.8 reference provides. This record observes Baïkal 0.10.1
and Nextcloud 34.0.3 against that bar and re-observes the reference. It records
behavior of these exact images; it does not infer support from product names,
DAV advertisements, or other versions. The
[sanitized evidence](observations/move-interoperability-2026-09-24.jsonl)
contains one record per runtime plus the MCP lane summary.

## Revisions and environment

Base commit: `17cb544630f66ef70a749527d719a57a3f5650df` with the profile change
in this record applied. Linux x64 host with 4 shared CPUs, .NET SDK 10.0.401,
Docker with the containerd image store. Every server bound to loopback with
generated credentials and disposable Calendars; each container was removed
after its lane.

| Runtime | Pinned image digest | Observed runtime |
| --- | --- | --- |
| Radicale 3.7.8 (reference) | `ghcr.io/kozea/radicale@sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80` | Python 3.14.7 |
| Baïkal 0.10.1 | `ckulka/baikal:0.10.1-apache@sha256:658e9582f8d418392b4dfe7e816835b003694c8cb54ed0ba598eb5727b58fe22` | SQLite fixture |
| Nextcloud 34.0.3 | `nextcloud:34.0.3-apache@sha256:b97df9e0e1ee3c8c6cc009cb3f12ddce915d624d543b3bb93882025fe323a407` | PHP 8.5.10, sabre/dav 4.7.0, sabre/vobject 4.5.6, SQLite |

These are the digests the RFC coverage harness already pins. Docker Hub's
anonymous pull limit was exhausted on the shared host, so the Baïkal and
Nextcloud index digests were pulled through the `mirror.gcr.io` Docker Hub
mirror and tagged locally with the Docker Hub references above; the image IDs
equal the pinned index digests. Lanes ran on `linux/amd64`.

## Method

`scripts/observations/rfc-coverage/move_preconditions.py` creates a VTODO
source Calendar and a VEVENT+VTODO destination Calendar per run, then issues raw
`MOVE` requests with a strong `If-Match`, `Overwrite: F`, and an absolute
`Destination`. Each case reads both sides back independently. Destination
names use the same opaque UID-derived form as Semantic Move.

Required cases decide a profile:

- `success_between_calendars`: 201 or 204, source absent, destination present
  with a strong Entity Tag and the source bytes unchanged.
- `overwrite_false_occupied_destination`: 412; source and occupant unchanged.
- `uid_conflict_same_kind` and `uid_conflict_cross_kind`: the destination
  Calendar already holds the UID as a VTODO or a VEVENT. Rejection must map to
  the `conflict` outcome (409, 412, or 403 with `CALDAV:no-uid-conflict`) and
  both sides must be unchanged.

Supplementary cases are recorded but do not decide a profile:

- `stale_if_match`: the source changed after its Entity Tag was read.
- `success_same_calendar_rename`: Exact Move may rename within one Calendar.
- `scheduling_side_effects_absent` (Nextcloud only): an organizer Event with a
  local attendee moves between Calendars while automatic scheduling is
  advertised. It compares every attendee Calendar and scheduling-inbox member
  and the server's iMIP send attempts before and after the `MOVE`.

## Results

| Case | Radicale 3.7.8 | Baïkal 0.10.1 | Nextcloud 34.0.3 |
| --- | --- | --- | --- |
| Move between Calendars (required) | 201, bytes preserved | 201, bytes preserved | 201, bytes preserved |
| `Overwrite: F` occupied destination (required) | 412 | 412 | 412 |
| Same-kind UID conflict (required) | 409 `CALDAV:no-uid-conflict` | **201, duplicate UID committed** | 409 `CALDAV:no-uid-conflict` |
| Cross-kind UID conflict (required) | 409 `CALDAV:no-uid-conflict` | **201, duplicate UID committed** | 409 `CALDAV:no-uid-conflict` |
| Stale `If-Match` | **201, stale source moved** | 412 | 412 |
| Rename within one Calendar | 201 | 403 `Permission denied to rename file` | 403 `Permission denied to rename file` |
| Scheduling side effects of MOVE | Not advertised | Not observed | None: no attendee change, no iMIP attempt |
| Profile decision | Reference, unchanged | **Not promoted** | Promoted as `nextcloud-34.0.3` |

### Nextcloud 34.0.3

Every required case passed, and Nextcloud also rejects a stale `If-Match` on
`MOVE`. It rejects a rename within one Calendar with HTTP 403 and leaves both
hrefs unchanged. The runtime therefore records that the profile does not commit
same-Calendar Move: under `nextcloud-34.0.3`, Exact Move to a destination in
the source Calendar returns `unsupported_capability` with `not_attempted`
before any resource read or write. Semantic Move never targets the source
Calendar. The `PUT` of the organizer Event delivered a `REQUEST` to the local
attendee and attempted one iMIP message. The following `MOVE` changed no
attendee resource and attempted no further iMIP delivery. The sabre/dav 4.7.0
scheduling plugin explicitly skips its unbind handler for `MOVE`.

A published candidate
(MCP `a1eee78d9afe6a55d3846b60ed5406b2b23a2100a90e935a02dff30bb73e4a78`,
Core `5d7076ce96fdc94e1f78a40c5787475b04d2f6fdaaa2b3e9f7338898fe140390`) ran
the persistent stdio `functional.py` lane with
`CALDAV_INTEROPERABILITY_PROFILE=nextcloud-34.0.3` and an explicit Calendar
scope on a fresh disposable user. All 27 catalog tools were called (90 calls,
22 independent authoritative checks). Both Semantic Moves (To-dos to archive
and back) returned `success`/`committed` in 239.6 and 238.4 ms and were
confirmed by independent reads. The same-Calendar Exact Move returned
`unsupported_capability`/`not_attempted` in 110.9 ms.

### Baïkal 0.10.1

Baïkal commits a `MOVE` whose UID already exists in the destination Calendar,
for both the same and a different component kind. That leaves two Calendar
Object Resources with one UID in one Calendar, which the server-authoritative
Move contract delegates to `CALDAV:no-uid-conflict`. Baïkal is not promoted.
Its `If-Match` and `Overwrite: F` enforcement do not compensate, because no
client-side scan replaces the atomic UID decision (ADR 0006). Semantic and
Exact Move stay `unsupported_capability` on Baïkal.

### Radicale 3.7.8 reference

The reference passes every required case but does not evaluate `If-Match` on
`MOVE`: the stale source was moved with 201. `radicale/app/move.py` in the
pinned image contains no `If-Match` check, and `strict_preconditions` applies
only to `PUT`, so all three conformance variants share this behavior. The
existing conformance witness for a stale revision stops before dispatch
because the Move module re-reads the source revision. A change between that
read and the `MOVE` is not server-guarded on Radicale: the newer content is
moved and bilateral reconciliation reports `fidelity_failure` after the commit
instead of `not_committed`. The
README no longer claims atomic `If-Match` enforcement for the verified
profiles. The Radicale profile itself is unchanged by this record.

## Decisions

- The Baïkal and Nextcloud lanes stay out of `scripts/run-test-suite.sh` and
  CI. The Nextcloud image is 2.2 GB unpacked and installs itself per fixture; the shared runners already carry three Radicale variants. This
  observation harness, the unit tests for the profile registry and Move
  authorization, and the contract test for the pinned profile are the evidence.
  Changing a pinned digest requires rerunning this harness and a new dated
  record.
- The profile contract lives at
  [`contracts/0.3.0/nextcloud-34.0.3-profile.json`](../contracts/0.3.0/nextcloud-34.0.3-profile.json).
  Historical contract directories remain immutable.

## Reproduction

```bash
python3 scripts/observations/rfc-coverage/infra.py up /tmp/caldav-rfc-run
python3 scripts/observations/rfc-coverage/infra.py seed /tmp/caldav-rfc-run --count 24
python3 scripts/observations/rfc-coverage/move_preconditions.py /tmp/caldav-rfc-run \
  --output /tmp/caldav-rfc-run/move-preconditions.json
python3 scripts/observations/rfc-coverage/compat.py baikal /tmp/caldav-rfc-baikal \
  --telemetry-root /tmp/caldav-rfc-run
python3 scripts/observations/rfc-coverage/infra.py seed /tmp/caldav-rfc-baikal --count 24
python3 scripts/observations/rfc-coverage/move_preconditions.py /tmp/caldav-rfc-baikal \
  --output /tmp/caldav-rfc-baikal/move-preconditions.json
python3 scripts/observations/rfc-coverage/infra.py down /tmp/caldav-rfc-baikal
python3 scripts/observations/rfc-coverage/compat.py nextcloud /tmp/caldav-rfc-nextcloud \
  --telemetry-root /tmp/caldav-rfc-run
python3 scripts/observations/rfc-coverage/compat.py nextcloud /tmp/caldav-rfc-nc-attendee \
  --fresh-user-from /tmp/caldav-rfc-nextcloud
python3 scripts/observations/rfc-coverage/compat.py nextcloud /tmp/caldav-rfc-nc-probe \
  --fresh-user-from /tmp/caldav-rfc-nextcloud
python3 scripts/observations/rfc-coverage/move_preconditions.py /tmp/caldav-rfc-nc-probe \
  --output /tmp/caldav-rfc-nc-probe/move-preconditions.json \
  --attendee-root /tmp/caldav-rfc-nc-attendee
python3 scripts/observations/rfc-coverage/compat.py nextcloud /tmp/caldav-rfc-nc-mcp \
  --fresh-user-from /tmp/caldav-rfc-nextcloud --move-profile
python3 scripts/observations/rfc-coverage/infra.py seed /tmp/caldav-rfc-nc-mcp --count 24
python3 scripts/observations/rfc-coverage/functional.py /tmp/caldav-rfc-nc-mcp \
  /tmp/caldav-rfc-run/candidate/DotnetAgents.CalDav.Mcp.dll \
  --output /tmp/caldav-rfc-run/nextcloud-profile-live --exact-scope
python3 scripts/observations/rfc-coverage/infra.py down /tmp/caldav-rfc-nextcloud
python3 scripts/observations/rfc-coverage/infra.py down /tmp/caldav-rfc-run
```

The scheduling case sets disposable `example.invalid` email addresses on the two
fixture users inside the owned Nextcloud container. Fresh users avoid
Nextcloud's default ten-Calendar-per-user hourly creation limit.
