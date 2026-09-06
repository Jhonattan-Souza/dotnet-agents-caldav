using System.Xml;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

[Flags]
internal enum DavMutationErrorKind
{
    None = 0,
    NoUidConflict = 1,
    UnsupportedCapability = 2
}

internal static class DavMutationErrorReader
{
    private const int MaximumErrorBodyBytes = 64 * 1024;

    public static async Task<DavMutationErrorKind> ReadAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaximumErrorBodyBytes)
            return DavMutationErrorKind.None;
        try
        {
            await using var source = await content.ReadAsStreamAsync(cancellationToken);
            using var destination = new MemoryStream();
            var buffer = new byte[8192];
            while (destination.Length <= MaximumErrorBodyBytes)
            {
                var remainingPlusOne = (MaximumErrorBodyBytes - (int)destination.Length) + 1;
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, remainingPlusOne)),
                    cancellationToken);
                if (read == 0)
                    return Classify(destination.ToArray(), content.Headers.ContentType?.CharSet);
                if (destination.Length + read > MaximumErrorBodyBytes)
                    return DavMutationErrorKind.None;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch (Exception exception) when (exception is XmlException
            or HttpRequestException
            or IOException
            or OperationCanceledException)
        {
            return DavMutationErrorKind.None;
        }
        return DavMutationErrorKind.None;
    }

    private static DavMutationErrorKind Classify(byte[] body, string? charset)
    {
        var document = DavResponseParser.ParseDocument(body, charset);
        var result = DavMutationErrorKind.None;
        if (DavResponseParser.IsNoUidConflictError(document))
            result |= DavMutationErrorKind.NoUidConflict;
        if (DavResponseParser.IsUnsupportedCapabilityError(document))
            result |= DavMutationErrorKind.UnsupportedCapability;
        return result;
    }
}
