using System.Text;
using System.Text.Json;
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
    public const string Username = "conformance";
    public const string Password = "conformance";
    public const string Image = "ghcr.io/kozea/radicale@sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80";
    public const string IndexDigest = "sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80";
    public const string Amd64ManifestDigest = "sha256:7e2d729c434574762b058d57c7c81641ade11655da6d0eede948512d53873e71";
    public const string Arm64ManifestDigest = "sha256:1691eb75474f38f9c0ce75e60a026a3c338b7c91a1cd9ed622557f141b0eb5b8";

    private const int RadicalePort = 5232;
    private IContainer? _container;

    public RadicaleConformanceVariant Variant { get; } = RadicaleConformanceVariant.FromEnvironment();

    public RadicaleRuntimeEvidence Runtime { get; private set; } = null!;

    public string BaseUrl { get; private set; } = null!;

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
                .UntilInternalTcpPortIsAvailable(RadicalePort)
                .UntilHttpRequestIsSucceeded(
                    request => request
                        .ForPort(RadicalePort)
                        .ForPath("/")
                        .ForStatusCodeMatching(status => (int)status < 500),
                    strategy => strategy
                        .WithInterval(TimeSpan.FromMilliseconds(250))
                        .WithRetries(60)
                        .WithTimeout(TimeSpan.FromSeconds(30))))
            .Build();

        await _container.StartAsync();
        BaseUrl = $"http://localhost:{_container.GetMappedPublicPort(RadicalePort)}";

        var configuredStrictPreconditions = await ReadConfigurationValueAsync("strict_preconditions");
        var strictPreconditionsApplied = await ObserveStrictPreconditionsAsync();
        if ((configuredStrictPreconditions == "true") != strictPreconditionsApplied)
        {
            throw new InvalidOperationException("Radicale strict_preconditions configuration was not applied by the running server.");
        }

        var manifest = await ReadResolvedManifestAsync();
        Runtime = new RadicaleRuntimeEvidence(
            IndexDigest,
            manifest.Digest,
            manifest.Architecture,
            Variant.Name,
            Variant.TimeZone,
            strictPreconditionsApplied,
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

    public async Task<RadicaleConformanceResource> SeedResourceAsync(
        string resourceName,
        string content,
        CancellationToken cancellationToken)
    {
        const string script = """
            import base64
            import json
            import sys
            import urllib.parse
            import urllib.request

            name = urllib.parse.quote(sys.argv[1], safe='')
            content = base64.b64decode(sys.argv[2])
            authorization = 'Basic ' + sys.argv[3]
            url = 'http://127.0.0.1:5232/conformance/conformance/' + name
            headers = {'Authorization': authorization, 'Connection': 'close'}

            put = urllib.request.Request(
                url,
                data=content,
                headers=headers | {'Content-Type': 'text/calendar; charset=utf-8'},
                method='PUT')
            with urllib.request.urlopen(put) as response:
                if response.status not in (201, 204):
                    raise RuntimeError(f'PUT returned {response.status}')

            get = urllib.request.Request(url, headers=headers, method='GET')
            with urllib.request.urlopen(get) as response:
                etag = response.headers.get('ETag')
                if not etag or etag.startswith('W/'):
                    raise RuntimeError('GET did not return a strong ETag')
                observed = response.read()

            print(json.dumps({
                'entityTag': etag,
                'content': base64.b64encode(observed).decode('ascii')
            }))
            """;
        var contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
        var credentialsBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
        var result = await _container!.ExecAsync(
            ["/app/bin/python", "-c", script, resourceName, contentBase64, credentialsBase64],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Unable to seed the Radicale conformance resource: {result.Stderr}");
        }

        using var document = JsonDocument.Parse(result.Stdout);
        var root = document.RootElement;
        return new RadicaleConformanceResource(
            root.GetProperty("entityTag").GetString()!,
            Convert.FromBase64String(root.GetProperty("content").GetString()!));
    }

    public async Task DeleteResourceAsync(
        string resourceName,
        string entityTag,
        CancellationToken cancellationToken)
    {
        const string script = """
            import sys
            import urllib.parse
            import urllib.request

            name = urllib.parse.quote(sys.argv[1], safe='')
            authorization = 'Basic ' + sys.argv[3]
            url = 'http://127.0.0.1:5232/conformance/conformance/' + name
            request = urllib.request.Request(
                url,
                headers={
                    'Authorization': authorization,
                    'Connection': 'close',
                    'If-Match': sys.argv[2]
                },
                method='DELETE')
            with urllib.request.urlopen(request) as response:
                if response.status not in (200, 204):
                    raise RuntimeError(f'DELETE returned {response.status}')
            """;
        var credentialsBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));
        var result = await _container!.ExecAsync(
            ["/app/bin/python", "-c", script, resourceName, entityTag, credentialsBase64],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Unable to delete the Radicale conformance resource: {result.Stderr}");
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

    private async Task<string> ReadConfigurationValueAsync(string key)
    {
        var result = await _container!.ExecAsync(["/bin/sh", "-c", $"sed -n 's/^{key} = //p' /config/config"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Unable to inspect the Radicale configuration: {result.Stderr}");
        }

        return result.Stdout.Trim();
    }

    private async Task<RadicaleManifestEvidence> ReadResolvedManifestAsync()
    {
        var architecture = await ReadRuntimeValueAsync("import platform; print(platform.machine())");
        return architecture switch
        {
            "x86_64" => new(architecture, Amd64ManifestDigest),
            "aarch64" => new(architecture, Arm64ManifestDigest),
            _ => throw new InvalidOperationException($"Unsupported Radicale runtime architecture '{architecture}'.")
        };
    }

    private async Task<bool> ObserveStrictPreconditionsAsync()
    {
        const string script = """
            import urllib.error
            import urllib.request

            base = 'http://127.0.0.1:5232/conformance/conformance/'
            body = b'BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:strict-precondition-probe\r\nDTSTAMP:20260815T000000Z\r\nDTSTART:20260816T000000Z\r\nSUMMARY:probe\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n'

            def request(method, url, data=None, headers=None):
                try:
                    return urllib.request.urlopen(urllib.request.Request(url, data=data, headers=headers or {}, method=method)).status
                except urllib.error.HTTPError as error:
                    return error.code

            credentials = {'Authorization': 'Basic Y29uZm9ybWFuY2U6Y29uZm9ybWFuY2U='}
            collection = request('MKCALENDAR', base, headers=credentials)
            if collection not in (201, 405):
                raise RuntimeError(f'MKCALENDAR returned {collection}')
            created = request('PUT', base + 'probe.ics', body, credentials | {'Content-Type': 'text/calendar'})
            if created not in (201, 204):
                raise RuntimeError(f'initial PUT returned {created}')
            replacement = request('PUT', base + 'probe.ics', body, credentials | {'Content-Type': 'text/calendar'})
            if replacement not in (204, 412):
                raise RuntimeError(f'unconditional replacement returned {replacement}')
            print(replacement)
            """;
        var result = await _container!.ExecAsync(["/app/bin/python", "-c", script]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Unable to observe Radicale strict-precondition behavior: {result.Stderr}");
        }

        return result.Stdout.Trim() == "412";
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
    string ResolvedPlatformManifestDigest,
    string RuntimeArchitecture,
    string Variant,
    string ConfiguredTimeZone,
    bool StrictPreconditions,
    string RadicaleVersion,
    string PythonVersion,
    string VobjectVersion,
    string RuntimeTimeZone);

internal sealed record RadicaleManifestEvidence(string Architecture, string Digest);

public sealed record RadicaleConformanceResource(string EntityTag, byte[] Utf8);

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
