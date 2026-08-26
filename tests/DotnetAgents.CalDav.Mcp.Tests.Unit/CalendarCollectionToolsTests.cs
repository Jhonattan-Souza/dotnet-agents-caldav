using System.Text.Json;
using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarCollectionToolsTests
{
    [Fact]
    public async Task DeleteRawAsync_FirstRoundReviewsExactHrefAndRequiresConfirmation()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var module = Substitute.For<ICalendarCollectionModule>();
        var descriptor = Descriptor(href);
        var binding = new CalendarCollectionDeleteReviewBinding(href, "digest");
        module.ReviewDeleteAsync(Arg.Any<CalendarCollectionDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarCollectionDeleteReviewResult(null, binding, descriptor));
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(module, time);

        var exception = await Should.ThrowAsync<InputRequiredException>(() => sut.DeleteRawAsync(
            new Dictionary<string, JsonElement>
            {
                ["href"] = JsonSerializer.SerializeToElement(href)
            },
            null,
            null,
            true,
            CancellationToken.None));

        exception.Result.RequestState.ShouldNotBeNullOrWhiteSpace();
        var inputRequests = exception.Result.InputRequests!;
        inputRequests.ShouldContainKey("confirm_delete");
        inputRequests["confirm_delete"].ElicitationParams!.Message.ShouldContain(href);
        await module.DidNotReceive().ExecuteConfirmedDeleteAsync(
            Arg.Any<CalendarCollectionDeleteRequest>(),
            Arg.Any<CalendarCollectionDeleteReviewBinding>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRawAsync_AcceptedContinuationExecutesOnlyAfterStateMatches()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var module = Substitute.For<ICalendarCollectionModule>();
        var descriptor = Descriptor(href);
        var binding = new CalendarCollectionDeleteReviewBinding(href, "digest");
        module.ReviewDeleteAsync(Arg.Any<CalendarCollectionDeleteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CalendarCollectionDeleteReviewResult(null, binding, descriptor));
        module.ExecuteConfirmedDeleteAsync(
                Arg.Any<CalendarCollectionDeleteRequest>(),
                Arg.Any<CalendarCollectionDeleteReviewBinding>(),
                Arg.Any<CancellationToken>())
            .Returns(CalendarCollectionDeleteResult.Success(descriptor));
        var time = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-16T12:00:00Z"));
        var sut = CreateTool(module, time);
        var first = await Should.ThrowAsync<InputRequiredException>(() => sut.DeleteRawAsync(
            new Dictionary<string, JsonElement>
            {
                ["href"] = JsonSerializer.SerializeToElement(href)
            },
            null,
            null,
            true,
            CancellationToken.None));

        var result = await sut.DeleteRawAsync(
            new Dictionary<string, JsonElement>
            {
                ["href"] = JsonSerializer.SerializeToElement(href)
            },
            first.Result.RequestState,
            new Dictionary<string, InputResponse>
            {
                ["confirm_delete"] = InputResponse.FromElicitResult(new ElicitResult
                {
                    Action = "accept",
                    Content = new Dictionary<string, JsonElement>
                    {
                        ["confirm"] = JsonSerializer.SerializeToElement(true)
                    }
                })
            },
            true,
            CancellationToken.None);

        result.IsError.ShouldBe(false);
        result.StructuredContent!.Value.GetProperty("outcome").GetString().ShouldBe("success");
        await module.Received(1).ExecuteConfirmedDeleteAsync(
            new CalendarCollectionDeleteRequest(href),
            binding,
            Arg.Any<CancellationToken>());
    }

    private static CalendarCollectionTools CreateTool(
        ICalendarCollectionModule module,
        TimeProvider time) => new(
            module,
            new CalendarMutationRequestStateProtector(
                time,
                Options.Create(new CalDavOptions
                {
                    BaseUrl = "https://cal.example",
                    Username = "user",
                    Password = "password"
                }),
                Enumerable.Range(0, 64).Select(value => (byte)value).ToArray()),
            time,
            new CalendarMutationAdmission(time));

    private static CalendarDescriptor Descriptor(string href) => new()
    {
        Href = href,
        DisplayName = "Tasks",
        DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
        EventSupport = EntityKindSupport.NotAdvertised,
        TodoSupport = EntityKindSupport.Advertised
    };
}
