using System.Text;
using System.Xml;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

/// <summary>Preserves XML encoding precedence while consuming a bounded, asynchronous response stream.</summary>
internal sealed class XmlStreamingResponseReader(XmlReader reader, XmlEncodingValidationStream stream) : IDisposable
{
    internal XmlReader Reader => reader;

    internal static async Task<XmlStreamingResponseReader> CreateAsync(
        Stream source,
        string? charset,
        XmlReaderSettings settings,
        int maximumDetectionBytes,
        CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        var length = await ReadPrefixAsync(source, prefix, cancellationToken).ConfigureAwait(false);
        var (encoding, preambleLength) = XmlResponseReader.SelectEncoding(prefix.AsSpan(0, length), charset);
        var stream = new XmlEncodingValidationStream(
            source,
            prefix.AsMemory(preambleLength, length - preambleLength),
            encoding is null ? maximumDetectionBytes : 0,
            cancellationToken);
        return CreateReader(stream, encoding, XmlResponseReader.SafeSettings(settings));
    }

    internal async Task<bool> ReadAsync()
    {
        var read = await reader.ReadAsync().ConfigureAwait(false);
        stream.CompleteEncodingDetection(reader.NodeType == XmlNodeType.XmlDeclaration
            ? reader.GetAttribute("encoding")
            : null);
        return read;
    }

    public void Dispose() => reader.Dispose();

    private static XmlStreamingResponseReader CreateReader(
        XmlEncodingValidationStream stream,
        Encoding? encoding,
        XmlReaderSettings settings)
    {
        try
        {
            var reader = encoding is null
                ? XmlReader.Create(stream, settings)
                : XmlReader.Create(new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 4096), settings);
            return new XmlStreamingResponseReader(reader, stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static async Task<int> ReadPrefixAsync(Stream source, byte[] prefix, CancellationToken cancellationToken)
    {
        var length = 0;
        while (length < prefix.Length)
        {
            var read = await source.ReadAsync(prefix.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            length += read;
        }
        return length;
    }
}
