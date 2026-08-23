# Operation-scoped discovery reuse evidence

Date: 2026-08-23
Baseline revision: `5daf24b7434ca12359dbb8a053071bc25399a702`
Implementation revisions: `668116fa5d3e0508bcaed5d224aef826a95efce5` and `adb358e4deb03f037859ba5f72cf5646648cd6a0`
Changed observation revision: `4ddfad025a1503d1abe9472b31e5b6413bd7823a`
Final #106 stack revision: `5a01659dc6c1a6c2a1fe53454bc62ee78684e9a5`
Runtime: .NET SDK `10.0.100` (`b0f34d51fc`), .NET runtime `10.0.0`, Linux `7.2.0-1-cachyos` `x86_64`, Omarchy `4.0.0`, Release, deterministic in-memory `ICalendarClient`
Server digest: N/A (no real-server fixture)
Corpus and input: one in-scope Calendar and one strong-revision Event Patch that changes Summary and Last Modified; the stdio boundary separately invokes `calendars.list` twice and reviews then confirms `calendar_resources.delete`

## Focused work count

`CalendarEntityPatchServiceTests.PatchEventAsync_ChangesOnlySummaryAndLastModifiedWithExactReviewedRevision` exercises a multi-phase semantic mutation through the public service boundary.

| Revision | Discovery acquisitions | Focused run duration |
| --- | ---: | ---: |
| Baseline | 2 | 795 ms |
| Operation-scoped coordinator | 1 | 735 ms |

The acquisition count is the acceptance signal: discovery work fell from two acquisitions to one inside the tool operation. Durations are included only as supporting observations from the same focused runs; they are not a timing threshold or general performance claim.

## Lifetime and MRTR boundaries

- `CalendarList_EachStdioToolCallPerformsFreshDiscovery` invokes the real MCP server twice in one stdio process and observes six PROPFIND requests: three per invocation. This proves the SDK activates a fresh transient tool/service target rather than retaining discovery across calls.
- `CalendarResourceDelete_NativeSdkCompletesMrtrOverStdioWithoutDocker` observes six PROPFIND requests: three for the review call and three for the confirmed call. The pre-change execution graph required nine because the confirmed call rediscovered inside its own phases.
- The response assertions exclude a credential sentinel, and the operation key contains only an opaque operation-context generation rather than a credential or reusable credential hash.

## Cleanup

The deterministic fixture creates no container, temporary Calendar, credential, listener, artifact root, or persistent cache. The stdio regressions dispose their local listeners and MCP clients and terminate the child process in `finally`; the coordinator owns no timer, background worker, or process-lifetime entry requiring cleanup. Page-assembly allocation is N/A because this change does not assemble query pages.
