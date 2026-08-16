using DotnetAgents.CalDav.Core.Abstractions;

namespace DotnetAgents.CalDav.Core.Services;

internal sealed class CalendarEntityIdentityGenerator : ICalendarEntityIdentityGenerator
{
    public string CreateUid() => Guid.NewGuid().ToString("D");
}
