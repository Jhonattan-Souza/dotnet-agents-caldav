using DotnetAgents.CalDav.Core.Abstractions;
using DotnetAgents.CalDav.Core.Configuration;
using DotnetAgents.CalDav.Core.Models;
using DotnetAgents.CalDav.Core.Services;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Services;

public class TaskListResolverTests
{
    private static TaskList T(string displayName, string href = "") =>
        new() { DisplayName = displayName, Href = href };

    private static IOptions<CalDavOptions> Opts(string? defaultTaskList = null)
    {
        var opts = new CalDavOptions { DefaultTaskList = defaultTaskList };
        var wrapper = Substitute.For<IOptions<CalDavOptions>>();
        wrapper.Value.Returns(opts);
        return wrapper;
    }

    private static ITaskListResolver CreateResolver(string? defaultTaskList = null) =>
        new TaskListResolver(Opts(defaultTaskList));

    // ─── Rule 1: Exact case-insensitive display name match ─────────────────

    [Fact]
    public async Task ResolveAsync_ExactMatch_ReturnsMatchingList()
    {
        var resolver = CreateResolver();
        var lists = new List<TaskList> { T("My Tasks"), T("Shopping") };

        var result = await resolver.ResolveAsync(lists, "My Tasks", TestContext.Current.CancellationToken);

        result.DisplayName.ShouldBe("My Tasks");
    }

    [Fact]
    public async Task ResolveAsync_ExactMatchCaseInsensitive_ReturnsMatchingList()
    {
        var resolver = CreateResolver();
        var lists = new List<TaskList> { T("My Tasks"), T("Shopping") };

        var result = await resolver.ResolveAsync(lists, "my tasks", TestContext.Current.CancellationToken);

        result.DisplayName.ShouldBe("My Tasks");
    }

    [Fact]
    public async Task ResolveAsync_ExactMatch_SetsIsDefaultFalse_WhenNotConfiguredDefault()
    {
        var resolver = CreateResolver();
        var lists = new List<TaskList> { T("My Tasks"), T("Shopping") };

        var result = await resolver.ResolveAsync(lists, "My Tasks", TestContext.Current.CancellationToken);

        result.IsDefault.ShouldBeFalse();
    }

    [Fact]
    public async Task ResolveAsync_ExactMatch_SetsIsDefaultTrue_WhenConfiguredDefault()
    {
        var resolver = CreateResolver(defaultTaskList: "My Tasks");
        var lists = new List<TaskList> { T("My Tasks"), T("Shopping") };

        var result = await resolver.ResolveAsync(lists, "My Tasks", TestContext.Current.CancellationToken);

        result.IsDefault.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_ExactMatchAmbiguous_ThrowsWithCandidates()
    {
        var resolver = CreateResolver();
        // Two lists with same display name (edge case from server)
        var lists = new List<TaskList> { T("Tasks"), T("Tasks") };

        var ex = await Should.ThrowAsync<TaskListResolutionException>(
            () => resolver.ResolveAsync(lists, "Tasks", TestContext.Current.CancellationToken));

        ex.UserInput.ShouldBe("Tasks");
        ex.AvailableLists.ShouldContain("Tasks");
        ex.Message.ShouldContain("Ambiguous");
    }

    // ─── Rule 2: Alias matching ────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_AliasTasks_ResolvesToSingleTasksList()
    {
        var resolver = CreateResolver();
        var lists = new List<TaskList> { T("My Tasks"), T("Shopping List") };

        var result = await resolver.ResolveAsync(lists, "tasks", TestContext.Current.CancellationToken);

        result.DisplayName.ShouldBe("My Tasks");
    }

    [Fact]
    public async Task ResolveAsync_AliasTaskList_ResolvesToSingleTasksList()
    {
        var resolver = CreateResolver();
        var lists = new List<TaskList> { T("My Tasks"), T("Shopping List") };

        var result = await resolver.ResolveAsync(lists, "task list", TestContext.Current.CancellationToken);

        result.DisplayName.ShouldBe("My Tasks");
    }

    [Fact]
    public async Task ResolveAsync_AliasShopping_ResolvesToSingleShoppingList()
    {
        var resolver = CreateResolver();
        var lists = new List<TaskList> { T("My Tasks"), T("Shopping List") };

        var result = await resolver.ResolveAsync(lists, "shopping", TestContext.Current.CancellationToken);

        result.DisplayName.ShouldBe("Shopping List");
    }

    [Fact]
    public async Task ResolveAsync_AliasShoppingList_ResolvesToSingleShoppingList()
    {
        var resolver = CreateResolver();
        var lists = new List<TaskList> { T("My Tasks"), T("Shopping List") };

        var result = await resolver.ResolveAsync(lists, "shopping list", TestContext.Current.CancellationToken);

        result.DisplayName.ShouldBe("Shopping List");
    }

    [Fact]
    public async Task ResolveAsync_AliasAmbiguous_ThrowsWithCandidates()
    {
        var resolver = CreateResolver();
        // Two lists that both contain "Tasks"
        var lists = new List<TaskList> { T("Work Tasks"), T("Personal Tasks") };

        var ex = await Should.ThrowAsync<TaskListResolutionException>(
            () => resolver.ResolveAsync(lists, "tasks", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("ambiguous", Case.Insensitive);
        ex.Message.ShouldContain("Work Tasks");
        ex.Message.ShouldContain("Personal Tasks");
    }

    [Fact]
    public async Task ResolveAsync_UnknownAlias_FallsThroughToNotFound()
    {
        var resolver = CreateResolver();
        var lists = new List<TaskList> { T("My Tasks"), T("Shopping List") };

        var ex = await Should.ThrowAsync<TaskListResolutionException>(
            () => resolver.ResolveAsync(lists, "errands", TestContext.Current.CancellationToken));

        ex.UserInput.ShouldBe("errands");
        ex.AvailableLists.ShouldBe(["My Tasks", "Shopping List"]);
    }

    // ─── Rule 3: Configured default ────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_NullInput_UsesConfiguredDefault()
    {
        var resolver = CreateResolver(defaultTaskList: "Shopping List");
        var lists = new List<TaskList> { T("My Tasks"), T("Shopping List") };

        var result = await resolver.ResolveAsync(lists, null, TestContext.Current.CancellationToken);

        result.DisplayName.ShouldBe("Shopping List");
        result.IsDefault.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_EmptyInput_UsesConfiguredDefault()
    {
        var resolver = CreateResolver(defaultTaskList: "My Tasks");
        var lists = new List<TaskList> { T("My Tasks"), T("Shopping List") };

        var result = await resolver.ResolveAsync(lists, "", TestContext.Current.CancellationToken);

        result.DisplayName.ShouldBe("My Tasks");
        result.IsDefault.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolveAsync_NoDefault_NoInput_ThrowsWithCandidates()
    {
        var resolver = CreateResolver();
        var lists = new List<TaskList> { T("My Tasks"), T("Shopping List") };

        var ex = await Should.ThrowAsync<TaskListResolutionException>(
            () => resolver.ResolveAsync(lists, null, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("No default task list configured");
        ex.AvailableLists.ShouldBe(["My Tasks", "Shopping List"]);
    }

    [Fact]
    public async Task ResolveAsync_DefaultNotOnServer_ThrowsWithCandidates()
    {
        var resolver = CreateResolver(defaultTaskList: "Nonexistent");
        var lists = new List<TaskList> { T("My Tasks"), T("Shopping List") };

        var ex = await Should.ThrowAsync<TaskListResolutionException>(
            () => resolver.ResolveAsync(lists, null, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("not found");
    }

    // ─── Rule 4: Empty server list ─────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_EmptyTaskLists_ThrowsWithEmptyCandidates()
    {
        var resolver = CreateResolver();

        var ex = await Should.ThrowAsync<TaskListResolutionException>(
            () => resolver.ResolveAsync([], "Anything", TestContext.Current.CancellationToken));

        ex.AvailableLists.ShouldBeEmpty();
        ex.Message.ShouldContain("No task lists available");
    }

    // ─── Exact match takes precedence over alias ───────────────────────────

    [Fact]
    public async Task ResolveAsync_ExactMatchWinsOverAlias()
    {
        var resolver = CreateResolver();
        var lists = new List<TaskList> { T("tasks"), T("Shopping List") };

        // "tasks" as exact match should win over alias expansion
        var result = await resolver.ResolveAsync(lists, "tasks", TestContext.Current.CancellationToken);

        result.DisplayName.ShouldBe("tasks");
    }

    // ─── Cancellation ──────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_Cancelled_ThrowsOperationCanceled()
    {
        var resolver = CreateResolver();
        var lists = new List<TaskList> { T("My Tasks") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(lists, "My Tasks", cts.Token));
    }
}
