# CalDAV Calendars MCP Server

.NET 10 stdio MCP server for CalDAV Events and To-dos, packaged as the `dotnet-agents-caldav` `dnx` tool.

## Commands

- Restore local tools and NuGet packages separately; `dotnet tool restore` does not restore project dependencies.

```bash
dotnet tool restore
dotnet restore
```

- Run one unit-test project, or focus a class/method with the verified VSTest filter form:

```bash
dotnet test tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj -c Release
dotnet test tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj -c Release --filter "FullyQualifiedName~TypeOrMethod"
```

- The integration project requires Docker. Its `RadicaleCollection` shares one digest-pinned official Radicale 3.7.8 Testcontainer and seeds Event and To-do Calendars.
- Match CI in this order; `--results-directory TestResults` is required because the report command reads the root `TestResults/` tree:

```bash
dotnet build -c Release --no-restore
dotnet test tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj -c Release --no-build --settings coverage.runsettings --collect:"XPlat Code Coverage" --logger "trx;LogFilePrefix=pr-core" --results-directory TestResults
dotnet test tests/DotnetAgents.CalDav.Mcp.Tests.Unit/DotnetAgents.CalDav.Mcp.Tests.Unit.csproj -c Release --no-build --settings coverage.runsettings --collect:"XPlat Code Coverage" --logger "trx;LogFilePrefix=pr-mcp" --results-directory TestResults
dotnet test tests/DotnetAgents.CalDav.IntegrationTests/DotnetAgents.CalDav.IntegrationTests.csproj -c Release --no-build --settings coverage.runsettings --collect:"XPlat Code Coverage" --logger "trx;LogFilePrefix=pr-integration" --results-directory TestResults
dotnet reportgenerator -reports:"TestResults/**/coverage.cobertura.xml" -targetdir:"coverage-report" -reporttypes:Cobertura -assemblyfilters:"+DotnetAgents.CalDav.Core;+DotnetAgents.CalDav.Mcp;-*Tests*;-xunit*;-testhost*"
bash scripts/verify-coverage.sh coverage-report 0.90 0.85
RADICALE_CONFORMANCE_VARIANT=strict-preconditions dotnet test tests/DotnetAgents.CalDav.IntegrationTests/DotnetAgents.CalDav.IntegrationTests.csproj -c Release --no-build --filter "FullyQualifiedName~RadicaleConformanceHarnessTests" --logger "trx;LogFilePrefix=strict-preconditions" --results-directory TestResults
RADICALE_CONFORMANCE_VARIANT=alternate-time-zone dotnet test tests/DotnetAgents.CalDav.IntegrationTests/DotnetAgents.CalDav.IntegrationTests.csproj -c Release --no-build --filter "FullyQualifiedName~RadicaleConformanceHarnessTests" --logger "trx;LogFilePrefix=alternate-time-zone" --results-directory TestResults
bash scripts/verify-test-results.sh TestResults
bash scripts/verify-release-evidence.sh contracts/0.2.0/requirement-evidence-catalog.md contracts/0.2.0/release-evidence-map.json TestResults
dotnet tool run slopwatch analyze --config .slopwatch/slopwatch.json --fail-on warning
```

- CI runs for every pull request and enforces warnings as errors, method complexity at most 10, 90% line coverage, 85% branch coverage, both pinned Radicale profiles, and complete mapped TRX evidence.

## Boundaries

- Runtime flow is `MCP tools -> ICalendarService -> CalendarService -> CalDavClient -> HttpClient`; `CalendarService` stays thin. WebDAV request/response logic belongs under `Core/Internal/Xml`, and iCalendar mapping belongs under `Core/Internal/Ical`.
- `Program.cs` maps `CALDAV_*` environment variables, then delegates startup to `CalDavMcpRunner` and `CalDavHostBuilder`; keep startup testable through those types rather than adding logic to top-level statements.
- The default host exposes exactly the frozen 16-tool semantic catalog, including `todos.complete`. It contains no legacy aliases. `CALDAV_EXPOSE_EXACT_TOOLS=true` independently enables the four exact Calendar resource tools.
- `.opencode/opencode.jsonc` launches the published NuGet tool through `dnx`; it does not run the current checkout. Do not treat that configuration as source-level end-to-end validation.

## Invariants

- Keep console providers disabled. Stdout is the JSON-RPC transport, and integration tests require a valid server run to leave both stdout and stderr clean; only startup validation failures write human-readable stderr.
- Calendar Object Resource snapshots are immutable. Preserve fetched strong Entity Tags on mutations, and use injected `TimeProvider` for To-do Completion timestamps.
- Calendar Names are display metadata, never identity. Defaults are independent for Events and To-dos, and explicit selection never falls back after failure.
- Exact tools are the independently gated raw surface. Keep their descriptions requiring explicitly provided absolute hrefs and complete caller-authored resources; semantic revision-bound mutations use the frozen contract and MRTR where required.
- Package versions are centralized in `Directory.Packages.props`; project files keep versionless `PackageReference` entries.
- For Ical.Net, `.ics`, VTODO, or recurrence changes, load the repo-local `ical-net` skill before editing.

## Packaging

- `src/DotnetAgents.CalDav.Mcp/.mcp/server.json` is packed metadata. Keep its source versions at `0.0.0`; the `v*` release workflow replaces both versions from the tag.
- When environment variables or packaged MCP metadata change, update `.mcp/server.json` and `McpMetadataTests` alongside the runtime mapping. The NuGet package is produced only from `src/DotnetAgents.CalDav.Mcp/DotnetAgents.CalDav.Mcp.csproj`.
