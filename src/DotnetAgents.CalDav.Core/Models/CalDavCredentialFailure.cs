using DotnetAgents.CalDav.Core.Internal;

namespace DotnetAgents.CalDav.Core.Models;

/// <summary>Identifies failures to obtain a CalDAV credential without exposing the transport exception type.</summary>
public static class CalDavCredentialFailure
{
    /// <summary>
    /// True when no credential could be obtained for a CalDAV request, such as a rejected or
    /// unreachable OAuth 2.0 token endpoint. The CalDAV request itself was not sent.
    /// </summary>
    public static bool IsCredentialFailure(Exception exception) => exception is CalDavAuthenticationException;
}
