using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Mcp.Tools;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

public sealed class CalendarEntityCursorProtectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Key = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();

    [Fact]
    public void ProtectUsesUniqueBoundedNonCredentialBearingTokens()
    {
        var protector = CreateProtector(new MutableTimeProvider(Now));

        var first = protector.Protect("query", "https://cal.example/a/", "https://cal.example/a/1.ics");
        var second = protector.Protect("query", "https://cal.example/a/", "https://cal.example/a/1.ics");

        first.ShouldNotBe(second);
        first.Length.ShouldBeLessThanOrEqualTo(CalendarEntityCursorProtector.MaximumCursorCharacters);
        first.ShouldNotContain("user");
        first.ShouldNotContain("password");
        first.ShouldNotContain("cal.example");
        protector.TryUnprotect(first, "query", out var continuation, out var expired).ShouldBeTrue();
        expired.ShouldBeFalse();
        continuation.ShouldBe(new CalendarEntityContinuation(
            "https://cal.example/a/", "https://cal.example/a/1.ics"));
    }

    [Fact]
    public void CredentialBindingHasNoDelimiterCollision()
    {
        var first = CreateProtector(
            new MutableTimeProvider(Now),
            OptionsFor("alpha\u001fbeta", "gamma"));
        var redistributed = CreateProtector(
            new MutableTimeProvider(Now),
            OptionsFor("alpha", "beta\u001fgamma"));
        var cursor = first.Protect("query", "https://cal.example/a/", "https://cal.example/a/1.ics");

        redistributed.TryUnprotect(cursor, "query", out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void NonCanonicalAlternatePadBitsAreRejected()
    {
        var protector = CreateProtector(new MutableTimeProvider(Now));
        var context = "query";
        var canonical = protector.Protect(context, "https://cal.example/a/", "https://cal.example/a/1.ics");
        while (canonical.Length % 4 == 0)
        {
            context += "x";
            canonical = protector.Protect(context, "https://cal.example/a/", "https://cal.example/a/1.ics");
        }
        var alternate = WithAlternatePadBits(canonical);

        Convert.FromBase64String(ToPaddedBase64(canonical))
            .ShouldBe(Convert.FromBase64String(ToPaddedBase64(alternate)));
        protector.TryUnprotect(alternate, context, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void RestartContextMismatchAndExpiryAreRejected()
    {
        var time = new MutableTimeProvider(Now);
        var protector = CreateProtector(time);
        var cursor = protector.Protect("occurrences", "https://cal.example/a/", "https://cal.example/a/1.ics");

        CreateProtector(time, key: Enumerable.Repeat((byte)7, 64).ToArray())
            .TryUnprotect(cursor, "occurrences", out _, out _).ShouldBeFalse();
        protector.TryUnprotect(cursor, "todos", out _, out _).ShouldBeFalse();
        time.Advance(TimeSpan.FromMinutes(10));
        protector.TryUnprotect(cursor, "occurrences", out _, out var expired).ShouldBeFalse();
        expired.ShouldBeTrue();
    }

    [Fact]
    public void OversizedCursorIsRefusedWithoutReturningPartialState()
    {
        var protector = CreateProtector(new MutableTimeProvider(Now));

        protector.TryProtect("query", "https://cal.example/a/", new string('x', 4096), out var cursor)
            .ShouldBeFalse();
        cursor.ShouldBeNull();
    }

    private static CalendarEntityCursorProtector CreateProtector(
        TimeProvider timeProvider,
        IOptions<CalDavOptions>? options = null,
        byte[]? key = null) => new(timeProvider, options ?? OptionsFor(), key ?? Key);

    private static IOptions<CalDavOptions> OptionsFor(
        string username = "user",
        string password = "password") => Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            Username = username,
            Password = password
        });

    private static string WithAlternatePadBits(string value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var index = alphabet.IndexOf(value[^1], StringComparison.Ordinal);
        return value[..^1] + alphabet[index ^ 1];
    }

    private static string ToPaddedBase64(string value)
    {
        var standard = value.Replace('-', '+').Replace('_', '/');
        return standard + new string('=', (4 - standard.Length % 4) % 4);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        internal void Advance(TimeSpan amount) => utcNow += amount;
    }
}
