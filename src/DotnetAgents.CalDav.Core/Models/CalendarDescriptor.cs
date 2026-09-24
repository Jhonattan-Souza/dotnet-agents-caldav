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

    /// <summary>The CALDAV:calendar-description text.</summary>
    public string? Description { get; init; }

    /// <summary>The separate WebDAV DAV:description text; never merged with <see cref="Description"/>.</summary>
    public string? DavDescription { get; init; }

    /// <summary>Calendar Color as <c>#RRGGBB</c>; an Apple <c>#RRGGBBAA</c> alpha channel is discarded.</summary>
    public string? Color { get; init; }

    /// <summary>
    /// Advisory CalendarServer <c>getctag</c> value when the server reports one. It is opaque
    /// and cheap change evidence only: never a revision, a precondition, or a sync token.
    /// </summary>
    public string? ChangeTag { get; init; }
    /// <summary>Non-negative Apple calendar-order sort position, when the server reports one.</summary>
    public int? Order { get; init; }

    public required EntityKindSupport EventSupport { get; init; }

    public required EntityKindSupport TodoSupport { get; init; }

    public IReadOnlyList<CapabilityEvidence> EventEvidence { get; init; } = [];

    public IReadOnlyList<CapabilityEvidence> TodoEvidence { get; init; } = [];

    /// <summary>Properties explicitly reported unavailable by discovery, with their DAV status.</summary>
    public IReadOnlyList<CalendarUnavailableProperty> UnavailableProperties { get; init; } = [];
}

/// <summary>Evidence that one Calendar property was unavailable in a discovery response.</summary>
public sealed record CalendarUnavailableProperty(string NamespaceUri, string LocalName, int StatusCode);

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
