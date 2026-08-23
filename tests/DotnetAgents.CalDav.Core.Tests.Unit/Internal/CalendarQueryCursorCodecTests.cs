using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarQueryCursorCodecTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Key = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();

    [Fact]
    public void CursorIsStableOpaqueAndRejectsEmptySnapshotIdentity()
    {
        var cursorKey = new CalendarQueryCursorKey(OptionsFor(), Key);
        var issuer = new CalendarQueryCursorIssuer(cursorKey);
        var expires = Now.AddMinutes(10);
        var id = Guid.NewGuid();

        var first = issuer.Issue(CalendarEntityQueryPageCodec.ToolName, id, 1, expires);
        var replay = issuer.Issue(CalendarEntityQueryPageCodec.ToolName, id, 1, expires);

        first.ShouldBe(replay);
        first.Length.ShouldBeLessThanOrEqualTo(CalendarQueryCursorIssuer.MaximumCursorCharacters);
        first.ShouldNotContain("password");
        first.ShouldNotContain("cal.example");
        var authenticator = new CalendarQueryCursorAuthenticator(cursorKey, new FixedTimeProvider(Now));
        authenticator.Authenticate(first, CalendarEntityQueryPageCodec.ToolName).Code
            .ShouldBe(CalendarQueryCursorAuthenticationCode.Valid);
        var empty = issuer.Issue(CalendarEntityQueryPageCodec.ToolName, Guid.Empty, 1, expires);
        authenticator.Authenticate(empty, CalendarEntityQueryPageCodec.ToolName).Code
            .ShouldBe(CalendarQueryCursorAuthenticationCode.Invalid);
    }

    [Theory]
    [InlineData("different-password", null, null)]
    [InlineData(null, "https://cal.example/private/", null)]
    [InlineData(null, null, "Different")]
    public void CursorBindsCredentialsScopeAndDefaults(
        string? password,
        string? scope,
        string? defaultName)
    {
        var issuer = new CalendarQueryCursorIssuer(new CalendarQueryCursorKey(OptionsFor(), Key));
        var cursor = issuer.Issue(CalendarEntityQueryPageCodec.ToolName, Guid.NewGuid(), 1, Now.AddMinutes(10));
        var changed = new CalendarQueryCursorKey(
            OptionsFor(password ?? "password", scope, defaultName),
            Key);

        new CalendarQueryCursorAuthenticator(changed, new FixedTimeProvider(Now))
            .Authenticate(cursor, CalendarEntityQueryPageCodec.ToolName).Code
            .ShouldBe(CalendarQueryCursorAuthenticationCode.Invalid);
    }

    [Fact]
    public void AuthenticExpiryIsDistinctFromTamperAndWrongTool()
    {
        var cursorKey = new CalendarQueryCursorKey(OptionsFor(), Key);
        var issuer = new CalendarQueryCursorIssuer(cursorKey);
        var cursor = issuer.Issue(CalendarEntityQueryPageCodec.ToolName, Guid.NewGuid(), 1, Now.AddMinutes(10));

        new CalendarQueryCursorAuthenticator(cursorKey, new FixedTimeProvider(Now.AddMinutes(10)))
            .Authenticate(cursor, CalendarEntityQueryPageCodec.ToolName).Code
            .ShouldBe(CalendarQueryCursorAuthenticationCode.Expired);
        new CalendarQueryCursorAuthenticator(cursorKey, new FixedTimeProvider(Now))
            .Authenticate(cursor, "todos.query").Code
            .ShouldBe(CalendarQueryCursorAuthenticationCode.Invalid);
        var tampered = cursor[..^1] + (cursor[^1] == 'A' ? 'B' : 'A');
        new CalendarQueryCursorAuthenticator(cursorKey, new FixedTimeProvider(Now))
            .Authenticate(tampered, CalendarEntityQueryPageCodec.ToolName).Code
            .ShouldBe(CalendarQueryCursorAuthenticationCode.Invalid);
    }

    [Theory]
    [InlineData(0, 64)]
    [InlineData(63, 63)]
    [InlineData(65, 65)]
    public void CursorKeyRejectsNullOptionsAndNonExactKeyMaterial(int optionMarker, int keyLength)
    {
        var options = optionMarker == 0 ? null : OptionsFor();
        var key = new byte[keyLength];

        if (options is null)
            Should.Throw<ArgumentNullException>(() => new CalendarQueryCursorKey(null!, key));
        else
            Should.Throw<ArgumentException>(() => new CalendarQueryCursorKey(options, key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("+")]
    [InlineData("A")]
    public void MalformedLexicalCursorsAreRejected(string cursor)
    {
        var key = new CalendarQueryCursorKey(OptionsFor(), Key);

        new CalendarQueryCursorAuthenticator(key, new FixedTimeProvider(Now))
            .Authenticate(cursor, CalendarEntityQueryPageCodec.ToolName).Code
            .ShouldBe(CalendarQueryCursorAuthenticationCode.Invalid);
    }

    [Fact]
    public void OversizedAndStructurallyShortCursorsAreRejected()
    {
        var key = new CalendarQueryCursorKey(OptionsFor(), Key);
        var authenticator = new CalendarQueryCursorAuthenticator(key, new FixedTimeProvider(Now));
        var exactlyTwentyEightBytes = CalendarQueryCursorIssuer.Base64UrlEncode(new byte[28]);

        authenticator.Authenticate(new string('A', 2049), CalendarEntityQueryPageCodec.ToolName).Code
            .ShouldBe(CalendarQueryCursorAuthenticationCode.Invalid);
        authenticator.Authenticate(exactlyTwentyEightBytes, CalendarEntityQueryPageCodec.ToolName).Code
            .ShouldBe(CalendarQueryCursorAuthenticationCode.Invalid);
    }

    [Fact]
    public void AuthenticatedPayloadStillRequiresPositivePositionAndExpiry()
    {
        var key = new CalendarQueryCursorKey(OptionsFor(), Key);
        var issuer = new CalendarQueryCursorIssuer(key);
        var authenticator = new CalendarQueryCursorAuthenticator(key, new FixedTimeProvider(Now));

        authenticator.Authenticate(
                issuer.Issue(CalendarEntityQueryPageCodec.ToolName, Guid.NewGuid(), 0, Now.AddMinutes(10)),
                CalendarEntityQueryPageCodec.ToolName).Code
            .ShouldBe(CalendarQueryCursorAuthenticationCode.Invalid);
        authenticator.Authenticate(
                issuer.Issue(CalendarEntityQueryPageCodec.ToolName, Guid.NewGuid(), 1, DateTimeOffset.UnixEpoch),
                CalendarEntityQueryPageCodec.ToolName).Code
            .ShouldBe(CalendarQueryCursorAuthenticationCode.Invalid);
    }

    [Fact]
    public void CursorContextBindsTodoDefaultCanonicalScopeAndRequestTimeout()
    {
        var baseline = new CalendarQueryCursorKey(OptionsFor(), Key);
        var cursor = new CalendarQueryCursorIssuer(baseline).Issue(
            CalendarEntityQueryPageCodec.ToolName,
            Guid.NewGuid(),
            1,
            Now.AddMinutes(10));
        var changedOptions = OptionsFor();
        changedOptions.Value.CalendarHrefs = "https://cal.example/z/, https://cal.example/a/, https://cal.example/a/";
        changedOptions.Value.DefaultTodoCalendarName = " Todos ";
        changedOptions.Value.RequestTimeout = TimeSpan.FromSeconds(31);

        new CalendarQueryCursorAuthenticator(
                new CalendarQueryCursorKey(changedOptions, Key),
                new FixedTimeProvider(Now))
            .Authenticate(cursor, CalendarEntityQueryPageCodec.ToolName).Code
            .ShouldBe(CalendarQueryCursorAuthenticationCode.Invalid);
    }

    [Fact]
    public void CursorContextBindsConfiguredTemporalEvaluationContextWithoutDisclosingTheChange()
    {
        var baselineOptions = OptionsFor();
        baselineOptions.Value.EvaluationTimeZone = "America/Sao_Paulo";
        var baseline = new CalendarQueryCursorKey(baselineOptions, Key);
        var cursor = new CalendarQueryCursorIssuer(baseline).Issue(
            CalendarEntityQueryPageCodec.ToolName,
            Guid.NewGuid(),
            1,
            Now.AddMinutes(10));
        var changedOptions = OptionsFor();
        changedOptions.Value.EvaluationTimeZone = "Europe/London";

        var result = new CalendarQueryCursorAuthenticator(
                new CalendarQueryCursorKey(changedOptions, Key),
                new FixedTimeProvider(Now))
            .Authenticate(cursor, CalendarEntityQueryPageCodec.ToolName);

        result.Code.ShouldBe(CalendarQueryCursorAuthenticationCode.Invalid);
        cursor.ShouldNotContain("America");
        cursor.ShouldNotContain("Sao_Paulo");
    }

    [Fact]
    public void CursorAuthenticatesTheExactEffectiveCallerContextBinding()
    {
        var key = new CalendarQueryCursorKey(OptionsFor(), Key);
        var issuer = new CalendarQueryCursorIssuer(key);
        var authenticator = new CalendarQueryCursorAuthenticator(key, new FixedTimeProvider(Now));
        var contextA = CalendarTemporalEvaluationContextCodec.Encode(new TemporalEvaluationContext(
            "America/Sao_Paulo",
            TemporalEvaluationContextSource.Caller));
        var contextB = CalendarTemporalEvaluationContextCodec.Encode(new TemporalEvaluationContext(
            "Europe/London",
            TemporalEvaluationContextSource.Caller));
        var protectedCursor = issuer.Issue(
            CalendarEntityQueryPageCodec.ToolName,
            Guid.NewGuid(),
            1,
            Now.AddMinutes(10),
            contextA);

        var cursor = authenticator.Authenticate(protectedCursor, CalendarEntityQueryPageCodec.ToolName)
            .Cursor.ShouldNotBeNull();

        authenticator.MatchesTemporalContext(cursor, contextA.Span).ShouldBeTrue();
        authenticator.MatchesTemporalContext(cursor, contextB.Span).ShouldBeFalse();
        protectedCursor.ShouldNotContain("America");
        protectedCursor.ShouldNotContain("London");
    }

    [Fact]
    public void IssuerRejectsProtectedCursorBeyondTheLexicalLimit()
    {
        var issuer = new CalendarQueryCursorIssuer(new CalendarQueryCursorKey(OptionsFor(), Key));

        Should.Throw<InvalidOperationException>(() => issuer.Issue(
            new string('x', CalendarQueryCursorIssuer.MaximumCursorCharacters),
            Guid.NewGuid(),
            1,
            Now.AddMinutes(10)));
    }

    private static IOptions<CalDavOptions> OptionsFor(
        string password = "password",
        string? scope = null,
        string? defaultName = null) => Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = "user",
            Password = password,
            CalendarHrefs = scope,
            DefaultEventCalendarName = defaultName
        });

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
