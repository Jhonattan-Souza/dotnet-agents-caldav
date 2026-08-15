using DotnetAgents.CalDav.IntegrationTests.Fixtures;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests;

[Collection("RadicaleConformanceCollection")]
public sealed class RadicaleConformanceHarnessTests(RadicaleConformanceFixture fixture)
{
    [Fact]
    public void Pinned_profile_records_the_runtime_and_selected_variant()
    {
        fixture.Runtime.IndexDigest.ShouldBe(RadicaleConformanceFixture.IndexDigest);
        fixture.Runtime.Amd64ManifestDigest.ShouldBe(RadicaleConformanceFixture.Amd64ManifestDigest);
        fixture.Runtime.Arm64ManifestDigest.ShouldBe(RadicaleConformanceFixture.Arm64ManifestDigest);
        fixture.Runtime.RadicaleVersion.ShouldBe("3.7.8");
        fixture.Runtime.PythonVersion.ShouldBe("3.14.7");
        fixture.Runtime.VobjectVersion.ShouldBe("0.9.9");
        fixture.Runtime.RuntimeTimeZone.ShouldBe(fixture.Variant.TimeZone);
        fixture.Runtime.StrictPreconditions.ShouldBe(fixture.Variant.StrictPreconditions);
    }
}
