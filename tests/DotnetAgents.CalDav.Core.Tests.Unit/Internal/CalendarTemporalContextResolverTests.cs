using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarTemporalContextResolverTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("invalid-zone", "Europe/London", null)]
    [InlineData("America/Sao_Paulo", null, "America/Sao_Paulo")]
    [InlineData("America/Sao_Paulo", "Europe/London", "America/Sao_Paulo")]
    [InlineData(null, "Europe/London", "Europe/London")]
    public void RequiredContextUsesCallerThenConfiguration(string? caller, string? configured, string? expected)
    {
        var resolver = new CalendarTemporalContextResolver(Options.Create(new CalDavOptions
        {
            EvaluationTimeZone = configured
        }));
        var result = resolver.Resolve(new CalendarTemporalContextRequest(true, caller, "To-do"));
        if (expected is not null)
        {
            result.Context!.TimeZone.ShouldBe(expected);
            result.Error.ShouldBeNull();
            return;
        }
        result.Error!.Code.ShouldBe(QueryFailureCode.InvalidInput);
        result.Error.Message.ShouldContain("IANA");
        result.Error.Message.ShouldContain("evaluationTimeZone");
        result.Error.Message.ShouldNotContain("bounded");
        if (caller is null)
            result.Error.Message.ShouldContain("CALDAV_EVALUATION_TIME_ZONE");
    }
}
