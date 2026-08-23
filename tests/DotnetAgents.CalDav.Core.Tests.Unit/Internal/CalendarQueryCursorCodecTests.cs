using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
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
