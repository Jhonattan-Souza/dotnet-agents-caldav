using System.Text.Json;
using DotnetAgents.CalDav.IntegrationTests.Fixtures;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

[Collection("RadicaleConformanceCollection")]
public sealed class RadicaleConformanceHarnessTests(RadicaleConformanceFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public void Pinned_profile_records_the_runtime_and_selected_variant()
    {
        output.WriteLine(JsonSerializer.Serialize(fixture.Runtime));
        fixture.Runtime.IndexDigest.ShouldBe(RadicaleConformanceFixture.IndexDigest);
        new[] { RadicaleConformanceFixture.Amd64ManifestDigest, RadicaleConformanceFixture.Arm64ManifestDigest }
            .ShouldContain(fixture.Runtime.ResolvedPlatformManifestDigest);
        fixture.Runtime.ResolvedPlatformManifestDigest.ShouldBe(fixture.Runtime.RuntimeArchitecture switch
        {
            "x86_64" => RadicaleConformanceFixture.Amd64ManifestDigest,
            "aarch64" => RadicaleConformanceFixture.Arm64ManifestDigest,
            _ => throw new InvalidOperationException($"Unsupported architecture {fixture.Runtime.RuntimeArchitecture}")
        });
        fixture.Runtime.RadicaleVersion.ShouldBe("3.7.8");
        fixture.Runtime.PythonVersion.ShouldBe("3.14.7");
        fixture.Runtime.VobjectVersion.ShouldBe("0.9.9");
        fixture.Runtime.RuntimeTimeZone.ShouldBe(fixture.Variant.TimeZone);
        fixture.Runtime.StrictPreconditions.ShouldBe(fixture.Variant.StrictPreconditions);
    }
}
