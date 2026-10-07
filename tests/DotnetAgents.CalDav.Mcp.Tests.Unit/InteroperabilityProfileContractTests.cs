using System.Text.Json.Nodes;
using DotnetAgents.CalDav.Core.Configuration;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class InteroperabilityProfileContractTests
{
    private const string NextcloudIndexDigest = "sha256:b97df9e0e1ee3c8c6cc009cb3f12ddce915d624d543b3bb93882025fe323a407";

    [Fact]
    public void Nextcloud_profile_pins_the_observed_image_and_runtime()
    {
        var profile = ReadProfile(CalDavInteroperabilityProfiles.Nextcloud);

        profile["profile"]!.GetValue<string>().ShouldBe(CalDavInteroperabilityProfiles.Nextcloud);
        profile["image"]!.GetValue<string>().ShouldBe("docker.io/library/nextcloud@" + NextcloudIndexDigest);
        profile["ociIndexDigest"]!.GetValue<string>().ShouldBe(NextcloudIndexDigest);
        profile["platformManifests"]!.AsObject().Select(entry => entry.Key)
            .ShouldBe(["linux/amd64", "linux/arm64"]);
        profile["runtime"]!["nextcloud"]!.GetValue<string>().ShouldBe("34.0.3");
        profile["runtime"]!["sabre/dav"]!.GetValue<string>().ShouldBe("4.7.0");
        profile["evidenceFixture"]!.GetValue<string>()
            .ShouldBe("scripts/observations/rfc-coverage/move_preconditions.py");
        profile["legacyTaskFixturesAreEvidence"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void Every_supported_profile_has_one_family_contract_and_catalog_documentation()
    {
        var catalog = JsonNode.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "DotnetAgents.CalDav.Mcp", "Contracts", "mcp-tool-catalog.json")))!;
        var environment = catalog["environment"]!.AsArray()
            .Single(item => item!["name"]!.GetValue<string>() == "CALDAV_INTEROPERABILITY_PROFILE")!;
        var description = environment["description"]!.GetValue<string>();

        environment["required"]!.GetValue<bool>().ShouldBeFalse();
        environment["enum"]!.AsArray().Select(value => value!.GetValue<string>())
            .ShouldBe(CalDavInteroperabilityProfiles.Supported);
        Directory.GetFiles(Path.Combine(RepositoryRoot(), "contracts"), "*-profile.json")
            .Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)
            .ShouldBe(CalDavInteroperabilityProfiles.Supported.Select(profile => profile + "-profile.json")
                .Order(StringComparer.Ordinal));
        foreach (var profile in CalDavInteroperabilityProfiles.Supported)
        {
            ReadProfile(profile)["profile"]!.GetValue<string>().ShouldBe(profile);
            description.ShouldContain(profile);
        }
        // A Verified Runtime pin bump changes the profile contract, never the operator-facing description.
        description.ShouldNotMatch(@"\d+\.\d+");
        description.ShouldContain("#supported-servers");
    }

    private static JsonObject ReadProfile(string profile)
    {
        var path = Path.Combine(RepositoryRoot(), "contracts", profile + "-profile.json");
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotnetAgentsCalDav.slnx")))
            directory = directory.Parent;
        return directory!.FullName;
    }
}
