using System.Collections.Generic;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests.Fixtures;

/// <summary>
/// xUnit collection fixture that manages a Radicale CalDAV container via Testcontainers,
/// seeds Event and To-do Calendars for live protocol tests.
/// </summary>
public sealed class RadicaleFixture : IAsyncLifetime
{
    private IContainer? _container;
    private const string TestUsername = "caldavtest";
    private const string TestPassword = "caldavtest123";
    private const int RadicalePort = 5232;
    private const string TaskCollectionName = "tasks";
    private const string EventCollectionName = "events";
    private const string ShoppingCollectionName = "shopping";
    private const string WorkCollectionName = "work";

    /// <summary>The href of the seeded To-do Calendar (e.g. <c>/caldavtest/tasks/</c>).</summary>
    public string TodoCalendarHref { get; private set; } = null!;

    /// <summary>The href of the isolated seeded Event calendar.</summary>
    public string EventCalendarHref { get; private set; } = null!;

    /// <summary>The href of the seeded Shopping To-do Calendar.</summary>
    public string ShoppingCalendarHref { get; private set; } = null!;

    /// <summary>The href of the seeded Work To-do Calendar.</summary>
    public string WorkCalendarHref { get; private set; } = null!;

    /// <summary>The base URL of the Radicale container (e.g. <c>http://localhost:31234</c>).</summary>
    public string BaseUrl { get; private set; } = null!;

    /// <summary>
    /// Populates process environment variables with working CalDAV credentials
    /// for the live Radicale container backing this fixture.
    /// </summary>
    public void ConfigureCalDavEnvironment(IDictionary<string, string?> environment)
    {
        environment["CALDAV_URL"] = BaseUrl;
        environment["CALDAV_USERNAME"] = TestUsername;
        environment["CALDAV_PASSWORD"] = TestPassword;
    }

    // ── IAsyncLifetime ─────────────────────────────────────────────────────

    public async ValueTask InitializeAsync()
    {
        // 1. Build and start the Radicale container with htpasswd auth.
        var configContent = BuildRadicaleConfig();
        var usersContent = BuildUsersFile();
        var configBytes = Encoding.UTF8.GetBytes(configContent);
        var usersBytes = Encoding.UTF8.GetBytes(usersContent);

        _container = new ContainerBuilder("ghcr.io/kozea/radicale@sha256:3a0080ea51ac69dcd74e345b9587dc14a8c8af0652046069005749f9a75c5c80")
            .WithPortBinding(RadicalePort, true)
            .WithEnvironment("TZ", "UTC")
            .WithCommand("--config", "/config/config", "--hosts", "0.0.0.0:5232,[::]:5232")
            .WithResourceMapping(configBytes, "/config/config", 0, 0,
                UnixFileModes.UserRead | UnixFileModes.UserWrite |
                UnixFileModes.GroupRead | UnixFileModes.OtherRead)
            .WithResourceMapping(usersBytes, "/config/users", 0, 0,
                UnixFileModes.UserRead | UnixFileModes.UserWrite |
                UnixFileModes.GroupRead | UnixFileModes.OtherRead)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilInternalTcpPortIsAvailable(RadicalePort))
            .Build();

        await _container.StartAsync();

        var port = _container.GetMappedPublicPort(RadicalePort);
        BaseUrl = $"http://localhost:{port}";

        // 2. Provision the fixture through Radicale's loopback interface. Product traffic below
        // still uses the published BaseUrl and the production HTTP stack.
        var cancellationToken = TestContext.Current.CancellationToken;
        await CreateUserPrincipalAsync(cancellationToken);
        EventCalendarHref = await CreateCalendarCollectionAsync(
            EventCollectionName,
            "Events",
            "VEVENT",
            cancellationToken);
        TodoCalendarHref = await CreateCalendarCollectionAsync(
            TaskCollectionName,
            "Tasks",
            "VTODO",
            cancellationToken);
        ShoppingCalendarHref = await CreateCalendarCollectionAsync(
            ShoppingCollectionName,
            "Shopping",
            "VTODO",
            cancellationToken);
        WorkCalendarHref = await CreateCalendarCollectionAsync(
            WorkCollectionName,
            "Work",
            "VTODO",
            cancellationToken);

    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    // ── Radicale config ────────────────────────────────────────────────────

    private static string BuildRadicaleConfig() => """
        [server]
        hosts = 0.0.0.0:5232

        [auth]
        type = htpasswd
        htpasswd_filename = /config/users
        htpasswd_encryption = plain

        [rights]
        type = owner_write

        [storage]
        filesystem_folder = /var/lib/radicale/collections

        [web]
        type = internal

        [logging]
        level = info
        """;

    private static string BuildUsersFile() => $"{TestUsername}:{TestPassword}";

    // ── Collection provisioning ─────────────────────────────────────────────

    private Task CreateUserPrincipalAsync(CancellationToken cancellationToken)
    {
        // MKCOL /caldavtest/ — creates the user's principal collection.
        var principalPath = $"/{TestUsername}/";
        return ProvisionCollectionAsync(principalPath, null, cancellationToken);
    }

    private async Task<string> CreateCalendarCollectionAsync(
        string collectionName,
        string displayName,
        string componentName,
        CancellationToken cancellationToken)
    {
        // Extended MKCOL creates a single-component calendar collection through
        // Radicale's internal loopback; product calls still use the mapped host URL.
        var collectionPath = $"/{TestUsername}/{collectionName}/";
        var body = $$"""
            <?xml version="1.0" encoding="utf-8" ?>
            <D:mkcol xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav">
              <D:set>
                <D:prop>
                  <D:resourcetype>
                    <D:collection/>
                    <C:calendar/>
                  </D:resourcetype>
                  <D:displayname>{{displayName}}</D:displayname>
                  <C:supported-calendar-component-set>
                    <C:comp name="{{componentName}}"/>
                  </C:supported-calendar-component-set>
                </D:prop>
              </D:set>
            </D:mkcol>
            """;
        await ProvisionCollectionAsync(collectionPath, body, cancellationToken);
        return collectionPath;
    }

    private async Task ProvisionCollectionAsync(
        string collectionPath,
        string? body,
        CancellationToken cancellationToken)
    {
        const string script = """
            import base64
            import sys
            import urllib.error
            import urllib.parse
            import urllib.request

            path = urllib.parse.quote(sys.argv[1], safe='/')
            body = base64.b64decode(sys.argv[2]) if sys.argv[2] else None
            authorization = 'Basic ' + sys.argv[3]
            headers = {'Authorization': authorization, 'Connection': 'close'}
            if body is not None:
                headers['Content-Type'] = 'application/xml; charset=utf-8'
            request = urllib.request.Request(
                'http://127.0.0.1:5232' + path,
                data=body,
                headers=headers,
                method='MKCOL')
            try:
                with urllib.request.urlopen(request) as response:
                    status = response.status
            except urllib.error.HTTPError as error:
                status = error.code
            if status not in (201, 405):
                raise RuntimeError(f'MKCOL {path} returned {status}')
            print(status)
            """;
        var bodyBase64 = body is null
            ? string.Empty
            : Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        var credentialsBase64 = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{TestUsername}:{TestPassword}"));
        var result = await _container!.ExecAsync(
            ["/app/bin/python", "-c", script, collectionPath, bodyBase64, credentialsBase64],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Unable to provision Radicale collection '{collectionPath}': {result.Stderr}");
        }
    }
}

/// <summary>
/// xUnit collection definition that shares the <see cref="RadicaleFixture"/>
/// across all tests in the collection.
/// </summary>
[CollectionDefinition("RadicaleCollection")]
public class RadicaleCollection : ICollectionFixture<RadicaleFixture>;
