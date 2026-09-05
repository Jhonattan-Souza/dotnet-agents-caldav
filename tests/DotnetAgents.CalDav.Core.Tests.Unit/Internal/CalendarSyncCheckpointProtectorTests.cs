using System.Text.Json;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public class CalendarSyncCheckpointProtectorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthenticCheckpointRetainsCollectionTokenInitialStateAndLimitMode(bool omitLimit)
    {
        var protector = new CalendarSyncCheckpointProtector();
        var binding = protector.ConfigurationBinding(Configuration());
        var state = new CalendarSyncCheckpoint("https://cal.example/cal/", "urn:sync:one", true, binding, OmitLimit: omitLimit);

        var handle = protector.Protect(state);

        handle.Length.ShouldBe(36);
        handle.ShouldStartWith("cs1_");
        handle[4..].ShouldAllBe(character => character >= '0' && character <= '9' || character >= 'a' && character <= 'f');
        protector.Unprotect(handle, binding).ShouldBe(state);
    }

    [Fact]
    public void ConfigurationBindingIsStableWithinSessionButDifferentAcrossSessions()
    {
        var options = Configuration();
        var protector = new CalendarSyncCheckpointProtector();
        var binding = protector.ConfigurationBinding(options);

        protector.ConfigurationBinding(options).ShouldBe(binding);
        new CalendarSyncCheckpointProtector().ConfigurationBinding(options).ShouldNotBe(binding);
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("username")]
    [InlineData("password")]
    [InlineData("scope")]
    [InlineData("configuration")]
    public void ChangedAuthorizationOrConfigurationRequiresExplicitReset(string change)
    {
        var options = Configuration();
        var protector = new CalendarSyncCheckpointProtector();
        var original = protector.ConfigurationBinding(options);
        var checkpoint = protector.Protect(new("https://cal.example/cal/", "urn:sync:one", false, original));
        switch (change)
        {
            case "origin": options.BaseUrl = "https://other.example"; break;
            case "username": options.Username = "other"; break;
            case "password": options.Password = "other"; break;
            case "scope": options.CalendarHrefs = "https://cal.example/other/"; break;
            case "configuration": options.RequestTimeout = TimeSpan.FromSeconds(15); break;
        }

        Should.Throw<CalendarProtocolException>(() => protector.Unprotect(checkpoint,
            protector.ConfigurationBinding(options))).Code.ShouldBe("sync_reset_required");
    }

    [Fact]
    public void TamperingAndNewSessionCannotReuseCheckpoint()
    {
        var protector = new CalendarSyncCheckpointProtector();
        var binding = protector.ConfigurationBinding(Configuration());
        var checkpoint = protector.Protect(new("https://cal.example/cal/", "urn:sync:one", false, binding));
        var changedPayload = checkpoint[..^1] + (checkpoint[^1] == '0' ? '1' : '0');

        Should.Throw<CalendarProtocolException>(() => protector.Unprotect(changedPayload, binding)).Code.ShouldBe("sync_reset_required");
        Should.Throw<CalendarProtocolException>(() => new CalendarSyncCheckpointProtector().Unprotect(checkpoint, binding))
            .Code.ShouldBe("sync_reset_required");
        protector.Unprotect(checkpoint, binding).SyncToken.ShouldBe("urn:sync:one");
    }

    [Theory]
    [InlineData("")]
    [InlineData("untrusted")]
    [InlineData("a.b.c")]
    [InlineData("!.!")]
    [InlineData("YQ.Yg")]
    [InlineData("cs1_00000000000000000000000000000000")]
    public void MalformedCheckpointsGiveRecoveryWithoutLeakingTheirContent(string value)
    {
        var exception = Should.Throw<CalendarProtocolException>(() => new CalendarSyncCheckpointProtector().Unprotect(value, "binding"));
        exception.Code.ShouldBe("sync_reset_required");
        exception.Message.ShouldContain("calendarHref and no checkpoint");
    }

    [Fact]
    public void OversizedStateDoesNotEvictPreviouslyUsableCheckpoints()
    {
        var protector = new CalendarSyncCheckpointProtector();
        var retained = protector.Protect(new("https://cal.example/cal/", "urn:sync:prior", false, "binding"));
        Should.Throw<CalendarProtocolException>(() => protector.Unprotect(new string('a', 37), "binding"))
            .Code.ShouldBe("sync_reset_required");
        Should.Throw<CalendarProtocolException>(() => protector.Protect(new(
                "https://cal.example/cal/", new string('a', (int)CalendarSyncCheckpointProtector.MaximumRetainedBytes), false, "binding")))
            .Code.ShouldBe("payload_too_large");
        protector.Unprotect(retained, "binding").SyncToken.ShouldBe("urn:sync:prior");
        protector.RetainedCheckpointCount.ShouldBe(1);
    }

    [Fact]
    public void UnsupportedVersionAndIncompleteStateAreNotRetained()
    {
        var protector = new CalendarSyncCheckpointProtector();
        foreach (var state in new[]
        {
            new CalendarSyncCheckpoint("https://cal.example/cal/", "urn:sync:one", false, "binding", 2),
            new CalendarSyncCheckpoint("", "urn:sync:one", false, "binding"),
            new CalendarSyncCheckpoint("https://cal.example/cal/", "", false, "binding"),
            new CalendarSyncCheckpoint("https://cal.example/cal/", "urn:sync:one", false, "")
        })
            Should.Throw<CalendarProtocolException>(() => protector.Unprotect(protector.Protect(state), "binding"))
                .Code.ShouldBe("sync_reset_required");
        protector.RetainedCheckpointCount.ShouldBe(0);
    }

    [Fact]
    public void NoChangePollingReusesOneHandleAndOneRetainedState()
    {
        var protector = new CalendarSyncCheckpointProtector();
        var state = new CalendarSyncCheckpoint("https://cal.example/cal/", "urn:sync:one", false, "binding", OmitLimit: true);
        var handle = protector.Protect(state);

        for (var index = 0; index < 4 * CalendarSyncCheckpointProtector.MaximumCheckpoints; index++)
            protector.Protect(state with { }).ShouldBe(handle);

        protector.RetainedCheckpointCount.ShouldBe(1);
        protector.RetainedBytes.ShouldBe(JsonSerializer.SerializeToUtf8Bytes(state).Length);
    }

    [Fact]
    public void AdvanceKeepsThePriorImmutableStateAvailableForExactReplay()
    {
        var protector = new CalendarSyncCheckpointProtector();
        var prior = new CalendarSyncCheckpoint("https://cal.example/cal/", "urn:sync:one", true, "binding", OmitLimit: true);
        var advanced = prior with { SyncToken = "urn:sync:two", Initial = false };
        var priorHandle = protector.Protect(prior);
        var advancedHandle = protector.Protect(advanced);

        advancedHandle.ShouldNotBe(priorHandle);
        protector.Unprotect(priorHandle, "binding").ShouldBe(prior);
        protector.Unprotect(advancedHandle, "binding").ShouldBe(advanced);
        protector.Unprotect(priorHandle, "binding").ShouldBe(prior);
        protector.RetainedCheckpointCount.ShouldBe(2);
    }

    [Fact]
    public void DedupePreservesCollectionInitialAndLimitModeDistinctions()
    {
        var protector = new CalendarSyncCheckpointProtector();
        var original = new CalendarSyncCheckpoint("https://cal.example/cal/", "urn:sync:one", false, "binding");
        var states = new[]
        {
            original,
            original with { CalendarHref = "https://cal.example/other/" },
            original with { Initial = true },
            original with { OmitLimit = true },
            original with { ConfigurationBinding = "different-binding" }
        };

        var handles = states.Select(protector.Protect).ToArray();

        handles.Distinct().Count().ShouldBe(states.Length);
        for (var index = 0; index < states.Length; index++)
            protector.Unprotect(handles[index], states[index].ConfigurationBinding).ShouldBe(states[index]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EntryLimitEvictsLeastRecentlyUsedStateAndHonorsReplayOrPollingActivity(bool touchByPolling)
    {
        var protector = new CalendarSyncCheckpointProtector();
        var states = Enumerable.Range(0, CalendarSyncCheckpointProtector.MaximumCheckpoints)
            .Select(index => new CalendarSyncCheckpoint("https://cal.example/cal/", $"urn:sync:{index}", false, "binding")).ToArray();
        var handles = states.Select(protector.Protect).ToArray();
        if (touchByPolling)
            protector.Protect(states[0] with { }).ShouldBe(handles[0]);
        else
            protector.Unprotect(handles[0], "binding").ShouldBe(states[0]);

        var newest = protector.Protect(states[0] with { SyncToken = "urn:sync:newest" });

        protector.RetainedCheckpointCount.ShouldBe(CalendarSyncCheckpointProtector.MaximumCheckpoints);
        Should.Throw<CalendarProtocolException>(() => protector.Unprotect(handles[1], "binding")).Code.ShouldBe("sync_reset_required");
        protector.Unprotect(handles[0], "binding").ShouldBe(states[0]);
        protector.Unprotect(newest, "binding").SyncToken.ShouldBe("urn:sync:newest");
        var replacement = protector.Protect(states[1]);
        replacement.ShouldNotBe(handles[1]);
        protector.Unprotect(replacement, "binding").ShouldBe(states[1]);
        protector.RetainedBytes.ShouldBeLessThanOrEqualTo(CalendarSyncCheckpointProtector.MaximumRetainedBytes);
    }

    [Fact]
    public void SerializedByteBudgetEvictsBeforeEntryCountLimit()
    {
        var protector = new CalendarSyncCheckpointProtector();
        var href = "https://cal.example/" + new string('c', 8000) + "/";
        var padding = new string('t', 8000);
        CalendarSyncCheckpoint State(int index) => new(href, $"urn:sync:{index:D4}:{padding}", false, "binding");
        var entryBytes = JsonSerializer.SerializeToUtf8Bytes(State(0)).Length;
        var capacity = (int)(CalendarSyncCheckpointProtector.MaximumRetainedBytes / entryBytes);
        capacity.ShouldBeLessThan(CalendarSyncCheckpointProtector.MaximumCheckpoints);
        var oldest = protector.Protect(State(0));
        for (var index = 1; index < capacity; index++)
            protector.Protect(State(index));

        var newest = protector.Protect(State(capacity));

        protector.RetainedCheckpointCount.ShouldBe(capacity);
        protector.RetainedBytes.ShouldBe((long)capacity * entryBytes);
        protector.RetainedBytes.ShouldBeLessThanOrEqualTo(CalendarSyncCheckpointProtector.MaximumRetainedBytes);
        Should.Throw<CalendarProtocolException>(() => protector.Unprotect(oldest, "binding")).Code.ShouldBe("sync_reset_required");
        protector.Unprotect(newest, "binding").ShouldBe(State(capacity));
    }

    [Fact]
    public async Task ConcurrentPublicationOfIdenticalStateRetainsOneHandle()
    {
        var protector = new CalendarSyncCheckpointProtector();
        var state = new CalendarSyncCheckpoint("https://cal.example/cal/", "urn:sync:one", false, "binding", OmitLimit: true);

        var handles = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => protector.Protect(state with { }))));

        handles.Distinct().Count().ShouldBe(1);
        protector.RetainedCheckpointCount.ShouldBe(1);
        protector.Unprotect(handles[0], "binding").ShouldBe(state);
    }

    private static CalDavOptions Configuration() => new()
    {
        BaseUrl = "https://cal.example", Username = "user", Password = "password", CalendarHrefs = "https://cal.example/cal/"
    };
}
