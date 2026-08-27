using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;

namespace DotnetAgents.CalDav.Core.Tests.Unit;

internal static class CalendarOperationDiscoveryResultFactory
{
    internal static CalendarOperationDiscoveryResult Create(
        CalendarDiscoveryResult discovery,
        CalendarSelectionResult eventDefault,
        CalendarSelectionResult todoDefault) => new(
            CalendarDiscoveryKey.Create(
                new CalDavOptions
                {
                    BaseUrl = "https://cal.example/",
                    Username = "scripted-principal"
                },
                CalendarOperationContextGeneration.Create()),
            discovery,
            eventDefault,
            todoDefault);
}
