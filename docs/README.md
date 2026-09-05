# Developer documentation

Use [Release validation](release-process.md) for build, test, coverage, and
package checks. Follow the [test suite manifest](../scripts/test-suite-manifest.json)
for the current projects, conformance variants, and result artifacts.

For public model terminology, read [CONTEXT.md](../CONTEXT.md). The
[live MCP catalog](../src/DotnetAgents.CalDav.Mcp/Contracts/mcp-tool-catalog.json)
describes the shipped tool surface. Find behavioral regressions in
[Core tests](../tests/DotnetAgents.CalDav.Core.Tests.Unit),
[MCP tests](../tests/DotnetAgents.CalDav.Mcp.Tests.Unit), and
[integration tests](../tests/DotnetAgents.CalDav.IntegrationTests).

## Design decisions

- [Compact To-do queries](adr/0001-compact-todo-query.md)
- [Conditional resource creation](adr/0002-authoritative-conditional-create.md)
- [Calendar creation module](adr/0003-deep-calendar-creation-module.md)
- [Query module and immutable result snapshots](adr/0004-deep-query-result-snapshot-module.md)
- [Configured temporal evaluation context](adr/0005-configured-temporal-evaluation-context.md)
- [Server-authoritative Move](adr/0006-server-authoritative-semantic-move.md)

## Historical performance observations

The reports below record measurements at their stated revisions. Use their
reproduction instructions to investigate those results; run the current suite
to establish whether a checkout passes.

- [August 21 load-test baseline](performance-load-test-2026-08-21.md) and
  [August 23 follow-up](performance-load-test-2026-08-23.md)
- [Discovery reuse](performance-discovery-reuse-2026-08-23.md)
- [Entity query snapshots](performance-query-snapshots-2026-08-23.md),
  [Occurrence query snapshots](performance-occurrence-query-snapshots-2026-08-23.md),
  and [To-do query snapshots](performance-todo-query-snapshots-2026-08-23.md)
- [Direct GET compatibility](performance-direct-get-compatibility-2026-08-23.md)
- [Temporal context](performance-temporal-context-2026-08-23.md)
- [Server-authoritative Move](performance-server-authoritative-move-2026-08-23.md)
