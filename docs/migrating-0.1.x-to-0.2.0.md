# Migrating from 0.1.x to 0.2.0

Version 0.2.0 is a deliberate breaking replacement for the task-specific contract. The NuGet package remains `dotnet-agents-caldav` and the MCP server remains `io.github.jhonattan-souza/dotnet-agents-caldav`, but there is no legacy mode, alias catalog, or automatic configuration translator.

The upgrade changes client configuration, tool names, schemas, selection, and write safety. It does not rewrite Calendar Object Resources. No CalDAV data migration is required or performed during upgrade or rollback.

## Change the configuration

Before, with 0.1.4:

```json
{
  "command": "dnx",
  "args": ["--yes", "dotnet-agents-caldav@0.1.4"],
  "env": {
    "CALDAV_URL": "https://caldav.example.com",
    "CALDAV_USERNAME": "user",
    "CALDAV_PASSWORD": "secret",
    "CALDAV_TASK_LISTS": "/user/tasks/,/user/work/",
    "CALDAV_DEFAULT_TASK_LIST": "Tasks",
    "CALDAV_EXPOSE_ADVANCED_TOOLS": "false"
  }
}
```

After, with 0.2.0:

```json
{
  "command": "dnx",
  "args": ["--yes", "dotnet-agents-caldav@0.2.0"],
  "env": {
    "CALDAV_URL": "https://caldav.example.com",
    "CALDAV_USERNAME": "user",
    "CALDAV_PASSWORD": "secret",
    "CALDAV_CALENDAR_HREFS": "https://caldav.example.com/user/tasks/,https://caldav.example.com/user/work/",
    "CALDAV_DEFAULT_TODO_CALENDAR_NAME": "Tasks",
    "CALDAV_DEFAULT_EVENT_CALENDAR_NAME": "Work",
    "CALDAV_EXPOSE_EXACT_TOOLS": "false"
  }
}
```

`CALDAV_URL`, `CALDAV_USERNAME`, and `CALDAV_PASSWORD` are unchanged. `CALDAV_URL` may be an absolute server endpoint or an absolute Calendar Home URL. A configured Calendar Home is used directly only after its DAV `calendar-home-set` property proves that exact canonical URL; otherwise discovery follows the server endpoint through well-known, principal, and Calendar Home resolution. The removed names are not interpreted by 0.2.0.

| Removed 0.1.x setting | 0.2.0 replacement | Meaning |
| --- | --- | --- |
| `CALDAV_TASK_LISTS` | `CALDAV_CALENDAR_HREFS` | Optional comma-separated allowlist of exact canonical Calendar hrefs; omission admits every discovered Calendar. |
| `CALDAV_DEFAULT_TASK_LIST` | `CALDAV_DEFAULT_TODO_CALENDAR_NAME` | Optional exact Calendar Name used only when a To-do operation omits selection. |
| none | `CALDAV_DEFAULT_EVENT_CALENDAR_NAME` | Independent optional Event default. |
| `CALDAV_EXPOSE_ADVANCED_TOOLS` | `CALDAV_EXPOSE_EXACT_TOOLS` | Optional gate for the four complete-resource tools; false by default. |

Event and To-do defaults are independent. An explicit missing, ambiguous, incompatible, or out-of-scope selection never falls back to either default. Calendar Names are display metadata; canonical hrefs are Calendar identity.

## Migrate every tool workflow

These are workflow changes, not aliases or mechanical renames.

| Removed 0.1.x tool | 0.2.0 workflow |
| --- | --- |
| `list_task_lists` | `calendars.list` |
| `show_tasks` | `calendar_entities.query` with `entityKinds: ["todo"]` and default or selected scope |
| `find_tasks` | `calendar_entities.query` with To-do kind and explicit scope; paginate and select candidates from the returned projections |
| `list_tasks` | `calendar_entities.query` with To-do kind and an href-selected Calendar |
| `get_task` | `calendar_resources.get` with the absolute resource href |
| `add_task` | `todos.create` with the To-do default or a selected Calendar |
| `create_task` | `todos.create` with an href-selected Calendar |
| `update_task` | `todos.patch` with the complete current revision reference and explicit patch operations |
| `complete_task` | `todos.complete` with the complete current revision reference |
| `complete_task_by_summary` | Query, require exactly one reviewed resource revision, then call `todos.complete` |
| `delete_task` | `calendar_resources.delete` with the complete current revision reference and MRTR confirmation |
| `delete_task_by_summary` | Query, require exactly one reviewed resource revision, then call `calendar_resources.delete` with MRTR confirmation |

The 0.2.0 schemas are strict, closed, and camel-case. Calendar Scope is explicit, queries are bounded and paginated, and normal results carry authoritative `structuredContent`. Existing-resource writes require a strong Entity Tag. Scalar patches distinguish `set` from `clear`; collection patches distinguish `addRemove` from destructive `replaceAll`. Recurrence mutations name their original identity and scope. High-impact writes use MCP Multi Round-Trip Requests (MRTR).

Summary, Calendar Name, current start, and result position are never mutation identities. Query by those values only to find candidates, then require exactly one reviewed snapshot and carry its `href`, `entityUid`, `entityKind`, and exact strong `entityTag` into the write.

## To-do recipes

The snippets below show tool arguments. Use the schema returned by `tools/list` as the runtime authority.

### List persisted To-dos

Call `calendar_entities.query` for the To-do default:

```json
{
  "scope": { "mode": "default" },
  "entityKinds": ["todo"],
  "pageSize": 50
}
```

For one Calendar, use the complete selected scope below. Searching all Calendars requires `{ "mode": "all" }`; never broaden scope implicitly. Follow `nextCursor` until the requested bounded result is complete.

```json
{
  "scope": {
    "mode": "selected",
    "calendar": {
      "by": "href",
      "href": "https://caldav.example.com/user/work/"
    }
  },
  "entityKinds": ["todo"],
  "pageSize": 50
}
```

### Create a To-do

Call `todos.create`:

```json
{
  "destination": { "mode": "default" },
  "entity": {
    "kind": "todo",
    "fields": {
      "summary": "Submit expense report",
      "due": { "kind": "date", "value": "2026-08-31" },
      "priority": 1
    }
  }
}
```

Creation is collision-safe and returns a verified snapshot on success. Copy its complete `snapshot.entityRevision` into the next revision-bound write.

### Patch one reviewed To-do

Query or call `calendar_resources.get`, then copy the complete current `snapshot.entityRevision` into the next call's `snapshot`. This example changes the summary while preserving omitted fields:

```json
{
  "snapshot": {
    "href": "https://caldav.example.com/user/tasks/uid-123.ics",
    "entityUid": "uid-123",
    "entityKind": "todo",
    "entityTag": "\"revision-7\""
  },
  "target": { "scope": "master" },
  "patch": {
    "scalars": [
      { "field": "summary", "operation": "set", "value": "Submit approved expense report" }
    ]
  }
}
```

An unchanged semantic patch returns `outcome: "no_change"` without writing. `replaceAll`, recurrence-definition changes, this-and-future, and entire-set patches require MRTR confirmation.

### Complete one To-do

Call `todos.complete` with the latest To-do revision:

```json
{
  "snapshot": {
    "href": "https://caldav.example.com/user/tasks/uid-123.ics",
    "entityUid": "uid-123",
    "entityKind": "todo",
    "entityTag": "\"revision-8\""
  }
}
```

For a recurring To-do, also supply the exact original `recurrenceIdentity` returned by the Occurrence query. The server records the completion instant; the caller does not provide one. Completion never means stopping future recurrence.

### Delete one reviewed resource

Call `calendar_resources.delete` with the latest revision:

```json
{
  "revision": {
    "href": "https://caldav.example.com/user/tasks/uid-123.ics",
    "entityUid": "uid-123",
    "entityKind": "todo",
    "entityTag": "\"revision-9\""
  }
}
```

The first round returns MCP `input_required` with a revision-bound preview and opaque `requestState`. Present the preview, collect the requested confirmation, then continue the same tool call with that `requestState` and `inputResponses`. The server refetches and revalidates before writing. Decline, expiry, mismatch, or a changed revision writes nothing. Success includes a deletion receipt and verified absence.

## Interpret structured outcomes

- `isError: false` includes `success`, `no_change`, and declined confirmation. Inspect `mutationState`; a successful write is `committed`.
- Expected failures use `isError: true` and a schema-valid `code`, `category`, safe `message`, `retryable`, and `phase`. Mutation failures also report `mutationState` as `not_attempted`, `not_committed`, `committed`, or `unknown`.
- `conflict` means the Entity Tag is stale; review the authorized current snapshot and form a new intent. Never replay the old write automatically.
- `fidelity_failure`, `committed_but_unverified`, `committed_but_concurrency_unavailable`, and `indeterminate` describe post-dispatch truth. Report them exactly; do not present them as ordinary success.
- `opaque_resource`, `temporal_unresolved`, `recurrence_unevaluable`, and `unsupported_capability` are deliberate fail-closed limitations, not invitations to guess or regenerate content.

## Verify the deployment

Perform these checks in order after changing the package and environment:

1. Pin `dotnet-agents-caldav@0.2.0`, rename every removed setting, and restart the MCP client/server process.
2. Run MCP discovery and `tools/list`. The default catalog must contain the documented 16 semantic tools in deterministic order and no 0.1.x names.
3. Confirm `CALDAV_EXPOSE_EXACT_TOOLS=false` hides all four exact tools. In a separately authorized test, set it to `true`, restart, and confirm the four exact tools are appended.
4. Call `calendars.list`. Verify the configured href scope plus Event and To-do support. Resolve duplicate or missing names before writes.
5. Run `calendar_entities.query` with `scope: { "mode": "default" }` and `entityKinds: ["todo"]`, then repeat with `entityKinds: ["event"]`. Verify that each query resolves to its independently configured Calendar href, and confirm structured results and pagination.
6. On a disposable test resource, create one To-do and one Event, read back their verified snapshots, then perform one revision-bound scalar patch or To-do completion. Confirm the returned strong Entity Tag changes.
7. If validating deletion, use a disposable resource and complete the MRTR preview/confirmation flow. Confirm the deletion receipt and verified absence.
8. Stop the process normally. A valid stdio run must leave stdout as JSON-RPC transport and stderr clean.

The verified interoperability profile is the official Radicale 3.7.8 image pinned by OCI digest in `contracts/0.2.0/radicale-3.7.8-profile.json`. Other CalDAV servers remain unverified even when negotiated capabilities permit operation.

## Roll back to 0.1.4

Rollback restores the old client contract; it does not restore or transform Calendar data.

1. Change the package reference to the exact version `dotnet-agents-caldav@0.1.4`.
2. Restore `CALDAV_TASK_LISTS`, `CALDAV_DEFAULT_TASK_LIST`, and, if needed, `CALDAV_EXPOSE_ADVANCED_TOOLS`. Remove the 0.2.0-only replacement settings.
3. Keep `CALDAV_URL`, `CALDAV_USERNAME`, and `CALDAV_PASSWORD` unchanged.
4. Restart the MCP client/server process.
5. Rediscover the old catalog and verify the expected 0.1.4 task tools before resuming mutations.

No CalDAV data migration is required or performed by upgrade or rollback. Existing server-resident Event and To-do resources remain in place; 0.1.4 exposes only its older To-do contract.
