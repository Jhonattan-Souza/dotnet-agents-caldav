# Requirement-to-evidence catalog: compact To-do query contract 0.2.1

| Requirement | Implementation | Evidence |
| --- | --- | --- |
| TODO-SCOPE-001 Explicit scope and one-call compact read | `CalendarTodoTools`, `CalendarTodoQueryEngine` | `CalendarTodoToolsTests.QueryCoreAsync_ReturnsCompactOpenTodoWithRevisionTarget` |
| TODO-FILTER-002 Completion before pagination and budget | `CalendarTodoQueryEngine`, `CalendarTodoTools.CreatePage` | `CalendarTodoCompletionClassifierTests.Classify_UsesConservativeCompletionEvidence`; `CalendarTodoToolsTests.QueryCoreAsync_PaginatesWithBoundCursorAndProjectsAllFields` |
| TODO-NORMALIZE-003 Typed normalization | `CalendarTodoCompletionClassifier` | `CalendarTodoCompletionClassifierTests.Classify_ReportsContradictoryOrUnknownEvidence` |
| TODO-REVISION-004 Strong follow-up revision target | `CalendarTodoCompactItemResult` | `CalendarTodoToolsTests.QueryCoreAsync_ReturnsCompactOpenTodoWithRevisionTarget` |
| TODO-COMPAT-005 Backward compatibility | unchanged `calendar_entities.query` | `CalendarServiceTests.QueryEntitiesAsync_DefaultScopeUsesOnlyTheRequestedIndependentDefault` |
| TODO-RADICALE-006 Native server compatibility | Radicale 3.7.8 fixture and stdio harness | `CalendarMcpStdioIntegrationTests.TodoQuery_ReturnsNormalizedCompactResultsBeforePaginationOverNativeStdioAndRadicale` |

The pinned Radicale profile does not honor nested calendar-data property
projection. The implementation therefore keeps compact MCP output as the
wire-size guarantee and uses the existing bounded full-resource read path;
selective upstream projection is not claimed for this profile.
