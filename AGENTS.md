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

- `run-test-suite.sh` uses `--no-build --no-restore`; match pull-request CI in this order:

```bash
dotnet build -c Release --no-restore
bash scripts/run-test-suite.sh
dotnet tool run slopwatch analyze --config .slopwatch/slopwatch.json --fail-on warning
```

- `run-test-suite.sh` creates a fresh temporary artifact directory, removes it after complete success, and prints and preserves it after failure. A caller-provided `--artifacts-dir` must be empty and remains caller-owned.
- Coverage aggregation accepts exactly one current root-level Cobertura and OpenCover report for each test project; nested or historical reports are never merged.
- Pull-request and release gates enforce warnings as errors, method complexity at most 10, 90% line coverage, 85% branch coverage, complete test results with no skipped/explicit/quarantined/flaky evidence, and baseline, strict-preconditions, and alternate-time-zone variants of one digest-pinned Radicale 3.7.8 profile.

## Invariants

- Keep console providers disabled. Stdout is the JSON-RPC transport; a valid no-request EOF run leaves both streams clean, and only startup validation failures write human-readable stderr. OTLP remains opt-in, allowlisted, 250 ms bounded, and isolated so collector failures cannot change results or stdio.
- Exact tools are the independently gated raw surface. Keep their descriptions requiring explicitly provided absolute hrefs and complete caller-authored resources; semantic revision-bound mutations use the frozen contract and MRTR where required.
- For public model or schema vocabulary, use `CONTEXT.md`.
- Package versions are centralized in `Directory.Packages.props`; project files keep versionless `PackageReference` entries.

## Packaging

- `src/DotnetAgents.CalDav.Mcp/.mcp/server.json` is packed metadata. Keep its source versions at `0.0.0`; the `v*` release workflow replaces both versions from the tag.
- Feature pull requests update the live catalog under `src/DotnetAgents.CalDav.Mcp/Contracts` and the `Unreleased` changelog. Create versioned contract snapshots, migration guides, and release notes only during an explicitly scoped release preparation after the selected features have merged.
- When environment variables or packaged MCP metadata change, update the runtime mapping, README table, live catalog, `.mcp/server.json`, and `McpMetadataTests` together. The NuGet package is produced only from `src/DotnetAgents.CalDav.Mcp/DotnetAgents.CalDav.Mcp.csproj`.
