using System.Net;
using System.Text;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Internal.Xml;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarMetadataModuleTests
{
    private const string Href = "https://cal.example/home/work/";
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace Cal = "urn:ietf:params:xml:ns:caldav";

    [Fact]
    public async Task Inspect_preserves_property_status_and_advertisement_without_enumerating_members()
    {
        using var fixture = new Fixture();
        var metadata = Metadata("Work", "Plans");
        var successful = metadata.Descendants(Dav + "prop").First();
        successful.Add(
            new XElement(Dav + "supported-report-set", new XElement(Dav + "supported-report",
                new XElement(Dav + "report", new XElement(Dav + "sync-collection")))),
            new XElement(Dav + "current-user-privilege-set", new XElement(Dav + "privilege", new XElement(Dav + "read"))),
            new XElement(Cal + "max-resource-size", "1000000"),
            new XElement(Cal + "min-date-time", "20000101T000000Z"));
        fixture.Enqueue(207, metadata.ToString());
        fixture.EnqueueOptions("1, calendar-access, calendar-auto-schedule");

        var result = await fixture.Module.InspectAsync(Href, CancellationToken.None);

        result.DisplayName.ShouldBe("Work");
        result.ReportSupport.ShouldBe("available");
        result.Reports.ShouldBe([new CalendarProtocolName("DAV:", "sync-collection")]);
        result.Privileges.ShouldBe([new CalendarProtocolName("DAV:", "read")]);
        result.Limits.MaximumResourceBytes.ShouldBe(1000000);
        result.Limits.MinimumDateTime.ShouldBe("2000-01-01T00:00:00Z");
        result.Scheduling.State.ShouldBe("advertised");
        result.Properties.Single(value => value.LocalName == "max-instances").StatusCode.ShouldBeNull();
        fixture.Methods.ShouldBe(["PROPFIND", "OPTIONS"]);
        fixture.Depths.ShouldBe(["0", null]);
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("1, invalid value", "unknown")]
    [InlineData("1, calendar-access", "not_advertised")]
    public async Task Inspect_distinguishes_unknown_scheduling_from_successful_absence(string? dav, string expected)
    {
        using var fixture = new Fixture();
        fixture.Enqueue(207, Metadata("Work", null).ToString());
        fixture.EnqueueOptions(dav);

        var result = await fixture.Module.InspectAsync(Href, CancellationToken.None);

        result.Scheduling.State.ShouldBe(expected);
        result.Description.ShouldBeNull();
        result.Properties.Single(value => value.LocalName == "calendar-description").StatusCode.ShouldBe(404);
    }

    [Fact]
    public async Task Inspect_retains_metadata_when_options_is_forbidden()
    {
        using var fixture = new Fixture();
        fixture.Enqueue(207, Metadata("Work", null).ToString());
        fixture.Enqueue(403, string.Empty);

        var result = await fixture.Module.InspectAsync(Href, CancellationToken.None);

        result.DisplayName.ShouldBe("Work");
        result.Scheduling.ShouldBe(new CalendarSchedulingObservation("unknown", 403));
    }

    [Fact]
    public async Task Patch_sends_one_atomic_write_and_preserves_unaddressed_description()
    {
        using var fixture = new Fixture();
        fixture.Enqueue(207, Metadata("Old", "Keep this").ToString());
        fixture.Enqueue(207, PatchStatus((Dav + "displayname", 200)).ToString());
        fixture.Enqueue(207, Metadata("New & <name>", "Keep this").ToString());

        var result = await fixture.Module.PatchAsync(Href,
            new CalendarMetadataPatch(new CalendarMetadataTextPatch("set", "New & <name>")), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Error.ShouldBeNull();
        result.Calendar!.Description.ShouldBe("Keep this");
        fixture.Methods.ShouldBe(["PROPFIND", "PROPPATCH", "PROPFIND"]);
        var body = XElement.Parse(fixture.Bodies[1]!);
        body.Descendants(Dav + "displayname").Single().Value.ShouldBe("New & <name>");
        body.Descendants(Cal + "calendar-description").ShouldBeEmpty();
    }

    [Fact]
    public async Task Patch_sets_description_language_and_removes_name_in_one_request()
    {
        using var fixture = new Fixture();
        fixture.Enqueue(207, Metadata("Old", "Old description").ToString());
        fixture.Enqueue(207, PatchStatus((Dav + "displayname", 200), (Cal + "calendar-description", 200)).ToString());
        var after = Metadata(null, "Planos");
        after.Descendants(Cal + "calendar-description").Single().SetAttributeValue(XNamespace.Xml + "lang", "pt-BR");
        fixture.Enqueue(207, after.ToString());

        var result = await fixture.Module.PatchAsync(Href,
            new CalendarMetadataPatch(new CalendarMetadataTextPatch("remove"),
                new CalendarMetadataTextPatch("set", "Planos", "pt-BR")), CancellationToken.None);

        result.Error.ShouldBeNull();
        result.Calendar!.DisplayName.ShouldBeNull();
        result.Calendar.DescriptionLanguage.ShouldBe("pt-BR");
        var body = XElement.Parse(fixture.Bodies[1]!);
        body.Elements(Dav + "remove").Single().Descendants(Dav + "displayname").Count().ShouldBe(1);
        body.Descendants(Cal + "calendar-description").Single().Attribute(XNamespace.Xml + "lang")!.Value.ShouldBe("pt-BR");
    }

    [Theory]
    [InlineData(403, 424, "not_committed", "upstream_forbidden", 2)]
    [InlineData(200, 403, "unknown", "indeterminate", 3)]
    [InlineData(202, 200, "unknown", "indeterminate", 3)]
    public async Task Patch_uses_atomic_property_status_truth(
        int nameStatus, int descriptionStatus, string state, string code, int requestCount)
    {
        using var fixture = new Fixture();
        fixture.Enqueue(207, Metadata("Old", "Old").ToString());
        fixture.Enqueue(207, PatchStatus((Dav + "displayname", nameStatus), (Cal + "calendar-description", descriptionStatus)).ToString());
        fixture.Enqueue(207, Metadata("New", "Changed").ToString());

        var result = await fixture.Module.PatchAsync(Href, BothPatch(), CancellationToken.None);

        StateName(result.MutationState).ShouldBe(state);
        result.Error!.Code.ShouldBe(code);
        fixture.Methods.Count.ShouldBe(requestCount);
        fixture.Methods.Count(method => method == "PROPPATCH").ShouldBe(1);
    }

    [Theory]
    [InlineData("wrong_target")]
    [InlineData("missing_status")]
    [InlineData("malformed_xml")]
    public async Task Patch_cannot_infer_noncommit_from_unusable_multistatus(string responseKind)
    {
        using var fixture = new Fixture();
        fixture.Enqueue(207, Metadata("Old", "Old").ToString());
        var reply = PatchStatus((Dav + "displayname", 200), (Cal + "calendar-description", 200));
        if (responseKind == "wrong_target")
            reply.Descendants(Dav + "href").Single().Value = "https://cal.example/home/other/";
        if (responseKind == "missing_status")
            reply.Descendants(Dav + "propstat").First().Element(Dav + "status")!.Remove();
        fixture.Enqueue(207, responseKind == "malformed_xml" ? "<broken" : reply.ToString());
        fixture.Enqueue(207, Metadata("New", "Changed").ToString());

        var result = await fixture.Module.PatchAsync(Href, BothPatch(), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.Unknown);
        result.Error!.Code.ShouldBe("indeterminate");
        fixture.Methods.Count.ShouldBe(3);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(202)]
    [InlineData(500)]
    public async Task Patch_nonstandard_acknowledgement_or_server_failure_stays_uncertain(int status)
    {
        using var fixture = new Fixture();
        fixture.Enqueue(207, Metadata("Old", "Old").ToString());
        fixture.Enqueue(status, string.Empty);
        fixture.Enqueue(207, Metadata("New", "Changed").ToString());

        var result = await fixture.Module.PatchAsync(Href, BothPatch(), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.Unknown);
        result.Error!.Code.ShouldBe("indeterminate");
        result.Error.Retryable.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Patch_acknowledged_commit_survives_missing_or_conflicting_readback(bool inaccessible)
    {
        using var fixture = new Fixture();
        fixture.Enqueue(207, Metadata("Old", "Old").ToString());
        fixture.Enqueue(207, PatchStatus((Dav + "displayname", 200), (Cal + "calendar-description", 200)).ToString());
        fixture.Enqueue(inaccessible ? 403 : 207, inaccessible ? string.Empty : Metadata("Another writer", "Changed").ToString());

        var result = await fixture.Module.PatchAsync(Href, BothPatch(), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Error!.Code.ShouldBe("committed_but_unverified");
        result.Error.Retryable.ShouldBeFalse();
    }

    [Fact]
    public async Task Patch_transport_interruption_is_not_replayed_or_reported_as_noncommit()
    {
        using var fixture = new Fixture();
        fixture.Enqueue(207, Metadata("Old", "Old").ToString());
        fixture.EnqueueFailure(new HttpRequestException("disconnected"));
        fixture.Enqueue(207, Metadata("New", "Changed").ToString());

        var result = await fixture.Module.PatchAsync(Href, BothPatch(), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.Unknown);
        fixture.Methods.ShouldBe(["PROPFIND", "PROPPATCH", "PROPFIND"]);
    }

    [Fact]
    public async Task Patch_recognizes_collection_href_without_slash_and_successful_no_content_property_status()
    {
        using var fixture = new Fixture();
        fixture.Enqueue(207, Metadata("Old", "Old").ToString());
        var response = PatchStatus((Dav + "displayname", 204), (Cal + "calendar-description", 204));
        response.Descendants(Dav + "href").Single().Value = new Uri(Href).AbsolutePath.TrimEnd('/');
        fixture.Enqueue(207, response.ToString());
        fixture.Enqueue(207, Metadata(null, null).ToString());

        var result = await fixture.Module.PatchAsync(Href, new CalendarMetadataPatch(
            new CalendarMetadataTextPatch("remove"), new CalendarMetadataTextPatch("remove")), CancellationToken.None);

        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Error.ShouldBeNull();
        result.Calendar!.DisplayName.ShouldBeNull();
        result.Calendar.Description.ShouldBeNull();
    }

    [Theory]
    [MemberData(nameof(InvalidPatches))]
    public async Task Patch_invalid_input_fails_before_network(CalendarMetadataPatch patch)
    {
        using var fixture = new Fixture();

        var error = await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.PatchAsync(Href, patch, CancellationToken.None));

        error.Code.ShouldBe("invalid_input");
        fixture.Methods.ShouldBeEmpty();
    }

    public static TheoryData<CalendarMetadataPatch> InvalidPatches => new()
    {
        new CalendarMetadataPatch(),
        new CalendarMetadataPatch(new CalendarMetadataTextPatch("set")),
        new CalendarMetadataPatch(new CalendarMetadataTextPatch("remove", "value")),
        new CalendarMetadataPatch(new CalendarMetadataTextPatch("set", " ")),
        new CalendarMetadataPatch(new CalendarMetadataTextPatch("set", new string('x', 257))),
        new CalendarMetadataPatch(new CalendarMetadataTextPatch("set", "Name", "en")),
        new CalendarMetadataPatch(Description: new CalendarMetadataTextPatch("set", "Text", "invalid language")),
        new CalendarMetadataPatch(Description: new CalendarMetadataTextPatch("set", new string('x', 4097))),
        new CalendarMetadataPatch(Description: new CalendarMetadataTextPatch("set", "Invalid\u0001"))
    };

    [Theory]
    [InlineData("https://other.example/home/work/", "invalid_input")]
    [InlineData("https://cal.example/home/work", "invalid_input")]
    [InlineData("https://cal.example/home/other/", "outside_scope")]
    public async Task Exact_scope_rejects_unsafe_or_unauthorized_targets_before_network(string href, string code)
    {
        using var fixture = new Fixture();

        var error = await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.InspectAsync(href, CancellationToken.None));

        error.Code.ShouldBe(code);
        fixture.Methods.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("calendar_marker_in_failed_propstat")]
    [InlineData("duplicate_property")]
    [InlineData("wrong_target")]
    public async Task Inspect_rejects_conflicting_or_failed_calendar_identity(string kind)
    {
        using var fixture = new Fixture();
        var metadata = Metadata("Work", null);
        if (kind == "calendar_marker_in_failed_propstat")
            metadata.Descendants(Dav + "status").First().Value = "HTTP/1.1 404 Missing";
        if (kind == "duplicate_property")
            metadata.Descendants(Dav + "prop").First().Add(new XElement(Dav + "displayname", "Other"));
        if (kind == "wrong_target")
            metadata.Descendants(Dav + "href").Single().Value = "/home/other/";
        fixture.Enqueue(207, metadata.ToString());

        var error = await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.InspectAsync(Href, CancellationToken.None));

        error.Code.ShouldBe("upstream_protocol_error");
        fixture.Methods.ShouldBe(["PROPFIND"]);
    }

    [Fact]
    public async Task Patch_will_not_update_an_ordinary_dav_collection()
    {
        using var fixture = new Fixture();
        var metadata = Metadata("Work", null);
        metadata.Descendants(Cal + "calendar").Single().Remove();
        fixture.Enqueue(207, metadata.ToString());

        var error = await Should.ThrowAsync<CalendarProtocolException>(() => fixture.Module.PatchAsync(Href, BothPatch(), CancellationToken.None));

        error.Code.ShouldBe("unsupported_capability");
        fixture.Methods.ShouldBe(["PROPFIND"]);
    }

    private static CalendarMetadataPatch BothPatch() => new(
        new CalendarMetadataTextPatch("set", "New"), new CalendarMetadataTextPatch("set", "Changed"));

    private static string StateName(CalendarMutationState state) => state switch
    {
        CalendarMutationState.NotCommitted => "not_committed",
        CalendarMutationState.Unknown => "unknown",
        _ => state.ToString()
    };

    private static XElement Metadata(string? name, string? description)
    {
        var present = new List<XElement>
        {
            new(Dav + "resourcetype", new XElement(Dav + "collection"), new XElement(Cal + "calendar"))
        };
        var missing = new List<XElement>();
        AddMetadataProperty(present, missing, Dav + "displayname", name);
        AddMetadataProperty(present, missing, Cal + "calendar-description", description);
        return MultiStatus(new XElement(Dav + "propstat", new XElement(Dav + "prop", present),
            new XElement(Dav + "status", "HTTP/1.1 200 OK")),
            new XElement(Dav + "propstat", new XElement(Dav + "prop", missing),
                new XElement(Dav + "status", "HTTP/1.1 404 Not Found")));
    }

    private static void AddMetadataProperty(List<XElement> present, List<XElement> missing, XName name, string? value)
    {
        (value is null ? missing : present).Add(new XElement(name, value));
    }

    private static XElement PatchStatus(params (XName Name, int Status)[] properties) => MultiStatus(properties.Select(property =>
        new XElement(Dav + "propstat", new XElement(Dav + "prop", new XElement(property.Name)),
            new XElement(Dav + "status", $"HTTP/1.1 {property.Status} Status"))).ToArray());

    private static XElement MultiStatus(params XElement[] properties) => new(Dav + "multistatus",
        new XElement(Dav + "response", new XElement(Dav + "href", Href), properties));

    private sealed class Fixture : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        private readonly HttpClient _httpClient;
        public readonly List<string> Methods = [];
        public readonly List<string?> Bodies = [];
        public readonly List<string?> Depths = [];
        public CalendarMetadataModule Module { get; }

        public Fixture()
        {
            _httpClient = new HttpClient(this, disposeHandler: false) { BaseAddress = new Uri("https://cal.example/") };
            var options = Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example/", Username = "user", Password = "password", CalendarHrefs = Href
            });
            Module = new CalendarMetadataModule(new CalDavClient(_httpClient, options, NullLogger<CalDavClient>.Instance));
        }

        public void Enqueue(int status, string body) => _responses.Enqueue(() => new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        });

        public void EnqueueOptions(string? dav) => _responses.Enqueue(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) };
            if (dav is not null)
                response.Headers.TryAddWithoutValidation("DAV", dav);
            return response;
        });

        public void EnqueueFailure(Exception exception) => _responses.Enqueue(() => throw exception);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method.Method);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            Depths.Add(request.Headers.TryGetValues("Depth", out var depth) ? depth.Single() : null);
            var response = _responses.Dequeue()();
            response.RequestMessage = request;
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _httpClient.Dispose();
            base.Dispose(disposing);
        }
    }
}
