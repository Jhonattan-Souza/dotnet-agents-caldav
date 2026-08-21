# Release validation

The release pipeline answers two questions before an artifact can leave the
job: whether the implementation behaves correctly, and whether the exact
NuGet package can be installed and used as an MCP server.

## Behavioral gates

Pull requests and releases run the Release build, all unit and integration
tests, the 90% line and 85% branch coverage thresholds, both digest-pinned
Radicale conformance variants, complete-test-result validation, and Slopwatch.
The MCP-over-stdio integration suite remains the executable authority for
protocol behavior. Test names and Markdown row counts are not release inputs.

`scripts/verify-test-results.sh` rejects unsuccessful TRX results and disabled,
quarantined, or flaky tests. It does not map individual tests to requirement
documents.

## Final-package gate

After the tag-versioned build is packed, and before upload or publication,
`scripts/verify-release-package.sh <version> <artifact-directory>` verifies:

- exactly one `.nupkg` and one `.snupkg`, with the requested package ID and version;
- the first-party executable payload, PDBs, README, and both MCP metadata copies;
- matching generated release versions and stdio package identity in the metadata;
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
- PDBs and the symbol package for SourceLink and debugging.

Versioned contracts, schemas, interoperability profiles, compatibility
matrices, migration guides, the changelog, release notes, ADRs, and the agent
skill remain in the repository. GitHub Releases are the distribution surface
for release history.

## Retired audit gate

The former requirement catalogs and release-evidence maps coupled contract
versions, Markdown structure, test names, TRX parsing, workflows, and package
contents. Formal requirement-to-TRX traceability is not a product requirement,
so those files and their verifier were removed. Behavioral tests and the
installed-artifact gate remain authoritative.
