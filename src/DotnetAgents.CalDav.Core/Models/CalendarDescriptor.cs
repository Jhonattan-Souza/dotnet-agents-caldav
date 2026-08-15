namespace DotnetAgents.CalDav.Core.Models;

/// <summary>Describes one discovered CalDAV Calendar collection.</summary>
public sealed record CalendarDescriptor
{
    /// <summary>Canonical absolute href that identifies the Calendar.</summary>
    public required string Href { get; init; }

    /// <summary>Server-provided or href-derived display name; never identity.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Explains how <see cref="DisplayName"/> was obtained.</summary>
    public required DisplayNameProvenance DisplayNameProvenance { get; init; }

    public string? Description { get; init; }

    public string? Color { get; init; }

    public required EntityKindSupport EventSupport { get; init; }

    public required EntityKindSupport TodoSupport { get; init; }

    public IReadOnlyList<CapabilityEvidence> EventEvidence { get; init; } = [];

    public IReadOnlyList<CapabilityEvidence> TodoEvidence { get; init; } = [];
}

/// <summary>Provenance for a Calendar display name.</summary>
public enum DisplayNameProvenance
{
    DavDisplayName,
    DerivedFromHref,
    Missing
}

/// <summary>Advertisement state for one Calendar Entity Kind.</summary>
public enum EntityKindSupport
{
    Advertised,
    NotAdvertised,
    Unknown
}

/// <summary>Raw standards discovery evidence for a capability state.</summary>
public sealed record CapabilityEvidence(string Source, string Value);
