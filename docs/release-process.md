# Release validation

Use the checks below to validate behavior and the final NuGet package before
publication.

## Behavioral gates

Pull requests and releases run the Release build, all unit and integration
tests, the 90% line and 85% branch coverage thresholds, digest-pinned Radicale
conformance in the baseline, strict-preconditions, and alternate-time-zone
variants, complete-test-result validation, and Slopwatch.
Run them from the repository root:

```bash
dotnet tool restore
dotnet restore
dotnet build -c Release --no-restore
bash scripts/run-test-suite.sh
dotnet tool run slopwatch analyze --config .slopwatch/slopwatch.json --fail-on warning
```

The runner uses `scripts/verify-test-artifacts.sh` to check the manifest's TRX
results and coverage files. At the end of the suite it also runs
`scripts/verify-test-source-policy.py` to reject disabled, quarantined, or flaky
tests. The MCP-over-stdio integration tests exercise protocol behavior.

## Final-package gate

After the tag-versioned build is packed, and before upload or publication,
`scripts/verify-release-package.sh <version> <artifact-directory>` verifies:

- exactly one `.nupkg` and one `.snupkg`, with the requested package ID and version;
- the first-party executable payload, PDBs, README, both MCP metadata copies, and bundled CalDAV Agent Skill;
- matching generated release versions and stdio package identity in the metadata;
- byte-for-byte identity between the bundled skill and its repository source;
- absence of repository-only documentation from the package;
- installation of the exact package with the artifact directory as the only NuGet source;
- MCP initialization and the default `tools/list` result from the installed executable.

The smoke test uses deterministic dummy configuration and does not call a
CalDAV server. The client test runs from the repository, but the server process
is the executable installed from the final `.nupkg` into a temporary tool path.

## Package contents

The NuGet package contains only files with a package-consumer purpose:

- the .NET tool payload for execution;
- `README.md` for the NuGet package page;
- root and tool-path `.mcp/server.json` for MCP discovery and installed-tool metadata;
- `skills/caldav-calendars/SKILL.md` for harness-neutral Agent Skills discovery or installation;
- PDBs and the symbol package for SourceLink and debugging.

Versioned contracts, schemas, interoperability profiles, compatibility
matrices, and ADRs remain in the repository. Published release
history is maintained through GitHub Releases.
