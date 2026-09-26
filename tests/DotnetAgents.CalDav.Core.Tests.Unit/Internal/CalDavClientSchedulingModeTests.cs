using System.Net;
using System.Text;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Services;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public partial class CalDavClientTests
{
    private const string SchedulingCalendarHref = "https://example.com/calendar/";
    private const string AdvertisedScheduling = "1, calendar-access, calendar-auto-schedule";
    private const string AbsentScheduling = "1, calendar-access";

    private static readonly byte[] ParticipationData = Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nORGANIZER:mailto:owner@example.com\r\n"
        + "ATTENDEE:mailto:guest@example.com\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    private static readonly byte[] OrdinaryData = Encoding.UTF8.GetBytes(
        "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nSUMMARY:Focus\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

    [Theory]
    [InlineData(false, "absent", "allowed")]
    [InlineData(false, "advertised", "blocked")]
    [InlineData(false, "unknown", "blocked")]
    [InlineData(true, "absent", "allowed")]
    [InlineData(true, "advertised", "allowed_with_server_scheduling")]
    [InlineData(true, "unknown", "blocked")]
    public void SchedulingDecision_HonorsModeAndEvidence(bool serverManaged, string evidence, string expected) =>
        DecisionName(CalendarSchedulingSafety.Decide(serverManaged, Evidence(evidence))).ShouldBe(expected);

    [Theory]
    [InlineData(AbsentScheduling, "absent")]
    [InlineData(AdvertisedScheduling, "advertised")]
    [InlineData("1, <urn:x,calendar-auto-schedule,y>", "absent")]
    [InlineData("invalid compliance", "unknown")]
    [InlineData("", "unknown")]
    public void SchedulingEvidence_ReadsOnlyWellFormedComplianceClasses(string dav, string expected) =>
        CalendarSchedulingSafety.ReadEvidence([dav]).ShouldBe(Evidence(expected));

    // Matrix: scheduling mode x OPTIONS evidence x participation. Unknown evidence blocks in every mode,
    // and only a server-managed write on an advertising server records possible side effects.
    [Theory]
    [InlineData(null, AbsentScheduling, true, true, false)]
    [InlineData(null, AdvertisedScheduling, true, false, false)]
    [InlineData(null, "invalid compliance", true, false, false)]
    [InlineData(null, null, true, false, false)]
    [InlineData(CalDavSchedulingModes.StorageOnly, AbsentScheduling, true, true, false)]
    [InlineData(CalDavSchedulingModes.StorageOnly, AdvertisedScheduling, true, false, false)]
    [InlineData(CalDavSchedulingModes.StorageOnly, AdvertisedScheduling, false, true, false)]
    [InlineData(CalDavSchedulingModes.ServerManaged, AbsentScheduling, true, true, false)]
    [InlineData(CalDavSchedulingModes.ServerManaged, AdvertisedScheduling, true, true, true)]
    [InlineData(CalDavSchedulingModes.ServerManaged, "invalid compliance", true, false, false)]
    [InlineData(CalDavSchedulingModes.ServerManaged, null, true, false, false)]
    [InlineData(CalDavSchedulingModes.ServerManaged, AdvertisedScheduling, false, true, false)]
    public async Task SchedulingMode_DecidesParticipationWritesFromFreshEvidence(
        string? mode,
        string? dav,
        bool participation,
        bool allowed,
        bool sideEffectsPossible)
    {
        var requests = new List<HttpRequestMessage>();
        var sut = CreateSut(OptionsHandler(requests, HttpStatusCode.OK, dav), SchedulingOptions(mode));
        var data = participation ? ParticipationData : OrdinaryData;
        var state = CalendarOperationProgress.CreateState();
        using (CalendarOperationProgress.Attach(state))
        {
            (await sut.IsStorageOnlyMutationAllowedAsync(SchedulingCalendarHref, data, default,
                CancellationToken.None)).ShouldBe(allowed);
            CalendarOperationProgress.SchedulingSideEffectsPossible.ShouldBe(sideEffectsPossible);
        }

        state.SchedulingSideEffectsPossible.ShouldBe(sideEffectsPossible);
        requests.Count.ShouldBe(participation ? 1 : 0);
        requests.ShouldAllBe(request => request.Method == HttpMethod.Options);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.Found)]
    public async Task SchedulingMode_ServerManagedStillRequiresSuccessfulOptions(HttpStatusCode status)
    {
        var sut = CreateSut(OptionsHandler([], status, AdvertisedScheduling),
            SchedulingOptions(CalDavSchedulingModes.ServerManaged));
        var state = CalendarOperationProgress.CreateState();
        using var scope = CalendarOperationProgress.Attach(state);

        (await sut.IsStorageOnlyMutationAllowedAsync(SchedulingCalendarHref, default, ParticipationData,
            CancellationToken.None)).ShouldBeFalse();
        state.SchedulingSideEffectsPossible.ShouldBeFalse();
    }

    [Fact]
    public async Task SchedulingMode_ServerManagedTransportFailureStillBlocks()
    {
        var sut = CreateSut(new StubHttpMessageHandler(_ => throw new HttpRequestException("unreachable")),
            SchedulingOptions(CalDavSchedulingModes.ServerManaged));

        (await sut.IsStorageOnlyMutationAllowedAsync(SchedulingCalendarHref, ParticipationData, default,
            CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task SchedulingMode_RecordingWithoutAnAttachedOperationIsHarmless()
    {
        var sut = CreateSut(OptionsHandler([], HttpStatusCode.OK, AdvertisedScheduling),
            SchedulingOptions(CalDavSchedulingModes.ServerManaged));

        (await sut.IsStorageOnlyMutationAllowedAsync(SchedulingCalendarHref, ParticipationData, default,
            CancellationToken.None)).ShouldBeTrue();
        CalendarOperationProgress.SchedulingSideEffectsPossible.ShouldBeFalse();
    }

    // Matrix: scheduling mode x OPTIONS evidence x member participation. The mode decides first; the member
    // scan runs only when the mode refuses, and a clean scan admits the DELETE without possible side effects.
    // A server-managed deletion on an advertising server is admitted without a scan and discloses possible.
    [Theory]
    [InlineData(null, AdvertisedScheduling, true, false, false, "OPTIONS PROPFIND REPORT")]
    [InlineData(null, AdvertisedScheduling, false, true, false, "OPTIONS PROPFIND REPORT PROPFIND DELETE")]
    [InlineData(CalDavSchedulingModes.StorageOnly, AdvertisedScheduling, true, false, false, "OPTIONS PROPFIND REPORT")]
    [InlineData(CalDavSchedulingModes.StorageOnly, AbsentScheduling, true, true, false, "OPTIONS DELETE")]
    [InlineData(CalDavSchedulingModes.ServerManaged, AdvertisedScheduling, true, true, true, "OPTIONS DELETE")]
    [InlineData(CalDavSchedulingModes.ServerManaged, AdvertisedScheduling, false, true, true, "OPTIONS DELETE")]
    [InlineData(CalDavSchedulingModes.ServerManaged, AbsentScheduling, true, true, false, "OPTIONS DELETE")]
    [InlineData(CalDavSchedulingModes.ServerManaged, "invalid compliance", true, false, false, "OPTIONS PROPFIND REPORT")]
    [InlineData(CalDavSchedulingModes.ServerManaged, "invalid compliance", false, true, false,
        "OPTIONS PROPFIND REPORT PROPFIND DELETE")]
    [InlineData(CalDavSchedulingModes.ServerManaged, null, true, false, false, "OPTIONS PROPFIND REPORT")]
    [InlineData(CalDavSchedulingModes.ServerManaged, null, false, true, false, "OPTIONS PROPFIND REPORT PROPFIND DELETE")]
    public async Task SchedulingMode_DecidesCollectionDeletion(
        string? mode,
        string? dav,
        bool participation,
        bool dispatched,
        bool sideEffectsPossible,
        string methods)
    {
        var server = new AutoScheduleCalendarServer { DavCompliance = dav };
        server.Add("member.ics", participation
            ? "ORGANIZER:mailto:owner@example.com\r\nATTENDEE:mailto:guest@example.com\r\n"
            : "SUMMARY:Focus\r\n");
        var state = CalendarOperationProgress.CreateState();
        using var scope = CalendarOperationProgress.Attach(state);

        var result = await CreateSut(new StubHttpMessageHandler(server.Handle), SchedulingOptions(mode))
            .DeleteCalendarCollectionAsync(AutoScheduleCalendarServer.CalendarHref, CancellationToken.None);

        result.Code.ShouldBe(dispatched
            ? CalendarCollectionDispatchCode.Dispatched
            : CalendarCollectionDispatchCode.SchedulingUnsafe);
        state.SchedulingSideEffectsPossible.ShouldBe(sideEffectsPossible);
        server.Methods.ShouldBe(methods.Split(' '));
    }

    private static CalendarSchedulingEvidence Evidence(string name) => name switch
    {
        "absent" => CalendarSchedulingEvidence.Absent,
        "advertised" => CalendarSchedulingEvidence.Advertised,
        _ => CalendarSchedulingEvidence.Unknown
    };

    private static string DecisionName(CalendarSchedulingDecision decision) => decision switch
    {
        CalendarSchedulingDecision.Allowed => "allowed",
        CalendarSchedulingDecision.AllowedWithServerScheduling => "allowed_with_server_scheduling",
        _ => "blocked"
    };

    private static CalDavOptions SchedulingOptions(string? mode) => new()
    {
        BaseUrl = "https://example.com/remote.php/dav",
        SchedulingMode = mode
    };

    private static StubHttpMessageHandler OptionsHandler(
        List<HttpRequestMessage> requests,
        HttpStatusCode status,
        string? dav) => new(request =>
        {
            requests.Add(request);
            var response = new HttpResponseMessage(status);
            if (dav is not null)
                response.Headers.TryAddWithoutValidation("DAV", dav);
            return response;
        });
}
