using System.Net.Http.Headers;
using DotnetAgents.CalDav.Core.Internal.Xml;

namespace DotnetAgents.CalDav.Core.Internal;

internal sealed partial class CalDavClient
{
    internal const int MaximumScannedCollectionMembers = CalendarQuerySnapshotPolicy.MaximumItems;
    internal const long MaximumScannedCollectionBytes = CalendarQuerySnapshotPolicy.MaximumBytes;

    /// <summary>
    /// A recursive collection DELETE is storage-only when OPTIONS proves automatic scheduling absent,
    /// or when a complete, stable member scan finds no participation data for the server to act on.
    /// </summary>
    private async Task<bool> IsCollectionDeletionPermittedAsync(string calendarHref, CancellationToken cancellationToken) =>
        await IsSchedulingPermittedAsync(calendarHref, cancellationToken).ConfigureAwait(false)
        || await ScanMembersWithoutParticipationAsync(calendarHref, cancellationToken).ConfigureAwait(false);

    private async Task<bool> ScanMembersWithoutParticipationAsync(string calendarHref, CancellationToken cancellationToken)
    {
        try
        {
            var before = await ReadMemberRevisionsAsync(calendarHref, cancellationToken).ConfigureAwait(false);
            if (before is null
                || !await MembersLackParticipationAsync(calendarHref, before, cancellationToken).ConfigureAwait(false))
                return false;

            // A member added or changed while the scan ran was not inspected. Re-reading the
            // revisions narrows that window to the round trip before DELETE; any drift fails closed.
            var after = await ReadMemberRevisionsAsync(calendarHref, cancellationToken).ConfigureAwait(false);
            return after is not null
                && after.Count == before.Count
                && after.All(member => before.TryGetValue(member.Key, out var tag)
                    && string.Equals(tag, member.Value, StringComparison.Ordinal));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception exception) when (IsUnprovenSchedulingEvidence(exception))
        {
            return false;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>?> ReadMemberRevisionsAsync(
        string calendarHref,
        CancellationToken cancellationToken)
    {
        var response = await SendPropFindAsync(calendarHref, DavRequestBuilder.BuildPropFindMemberRevisions(), 1,
            cancellationToken).ConfigureAwait(false);
        // DELETE is not redirected, so a listing of any other collection proves nothing about its target.
        if (!string.Equals(response.RequestUri.AbsoluteUri, calendarHref, StringComparison.Ordinal))
            return null;

        var calendarUri = response.RequestUri;
        var revisions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in DavResponseParser.ParseMemberRevisions(
                     DavResponseParser.ParseDocument(response.Content, response.Charset)))
        {
            if (IsCollectionSelfHref(calendarUri, member.Href))
                continue;
            if (!TryAddMemberRevision(calendarUri, member, revisions)
                || revisions.Count > MaximumScannedCollectionMembers)
                return null;
        }
        return revisions;
    }

    // A nested collection, an unknown type, or a missing strong revision cannot be inspected
    // within the bounded scan, so each keeps the scheduling-absence proof requirement.
    private bool TryAddMemberRevision(
        Uri calendarUri,
        CalendarCollectionMemberRevision member,
        Dictionary<string, string> revisions) =>
        member.IsCollection == false
        && EntityTagHeaderValue.TryParse(member.EntityTag, out var entityTag)
        && !entityTag.IsWeak
        && TryCanonicalizeResourceHref(calendarUri, member.Href, out var canonicalHref)
        && revisions.TryAdd(canonicalHref, entityTag.ToString());

    private async Task<bool> MembersLackParticipationAsync(
        string calendarHref,
        IReadOnlyDictionary<string, string> revisions,
        CancellationToken cancellationToken)
    {
        var scannedBytes = 0L;
        foreach (var batch in revisions.Keys.Order(StringComparer.Ordinal)
                     .Chunk(CalendarQueryPolicy.MaximumMultigetBatchSize))
        {
            var reads = await GetCalendarResourcesForQueryAsync(calendarHref, batch, cancellationToken)
                .ConfigureAwait(false);
            foreach (var read in reads)
            {
                // Only a successful strong read carries an entity tag; a missing, unreadable, or
                // changed member cannot match the listed revision.
                if (!string.Equals(read.EntityTag, revisions.GetValueOrDefault(read.ResourceHref!),
                        StringComparison.Ordinal))
                    return false;
                scannedBytes += read.AuthoritativeUtf8.Length;
                if (scannedBytes > MaximumScannedCollectionBytes
                    || CalendarSchedulingSafety.HasParticipation(read.AuthoritativeUtf8))
                    return false;
            }
        }
        return true;
    }
}
