[dotnet-skills]|IMPORTANT: Prefer retrieval-led reasoning over pretraining for any .NET work.
|flow:{skim repo patterns -> consult dotnet-skills by name -> implement smallest-change -> note conflicts}
|route:
|akka:{akka-net-best-practices,akka-net-testing-patterns,akka-hosting-actor-patterns,akka-net-aspire-configuration,akka-net-management}
|csharp:{modern-csharp-coding-standards,csharp-concurrency-patterns,api-design,type-design-performance}
|aspnetcore-web:{aspire-integration-testing,aspire-configuration,aspire-service-defaults,mailpit-integration,mjml-email-templates}
|data:{efcore-patterns,database-performance}
|di-config:{microsoft-extensions-configuration,dependency-injection-patterns}
|testing:{testcontainers-integration-tests,playwright-blazor-testing,snapshot-testing,verify-email-snapshots,playwright-ci-caching}
|dotnet:{dotnet-project-structure,dotnet-local-tools,package-management,serialization,dotnet-devcert-trust,ilspy-decompile,OpenTelemetry-NET-Instrumentation}
|quality-gates:{dotnet-slopwatch,crap-analysis}
|meta:{marketplace-publishing,skills-index-snippets}
|agents:{akka-net-specialist,docfx-specialist,dotnet-benchmark-designer,dotnet-concurrency-specialist,dotnet-performance-analyst,roslyn-incremental-generator-specialist}

# CalDAV Tasks MCP Server

MCP server for CalDAV VTODO management, distributed via `dnx`. Built with .NET 10 and ModelContextProtocol SDK.

## Build & Test

```bash
# Restore tools first (required for slopwatch, reportgenerator)
dotnet tool restore

# Build
dotnet build -c Release

# Run all tests (unit + integration)
dotnet test

# Run with coverage (outputs to TestResults/)
dotnet test --settings coverage.runsettings --collect:"XPlat Code Coverage"

# Generate coverage report
dotnet reportgenerator -reports:"TestResults/**/coverage.cobertura.xml" -targetdir:"coverage-report" -reporttypes:Cobertura

# Verify coverage thresholds (90% line, 85% branch)
bash scripts/verify-coverage.sh coverage-report 0.90 0.85

# Run Slopwatch quality gates
slopwatch analyze --config .slopwatch/slopwatch.json --fail-on warning
```

## Project Structure

- **`src/DotnetAgents.CalDav.Core/`** — Domain models, abstractions, and CalDAV client
  - `Abstractions/` — `ITaskService`, `ICalDavClient` interfaces
  - `Models/` — `TaskItem`, `TaskList`, enums (immutable records with `with` semantics)
  - `Services/` — `TaskService` implementation (thin wrapper over client)
  - `Internal/` — `CalDavClient`, WebDAV protocol handling, iCalendar (Ical.Net) parsing

- **`src/DotnetAgents.CalDav.Mcp/`** — MCP server entry point and tool definitions
  - `Tools/` — MCP tool classes with `[McpServerTool]` attributes:
    - `TaskListTools` — list task lists
    - `TaskQueryTools` — list/get tasks
    - `TaskMutationTools` — create/update/complete/delete
    - `ChatTaskTools` — chat-oriented wrappers (list-name resolution)
  - `Hosting/` — `CalDavMcpRunner`, DI configuration
  - `Program.cs` — Entry point, delegates to runner

- **`tests/`** — Three test projects:
  - `DotnetAgents.CalDav.Core.Tests.Unit/` — Unit tests with NSubstitute
  - `DotnetAgents.CalDav.Mcp.Tests.Unit/` — MCP tool unit tests
  - `DotnetAgents.CalDav.IntegrationTests/` — Integration tests using TestContainers (Radicale)

## Architecture

Layered flow:
```
MCP Tools → ITaskService → CalDavClient → HttpClient + Ical.Net
```

Key patterns:
- **Immutable models** — Use `with` expressions for updates (e.g., `task with { Status = Completed }`)
- **Optimistic concurrency** — ETag-based; always pass ETag on updates/deletes
- **Chat-oriented vs href-based tools** — Tools with "by summary" or "in list" naming accept display names; others require absolute hrefs
- **Time abstraction** — `TimeProvider` injected for testability (not DateTimeOffset.Now)

## Tool Naming Conventions

The MCP exposes two styles of tools:

1. **Href-based** (for known IDs): `create_task`, `update_task`, `complete_task`, `delete_task` — require absolute hrefs
2. **Chat-oriented** (for user-facing workflows): `caldav_add_task`, `caldav_complete_task_by_summary`, etc. — accept display names and resolve hrefs internally

When adding new tools, follow the existing pattern: href-based for raw API access, chat-oriented wrappers for common user workflows.

## Testing Notes

- **Unit tests** — Fast, use NSubstitute for mocks, Shouldly for assertions
- **Integration tests** — Use TestContainers with Radicale (Docker required). Fixture in `Fixtures/RadicaleFixture.cs` spins up container per test class
- **Coverage thresholds** — 90% line, 85% branch enforced in CI
- **CRAP analysis** — Cyclomatic complexity limit 10 (CA1502); check coverage report for hotspots

## Quality Gates

CI enforces (in order):
1. Build with `TreatWarningsAsErrors=true`
2. CA1502 cyclomatic complexity ≤ 10
3. Test pass with coverage collection
4. Coverage thresholds (90% line, 85% branch)
5. Slopwatch analysis (SW001-SW006 rules)

## Key Dependencies

- `ModelContextProtocol` 1.2.0 — MCP SDK
- `Ical.Net` 5.2.1 — iCalendar parsing/generation
- `Testcontainers` 4.11.0 — Integration test infrastructure
- `xunit.v3` 3.2.2 — Testing framework

## Distribution

Published as `DotnetAgents.CalDav.Mcp` on NuGet. Installed via:
```bash
dnx --yes DotnetAgents.CalDav.Mcp
```

Requires environment variables: `CALDAV_URL`, `CALDAV_USERNAME`, `CALDAV_PASSWORD`
