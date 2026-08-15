using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests.Fixtures;

/// <summary>
/// Starts the isolated Radicale interoperability profile used by the 0.2 contract.
/// It intentionally provisions no task-specific collections or resources.
/// </summary>
public sealed class RadicaleConformanceFixture : IAsyncLifetime
{
    public const string Image = "ghcr.io/kozea/radicale@sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80";
    public const string IndexDigest = "sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80";
    public const string Amd64ManifestDigest = "sha256:7e2d729c434574762b058d57c7c81641ade11655da6d0eede948512d53873e71";
    public const string Arm64ManifestDigest = "sha256:1691eb75474f38f9c0ce75e60a026a3c338b7c91a1cd9ed622557f141b0eb5b8";

    private const int RadicalePort = 5232;
    private IContainer? _container;

    public RadicaleConformanceVariant Variant { get; } = RadicaleConformanceVariant.FromEnvironment();

    public RadicaleRuntimeEvidence Runtime { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var config = Encoding.UTF8.GetBytes(BuildConfig(Variant.StrictPreconditions));

        _container = new ContainerBuilder(Image)
            .WithPortBinding(RadicalePort, true)
            .WithEnvironment("TZ", Variant.TimeZone)
            .WithCommand("--config", "/config/config", "--hosts", "0.0.0.0:5232,[::]:5232")
            .WithResourceMapping(config, "/config/config", 0, 0,
                UnixFileModes.UserRead | UnixFileModes.UserWrite |
                UnixFileModes.GroupRead | UnixFileModes.OtherRead)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request
                    .ForPort(RadicalePort)
                    .ForPath("/.well-known/caldav")))
            .Build();

        await _container.StartAsync();

        Runtime = new RadicaleRuntimeEvidence(
            IndexDigest,
            Amd64ManifestDigest,
            Arm64ManifestDigest,
            Variant.Name,
            Variant.TimeZone,
            Variant.StrictPreconditions,
            await ReadRuntimeValueAsync("import importlib.metadata; print(importlib.metadata.version('Radicale'))"),
            await ReadRuntimeValueAsync("import sys; print('.'.join(map(str, sys.version_info[:3])))"),
            await ReadRuntimeValueAsync("import importlib.metadata; print(importlib.metadata.version('vobject'))"),
            await ReadRuntimeValueAsync("import os; print(os.environ['TZ'])"));
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private async Task<string> ReadRuntimeValueAsync(string script)
    {
        var result = await _container!.ExecAsync(["/app/bin/python", "-c", script]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Unable to inspect the Radicale runtime: {result.Stderr}");
        }

        return result.Stdout.Trim();
    }

    private static string BuildConfig(bool strictPreconditions) => $$"""
        [server]
        hosts = 0.0.0.0:5232

        [auth]
        type = none

        [rights]
        type = owner_only

        [storage]
        filesystem_folder = /var/lib/radicale/collections
        strict_preconditions = {{strictPreconditions.ToString().ToLowerInvariant()}}

        [logging]
        level = warning
        """;
}

public sealed record RadicaleRuntimeEvidence(
    string IndexDigest,
    string Amd64ManifestDigest,
    string Arm64ManifestDigest,
    string Variant,
    string ConfiguredTimeZone,
    bool StrictPreconditions,
    string RadicaleVersion,
    string PythonVersion,
    string VobjectVersion,
    string RuntimeTimeZone);

public sealed record RadicaleConformanceVariant(string Name, string TimeZone, bool StrictPreconditions)
{
    public static RadicaleConformanceVariant FromEnvironment() => Environment.GetEnvironmentVariable("RADICALE_CONFORMANCE_VARIANT") switch
    {
        null or "" or "baseline" => new("baseline", "UTC", false),
        "strict-preconditions" => new("strict-preconditions", "UTC", true),
        "alternate-time-zone" => new("alternate-time-zone", "America/New_York", false),
        var value => throw new InvalidOperationException(
            $"RADICALE_CONFORMANCE_VARIANT must be baseline, strict-preconditions, or alternate-time-zone; received '{value}'.")
    };
}

[CollectionDefinition("RadicaleConformanceCollection", DisableParallelization = true)]
public sealed class RadicaleConformanceCollection : ICollectionFixture<RadicaleConformanceFixture>;
