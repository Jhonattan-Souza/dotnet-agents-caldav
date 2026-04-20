namespace DotnetAgents.CalDav.Mcp.Tests.Unit;

/// <summary>
/// Test time provider for deterministic testing.
/// Always returns the same fixed UTC time.
/// </summary>
public sealed class FixedTimeProvider : TimeProvider
{
    private readonly DateTimeOffset _utcNow;

    public FixedTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;
}