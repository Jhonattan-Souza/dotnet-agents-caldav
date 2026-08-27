using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class PackageArtifactTests
{
    private const string BundledSkillPackagePath = "skills/caldav-calendars/SKILL.md";

    [Fact]
    public void Package_BundlesDiscoverableCalDavSkillVerbatim()
    {
        var repositoryRoot = RepositoryRoot();
        var skillPath = Path.Combine(repositoryRoot, BundledSkillPackagePath);
        var expectedBytes = File.ReadAllBytes(skillPath);
        var temporaryDirectory = Directory.CreateTempSubdirectory("caldav-package-test-");

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
                    "-p:PackageVersion=0.0.0-skill-test",
                    "-o", temporaryDirectory.FullName
                ],
                repositoryRoot);

            result.ExitCode.ShouldBe(0, result.Output);
            var packagePath = Directory.GetFiles(temporaryDirectory.FullName, "*.nupkg")
                .Single(path => !path.EndsWith(".snupkg", StringComparison.Ordinal));
            using var package = ZipFile.OpenRead(packagePath);
            var entry = package.GetEntry(BundledSkillPackagePath);
            entry.ShouldNotBeNull();
            using var stream = entry.Open();
            using var actualBytes = new MemoryStream();
            stream.CopyTo(actualBytes);
            actualBytes.ToArray().ShouldBe(expectedBytes);
            AssertDiscoverableSkillFrontmatter(expectedBytes);
        }
        finally
        {
            temporaryDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void BundledSkill_ContainsAmbiguousDestinationGuidance()
    {
        var skill = File.ReadAllText(Path.Combine(RepositoryRoot(), BundledSkillPackagePath));

        skill.ShouldContain("Establish an **authorized destination selection** before creating or moving an Event or To-do.");
        skill.ShouldContain("If more than one compatible Calendar remains, ask the user to choose before the first write.");
        skill.ShouldContain("If none remains, report that no compatible Calendar is available");
        skill.ShouldContain("The schema permitting `default` does not establish one.");
        skill.ShouldContain("Resolve an unknown destination with read-only discovery before calling a mutation.");
        skill.ShouldContain("every create and move had an authorized destination selection before the first write");
        skill.ShouldNotContain("every mutation had an authorized destination selection");
        skill.ShouldNotContain(
            "Use the live schema's `default` scope or destination directly when the user has not selected another Calendar.");
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

    private static void AssertMetadataFileVersions(string metadataPath, string expectedVersion)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
        document.RootElement.GetProperty("version").GetString().ShouldBe(expectedVersion);
        document.RootElement.GetProperty("packages")[0].GetProperty("version").GetString()
            .ShouldBe(expectedVersion);
    }

    private static void AssertDiscoverableSkillFrontmatter(byte[] skillBytes)
    {
        var skill = Encoding.UTF8.GetString(skillBytes).ReplaceLineEndings("\n");
        skill.ShouldStartWith("---\n");
        var closingDelimiter = skill.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        closingDelimiter.ShouldBeGreaterThan(4);
        var frontmatter = skill[4..closingDelimiter].Split('\n');
        frontmatter.ShouldContain("name: caldav-calendars");
        frontmatter.ShouldContain(line =>
            line.StartsWith("description: ", StringComparison.Ordinal) &&
            line.Contains("CalDAV", StringComparison.Ordinal) &&
            line.Contains("MCP", StringComparison.Ordinal));
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
