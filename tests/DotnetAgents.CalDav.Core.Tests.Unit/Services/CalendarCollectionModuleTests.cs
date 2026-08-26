using System.Net;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Internal;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public sealed class CalendarCollectionModuleTests
{
    [Fact]
    public async Task Create_MixedCollectionUsesGeneratedHomeSetHrefAndVerifiesDescriptor()
    {
        var transport = new ScriptedTransport("https://cal.example/calendars/user/");
        var module = CreateModule(transport);

        var result = await module.CreateAsync(
            new CalendarCollectionCreateRequest(
                "Planning",
                [CalendarEntityKind.Event, CalendarEntityKind.Todo]),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionCreateCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Calendar!.Href.ShouldStartWith("https://cal.example/calendars/user/");
        result.Calendar.EventSupport.ShouldBe(EntityKindSupport.Advertised);
        result.Calendar.TodoSupport.ShouldBe(EntityKindSupport.Advertised);
        transport.CreateCount.ShouldBe(1);
        transport.LastCreate!.Href.ShouldEndWith("/");
    }

    [Fact]
    public async Task Create_RejectsDuplicateDisplayNameBeforeDispatch()
    {
        var transport = new ScriptedTransport("https://cal.example/calendars/user/");
        transport.Items.Add(Descriptor("https://cal.example/calendars/user/existing/", "Planning"));
        var module = CreateModule(transport);

        var result = await module.CreateAsync(
            new CalendarCollectionCreateRequest(" planning ", [CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionCreateCode.Conflict);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        transport.CreateCount.ShouldBe(0);
    }

    [Fact]
    public async Task DeleteRequiresFreshMatchingReviewAndVerifiesAbsenceWithoutMemberEnumeration()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var transport = new ScriptedTransport("https://cal.example/calendars/user/");
        transport.Items.Add(Descriptor(href, "Tasks", todo: true));
        var module = CreateModule(transport);

        var review = await module.ReviewDeleteAsync(new CalendarCollectionDeleteRequest(href), CancellationToken.None);
        review.Outcome.ShouldBeNull();
        review.Binding.ShouldNotBeNull();

        var result = await module.ExecuteConfirmedDeleteAsync(
            new CalendarCollectionDeleteRequest(href),
            review.Binding!,
            CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionDeleteCode.Success);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        transport.DeleteCount.ShouldBe(1);
        transport.ResourceEnumerationCount.ShouldBe(0);
        transport.Items.ShouldNotContain(item => item.Href == href);
    }

    [Fact]
    public async Task DeleteRejectsDescriptorDriftBeforeDispatch()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var transport = new ScriptedTransport("https://cal.example/calendars/user/");
        transport.Items.Add(Descriptor(href, "Tasks", todo: true));
        var module = CreateModule(transport);
        var review = await module.ReviewDeleteAsync(new CalendarCollectionDeleteRequest(href), CancellationToken.None);
        transport.Items[0] = Descriptor(href, "Renamed", todo: true);

        var result = await module.ExecuteConfirmedDeleteAsync(
            new CalendarCollectionDeleteRequest(href),
            review.Binding!,
            CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionDeleteCode.ConfirmationMismatch);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        transport.DeleteCount.ShouldBe(0);
    }

    [Fact]
    public async Task DeleteReportsMissingHrefAsNotFoundWhenItIsWithinConfiguredScope()
    {
        const string href = "https://cal.example/calendars/user/missing/";
        var transport = new ScriptedTransport("https://cal.example/calendars/user/");
        var module = CreateModule(transport, href);

        var review = await module.ReviewDeleteAsync(new CalendarCollectionDeleteRequest(href), CancellationToken.None);

        review.Outcome!.Code.ShouldBe(CalendarCollectionDeleteCode.NotFound);
        review.Binding.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteReportsMissingHrefOutsideConfiguredScope()
    {
        const string href = "https://cal.example/calendars/user/missing/";
        var transport = new ScriptedTransport("https://cal.example/calendars/user/");
        var module = CreateModule(transport, "https://cal.example/calendars/user/other/");

        var review = await module.ReviewDeleteAsync(new CalendarCollectionDeleteRequest(href), CancellationToken.None);

        review.Outcome!.Code.ShouldBe(CalendarCollectionDeleteCode.OutsideScope);
        review.Binding.ShouldBeNull();
    }

    [Fact]
    public async Task CreateDefinitiveDispatchPreservesCommittedStateWhenReconciliationFails()
    {
        var transport = new ScriptedTransport("https://cal.example/calendars/user/")
        {
            FailDiscoveryAfterDispatch = new HttpRequestException("discovery unavailable")
        };
        var module = CreateModule(transport);

        var result = await module.CreateAsync(
            new CalendarCollectionCreateRequest("Planning", [CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionCreateCode.CommittedButUnverified);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
        result.Retryable.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteDefinitiveDispatchPreservesCommittedStateWhenReconciliationFails()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var transport = new ScriptedTransport("https://cal.example/calendars/user/")
        {
            FailDiscoveryAfterCount = 2
        };
        transport.Items.Add(Descriptor(href, "Tasks", todo: true));
        var module = CreateModule(transport);
        var review = await module.ReviewDeleteAsync(new CalendarCollectionDeleteRequest(href), CancellationToken.None);

        var result = await module.ExecuteConfirmedDeleteAsync(
            new CalendarCollectionDeleteRequest(href),
            review.Binding!,
            CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionDeleteCode.CommittedButUnverified);
        result.MutationState.ShouldBe(CalendarMutationState.Committed);
    }

    [Fact]
    public async Task DeleteRejectedDispatchIsNotCommitted()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var transport = new ScriptedTransport("https://cal.example/calendars/user/")
        {
            DeleteDispatchCode = CalendarCollectionDispatchCode.Conflict
        };
        transport.Items.Add(Descriptor(href, "Tasks", todo: true));
        var module = CreateModule(transport);
        var review = await module.ReviewDeleteAsync(new CalendarCollectionDeleteRequest(href), CancellationToken.None);

        var result = await module.ExecuteConfirmedDeleteAsync(
            new CalendarCollectionDeleteRequest(href),
            review.Binding!,
            CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionDeleteCode.Conflict);
        result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        transport.Items.ShouldContain(item => item.Href == href);
    }

    [Fact]
    public async Task CreateAmbiguousTransportFailureReturnsUnknownMutationState()
    {
        var transport = new ScriptedTransport("https://cal.example/calendars/user/")
        {
            CreateDispatchException = new TimeoutException("write outcome unknown")
        };
        var module = CreateModule(transport);

        var result = await module.CreateAsync(
            new CalendarCollectionCreateRequest("Planning", [CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionCreateCode.Indeterminate);
        result.MutationState.ShouldBe(CalendarMutationState.Unknown);
        result.Retryable.ShouldBeTrue();
    }

    [Fact]
    public async Task DeleteAmbiguousTransportFailureReturnsUnknownMutationState()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var transport = new ScriptedTransport("https://cal.example/calendars/user/")
        {
            DeleteDispatchException = new TimeoutException("write outcome unknown")
        };
        transport.Items.Add(Descriptor(href, "Tasks", todo: true));
        var module = CreateModule(transport);
        var review = await module.ReviewDeleteAsync(new CalendarCollectionDeleteRequest(href), CancellationToken.None);

        var result = await module.ExecuteConfirmedDeleteAsync(
            new CalendarCollectionDeleteRequest(href),
            review.Binding!,
            CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionDeleteCode.Indeterminate);
        result.MutationState.ShouldBe(CalendarMutationState.Unknown);
        result.Retryable.ShouldBeTrue();
    }

    private static CalendarCollectionModule CreateModule(ScriptedTransport transport, string? scope = null) =>
        new(
            transport,
            Options.Create(new CalDavOptions
            {
                BaseUrl = "https://cal.example",
                CalendarHrefs = scope
            }));

    private static CalendarDescriptor Descriptor(string href, string name, bool todo = false) => new()
    {
        Href = href,
        DisplayName = name,
        DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
        EventSupport = todo ? EntityKindSupport.NotAdvertised : EntityKindSupport.Advertised,
        TodoSupport = todo ? EntityKindSupport.Advertised : EntityKindSupport.NotAdvertised,
        EventEvidence = todo ? [] : [new CapabilityEvidence("supported-calendar-component-set", "VEVENT")],
        TodoEvidence = todo ? [new CapabilityEvidence("supported-calendar-component-set", "VTODO")] : []
    };

    private sealed class ScriptedTransport(string homeSetHref) : ICalendarCollectionTransport
    {
        public List<CalendarDescriptor> Items { get; } = [];
        public int CreateCount { get; private set; }
        public int DeleteCount { get; private set; }
        public int ResourceEnumerationCount { get; private set; }
        public CalendarCollectionCreateDispatchRequest? LastCreate { get; private set; }
        public Exception? FailDiscoveryAfterDispatch { get; init; }
        public int? FailDiscoveryAfterCount { get; init; }
        public Exception? CreateDispatchException { get; init; }
        public Exception? DeleteDispatchException { get; init; }
        public CalendarCollectionDispatchCode DeleteDispatchCode { get; init; } = CalendarCollectionDispatchCode.Dispatched;
        private int DiscoveryCount { get; set; }

        public Task<CalendarCollectionDiscoverySnapshot> DiscoverAsync(CancellationToken cancellationToken)
        {
            DiscoveryCount++;
            if (FailDiscoveryAfterDispatch is not null && CreateCount > 0 && DiscoveryCount > 1)
                throw FailDiscoveryAfterDispatch;
            if (FailDiscoveryAfterCount is { } count && DiscoveryCount > count)
                throw new HttpRequestException("reconciliation unavailable");
            return Task.FromResult(new CalendarCollectionDiscoverySnapshot(homeSetHref, Items.ToArray()));
        }

        public Task<CalendarCollectionDispatchResult> CreateAsync(
            CalendarCollectionCreateDispatchRequest request,
            CancellationToken cancellationToken)
        {
            if (CreateDispatchException is not null)
                throw CreateDispatchException;
            CreateCount++;
            LastCreate = request;
            Items.Add(new CalendarDescriptor
            {
                Href = request.Href,
                DisplayName = request.DisplayName,
                DisplayNameProvenance = DisplayNameProvenance.DavDisplayName,
                EventSupport = request.EntityKinds.Contains(CalendarEntityKind.Event)
                    ? EntityKindSupport.Advertised
                    : EntityKindSupport.NotAdvertised,
                TodoSupport = request.EntityKinds.Contains(CalendarEntityKind.Todo)
                    ? EntityKindSupport.Advertised
                    : EntityKindSupport.NotAdvertised
            });
            return Task.FromResult(new CalendarCollectionDispatchResult(
                CalendarCollectionDispatchCode.Dispatched,
                (int)HttpStatusCode.Created));
        }

        public Task<CalendarCollectionDispatchResult> DeleteAsync(
            string href,
            CancellationToken cancellationToken)
        {
            if (DeleteDispatchException is not null)
                throw DeleteDispatchException;
            DeleteCount++;
            if (DeleteDispatchCode == CalendarCollectionDispatchCode.Dispatched)
                Items.RemoveAll(item => item.Href == href);
            return Task.FromResult(new CalendarCollectionDispatchResult(
                DeleteDispatchCode,
                (int)HttpStatusCode.NoContent));
        }
    }
}
