using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Json.Schema;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class PackageArtifactTests
{
    [Fact]
    public void McpProductionAssembly_UsesOnlyPublicCoreBoundaries()
    {
        var repositoryRoot = RepositoryRoot();
        var mcpSources = Directory.GetFiles(
            Path.Combine(repositoryRoot, "src", "DotnetAgents.CalDav.Mcp"),
            "*.cs",
            SearchOption.AllDirectories);

        mcpSources.ShouldAllBe(path => !File.ReadAllText(path).Contains(
            "DotnetAgents.CalDav.Core.Internal",
            StringComparison.Ordinal));
        File.ReadAllText(Path.Combine(
                repositoryRoot,
                "src",
                "DotnetAgents.CalDav.Core",
                "InternalsVisibleTo.cs"))
            .ShouldNotContain("InternalsVisibleTo(\"DotnetAgents.CalDav.Mcp");
    }

    [Fact]
    public void SelectedMcpAuthority_BindsStableSourcesAndOfficialSdkVersionWithoutDraftFallback()
    {
        var repositoryRoot = RepositoryRoot();

        AssertMcpAuthorityManifest(
            File.ReadAllText(Path.Combine(repositoryRoot, "contracts", "0.2.0", "mcp-authority-manifest.json")),
            File.ReadAllText(Path.Combine(repositoryRoot, "contracts", "0.2.0", "mcp-tool-catalog.json")),
            File.ReadAllText(Path.Combine(repositoryRoot, "Directory.Packages.props")));
    }

    [Fact]
    public void ReleasePackage_ContainsEveryAdoptionArtifact()
    {
        var repositoryRoot = RepositoryRoot();
        var packageDirectory = Directory.CreateTempSubdirectory("caldav-package-test-");

        try
        {
            var result = Run(
                "dotnet",
                [
                    "pack",
                    Path.Combine(repositoryRoot, "src", "DotnetAgents.CalDav.Mcp", "DotnetAgents.CalDav.Mcp.csproj"),
                    "-c", "Release",
                    "--no-build",
                    "--no-restore",
                    "-o", packageDirectory.FullName,
                    "/p:Version=0.2.0"
                ],
                repositoryRoot);

            result.ExitCode.ShouldBe(0, result.Output);
            var packagePath = Directory.GetFiles(packageDirectory.FullName, "*.nupkg").Single();
            using var package = ZipFile.OpenRead(packagePath);
            var entries = package.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);

            entries.ShouldContain(".mcp/server.json");
            entries.ShouldContain("README.md");
            entries.ShouldContain("contracts/0.2.0/mcp-tool-catalog.json");
            entries.ShouldContain("contracts/0.2.0/mcp-server.schema.json");
            entries.ShouldContain("contracts/0.2.0/mcp-authority-manifest.json");
            entries.ShouldContain("contracts/0.2.0/release-evidence-map.json");
            entries.ShouldContain("skills/caldav-calendars/SKILL.md");
            entries.ShouldContain("docs/migrating-0.1.x-to-0.2.0.md");
            entries.ShouldContain("CHANGELOG.md");
            entries.ShouldContain("RELEASE_NOTES.md");
        }
        finally
        {
            packageDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReleasePackage_SubstitutesBothMetadataVersionsFromTagWithoutChangingSource()
    {
        const string releaseTag = "v0.2.0";
        const string releaseVersion = "0.2.0";
        var repositoryRoot = RepositoryRoot();
        var temporaryDirectory = Directory.CreateTempSubdirectory("caldav-release-test-");
        var sourceMetadataPath = Path.Combine(
            repositoryRoot,
            "src",
            "DotnetAgents.CalDav.Mcp",
            ".mcp",
            "server.json");
        var generatedMetadataPath = Path.Combine(temporaryDirectory.FullName, "server.json");
        var packageDirectory = Path.Combine(temporaryDirectory.FullName, "packages");

        try
        {
            AssertMetadataFileVersions(sourceMetadataPath, "0.0.0");
            var generation = Run(
                "bash",
                [
                    Path.Combine(repositoryRoot, "scripts", "prepare-release-metadata.sh"),
                    releaseTag,
                    generatedMetadataPath
                ],
                repositoryRoot);
            generation.ExitCode.ShouldBe(0, generation.Output);

            var build = Run(
                "dotnet",
                [
                    "build",
                    Path.Combine(repositoryRoot, "src", "DotnetAgents.CalDav.Mcp", "DotnetAgents.CalDav.Mcp.csproj"),
                    "-c", "Release",
                    "--no-restore",
                    $"/p:McpServerMetadataPath={generatedMetadataPath}"
                ],
                repositoryRoot);
            build.ExitCode.ShouldBe(0, build.Output);

            var pack = Run(
                "dotnet",
                [
                    "pack",
                    Path.Combine(repositoryRoot, "src", "DotnetAgents.CalDav.Mcp", "DotnetAgents.CalDav.Mcp.csproj"),
                    "-c", "Release",
                    "--no-build",
                    "--no-restore",
                    "-o", packageDirectory,
                    $"/p:Version={releaseVersion}",
                    $"/p:McpServerMetadataPath={generatedMetadataPath}"
                ],
                repositoryRoot);
            pack.ExitCode.ShouldBe(0, pack.Output);

            var packagePath = Directory.GetFiles(packageDirectory, "*.nupkg").Single();
            using var package = ZipFile.OpenRead(packagePath);
            AssertMetadataVersions(ReadEntry(package, ".mcp/server.json"), releaseVersion);
            AssertMetadataVersions(ReadEntry(package, "tools/net10.0/any/.mcp/server.json"), releaseVersion);
            var nuspec = XDocument.Parse(ReadEntry(package, "dotnet-agents-caldav.nuspec"));
            var metadata = nuspec.Root!.Elements().Single(element => element.Name.LocalName == "metadata");
            metadata.Elements().Single(element => element.Name.LocalName == "version").Value
                .ShouldBe(releaseVersion);
            metadata.Elements().Single(element => element.Name.LocalName == "description").Value
                .ShouldContain("Calendars, Events, and To-dos");
            AssertMetadataFileVersions(sourceMetadataPath, "0.0.0");

            var defaultBuild = Run(
                "dotnet",
                [
                    "build",
                    Path.Combine(repositoryRoot, "src", "DotnetAgents.CalDav.Mcp", "DotnetAgents.CalDav.Mcp.csproj"),
                    "-c", "Release",
                    "--no-restore",
                    $"/p:McpServerMetadataPath={sourceMetadataPath}"
                ],
                repositoryRoot);
            defaultBuild.ExitCode.ShouldBe(0, defaultBuild.Output);
            AssertMetadataFileVersions(
                Path.Combine(repositoryRoot, "src", "DotnetAgents.CalDav.Mcp", "bin", "Release", "net10.0", ".mcp", "server.json"),
                "0.0.0");
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReleasePackage_AdoptionDocumentsDescribeOnlyTheUnifiedContract()
    {
        var repositoryRoot = RepositoryRoot();
        var packageDirectory = Directory.CreateTempSubdirectory("caldav-adoption-test-");

        try
        {
            var result = Run(
                "dotnet",
                [
                    "pack",
                    Path.Combine(repositoryRoot, "src", "DotnetAgents.CalDav.Mcp", "DotnetAgents.CalDav.Mcp.csproj"),
                    "-c", "Release",
                    "--no-build",
                    "--no-restore",
                    "-o", packageDirectory.FullName,
                    "/p:Version=0.2.0"
                ],
                repositoryRoot);
            result.ExitCode.ShouldBe(0, result.Output);

            var packagePath = Directory.GetFiles(packageDirectory.FullName, "*.nupkg").Single();
            using var package = ZipFile.OpenRead(packagePath);
            var catalogJson = ReadEntry(package, "contracts/0.2.0/mcp-tool-catalog.json");
            AssertPackedCatalog(catalogJson);
            AssertEvidenceMap(ReadEntry(package, "contracts/0.2.0/release-evidence-map.json"));
            AssertMcpAuthorityManifest(
                ReadEntry(package, "contracts/0.2.0/mcp-authority-manifest.json"),
                catalogJson,
                File.ReadAllText(Path.Combine(repositoryRoot, "Directory.Packages.props")));
            var registrySchema = McpRegistrySchema.Parse(
                ReadEntry(package, "contracts/0.2.0/mcp-server.schema.json"));
            var rootMetadata = ReadEntry(package, ".mcp/server.json");
            var toolMetadata = ReadEntry(package, "tools/net10.0/any/.mcp/server.json");
            AssertValidMetadata(registrySchema, rootMetadata);
            AssertValidMetadata(registrySchema, toolMetadata);
            AssertPackedMetadata(rootMetadata);
            AssertMigrationGuide(ReadEntry(package, "docs/migrating-0.1.x-to-0.2.0.md"), catalogJson);
            AssertBundledSkill(ReadEntry(package, "skills/caldav-calendars/SKILL.md"));
            AssertReleaseDocuments(
                ReadEntry(package, "README.md"),
                ReadEntry(package, "CHANGELOG.md"),
                ReadEntry(package, "RELEASE_NOTES.md"));
        }
        finally
        {
            packageDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReleaseWorkflow_RepeatsTheNormativeGatesBeforePacking()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "release.yml"));
        var tests = workflow.IndexOf("Run tests with coverage", StringComparison.Ordinal);
        var coverage = workflow.IndexOf("Verify coverage thresholds", StringComparison.Ordinal);
        var strictProfile = workflow.IndexOf("RADICALE_CONFORMANCE_VARIANT=strict-preconditions", StringComparison.Ordinal);
        var alternateProfile = workflow.IndexOf("RADICALE_CONFORMANCE_VARIANT=alternate-time-zone", StringComparison.Ordinal);
        var slopwatch = workflow.IndexOf("Run Slopwatch", StringComparison.Ordinal);
        var pack = workflow.IndexOf("- name: Pack", StringComparison.Ordinal);
        var verifyPackage = workflow.IndexOf("- name: Verify final release packages", StringComparison.Ordinal);
        var upload = workflow.IndexOf("- name: Upload package artifacts", StringComparison.Ordinal);
        var push = workflow.IndexOf("- name: Push to NuGet", StringComparison.Ordinal);

        workflow.ShouldContain("scripts/prepare-release-metadata.sh");
        workflow.ShouldContain("VERSION_WITHOUT_BUILD=\"${PACKAGE_VERSION%%+*}\"");
        workflow.ShouldContain("[[ \"$VERSION_WITHOUT_BUILD\" == *-* ]]");
        workflow.ShouldContain("/p:McpServerMetadataPath");
        workflow.ShouldContain("bash scripts/verify-test-results.sh TestResults");
        workflow.ShouldContain("bash scripts/verify-release-evidence.sh contracts/0.2.0/requirement-evidence-catalog.md contracts/0.2.0/release-evidence-map.json TestResults");
        workflow.ShouldContain("bash scripts/verify-release-package.sh");
        AssertSerialProjectTestCommands(workflow, "release");
        tests.ShouldBeGreaterThan(0);
        coverage.ShouldBeGreaterThan(tests);
        strictProfile.ShouldBeGreaterThan(coverage);
        alternateProfile.ShouldBeGreaterThan(strictProfile);
        slopwatch.ShouldBeGreaterThan(alternateProfile);
        pack.ShouldBeGreaterThan(slopwatch);
        verifyPackage.ShouldBeGreaterThan(pack);
        upload.ShouldBeGreaterThan(verifyPackage);
        push.ShouldBeGreaterThan(verifyPackage);
    }

    [Fact]
    public void ReleaseWorkflow_GeneratesGitHubReleaseNotes()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "release.yml"));

        workflow.ShouldContain("workflow_dispatch:");
        workflow.ShouldContain("release_tag:");
        workflow.ShouldContain("ref: ${{ inputs.release_tag }}");
        workflow.ShouldContain("tag_name: ${{ inputs.release_tag }}");
        workflow.ShouldContain("generate_release_notes: true");
        workflow.ShouldNotContain("push:\n    tags:");
        workflow.ShouldNotContain("RELEASE_NOTES_LATEST.md");
        workflow.ShouldNotContain("body_path: RELEASE_NOTES_LATEST.md");
    }

    [Fact]
    public void ReleasePackageVerifier_AcceptsExactArtifactsAndRejectsTamperedContent()
    {
        const string releaseTag = "v0.2.0";
        const string releaseVersion = "0.2.0";
        var repositoryRoot = RepositoryRoot();
        var temporaryDirectory = Directory.CreateTempSubdirectory("caldav-final-package-test-");
        var metadataPath = Path.Combine(temporaryDirectory.FullName, "server.json");
        var packageDirectory = Path.Combine(temporaryDirectory.FullName, "packages");

        try
        {
            Run(
                "bash",
                [Path.Combine(repositoryRoot, "scripts", "prepare-release-metadata.sh"), releaseTag, metadataPath],
                repositoryRoot).ExitCode.ShouldBe(0);
            Run(
                "dotnet",
                [
                    "build",
                    Path.Combine(repositoryRoot, "src", "DotnetAgents.CalDav.Mcp", "DotnetAgents.CalDav.Mcp.csproj"),
                    "-c", "Release", "--no-restore", $"/p:McpServerMetadataPath={metadataPath}"
                ],
                repositoryRoot).ExitCode.ShouldBe(0);
            Run(
                "dotnet",
                [
                    "pack",
                    Path.Combine(repositoryRoot, "src", "DotnetAgents.CalDav.Mcp", "DotnetAgents.CalDav.Mcp.csproj"),
                    "-c", "Release", "--no-build", "--no-restore", "-o", packageDirectory,
                    $"/p:Version={releaseVersion}", $"/p:McpServerMetadataPath={metadataPath}"
                ],
                repositoryRoot).ExitCode.ShouldBe(0);

            var script = Path.Combine(repositoryRoot, "scripts", "verify-release-package.sh");
            var verified = Run("bash", [script, releaseVersion, packageDirectory, metadataPath], repositoryRoot);
            verified.ExitCode.ShouldBe(0, verified.Output);

            var packagePath = Directory.GetFiles(packageDirectory, "*.nupkg").Single();
            using (var package = ZipFile.Open(packagePath, ZipArchiveMode.Update))
            {
                var entry = package.GetEntry("contracts/0.2.0/mcp-tool-catalog.json").ShouldNotBeNull();
                entry.Delete();
                using var writer = new StreamWriter(package.CreateEntry(
                    "contracts/0.2.0/mcp-tool-catalog.json").Open());
                writer.Write("{}");
            }

            var rejected = Run("bash", [script, releaseVersion, packageDirectory, metadataPath], repositoryRoot);
            rejected.ExitCode.ShouldNotBe(0);
            rejected.Output.ShouldContain("mismatch", Case.Insensitive);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void PullRequestWorkflow_RequiresMappedTrxEvidenceForDocumentationAndCodeChanges()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "ci.yml"));

        workflow.ShouldNotContain("paths-ignore:");
        AssertSerialProjectTestCommands(workflow, "pr");
        workflow.ShouldContain("RADICALE_CONFORMANCE_VARIANT=strict-preconditions");
        workflow.ShouldContain("--logger \"trx;LogFilePrefix=strict-preconditions\"");
        workflow.ShouldContain("RADICALE_CONFORMANCE_VARIANT=alternate-time-zone");
        workflow.ShouldContain("--logger \"trx;LogFilePrefix=alternate-time-zone\"");
        workflow.ShouldContain("bash scripts/verify-test-results.sh TestResults");
        workflow.ShouldContain("bash scripts/verify-release-evidence.sh contracts/0.2.0/requirement-evidence-catalog.md contracts/0.2.0/release-evidence-map.json TestResults");
    }

    private static void AssertSerialProjectTestCommands(string workflow, string prefix)
    {
        var core = workflow.IndexOf(
            $"tests/DotnetAgents.CalDav.Core.Tests.Unit/DotnetAgents.CalDav.Core.Tests.Unit.csproj -c Release --no-build --settings coverage.runsettings --collect:\"XPlat Code Coverage\" --logger \"trx;LogFilePrefix={prefix}-core\"",
            StringComparison.Ordinal);
        var mcp = workflow.IndexOf(
            $"tests/DotnetAgents.CalDav.Mcp.Tests.Unit/DotnetAgents.CalDav.Mcp.Tests.Unit.csproj -c Release --no-build --settings coverage.runsettings --collect:\"XPlat Code Coverage\" --logger \"trx;LogFilePrefix={prefix}-mcp\"",
            StringComparison.Ordinal);
        var integration = workflow.IndexOf(
            $"tests/DotnetAgents.CalDav.IntegrationTests/DotnetAgents.CalDav.IntegrationTests.csproj -c Release --no-build --settings coverage.runsettings --collect:\"XPlat Code Coverage\" --logger \"trx;LogFilePrefix={prefix}-integration\"",
            StringComparison.Ordinal);

        core.ShouldBeGreaterThan(0);
        mcp.ShouldBeGreaterThan(core);
        integration.ShouldBeGreaterThan(mcp);
        workflow.ShouldNotContain("dotnet test -c Release --no-build");
    }

    [Fact]
    public void ReleaseEvidenceGate_RequiresEveryMappedTestToHavePassed()
    {
        var repositoryRoot = RepositoryRoot();
        var resultsDirectory = Directory.CreateTempSubdirectory("caldav-evidence-test-");
        var mapPath = Path.Combine(repositoryRoot, "contracts", "0.2.0", "release-evidence-map.json");
        var catalogPath = Path.Combine(repositoryRoot, "contracts", "0.2.0", "requirement-evidence-catalog.md");
        File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "verify-release-evidence.sh"))
            .ShouldNotContain("rg ");
        File.ReadAllText(Path.Combine(repositoryRoot, "scripts", "verify-test-results.sh"))
            .ShouldNotContain("rg ");
        var mapDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(mapPath)));
        var catalogDigest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(catalogPath)));

        try
        {
            using var map = JsonDocument.Parse(File.ReadAllText(mapPath));
            var testNames = map.RootElement.GetProperty("requirements")
                .EnumerateArray()
                .SelectMany(row => row.GetProperty("testNameContains").EnumerateArray())
                .Select(value => value.GetString().ShouldNotBeNull())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var results = new XDocument(
                new XElement("TestRun",
                    new XElement("Results", testNames.Select(testName =>
                        new XElement("UnitTestResult",
                            new XAttribute("testName", $"Evidence.{testName}"),
                            new XAttribute("outcome", "Passed")))),
                    new XElement("ResultSummary",
                        new XElement("Counters",
                            new XAttribute("total", testNames.Length),
                            new XAttribute("passed", testNames.Length)))));
            var trxPath = Path.Combine(resultsDirectory.FullName, "evidence.trx");
            results.Save(trxPath);
            results.Save(Path.Combine(resultsDirectory.FullName, "strict-preconditions.trx"));
            results.Save(Path.Combine(resultsDirectory.FullName, "alternate-time-zone.trx"));

            var passing = Run(
                "bash",
                [
                    Path.Combine(repositoryRoot, "scripts", "verify-release-evidence.sh"),
                    catalogPath,
                    mapPath,
                    resultsDirectory.FullName
                ],
                repositoryRoot);
            passing.ExitCode.ShouldBe(0, passing.Output);

            results.Descendants("UnitTestResult").First().SetAttributeValue("outcome", "Failed");
            results.Save(trxPath);
            var failing = Run(
                "bash",
                [
                    Path.Combine(repositoryRoot, "scripts", "verify-release-evidence.sh"),
                    catalogPath,
                    mapPath,
                    resultsDirectory.FullName
                ],
                repositoryRoot);
            failing.ExitCode.ShouldBe(72, failing.Output);

            results.Descendants("UnitTestResult").First().SetAttributeValue("outcome", "Passed");
            results.Descendants("UnitTestResult").Skip(1).First().SetAttributeValue("outcome", "NotExecuted");
            results.Save(trxPath);
            var skipped = Run(
                "bash",
                [
                    Path.Combine(repositoryRoot, "scripts", "verify-release-evidence.sh"),
                    catalogPath,
                    mapPath,
                    resultsDirectory.FullName
                ],
                repositoryRoot);
            skipped.ExitCode.ShouldBe(72, skipped.Output);
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(mapPath)))
                .ShouldBe(mapDigest);
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(catalogPath)))
                .ShouldBe(catalogDigest);
        }
        finally
        {
            resultsDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("v1.2.3-alpha.1", "1.2.3-alpha.1")]
    [InlineData("v1.2.3-alpha.1+build.5", "1.2.3-alpha.1+build.5")]
    [InlineData("v1.2.3+build.5", "1.2.3+build.5")]
    public void ReleaseMetadataScript_AcceptsCanonicalSemVer(string tag, string expectedVersion)
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory("caldav-semver-test-");

        try
        {
            var outputPath = Path.Combine(temporaryDirectory.FullName, "server.json");
            var result = Run(
                "bash",
                [Path.Combine(RepositoryRoot(), "scripts", "prepare-release-metadata.sh"), tag, outputPath],
                RepositoryRoot());

            result.ExitCode.ShouldBe(0, result.Output);
            AssertMetadataFileVersions(outputPath, expectedVersion);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("v01.2.3")]
    [InlineData("v1.02.3")]
    [InlineData("v1.2.03")]
    [InlineData("v1.2.3-01")]
    [InlineData("v1.2.3-..")]
    [InlineData("v1.2.3+")]
    public void ReleaseMetadataScript_RejectsNoncanonicalSemVer(string tag)
    {
        var temporaryDirectory = Directory.CreateTempSubdirectory("caldav-semver-test-");

        try
        {
            var outputPath = Path.Combine(temporaryDirectory.FullName, "server.json");
            var result = Run(
                "bash",
                [Path.Combine(RepositoryRoot(), "scripts", "prepare-release-metadata.sh"), tag, outputPath],
                RepositoryRoot());

            result.ExitCode.ShouldBe(65);
            File.Exists(outputPath).ShouldBeFalse();
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    private static (int ExitCode, string Output) Run(
        string fileName,
        IReadOnlyCollection<string> arguments,
        string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return (process.ExitCode, $"{standardOutput.GetAwaiter().GetResult()}\n{standardError.GetAwaiter().GetResult()}");
    }

    private static string ReadEntry(ZipArchive package, string path)
    {
        var entry = package.GetEntry(path).ShouldNotBeNull($"Package must contain {path}.");
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    private static void AssertMetadataFileVersions(string metadataPath, string expectedVersion) =>
        AssertMetadataVersions(File.ReadAllText(metadataPath), expectedVersion);

    private static void AssertMetadataVersions(string metadata, string expectedVersion)
    {
        using var document = JsonDocument.Parse(metadata);
        document.RootElement.GetProperty("version").GetString().ShouldBe(expectedVersion);
        document.RootElement.GetProperty("packages")[0].GetProperty("version").GetString()
            .ShouldBe(expectedVersion);
    }

    private static void AssertValidMetadata(JsonSchema registrySchema, string metadata)
    {
        using var document = JsonDocument.Parse(metadata);
        registrySchema.Evaluate(document.RootElement).IsValid.ShouldBeTrue();
    }

    private static void AssertPackedCatalog(string catalogJson)
    {
        using var catalog = JsonDocument.Parse(catalogJson);
        var root = catalog.RootElement;
        root.GetProperty("contractVersion").GetString().ShouldBe("0.2.0");
        root.GetProperty("protocolRevision").GetString().ShouldBe("2026-07-28");
        var tools = root.GetProperty("tools").EnumerateArray().ToArray();
        tools.Length.ShouldBe(20);
        foreach (var tool in tools)
        {
            tool.TryGetProperty("inputSchema", out _).ShouldBeTrue();
            tool.TryGetProperty("outputSchema", out _).ShouldBeTrue();
        }
    }

    private static void AssertEvidenceMap(string evidenceMapJson)
    {
        using var evidenceMap = JsonDocument.Parse(evidenceMapJson);
        evidenceMap.RootElement.GetProperty("contractVersion").GetString().ShouldBe("0.2.0");
        evidenceMap.RootElement.GetProperty("semanticFixtureInventory")
            .GetProperty("categories").GetArrayLength().ShouldBe(16);
        evidenceMap.RootElement.GetProperty("semanticFixtureInventory")
            .GetProperty("pairwiseCrossProducts").GetArrayLength().ShouldBe(6);
        var rows = evidenceMap.RootElement.GetProperty("requirements").EnumerateArray().ToArray();
        rows.Length.ShouldBe(96);
        rows.Select(row => row.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count().ShouldBe(96);
        rows.ShouldAllBe(row => row.GetProperty("testNameContains").GetArrayLength() > 0);
    }

    private static void AssertMcpAuthorityManifest(string manifestJson, string catalogJson, string packagesXml)
    {
        using var manifest = JsonDocument.Parse(manifestJson);
        using var catalog = JsonDocument.Parse(catalogJson);
        var root = manifest.RootElement;
        var protocol = root.GetProperty("protocol");
        protocol.GetProperty("revision").GetString().ShouldBe("2026-07-28");
        protocol.GetProperty("stableTag").GetString().ShouldBe("2026-07-28");
        protocol.GetProperty("commit").GetString().ShouldBe("5f5440bb26a62e2cf3440b92da5a667efa03b267");
        protocol.GetProperty("revision").GetString()
            .ShouldBe(catalog.RootElement.GetProperty("protocolRevision").GetString());
        var sources = protocol.GetProperty("sourceDocuments").EnumerateArray().ToArray();
        sources.Select(source => source.GetProperty("category").GetString()).ShouldBe([
            "specification", "changelog", "discovery", "tools", "multiRoundTripRequests", "stdio"
        ]);
        sources.ShouldAllBe(source => source.GetProperty("url").GetString()!.StartsWith(
            "https://github.com/modelcontextprotocol/modelcontextprotocol/",
            StringComparison.Ordinal));
        sources.ShouldAllBe(source => source.GetProperty("url").GetString()!.Contains(
            protocol.GetProperty("commit").GetString()!,
            StringComparison.Ordinal));
        sources.Select(source => new Uri(source.GetProperty("url").GetString()!).AbsolutePath).ShouldBe([
            "/modelcontextprotocol/modelcontextprotocol/tree/5f5440bb26a62e2cf3440b92da5a667efa03b267/docs/specification/2026-07-28",
            "/modelcontextprotocol/modelcontextprotocol/blob/5f5440bb26a62e2cf3440b92da5a667efa03b267/docs/specification/2026-07-28/changelog.mdx",
            "/modelcontextprotocol/modelcontextprotocol/blob/5f5440bb26a62e2cf3440b92da5a667efa03b267/docs/specification/2026-07-28/server/discover.mdx",
            "/modelcontextprotocol/modelcontextprotocol/blob/5f5440bb26a62e2cf3440b92da5a667efa03b267/docs/specification/2026-07-28/server/tools.mdx",
            "/modelcontextprotocol/modelcontextprotocol/blob/5f5440bb26a62e2cf3440b92da5a667efa03b267/docs/specification/2026-07-28/basic/patterns/mrtr.mdx",
            "/modelcontextprotocol/modelcontextprotocol/blob/5f5440bb26a62e2cf3440b92da5a667efa03b267/docs/specification/2026-07-28/basic/transports/stdio.mdx"
        ]);
        var sdk = root.GetProperty("sdk");
        sdk.GetProperty("package").GetString().ShouldBe("ModelContextProtocol");
        sdk.GetProperty("version").GetString().ShouldBe("2.2.0");
        XDocument.Parse(packagesXml).Descendants("McpSdkVersion").Single().Value
            .ShouldBe(sdk.GetProperty("version").GetString());
        sdk.GetProperty("release").GetString().ShouldBe(
            "https://github.com/modelcontextprotocol/csharp-sdk/releases/tag/v2.2.0");
        root.GetProperty("authorityPolicy").GetProperty("excludedSources").EnumerateArray()
            .Select(item => item.GetString()).ShouldBe([
                "draftSpecification", "deprecatedSamples", "thirdPartyExamples"
            ]);
    }

    private static void AssertPackedMetadata(string metadataJson)
    {
        using var metadata = JsonDocument.Parse(metadataJson);
        var root = metadata.RootElement;
        root.GetProperty("name").GetString()
            .ShouldBe("io.github.jhonattan-souza/dotnet-agents-caldav");
        root.GetProperty("description").GetString().ShouldBe(
            "CalDAV MCP server for Calendars, Events, To-dos, and revision-bound semantic or exact operations.");
        var package = root.GetProperty("packages")[0];
        package.GetProperty("registryType").GetString().ShouldBe("nuget");
        package.GetProperty("identifier").GetString().ShouldBe("dotnet-agents-caldav");
        package.GetProperty("runtimeHint").GetString().ShouldBe("dnx");
        package.GetProperty("transport").GetProperty("type").GetString().ShouldBe("stdio");
        var environment = package.GetProperty("environmentVariables").EnumerateArray().ToArray();
        environment.Select(variable => variable.GetProperty("name").GetString()).ShouldBe(
        [
            "CALDAV_URL",
            "CALDAV_USERNAME",
            "CALDAV_PASSWORD",
            "CALDAV_CALENDAR_HREFS",
            "CALDAV_DEFAULT_TODO_CALENDAR_NAME",
            "CALDAV_DEFAULT_EVENT_CALENDAR_NAME",
            "CALDAV_EXPOSE_EXACT_TOOLS"
        ]);
        var expected = new (string Description, bool Required, bool Secret)[]
        {
            ("Absolute CalDAV server endpoint or Calendar Home URL", true, false),
            ("Username for CalDAV Basic authentication", true, false),
            ("Password for CalDAV Basic authentication", true, true),
            ("Comma-separated exact canonical Calendar href allowlist (optional; omit to discover every Calendar)", false, false),
            ("Display name of the default Calendar for To-do operations (optional)", false, false),
            ("Display name of the default Calendar for Event operations (optional)", false, false),
            ("Set to true to expose protected exact Calendar Object Resource tools (optional)", false, false)
        };
        for (var index = 0; index < expected.Length; index++)
        {
            environment[index].GetProperty("description").GetString().ShouldBe(expected[index].Description);
            environment[index].GetProperty("isRequired").GetBoolean().ShouldBe(expected[index].Required);
            var isSecret = environment[index].TryGetProperty("isSecret", out var secret) && secret.GetBoolean();
            isSecret.ShouldBe(expected[index].Secret);
        }
    }

    private static void AssertMigrationGuide(string migrationGuide, string catalogJson)
    {
        var toolMappings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["list_task_lists"] = "calendars.list",
            ["show_tasks"] = "calendar_entities.query",
            ["find_tasks"] = "calendar_entities.query",
            ["list_tasks"] = "calendar_entities.query",
            ["get_task"] = "calendar_resources.get",
            ["add_task"] = "todos.create",
            ["create_task"] = "todos.create",
            ["update_task"] = "todos.patch",
            ["complete_task"] = "todos.complete",
            ["complete_task_by_summary"] = "todos.complete",
            ["delete_task"] = "calendar_resources.delete",
            ["delete_task_by_summary"] = "calendar_resources.delete"
        };

        migrationGuide.ShouldContain("# Migrating from 0.1.x to 0.2.0");
        migrationGuide.ShouldContain("Before, with 0.1.4");
        migrationGuide.ShouldContain("After, with 0.2.0");
        migrationGuide.ShouldContain("## To-do recipes");
        migrationGuide.ShouldContain("## Interpret structured outcomes");
        migrationGuide.ShouldContain("## Verify the deployment");
        migrationGuide.ShouldContain("## Roll back to 0.1.4");
        migrationGuide.ShouldContain("No CalDAV data migration is required or performed");
        migrationGuide.ShouldContain("requestState");
        migrationGuide.ShouldContain("inputResponses");
        migrationGuide.ShouldContain("mutationState");
        migrationGuide.ShouldContain("structuredContent");
        migrationGuide.ShouldContain("snapshot.entityRevision");
        migrationGuide.ShouldContain("entityKinds: [\"todo\"]");
        migrationGuide.ShouldContain("entityKinds: [\"event\"]");
        var lines = migrationGuide.Split('\n');
        foreach (var mapping in toolMappings)
        {
            var row = lines.Single(line => line.StartsWith($"| `{mapping.Key}` |", StringComparison.Ordinal));
            row.ShouldContain($"`{mapping.Value}`");
        }

        var selectedQuery = ExtractJsonAfter(migrationGuide, "For one Calendar, use the complete selected scope below.");
        var schemaDocument = JsonNode.Parse(catalogJson)!.AsObject();
        schemaDocument["$ref"] = "#/$defs/entityQueryInput";
        var querySchema = JsonSchema.FromText(schemaDocument.ToJsonString());
        using var selectedQueryDocument = JsonDocument.Parse(selectedQuery);
        querySchema.Evaluate(selectedQueryDocument.RootElement).IsValid.ShouldBeTrue();
    }

    private static string ExtractJsonAfter(string markdown, string marker)
    {
        var markerIndex = markdown.IndexOf(marker, StringComparison.Ordinal);
        markerIndex.ShouldBeGreaterThanOrEqualTo(0);
        var fenceStart = markdown.IndexOf("```json\n", markerIndex, StringComparison.Ordinal);
        fenceStart.ShouldBeGreaterThan(markerIndex);
        var contentStart = fenceStart + "```json\n".Length;
        var fenceEnd = markdown.IndexOf("\n```", contentStart, StringComparison.Ordinal);
        fenceEnd.ShouldBeGreaterThan(contentStart);
        return markdown[contentStart..fenceEnd];
    }

    private static void AssertBundledSkill(string bundledSkill)
    {
        string[] unifiedTools =
        [
            "calendars.list", "calendar_entities.query", "calendar_occurrences.query",
            "calendar_resources.get", "events.create", "events.patch", "todos.create", "todos.patch",
            "todos.complete", "calendar_occurrences.add", "calendar_occurrences.exclude",
            "calendar_occurrences.restore_exclusion", "calendar_occurrences.cancel",
            "calendar_occurrences.restore_cancellation", "calendar_resources.move",
            "calendar_resources.delete", "calendar_resources.exact_get", "calendar_resources.exact_create",
            "calendar_resources.exact_replace", "calendar_resources.exact_move"
        ];
        string[] removedTools =
        [
            "list_task_lists", "show_tasks", "find_tasks", "list_tasks", "get_task", "add_task",
            "create_task", "update_task", "complete_task", "complete_task_by_summary", "delete_task",
            "delete_task_by_summary"
        ];

        bundledSkill.ShouldContain("name: caldav-calendars");
        bundledSkill.ShouldContain("Calendar href is identity");
        bundledSkill.ShouldContain("Multi Round-Trip Requests");
        bundledSkill.ShouldContain("snapshot.entityRevision");
        bundledSkill.ShouldNotContain("exact `resourceRevision`");
        unifiedTools.ShouldAllBe(tool => bundledSkill.Contains(tool, StringComparison.Ordinal));
        removedTools.ShouldAllBe(tool => !bundledSkill.Contains(tool, StringComparison.Ordinal));
    }

    private static void AssertReleaseDocuments(string readme, string changelog, string releaseNotes)
    {
        readme.ShouldContain("Migrating from 0.1.x");
        readme.ShouldContain("rollback to pinned version 0.1.4");
        changelog.ShouldContain("## [0.2.0] - 2026-08-17");
        changelog.ShouldContain("Removed all twelve 0.1.x task tools");
        releaseNotes.ShouldContain("## 0.2.0 — 2026-08-17");
        releaseNotes.ShouldContain("deliberate breaking replacement");
        releaseNotes.ShouldContain("CalDAV scheduling transport is unsupported");
        releaseNotes.ShouldContain("Radicale 3.7.8");
        releaseNotes.ShouldContain("recurrence_unevaluable");
        releaseNotes.ShouldContain("Pin `dotnet-agents-caldav@0.1.4`");
        releaseNotes.ShouldContain("no CalDAV data migration", Case.Insensitive);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DotnetAgentsCalDav.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
