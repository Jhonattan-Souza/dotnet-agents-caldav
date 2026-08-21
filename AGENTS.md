# CalDAV Calendars MCP Server

.NET 10 stdio MCP server for CalDAV Events and To-dos, packaged as the `dotnet-agents-caldav` `dnx` tool.

## Commands

- Restore local tools and NuGet packages separately; `dotnet tool restore` does not restore project dependencies.

```bash
dotnet tool restore
dotnet restore
```

- Run one unit-test project, or focus a class/method with the native xUnit MTP filters:

```bash
dotnet test --project tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj -c Release
dotnet test --project tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj -c Release --filter-class '*TypeName'
dotnet test --project tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj -c Release --filter-method '*TypeName.MethodName'
```

- The integration project requires Docker. Its `RadicaleCollection` shares one digest-pinned official Radicale 3.7.8 Testcontainer and seeds Event and To-do Calendars.
- Match CI in this order:

```bash
dotnet build -c Release --no-restore
bash scripts/run-test-suite.sh
dotnet tool run slopwatch analyze --config .slopwatch/slopwatch.json --fail-on warning
```

- `run-test-suite.sh` creates a fresh temporary artifact directory, removes it after complete success, and prints and preserves it after failure. A caller-provided `--artifacts-dir` must be empty and remains caller-owned.
- Coverage aggregation accepts exactly one current root-level Cobertura and OpenCover report for each test project; nested or historical reports are never merged.
- CI runs for every pull request and enforces warnings as errors, method complexity at most 10, 90% line coverage, 85% branch coverage, both pinned Radicale profiles, and complete test results with no disabled evidence.

## Boundaries

- Runtime flow is `MCP tools -> ICalendarService -> CalendarService -> CalDavClient -> HttpClient`; `CalendarService` stays thin. WebDAV request/response logic belongs under `Core/Internal/Xml`, and iCalendar mapping belongs under `Core/Internal/Ical`.
- `Program.cs` maps `CALDAV_*` environment variables, then delegates startup to `CalDavMcpRunner` and `CalDavHostBuilder`; keep startup testable through those types rather than adding logic to top-level statements.
- The default host exposes a 17-tool semantic catalog, including `todos.complete` and `todos.query`. It contains no legacy aliases. Create collisions are decided by conditional PUT without collection enumeration. `CALDAV_EXPOSE_EXACT_TOOLS=true` independently enables the four exact Calendar resource tools.
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
- Feature pull requests update the live catalog under `src/DotnetAgents.CalDav.Mcp/Contracts` and the `Unreleased` changelog. Create versioned contract snapshots, migration guides, and release notes only during an explicitly scoped release preparation after the selected features have merged.
- When environment variables or packaged MCP metadata change, update `.mcp/server.json` and `McpMetadataTests` alongside the runtime mapping. The NuGet package is produced only from `src/DotnetAgents.CalDav.Mcp/DotnetAgents.CalDav.Mcp.csproj`.
