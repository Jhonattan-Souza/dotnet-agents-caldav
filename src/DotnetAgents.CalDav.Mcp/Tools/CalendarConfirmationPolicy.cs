using System.Text.Json;
using System.Text.Json.Nodes;
using DotnetAgents.CalDav.Core.Configuration;
using ModelContextProtocol.Protocol;

namespace DotnetAgents.CalDav.Mcp.Tools;

/// <summary>
/// Applies the configured <c>CALDAV_CONFIRMATION_POLICY</c> to the mutations that define an MRTR
/// confirmation, and discloses a confirmation the policy skipped. See ADR 0010.
/// </summary>
/// <remarks>
/// The execution policy attaches the validated value for each tool call. Code that runs without an
/// attached value, such as a direct raw tool call, uses <see cref="CalDavConfirmationPolicies.Always"/>, so
/// a missing attachment can only add a confirmation, never remove one.
/// </remarks>
internal static class CalendarConfirmationPolicy
{
    internal const string PropertyName = "confirmation";
    internal const string SkippedByPolicy = "skipped_by_policy";

    private static readonly AsyncLocal<string?> Policy = new();
    private static readonly AsyncLocal<bool> Skipped = new();

    internal static string Current => Policy.Value ?? CalDavConfirmationPolicies.Always;

    internal static PolicyScope Attach(string? policy)
    {
        var previous = Policy.Value;
        Policy.Value = policy;
        return new PolicyScope(previous);
    }

    /// <summary>
    /// Reports whether a mutation whose confirmation the default policy requires still asks for it.
    /// </summary>
    /// <param name="destructiveScope">
    /// Whether the mutation reaches beyond one resource or one recurrence instance: collection deletion,
    /// a recurrence-definition, this-and-future or entire-set patch, or an exact write.
    /// </param>
    internal static bool RequiresConfirmation(bool destructiveScope) => Current switch
    {
        CalDavConfirmationPolicies.Never => false,
        CalDavConfirmationPolicies.DestructiveScope => destructiveScope,
        _ => true
    };

    /// <summary>Marks the rest of this call as a mutation that runs without the confirmation it defines.</summary>
    internal static SkipScope Skip()
    {
        var previous = Skipped.Value;
        Skipped.Value = true;
        return new SkipScope(previous);
    }

    /// <summary>
    /// Adds the skip disclosure to a result whose write committed or may have committed. Results that
    /// attempted nothing carry no disclosure, because the skipped confirmation changed nothing for them.
    /// </summary>
    internal static void Apply(CallToolResult result)
    {
        if (!Skipped.Value
            || result.StructuredContent is not { ValueKind: JsonValueKind.Object } structured
            || !structured.TryGetProperty("mutationState", out var mutationState)
            || mutationState.ValueKind != JsonValueKind.String
            || mutationState.GetString() is not ("committed" or "unknown"))
            return;

        var body = JsonObject.Create(structured)!;
        body[PropertyName] = SkippedByPolicy;
        result.StructuredContent = JsonSerializer.SerializeToElement(body);
    }

    internal sealed class PolicyScope(string? previous) : IDisposable
    {
        public void Dispose() => Policy.Value = previous;
    }

    internal sealed class SkipScope(bool previous) : IDisposable
    {
        public void Dispose() => Skipped.Value = previous;
    }
}
