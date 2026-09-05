using System.Net;
using System.Text;
using DotnetAgents.CalDav.Core.Internal;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public partial class CalDavClientTests
{
    [Theory]
    [InlineData("1, calendar-access", true)]
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
    [InlineData("http")]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("cancellation")]
    public async Task SchedulingSafety_TransportFailureIsNotSchedulingAbsence(string failure)
    {
        var handler = new StubHttpMessageHandler(_ => throw failure switch
        {
            "http" => new HttpRequestException("collector-free transport failure"),
            "io" => new IOException("transport failure"),
            "timeout" => new TimeoutException(),
            _ => new OperationCanceledException()
        });
        var sut = CreateSut(handler);
        var data = Encoding.UTF8.GetBytes("ORGANIZER:mailto:x\r\n");
        (await sut.IsStorageOnlyMutationAllowedAsync("https://example.com/calendar/", data, default,
            CancellationToken.None)).ShouldBeFalse();
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
