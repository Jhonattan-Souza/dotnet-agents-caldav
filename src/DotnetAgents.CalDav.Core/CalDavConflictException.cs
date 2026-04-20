namespace DotnetAgents.CalDav.Core;

/// <summary>
/// Thrown when a CalDAV update or delete fails due to ETag mismatch (HTTP 412 Precondition Failed).
/// The caller should re-fetch the resource, merge changes, and retry.
/// </summary>
public sealed class CalDavConflictException : Exception
{
    public string? CurrentEtag { get; }
    public string Href { get; }

    public CalDavConflictException(string href, string? currentEtag)
        : base($"Precondition Failed for {href}. The resource was modified by another client (ETag mismatch).")
    {
        Href = href;
        CurrentEtag = currentEtag;
    }

    public CalDavConflictException(string href, string? currentEtag, string message)
        : base(message)
    {
        Href = href;
        CurrentEtag = currentEtag;
    }

    public CalDavConflictException(string href, string? currentEtag, string message, Exception innerException)
        : base(message, innerException)
    {
        Href = href;
        CurrentEtag = currentEtag;
    }
}