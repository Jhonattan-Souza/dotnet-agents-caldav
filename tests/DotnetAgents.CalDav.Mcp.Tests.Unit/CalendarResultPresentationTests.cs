using System.Collections.Immutable;
using System.Text.Json;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Mcp.Hosting;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarResultPresentationTests
{
    [Fact]
    public void NativeProtocolAdaptersUseCompleteCompatibilityText()
    {
        AssertEquivalent(CalendarProtocolToolSupport.Success(new { checkpoint = "opaque", changes = new[] { "href" } }));
        AssertEquivalent(CalendarProtocolToolSupport.Error(new CalendarProtocolException("invalid_input", "Correct the input.")));
        var overflow = CalendarProtocolToolSupport.Success(new
        {
            mutationState = "committed", calendar = new { description = new string('x', 3 * 1024 * 1024) }
        }, CalendarMutationState.Committed);
        AssertEquivalent(overflow);
        overflow.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        overflow.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe("committed");
    }

    [Theory]
    [InlineData("success")]
    [InlineData("no_change")]
    [InlineData("confirmation_declined")]
    public void TextOnlyClientReadsCompleteResultAndResourceLinks(string outcome)
    {
        var link = new ResourceLinkBlock { Uri = "caldav-exact://snapshot/test", Name = "snapshot" };
        var result = CalendarToolResult.Success(new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                outcome,
                items = new[] { new { summary = "ação \"quoted\" \\ 東京", message = new string('x', 70_000) } },
                pagination = new { nextCursor = "opaque" },
                temporalEvaluationContext = new { timeZone = "America/Sao_Paulo", source = "caller" }
            }),
            Content = [new TextContentBlock { Text = "summary" }, link]
        }).FinalizeResult();

        AssertEquivalent(result);
        result.Content.OfType<ResourceLinkBlock>().Single().ShouldBeSameAs(link);
        result.IsError.ShouldNotBe(true);
    }

    [Theory]
    [InlineData(10, "invalid_input")]
    [InlineData(70_000, "payload_too_large")]
    public void ViolationsAreEnrichedBeforeTextAndBudget(int pointerLength, string expectedCode)
    {
        var result = CalendarToolResult.WithViolations(
            () => CalendarResourceTools.CreateInputGuardError(false),
            [new CalendarInputViolation("/" + new string('x', pointerLength), "unknown_member", "Fix the member.")]);

        AssertEquivalent(result);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe(expectedCode);
        if (pointerLength == 10)
            result.StructuredContent.Value.GetProperty("violations")[0].GetProperty("pointer").GetString()
                .ShouldBe("/xxxxxxxxxx");
        CalendarQueryToolSupport.MeasureResult(result).ShouldBeLessThan(4 * 1024 * 1024);
    }

    [Fact]
    public void InvalidTemporalArgumentShapesExplainTheRequiredIdentifier()
    {
        foreach (var result in new[]
        {
            CalendarEntityTools.CreateInputGuardError(false),
            CalendarOccurrenceTools.CreateInputGuardError(false),
            CalendarTodoTools.CreateInputGuardError(false)
        })
        {
            AssertEquivalent(result);
            var message = result.StructuredContent!.Value.GetProperty("message").GetString().ShouldNotBeNull();
            message.ShouldContain("IANA");
            message.ShouldContain("evaluationTimeZone");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AdmissionAndDeadlineResultsHaveCompleteText(bool mutation)
    {
        AssertEquivalent(CalendarExecutionPolicy.CreateBusyResult(mutation));
        AssertEquivalent(CalendarExecutionPolicy.CreateDeadlineResult(mutation));
    }

    [Theory]
    [InlineData("committed")]
    [InlineData("unknown")]
    public void UnpagedOverflowPreservesMutationState(string mutationState)
    {
        var result = CalendarToolResult.Success(new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(new
            {
                outcome = "success", mutationState, data = new string('x', 3 * 1024 * 1024)
            }),
            Content = []
        }).FinalizeResult();

        AssertEquivalent(result);
        result.StructuredContent!.Value.GetProperty("code").GetString().ShouldBe("payload_too_large");
        result.StructuredContent.Value.GetProperty("mutationState").GetString().ShouldBe(mutationState);
    }

    [Fact]
    public void AdditionalTextAndSnapshotDiagnosticsConsumeHumanBudget()
    {
        var result = new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(new { snapshot = new { diagnostics = new[] { "detail" } } }),
            Content = [new TextContentBlock { Text = "summary" }, new TextContentBlock { Text = new string('x', 65_536) }]
        };
        CalendarToolResult.Success(result).FinalizeResult().StructuredContent!.Value
            .GetProperty("code").GetString().ShouldBe("payload_too_large");
    }

    [Fact]
    public void OversizedReplacementIsAlsoMeasured()
    {
        var result = new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(new { message = new string('x', 65_536) }),
            Content = []
        };
        Should.Throw<InvalidOperationException>(() => CalendarQueryToolSupport.EnsureBoundedResult(result, (_, _) => result));
    }

    [Fact]
    public void EveryCodecMatchesSdkWithEscapesSeparatorsAndCursor()
    {
        VerifyCodec(new CalendarEntityQueryPageCodec());
        VerifyCodec(new CalendarOccurrenceQueryPageCodec());
        VerifyCodec(new CalendarTodoQueryPageCodec());
    }

    [Fact]
    public void EmptyEnvelopeMustFitAndDiagnosticPagesShrinkWithoutLosingItems()
    {
        VerifyAdmission(new CalendarEntityQueryPageCodec());
        VerifyAdmission(new CalendarOccurrenceQueryPageCodec());
        VerifyAdmission(new CalendarTodoQueryPageCodec());
    }

    private static void VerifyAdmission<T>(ICalendarQueryPageCodec<T> codec)
    {
        var key = new CalendarQueryCursorKey(Options.Create(new CalDavOptions()), new byte[64]);
        var admission = new CalendarQueryPageAdmission(new CalendarQueryCursorIssuer(key));
        var snapshot = new CalendarQuerySnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(10),
            [], "[]"u8.ToArray(), 0);
        var empty = admission.Plan(snapshot, 0, 3, codec, CancellationToken.None).Value!;
        var emptyPage = codec.Materialize(snapshot, empty);
        CalendarQueryToolSupport.MeasureResult(new CallToolResult
        {
            IsError = false, StructuredContent = emptyPage.StructuredContent,
            Content = [new TextContentBlock { Text = emptyPage.HumanText }]
        }).ShouldBe(empty.MeasuredCallToolResultBytes);
        var largeContext = JsonSerializer.SerializeToUtf8Bytes(new { timeZone = new string('x', 3 * 1024 * 1024) });
        admission.Plan(snapshot with { TemporalEvaluationContextUtf8 = largeContext }, 0, 3, codec, CancellationToken.None)
            .Error!.Code.ShouldBe(QueryFailureCode.PayloadTooLarge);
        var items = Enumerable.Range(0, 3).Select(index => new StoredCalendarEntityQueryItem(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                index, diagnostics = new[] { new QueryDiagnostic("test", new string('x', 40_000), "warning") }
            }))).ToImmutableArray();
        snapshot = snapshot with { Items = items };
        var seen = new List<int>();
        for (var position = 0; position < 3; position++)
        {
            var plan = admission.Plan(snapshot, position, 3, codec, CancellationToken.None).Value!;
            plan.Items.Count.ShouldBe(1);
            var page = codec.Materialize(snapshot, plan);
            var replay = codec.Materialize(snapshot, admission.Plan(snapshot, position, 3, codec, CancellationToken.None).Value!);
            replay.HumanText.ShouldBe(page.HumanText);
            seen.Add(page.StructuredContent.GetProperty("items")[0].GetProperty("index").GetInt32());
        }
        seen.ShouldBe([0, 1, 2]);
    }

    private static void VerifyCodec<T>(ICalendarQueryPageCodec<T> codec)
    {
        var context = CalendarTemporalEvaluationContextCodec.Encode(
            new TemporalEvaluationContext("America/Sao_Paulo", TemporalEvaluationContextSource.Caller));
        var items = Enumerable.Range(0, 3).Select(index => new StoredCalendarEntityQueryItem(
            JsonSerializer.SerializeToUtf8Bytes(new { index, summary = "ação \" \\ / 東京 😀" }))).ToImmutableArray();
        var snapshot = new CalendarQuerySnapshot(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(10),
            items, "[]"u8.ToArray(), 1000, context);
        var key = new CalendarQueryCursorKey(Options.Create(new CalDavOptions()), new byte[64]);
        var admission = new CalendarQueryPageAdmission(new CalendarQueryCursorIssuer(key));
        foreach (var size in new[] { 1, 2, 3 })
        {
            var plan = admission.Plan(snapshot, 0, size, codec, CancellationToken.None).Value!;
            var page = codec.Materialize(snapshot, plan);
            var result = new CallToolResult
            {
                IsError = false, StructuredContent = page.StructuredContent,
                Content = [new TextContentBlock { Text = page.HumanText }]
            };
            AssertEquivalent(result);
            CalendarQueryToolSupport.MeasureResult(result).ShouldBe(plan.MeasuredCallToolResultBytes);
        }
    }

    private static void AssertEquivalent(CallToolResult result)
    {
        using var text = JsonDocument.Parse(result.Content.OfType<TextContentBlock>().First().Text);
        JsonElement.DeepEquals(text.RootElement, result.StructuredContent!.Value).ShouldBeTrue();
    }
}
