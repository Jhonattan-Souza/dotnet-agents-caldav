using System.Collections.Generic;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DotnetAgents.CalDav.IntegrationTests.Fixtures;

/// <summary>
/// xUnit collection fixture that manages a Radicale CalDAV container via Testcontainers,
/// seeds multiple task collections, and provides a production-wired <see cref="ITaskService"/>.
/// </summary>
public sealed class RadicaleFixture : IAsyncLifetime
{
    private IContainer? _container;
    private ServiceProvider? _serviceProvider;
    private HttpClient? _adminHttpClient;

    private const string TestUsername = "caldavtest";
    private const string TestPassword = "caldavtest123";
    private const int RadicalePort = 5232;
    private const string TaskCollectionName = "tasks";
    private const string ShoppingCollectionName = "shopping";
    private const string WorkCollectionName = "work";

    /// <summary>Production-wired <see cref="ITaskService"/> for test consumption.</summary>
    public ITaskService TaskService { get; private set; } = null!;

    /// <summary>The href of the seeded task collection (e.g. <c>/caldavtest/tasks/</c>).</summary>
    public string TaskListHref { get; private set; } = null!;

    /// <summary>The href of the seeded shopping task collection.</summary>
    public string ShoppingListHref { get; private set; } = null!;

    /// <summary>The href of the seeded work task collection.</summary>
    public string WorkListHref { get; private set; } = null!;

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

        _container = new ContainerBuilder("tomsquest/docker-radicale:latest")
            .WithPortBinding(RadicalePort, true)
            .WithResourceMapping(configBytes, "/config/config", 0, 0,
                UnixFileModes.UserRead | UnixFileModes.UserWrite |
                UnixFileModes.GroupRead | UnixFileModes.OtherRead)
            .WithResourceMapping(usersBytes, "/config/users", 0, 0,
                UnixFileModes.UserRead | UnixFileModes.UserWrite |
                UnixFileModes.GroupRead | UnixFileModes.OtherRead)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPort(RadicalePort)
                    .ForPath("/.well-known/caldav")))
            .Build();

        await _container.StartAsync();

        var port = _container.GetMappedPublicPort(RadicalePort);
        BaseUrl = $"http://localhost:{port}";

        // 2. Set up an admin HttpClient for fixture setup (MKCOL requests).
        _adminHttpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl)
        };
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{TestUsername}:{TestPassword}"));
        _adminHttpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);

        // 3. Create user principal and task collections.
        await CreateUserPrincipalAsync();
        TaskListHref = await CreateTaskCollectionAsync(TaskCollectionName, "Tasks");
        ShoppingListHref = await CreateTaskCollectionAsync(ShoppingCollectionName, "Shopping");
        WorkListHref = await CreateTaskCollectionAsync(WorkCollectionName, "Work");

        // 4. Wire production DI (AddCalDavTasks) against the live container.
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
        services.AddCalDavTasks(options =>
        {
            options.BaseUrl = BaseUrl;
            options.Username = TestUsername;
            options.Password = TestPassword;
        });

        _serviceProvider = services.BuildServiceProvider();
        TaskService = _serviceProvider.GetRequiredService<ITaskService>();
    }

    public async ValueTask DisposeAsync()
    {
        _adminHttpClient?.Dispose();

        if (_serviceProvider is not null)
        {
            await _serviceProvider.DisposeAsync();
        }

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
        filesystem_folder = /data/collections

        [web]
        type = internal

        [logging]
        level = info
        """;

    private static string BuildUsersFile() => $"{TestUsername}:{TestPassword}";

    // ── Collection provisioning ─────────────────────────────────────────────

    private async Task CreateUserPrincipalAsync()
    {
        // MKCOL /caldavtest/ — creates the user's principal collection.
        var principalPath = $"/{TestUsername}/";
        var response = await _adminHttpClient!.SendAsync(
            new HttpRequestMessage(new HttpMethod("MKCOL"), principalPath));

        // 201 Created = new, 405 Method Not Allowed = already exists — both are fine.
        if (response.StatusCode != HttpStatusCode.Created &&
            response.StatusCode != HttpStatusCode.MethodNotAllowed)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Failed to create user principal at {principalPath}. Status: {response.StatusCode}, Body: {body}");
        }
    }

    private async Task<string> CreateTaskCollectionAsync(string collectionName, string displayName)
    {
        // Extended MKCOL to create a VTODO-capable calendar collection.
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
                    <C:comp name="VTODO"/>
                  </C:supported-calendar-component-set>
                </D:prop>
              </D:set>
            </D:mkcol>
            """;
        var content = new StringContent(body, Encoding.UTF8, "application/xml");
        var request = new HttpRequestMessage(new HttpMethod("MKCOL"), collectionPath) { Content = content };
        var response = await _adminHttpClient!.SendAsync(request);

        if (response.StatusCode != HttpStatusCode.Created &&
            response.StatusCode != HttpStatusCode.MethodNotAllowed)
        {
            var responseBody = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Failed to create task collection at {collectionPath}. Status: {response.StatusCode}, Body: {responseBody}");
        }

        return collectionPath;
    }
}

/// <summary>
/// xUnit collection definition that shares the <see cref="RadicaleFixture"/>
/// across all tests in the collection.
/// </summary>
[CollectionDefinition("RadicaleCollection")]
public class RadicaleCollection : ICollectionFixture<RadicaleFixture>;
