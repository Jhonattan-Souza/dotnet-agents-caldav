using System.Net;
using System.Text;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Ical;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public partial class CalDavClientTests
{
    [Theory]
    [InlineData("1, calendar-access", true)]
    [InlineData("1, calendar-access, vendor.feature", true)]
    [InlineData("1, calendar-access, !#$%&'*+-.^_`|~", true)]
    [InlineData("1, <urn:x,calendar-auto-schedule,y>", true)]
    [InlineData(",1,,calendar-access,", true)]
    [InlineData("1, <urn:x,calendar-auto-schedule,y>, calendar-auto-schedule", false)]
    [InlineData("1, calendar-access, calendar-auto-schedule", false)]
    [InlineData("1, CALENDAR-AUTO-SCHEDULE", false)]
    [InlineData("1, invalid compliance", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public async Task SchedulingSafety_RequiresFreshSuccessfulComplianceEvidence(string? dav, bool allowed)
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (dav is not null)
                response.Headers.TryAddWithoutValidation("DAV", dav);
            return response;
        });
        var sut = CreateSut(handler);
        var data = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nORGANIZER:mailto:user@example.com\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

        (await sut.IsStorageOnlyMutationAllowedAsync("https://example.com/calendar/", data, default,
            CancellationToken.None)).ShouldBe(allowed);
        (await sut.IsStorageOnlyMutationAllowedAsync("https://example.com/calendar/", default, data,
            CancellationToken.None)).ShouldBe(allowed);
        requests.Count.ShouldBe(2);
        requests.ShouldAllBe(request => request.Method == HttpMethod.Options);
    }

    [Fact]
    public async Task SchedulingSafety_DoesNotRequestOptionsForOrdinaryData()
    {
        var handler = new StubHttpMessageHandler(_ => throw new InvalidOperationException("Unexpected HTTP work"));
        var sut = CreateSut(handler);
        var data = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nSUMMARY:ATTENDEE@example.com\r\nEND:VCALENDAR\r\n");

        (await sut.IsStorageOnlyMutationAllowedAsync("https://example.com/calendar/", data, data,
            CancellationToken.None)).ShouldBeTrue();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.Found)]
    public async Task SchedulingSafety_DoesNotTreatUnsuccessfulOptionsAsAbsence(HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(status);
            response.Headers.Add("DAV", "1, calendar-access");
            return response;
        });
        var sut = CreateSut(handler);
        var data = Encoding.UTF8.GetBytes("ATTENDEE:mailto:user@example.com\r\n");

        (await sut.IsStorageOnlyMutationAllowedAsync("https://example.com/calendar/", data, default,
            CancellationToken.None)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("1, calendar-access, calendar-auto-schedule")]
    [InlineData("invalid compliance")]
    [InlineData("")]
    [InlineData(null)]
    public async Task CollectionDelete_SchedulingEvidenceBlocksDispatchWithoutEnumeration(string? dav)
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (dav is not null)
                response.Headers.TryAddWithoutValidation("DAV", dav);
            return response;
        });
        var result = await CreateSut(handler).DeleteCalendarCollectionAsync("https://example.com/calendar/", CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionDispatchCode.SchedulingUnsafe);
        requests.ShouldHaveSingleItem().Method.ShouldBe(HttpMethod.Options);
    }

    [Theory]
    [InlineData("1, calendar-access, vendor.feature")]
    [InlineData("1, <urn:x,calendar-auto-schedule,y>")]
    [InlineData(",1,,calendar-access,")]
    public async Task CollectionDelete_ValidExtensionEvidenceAllowsOneDelete(string dav)
    {
        var methods = new List<string>();
        var handler = new StubHttpMessageHandler(request =>
        {
            methods.Add(request.Method.Method);
            var response = new HttpResponseMessage(request.Method == HttpMethod.Options ? HttpStatusCode.OK : HttpStatusCode.NoContent);
            response.Headers.TryAddWithoutValidation("DAV", dav);
            return response;
        });

        var result = await CreateSut(handler).DeleteCalendarCollectionAsync("https://example.com/calendar/", CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionDispatchCode.Dispatched);
        methods.ShouldBe(["OPTIONS", "DELETE"]);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("cancellation")]
    [InlineData("polly_timeout")]
    [InlineData("circuit")]
    [InlineData("limiter")]
    public async Task SchedulingSafety_TransportFailureIsNotSchedulingAbsence(string failure)
    {
        var handler = new StubHttpMessageHandler(_ => throw failure switch
        {
            "http" => new HttpRequestException("collector-free transport failure"),
            "io" => new IOException("transport failure"),
            "timeout" => new TimeoutException(),
            "polly_timeout" => new TimeoutRejectedException(),
            "circuit" => new BrokenCircuitException(),
            "limiter" => new RateLimiterRejectedException(),
            _ => new OperationCanceledException()
        });
        var sut = CreateSut(handler);
        var data = Encoding.UTF8.GetBytes("ORGANIZER:mailto:x\r\n");
        (await sut.IsStorageOnlyMutationAllowedAsync("https://example.com/calendar/", data, default,
            CancellationToken.None)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("group.ATTENDEE")]
    [InlineData("group.ORGANIZER")]
    public async Task SchedulingSafety_GroupedPriorAndProposedDataBothRequireFreshOptions(string property)
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Add("DAV", "1, calendar-access, calendar-auto-schedule");
            return response;
        });
        var client = CreateSut(handler);
        var data = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\n" + property
            + ":mailto:owner@example.com\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");

        (await client.IsStorageOnlyMutationAllowedAsync("https://example.com/calendar/", data, default,
            CancellationToken.None)).ShouldBeFalse();
        (await client.IsStorageOnlyMutationAllowedAsync("https://example.com/calendar/", default, data,
            CancellationToken.None)).ShouldBeFalse();
        requests.Count.ShouldBe(2);
        requests.ShouldAllBe(request => request.Method == HttpMethod.Options);
    }

    [Theory]
    [InlineData("group.ATTENDEE:mailto:x", "ATTENDEE")]
    [InlineData("GROUP.organizer;CN=Person:mailto:x", "ORGANIZER")]
    [InlineData("very-long-group-prefix.ATTENDEE:mailto:x", "ATTENDEE")]
    [InlineData("gro\r\n up.ORGANIZER:mailto:x", "ORGANIZER")]
    [InlineData("group.ATTE\n\tNDEE:mailto:x", "ATTENDEE")]
    [InlineData("group\r\n .orga\r\n nizer:mailto:x", "ORGANIZER")]
    public void SchedulingSafety_UsesTheAuthoritativeParsersGroupedPropertyIdentity(string property, string identity)
    {
        var data = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\n" + property + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        CalendarContentDocument.Parse(data).Properties.ShouldContain(value => value.Name.Equals(identity, StringComparison.OrdinalIgnoreCase));
        CalendarSchedulingSafety.HasParticipation(data).ShouldBeTrue();
    }

    [Theory]
    [InlineData("SUMMARY:group.ATTENDEE:mailto:x")]
    [InlineData("SUMMARY;X-REF=\"group.ORGANIZER:mailto:x\":text")]
    [InlineData("SUMMARY:meeting\r\n group.ATTENDEE:mailto:x")]
    [InlineData("group.SUMMARY:ORGANIZER:mailto:x")]
    [InlineData("ATTENDEE.SUMMARY:text")]
    public void SchedulingSafety_DoesNotConfuseGroupsParametersOrValuesWithParticipation(string property)
    {
        var data = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\n" + property + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        CalendarContentDocument.Parse(data).Properties.ShouldContain(value => value.Name == "SUMMARY");
        CalendarSchedulingSafety.HasParticipation(data).ShouldBeFalse();
    }

    [Fact]
    public void SchedulingSafety_CalendarParserRejectsBareCarriageReturnBeforeAnUnscannableProperty()
    {
        var data = Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\rBEGIN:VEVENT\rgroup.ATTENDEE:mailto:x\rEND:VEVENT\rEND:VCALENDAR\r");
        Should.Throw<FormatException>(() => CalendarContentDocument.Parse(data));
    }

    [Theory]
    [InlineData("ORGANIZER:mailto:x")]
    [InlineData("organizer;CN=Person:mailto:x")]
    [InlineData("ATTENDEE:mailto:x")]
    [InlineData("ATTE\r\n NDEE:mailto:x")]
    [InlineData("ORG\n\tANIZER:mailto:x")]
    public void SchedulingSafety_DetectsFoldedAndCaseInsensitiveParticipation(string property) =>
        CalendarSchedulingSafety.HasParticipation(Encoding.UTF8.GetBytes("BEGIN:VCALENDAR\r\n" + property + "\r\nEND:VCALENDAR\r\n")).ShouldBeTrue();
}
