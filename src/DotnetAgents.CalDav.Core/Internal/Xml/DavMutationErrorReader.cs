using System.Text;

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
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

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
                    return Classify(StrictUtf8.GetString(destination.GetBuffer(), 0, (int)destination.Length));
                if (destination.Length + read > MaximumErrorBodyBytes)
                    return DavMutationErrorKind.None;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        catch (Exception exception) when (exception is DecoderFallbackException
            or HttpRequestException
            or IOException
            or OperationCanceledException)
        {
            return DavMutationErrorKind.None;
        }
        return DavMutationErrorKind.None;
    }

    private static DavMutationErrorKind Classify(string xml)
    {
        var result = DavMutationErrorKind.None;
        if (DavResponseParser.IsNoUidConflictError(xml))
            result |= DavMutationErrorKind.NoUidConflict;
        if (DavResponseParser.IsUnsupportedCapabilityError(xml))
            result |= DavMutationErrorKind.UnsupportedCapability;
        return result;
    }
}
