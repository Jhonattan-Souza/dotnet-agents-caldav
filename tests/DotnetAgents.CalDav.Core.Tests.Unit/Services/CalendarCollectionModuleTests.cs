using System.Net;
using System.Xml;
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
    public async Task Create_ExplicitDirectChildDestinationIsAccepted()
    {
        const string href = "https://cal.example/calendars/user/planning/";
        var transport = new ScriptedTransport("https://cal.example/calendars/user/");
        var module = CreateModule(transport);

        var result = await module.CreateAsync(
            new CalendarCollectionCreateRequest("Planning", [CalendarEntityKind.Event], href),
            CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionCreateCode.Success);
        transport.LastCreate!.Href.ShouldBe(href);
    }

    [Fact]
    public async Task Create_RejectsDestinationOutsideScopeAndOmittedDestinationWithScope()
    {
        const string allowed = "https://cal.example/calendars/user/allowed/";
        var transport = new ScriptedTransport("https://cal.example/calendars/user/");
        var module = CreateModule(transport, allowed);

        var omitted = await module.CreateAsync(
            new CalendarCollectionCreateRequest("Planning", [CalendarEntityKind.Event]),
            CancellationToken.None);
        var outside = await module.CreateAsync(
            new CalendarCollectionCreateRequest(
                "Planning",
                [CalendarEntityKind.Event],
                "https://cal.example/calendars/user/other/"),
            CancellationToken.None);

        omitted.Code.ShouldBe(CalendarCollectionCreateCode.OutsideScope);
        outside.Code.ShouldBe(CalendarCollectionCreateCode.InvalidInput);
        transport.CreateCount.ShouldBe(0);
    }

    [Fact]
    public async Task Create_RejectsInvalidInputVariantsBeforeDiscovery()
    {
        var transport = new ScriptedTransport("https://cal.example/calendars/user/");
        var module = CreateModule(transport);
        var requests = new[]
        {
            new CalendarCollectionCreateRequest(" ", [CalendarEntityKind.Event]),
            new CalendarCollectionCreateRequest(new string('x', 257), [CalendarEntityKind.Event]),
            new CalendarCollectionCreateRequest("Planning", []),
            new CalendarCollectionCreateRequest("Planning", [CalendarEntityKind.Event, CalendarEntityKind.Todo, CalendarEntityKind.Event]),
            new CalendarCollectionCreateRequest("Planning", [CalendarEntityKind.Event, CalendarEntityKind.Event]),
            new CalendarCollectionCreateRequest("Planning", [(CalendarEntityKind)999]),
            new CalendarCollectionCreateRequest("Planning", null!),
            new CalendarCollectionCreateRequest(null!, [CalendarEntityKind.Event])
        };

        foreach (var request in requests)
        {
            var result = await module.CreateAsync(request, CancellationToken.None);
            result.Code.ShouldBe(CalendarCollectionCreateCode.InvalidInput);
        }

        transport.DiscoveryCount.ShouldBe(0);
    }

    [Fact]
    public async Task Create_RejectsUnsafeAndNonDirectDestinations()
    {
        var transport = new ScriptedTransport("https://cal.example/calendars/user/");
        var module = CreateModule(transport);
        var destinations = new[]
        {
            "https://cal.example/calendars/user/planning/nested/",
            "https://cal.example/calendars/user/",
            "https://cal.example/calendars/other/planning/",
            "https://other.example/calendars/user/planning/",
            "https://cal.example/calendars/user/planning?x=1/",
            "https://cal.example/calendars/user/planning%2e/",
            "https://cal.example/calendars/user/planning%2f/",
            "https://cal.example/calendars/user/planning%5c/",
            "https://cal.example/calendars/user/planning/#fragment",
            "ftp://cal.example/calendars/user/planning/",
            "not a uri",
            "https://user@cal.example/calendars/user/planning/",
            "https://cal.example:444/calendars/user/planning/",
            "https://cal.example/calendars/user/planning"
        };

        foreach (var destination in destinations)
        {
            var result = await module.CreateAsync(
                new CalendarCollectionCreateRequest("Planning", [CalendarEntityKind.Event], destination),
                CancellationToken.None);
            result.Code.ShouldBe(CalendarCollectionCreateCode.InvalidInput);
        }

        transport.CreateCount.ShouldBe(0);
    }

    [Fact]
    public async Task CreateRejectsInvalidHomeSetAndSupportsHomeSetWithoutTrailingSlash()
    {
        var invalidHomeTransport = new ScriptedTransport("not a uri");
        var invalidHomeModule = CreateModule(invalidHomeTransport);
        var invalidHome = await invalidHomeModule.CreateAsync(
            new CalendarCollectionCreateRequest(
                "Planning",
                [CalendarEntityKind.Event],
                "https://cal.example/calendars/user/planning/"),
            CancellationToken.None);

        invalidHome.Code.ShouldBe(CalendarCollectionCreateCode.InvalidInput);

        const string homeSet = "https://cal.example/calendars/user";
        var noSlashTransport = new ScriptedTransport(homeSet);
        var noSlashModule = CreateModule(noSlashTransport);
        var accepted = await noSlashModule.CreateAsync(
            new CalendarCollectionCreateRequest(
                "Planning",
                [CalendarEntityKind.Event],
                "https://cal.example/calendars/user/planning/"),
            CancellationToken.None);

        accepted.Code.ShouldBe(CalendarCollectionCreateCode.Success);
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
    public async Task DeleteReportsDiscoveredHrefOutsideConfiguredScope()
    {
        const string href = "https://cal.example/calendars/user/other/";
        var transport = new ScriptedTransport("https://cal.example/calendars/user/");
        transport.Items.Add(Descriptor(href, "Other", todo: true));
        var module = CreateModule(transport, "https://cal.example/calendars/user/allowed/");

        var review = await module.ReviewDeleteAsync(new CalendarCollectionDeleteRequest(href), CancellationToken.None);

        review.Outcome!.Code.ShouldBe(CalendarCollectionDeleteCode.OutsideScope);
        review.Binding.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteRejectsInvalidHrefBeforeDiscovery()
    {
        var transport = new ScriptedTransport("https://cal.example/calendars/user/");
        var module = CreateModule(transport);

        var review = await module.ReviewDeleteAsync(
            new CalendarCollectionDeleteRequest("https://cal.example/calendars/user/tasks"),
            CancellationToken.None);

        review.Outcome!.Code.ShouldBe(CalendarCollectionDeleteCode.InvalidInput);
        transport.DiscoveryCount.ShouldBe(0);
    }

    [Fact]
    public async Task CreateReturnsCommittedButUnverifiedWhenDescriptorIsMissingOrMismatched()
    {
        var missingTransport = new ScriptedTransport("https://cal.example/calendars/user/")
        {
            SuppressCreatedItem = true
        };
        var missingModule = CreateModule(missingTransport);
        var missing = await missingModule.CreateAsync(
            new CalendarCollectionCreateRequest("Planning", [CalendarEntityKind.Event]),
            CancellationToken.None);

        missing.Code.ShouldBe(CalendarCollectionCreateCode.CommittedButUnverified);
        missing.MutationState.ShouldBe(CalendarMutationState.Committed);

        var mismatchTransport = new ScriptedTransport("https://cal.example/calendars/user/")
        {
            CreatedDescriptor = Descriptor(
                "https://cal.example/calendars/user/planning/",
                "Different",
                todo: true)
        };
        var mismatchModule = CreateModule(mismatchTransport);
        var mismatch = await mismatchModule.CreateAsync(
            new CalendarCollectionCreateRequest(
                "Planning",
                [CalendarEntityKind.Event],
                "https://cal.example/calendars/user/planning/"),
            CancellationToken.None);

        mismatch.Code.ShouldBe(CalendarCollectionCreateCode.CommittedButUnverified);
        mismatch.Calendar!.DisplayName.ShouldBe("Different");

        var capabilityMismatchTransport = new ScriptedTransport("https://cal.example/calendars/user/")
        {
            CreatedDescriptor = Descriptor(
                "https://cal.example/calendars/user/planning/",
                "Planning",
                todo: true)
        };
        var capabilityMismatch = await CreateModule(capabilityMismatchTransport).CreateAsync(
            new CalendarCollectionCreateRequest(
                "Planning",
                [CalendarEntityKind.Event],
                "https://cal.example/calendars/user/planning/"),
            CancellationToken.None);
        capabilityMismatch.Code.ShouldBe(CalendarCollectionCreateCode.CommittedButUnverified);
    }

    [Fact]
    public async Task CreatePossiblyDispatchedReconciliationFailureIsIndeterminate()
    {
        var transport = new ScriptedTransport("https://cal.example/calendars/user/")
        {
            CreateDispatchCode = CalendarCollectionDispatchCode.PossiblyDispatched,
            FailDiscoveryAfterDispatch = new HttpRequestException("reconciliation unavailable")
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
    public async Task DeleteReportsNotFoundWhenReviewedCollectionDisappears()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var transport = new ScriptedTransport("https://cal.example/calendars/user/");
        transport.Items.Add(Descriptor(href, "Tasks", todo: true));
        var module = CreateModule(transport);
        var review = await module.ReviewDeleteAsync(new CalendarCollectionDeleteRequest(href), CancellationToken.None);
        transport.Items.Clear();

        var result = await module.ExecuteConfirmedDeleteAsync(
            new CalendarCollectionDeleteRequest(href),
            review.Binding!,
            CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionDeleteCode.NotFound);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
    }

    [Fact]
    public async Task DeleteRejectsConfirmationWhenHrefChanges()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var transport = new ScriptedTransport("https://cal.example/calendars/user/");
        transport.Items.Add(Descriptor(href, "Tasks", todo: true));
        var module = CreateModule(transport);
        var review = await module.ReviewDeleteAsync(new CalendarCollectionDeleteRequest(href), CancellationToken.None);

        var result = await module.ExecuteConfirmedDeleteAsync(
            new CalendarCollectionDeleteRequest("https://cal.example/calendars/user/other/"),
            review.Binding!,
            CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionDeleteCode.ConfirmationMismatch);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        transport.DiscoveryCount.ShouldBe(1);
    }

    [Fact]
    public async Task DeleteRejectsCollectionThatLeavesConfiguredScopeBeforeDispatch()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var transport = new ScriptedTransport("https://cal.example/calendars/user/");
        transport.Items.Add(Descriptor(href, "Tasks", todo: true));
        var options = Options.Create(new CalDavOptions
        {
            BaseUrl = "https://cal.example",
            CalendarHrefs = href
        });
        var module = new CalendarCollectionModule(transport, options);
        var review = await module.ReviewDeleteAsync(new CalendarCollectionDeleteRequest(href), CancellationToken.None);
        options.Value.CalendarHrefs = "https://cal.example/calendars/user/other/";

        var result = await module.ExecuteConfirmedDeleteAsync(
            new CalendarCollectionDeleteRequest(href),
            review.Binding!,
            CancellationToken.None);

        result.Code.ShouldBe(CalendarCollectionDeleteCode.OutsideScope);
        result.MutationState.ShouldBe(CalendarMutationState.NotAttempted);
        transport.DeleteCount.ShouldBe(0);
    }

    [Fact]
    public async Task DiscoveryLimitIsRejectedBeforeCollectionOperation()
    {
        var transport = new ScriptedTransport("https://cal.example/calendars/user/");
        transport.Items.AddRange(Enumerable.Range(0, 257).Select(index =>
            Descriptor($"https://cal.example/calendars/user/{index}/", $"Calendar {index}")));
        var module = CreateModule(transport);

        await Should.ThrowAsync<CalendarDiscoveryLimitException>(() => module.ReviewDeleteAsync(
            new CalendarCollectionDeleteRequest("https://cal.example/calendars/user/0/"),
            CancellationToken.None));
    }

    [Fact]
    public async Task DeleteReturnsCommittedButUnverifiedWhenCollectionRemains()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var transport = new ScriptedTransport("https://cal.example/calendars/user/")
        {
            PreserveDeletedItem = true
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
    public async Task DeletePossiblyDispatchedReconciliationFailureIsIndeterminate()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        var transport = new ScriptedTransport("https://cal.example/calendars/user/")
        {
            DeleteDispatchCode = CalendarCollectionDispatchCode.PossiblyDispatched,
            FailDiscoveryAfterCount = 2
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
    }

    [Fact]
    public async Task CreateReconciliationFailureClassificationPreservesTruthAndRetryability()
    {
        var cases = new (Exception Exception, bool Retryable)[]
        {
            (new HttpRequestException("unavailable"), true),
            (new IOException("unavailable"), true),
            (new TimeoutException("timeout"), true),
            (new OperationCanceledException(), true),
            (new XmlException("malformed"), false),
            (new CalendarDiscoveryProtocolException("invalid"), false),
            (new CalendarDiscoveryUnsupportedCapabilityException("unsupported"), false),
            (new CalendarDiscoveryLimitException(300), false)
        };

        foreach (var (exception, retryable) in cases)
        {
            var transport = new ScriptedTransport("https://cal.example/calendars/user/")
            {
                FailDiscoveryAfterDispatch = exception
            };
            var module = CreateModule(transport);

            var result = await module.CreateAsync(
                new CalendarCollectionCreateRequest("Planning", [CalendarEntityKind.Event]),
                CancellationToken.None);

            result.Code.ShouldBe(CalendarCollectionCreateCode.CommittedButUnverified);
            result.MutationState.ShouldBe(CalendarMutationState.Committed);
            result.Retryable.ShouldBe(retryable);
        }
    }

    [Fact]
    public async Task CreateDispatchFailureClassificationDistinguishesAmbiguousAndDefinitiveFailures()
    {
        var ambiguous = new Exception[]
        {
            new HttpRequestException("unavailable"),
            new IOException("unavailable"),
            new TimeoutException("timeout"),
            new OperationCanceledException()
        };
        foreach (var exception in ambiguous)
        {
            var transport = new ScriptedTransport("https://cal.example/calendars/user/")
            {
                CreateDispatchException = exception
            };
            var result = await CreateModule(transport).CreateAsync(
                new CalendarCollectionCreateRequest("Planning", [CalendarEntityKind.Event]),
                CancellationToken.None);
            result.Code.ShouldBe(CalendarCollectionCreateCode.Indeterminate);
            result.MutationState.ShouldBe(CalendarMutationState.Unknown);
            result.Retryable.ShouldBeTrue();
        }

        var definitive = new Exception[]
        {
            new XmlException("malformed"),
            new CalendarDiscoveryProtocolException("invalid")
        };
        foreach (var exception in definitive)
        {
            var transport = new ScriptedTransport("https://cal.example/calendars/user/")
            {
                CreateDispatchException = exception
            };
            var result = await CreateModule(transport).CreateAsync(
                new CalendarCollectionCreateRequest("Planning", [CalendarEntityKind.Event]),
                CancellationToken.None);
            result.Code.ShouldBe(CalendarCollectionCreateCode.UpstreamProtocolError);
            result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        }
    }

    [Fact]
    public async Task DeleteDispatchFailureClassificationDistinguishesUnsupportedAndDefinitiveFailures()
    {
        const string href = "https://cal.example/calendars/user/tasks/";
        foreach (var exception in new Exception[]
        {
            new CalendarDiscoveryUnsupportedCapabilityException("unsupported"),
            new XmlException("malformed"),
            new CalendarDiscoveryProtocolException("invalid")
        })
        {
            var transport = new ScriptedTransport("https://cal.example/calendars/user/")
            {
                DeleteDispatchException = exception
            };
            transport.Items.Add(Descriptor(href, "Tasks", todo: true));
            var module = CreateModule(transport);
            var review = await module.ReviewDeleteAsync(new CalendarCollectionDeleteRequest(href), CancellationToken.None);

            var result = await module.ExecuteConfirmedDeleteAsync(
                new CalendarCollectionDeleteRequest(href),
                review.Binding!,
                CancellationToken.None);

            result.Code.ShouldBe(exception is CalendarDiscoveryUnsupportedCapabilityException
                ? CalendarCollectionDeleteCode.UnsupportedCapability
                : CalendarCollectionDeleteCode.UpstreamProtocolError);
            result.MutationState.ShouldBe(CalendarMutationState.NotCommitted);
        }
    }

    [Fact]
    public async Task DispatchMappingUsesProtocolErrorFallbackForUnknownDispatchCode()
    {
        var createTransport = new ScriptedTransport("https://cal.example/calendars/user/")
        {
            CreateDispatchCode = (CalendarCollectionDispatchCode)999
        };
        var create = await CreateModule(createTransport).CreateAsync(
            new CalendarCollectionCreateRequest("Planning", [CalendarEntityKind.Event]),
            CancellationToken.None);
        create.Code.ShouldBe(CalendarCollectionCreateCode.UpstreamProtocolError);
        create.MutationState.ShouldBe(CalendarMutationState.Unknown);

        const string href = "https://cal.example/calendars/user/tasks/";
        var deleteTransport = new ScriptedTransport("https://cal.example/calendars/user/")
        {
            DeleteDispatchCode = (CalendarCollectionDispatchCode)999
        };
        deleteTransport.Items.Add(Descriptor(href, "Tasks", todo: true));
        var deleteModule = CreateModule(deleteTransport);
        var review = await deleteModule.ReviewDeleteAsync(new CalendarCollectionDeleteRequest(href), CancellationToken.None);
        var delete = await deleteModule.ExecuteConfirmedDeleteAsync(
            new CalendarCollectionDeleteRequest(href),
            review.Binding!,
            CancellationToken.None);
        delete.Code.ShouldBe(CalendarCollectionDeleteCode.UpstreamProtocolError);
        delete.MutationState.ShouldBe(CalendarMutationState.Unknown);
    }

    [Theory]
    [InlineData(nameof(CalendarCollectionDispatchCode.Conflict), CalendarCollectionCreateCode.DestinationConflict)]
    [InlineData(nameof(CalendarCollectionDispatchCode.UnsupportedCapability), CalendarCollectionCreateCode.UnsupportedCapability)]
    [InlineData(nameof(CalendarCollectionDispatchCode.PayloadTooLarge), CalendarCollectionCreateCode.PayloadTooLarge)]
    [InlineData(nameof(CalendarCollectionDispatchCode.UpstreamUnauthorized), CalendarCollectionCreateCode.UpstreamUnauthorized)]
    [InlineData(nameof(CalendarCollectionDispatchCode.UpstreamForbidden), CalendarCollectionCreateCode.UpstreamForbidden)]
    [InlineData(nameof(CalendarCollectionDispatchCode.UpstreamRateLimited), CalendarCollectionCreateCode.UpstreamRateLimited)]
    [InlineData(nameof(CalendarCollectionDispatchCode.UpstreamUnavailable), CalendarCollectionCreateCode.UpstreamUnavailable)]
    [InlineData(nameof(CalendarCollectionDispatchCode.NotFound), CalendarCollectionCreateCode.UpstreamProtocolError)]
    [InlineData(nameof(CalendarCollectionDispatchCode.ProtocolError), CalendarCollectionCreateCode.UpstreamProtocolError)]
    public async Task CreateMapsRejectedDispatches(
        string dispatchName,
        CalendarCollectionCreateCode expectedCode)
    {
        var dispatchCode = Enum.Parse<CalendarCollectionDispatchCode>(dispatchName);
        var transport = new ScriptedTransport("https://cal.example/calendars/user/")
        {
            CreateDispatchCode = dispatchCode
        };
        var module = CreateModule(transport);

        var result = await module.CreateAsync(
            new CalendarCollectionCreateRequest("Planning", [CalendarEntityKind.Event]),
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(dispatchCode is CalendarCollectionDispatchCode.UpstreamUnavailable
            ? CalendarMutationState.Unknown
            : CalendarMutationState.NotCommitted);
    }

    [Theory]
    [InlineData(nameof(CalendarCollectionDispatchCode.Conflict), CalendarCollectionDeleteCode.Conflict)]
    [InlineData(nameof(CalendarCollectionDispatchCode.UnsupportedCapability), CalendarCollectionDeleteCode.UnsupportedCapability)]
    [InlineData(nameof(CalendarCollectionDispatchCode.PayloadTooLarge), CalendarCollectionDeleteCode.PayloadTooLarge)]
    [InlineData(nameof(CalendarCollectionDispatchCode.UpstreamUnauthorized), CalendarCollectionDeleteCode.UpstreamUnauthorized)]
    [InlineData(nameof(CalendarCollectionDispatchCode.UpstreamForbidden), CalendarCollectionDeleteCode.UpstreamForbidden)]
    [InlineData(nameof(CalendarCollectionDispatchCode.UpstreamRateLimited), CalendarCollectionDeleteCode.UpstreamRateLimited)]
    [InlineData(nameof(CalendarCollectionDispatchCode.UpstreamUnavailable), CalendarCollectionDeleteCode.UpstreamUnavailable)]
    [InlineData(nameof(CalendarCollectionDispatchCode.NotFound), CalendarCollectionDeleteCode.NotFound)]
    [InlineData(nameof(CalendarCollectionDispatchCode.ProtocolError), CalendarCollectionDeleteCode.UpstreamProtocolError)]
    public async Task DeleteMapsRejectedDispatches(
        string dispatchName,
        CalendarCollectionDeleteCode expectedCode)
    {
        var dispatchCode = Enum.Parse<CalendarCollectionDispatchCode>(dispatchName);
        const string href = "https://cal.example/calendars/user/tasks/";
        var transport = new ScriptedTransport("https://cal.example/calendars/user/")
        {
            DeleteDispatchCode = dispatchCode
        };
        transport.Items.Add(Descriptor(href, "Tasks", todo: true));
        var module = CreateModule(transport);
        var review = await module.ReviewDeleteAsync(new CalendarCollectionDeleteRequest(href), CancellationToken.None);

        var result = await module.ExecuteConfirmedDeleteAsync(
            new CalendarCollectionDeleteRequest(href),
            review.Binding!,
            CancellationToken.None);

        result.Code.ShouldBe(expectedCode);
        result.MutationState.ShouldBe(dispatchCode is CalendarCollectionDispatchCode.UpstreamUnavailable
            ? CalendarMutationState.Unknown
            : CalendarMutationState.NotCommitted);
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
        public CalendarCollectionDispatchCode CreateDispatchCode { get; init; } = CalendarCollectionDispatchCode.Dispatched;
        public CalendarCollectionDispatchCode DeleteDispatchCode { get; init; } = CalendarCollectionDispatchCode.Dispatched;
        public bool SuppressCreatedItem { get; init; }
        public bool PreserveDeletedItem { get; init; }
        public CalendarDescriptor? CreatedDescriptor { get; init; }
        public int DiscoveryCount { get; private set; }

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
            if (!SuppressCreatedItem)
            {
                Items.Add(CreatedDescriptor ?? new CalendarDescriptor
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
            }
            return Task.FromResult(new CalendarCollectionDispatchResult(
                CreateDispatchCode,
                (int)HttpStatusCode.Created));
        }

        public Task<CalendarCollectionDispatchResult> DeleteAsync(
            string href,
            CancellationToken cancellationToken)
        {
            if (DeleteDispatchException is not null)
                throw DeleteDispatchException;
            DeleteCount++;
            if (DeleteDispatchCode == CalendarCollectionDispatchCode.Dispatched && !PreserveDeletedItem)
                Items.RemoveAll(item => item.Href == href);
            return Task.FromResult(new CalendarCollectionDispatchResult(
                DeleteDispatchCode,
                (int)HttpStatusCode.NoContent));
        }
    }
}
