# CalDAV Tasks MCP Server

.NET 10 stdio MCP server for CalDAV VTODOs, packaged as the `dotnet-agents-caldav` `dnx` tool.

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

- The integration project requires Docker. Its `RadicaleCollection` shares one `tomsquest/docker-radicale` Testcontainer and seeds `Tasks`, `Shopping`, and `Work` collections.
- Match CI in this order; `--results-directory TestResults` is required because the report command reads the root `TestResults/` tree:

```bash
dotnet build -c Release --no-restore
dotnet test -c Release --settings coverage.runsettings --collect:"XPlat Code Coverage" --results-directory TestResults
dotnet reportgenerator -reports:"TestResults/**/coverage.cobertura.xml" -targetdir:"coverage-report" -reporttypes:Cobertura -assemblyfilters:"+DotnetAgents.CalDav.Core;+DotnetAgents.CalDav.Mcp;-*Tests*;-xunit*;-testhost*"
bash scripts/verify-coverage.sh coverage-report 0.90 0.85
dotnet tool run slopwatch analyze --config .slopwatch/slopwatch.json --fail-on warning
```

- CI enforces warnings as errors, method complexity at most 10, 90% line coverage, and 85% branch coverage. Markdown-only pull requests do not trigger CI.

## Boundaries

- Runtime flow is `MCP tools -> ITaskService -> TaskService -> CalDavClient -> HttpClient`; `TaskService` stays thin. WebDAV request/response logic belongs under `Core/Internal/Xml`, and iCalendar mapping belongs under `Core/Internal/Ical`.
- `Program.cs` maps `CALDAV_*` environment variables, then delegates startup to `CalDavMcpRunner` and `CalDavHostBuilder`; keep startup testable through those types rather than adding logic to top-level statements.
- The default host exposes only `TaskListTools` and `ChatTaskTools`. `CALDAV_EXPOSE_ADVANCED_TOOLS=true` additionally registers href-based `TaskQueryTools` and `TaskMutationTools`.
- `.opencode/opencode.jsonc` launches the published NuGet tool through `dnx`; it does not run the current checkout. Do not treat that configuration as source-level end-to-end validation.

## Invariants

- Keep console providers disabled. Stdout is the JSON-RPC transport, and integration tests require a valid server run to leave both stdout and stderr clean; only startup validation failures write human-readable stderr.
- `TaskItem` and `TaskList` are immutable records. Modify them with `with`, preserve fetched ETags on updates/deletes, and use injected `TimeProvider` for completion timestamps.
- Chat tools resolve explicit display names first, then `CALDAV_DEFAULT_TASK_LIST`; they never infer a list from task content. Summary-based mutations must return `not_found` or `ambiguous` unless exactly one task matches.
- Href-based tools are the advanced/raw surface. Keep their descriptions requiring an explicitly provided or confirmed absolute href; normal chat flows use list-name tools.
- Package versions are centralized in `Directory.Packages.props`; project files keep versionless `PackageReference` entries.
- For Ical.Net, `.ics`, VTODO, or recurrence changes, load the repo-local `ical-net` skill before editing.

## Packaging

- `src/DotnetAgents.CalDav.Mcp/.mcp/server.json` is packed metadata. Keep its source versions at `0.0.0`; the `v*` release workflow replaces both versions from the tag.
- When environment variables or packaged MCP metadata change, update `.mcp/server.json` and `McpMetadataTests` alongside the runtime mapping. The NuGet package is produced only from `src/DotnetAgents.CalDav.Mcp/DotnetAgents.CalDav.Mcp.csproj`.
