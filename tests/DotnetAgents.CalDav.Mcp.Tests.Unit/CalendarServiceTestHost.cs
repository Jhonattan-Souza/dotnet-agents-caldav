using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

internal sealed class CalendarServiceTestHost : IDisposable
{
    private readonly ServiceProvider _services;

    private CalendarServiceTestHost(ServiceProvider services)
    {
        _services = services;
        Service = services.GetRequiredService<ICalendarService>();
    }

    internal ICalendarService Service { get; }

    internal static CalendarServiceTestHost Create(
        ICalendarClient client,
        Action<CalDavOptions> configure,
        TimeProvider? timeProvider = null)
    {
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddCalDavCalendars(options =>
        {
            options.BaseUrl = "https://cal.example";
            options.Username = "test-user";
            options.Password = "test-password";
            configure(options);
        });
        registrations.AddSingleton<ICalendarClient>(client);
        if (timeProvider is not null)
            registrations.AddSingleton<TimeProvider>(timeProvider);
        return new CalendarServiceTestHost(registrations.BuildServiceProvider());
    }

    public void Dispose() => _services.Dispose();
}
