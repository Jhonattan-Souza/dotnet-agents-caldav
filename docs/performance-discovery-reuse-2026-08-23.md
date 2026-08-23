# Operation-scoped discovery reuse evidence

Date: 2026-08-23  
Baseline revision: `5daf24b7434ca12359dbb8a053071bc25399a702`  
Changed-code revisions: `668116f`, `adb358e`, `4ddfad0`  
Configuration: .NET 10, Release, deterministic in-memory `ICalendarClient`, one in-scope Calendar, one Event patch

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

The deterministic fixture creates no containers, listeners, or persistent cache. The stdio regressions dispose their local listeners and MCP clients and terminate the child process in `finally`; the coordinator owns no timer, background worker, or process-lifetime entry requiring cleanup.
