using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

/// <summary>Reads bounded XML bytes using the encoding precedence defined by RFC 7303.</summary>
internal static class XmlResponseReader
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly Encoding Utf16LittleEndian = new UnicodeEncoding(false, false, true);
    private static readonly Encoding Utf16BigEndian = new UnicodeEncoding(true, false, true);
    private static readonly Encoding Utf32LittleEndian = new UTF32Encoding(false, false, true);
    private static readonly Encoding Utf32BigEndian = new UTF32Encoding(true, false, true);

    internal static XDocument Load(byte[] body, string? charset, XmlReaderSettings settings, LoadOptions options = LoadOptions.None)
    {
        try
        {
            var (encoding, preambleLength) = SelectEncoding(body, charset);
            using var stream = new MemoryStream(body, preambleLength, body.Length - preambleLength, writable: false);
            return ReadDocument(body, stream, encoding, SafeSettings(settings), options);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new XmlException("The WebDAV response has an unsupported or malformed XML encoding.", exception);
        }
    }

    private static XDocument ReadDocument(byte[] body, Stream stream, Encoding? encoding, XmlReaderSettings settings, LoadOptions options)
    {
        if (encoding is null)
        {
            using var reader = XmlReader.Create(stream, settings);
            _ = reader.Read();
            var declaredEncoding = reader.NodeType == XmlNodeType.XmlDeclaration ? reader.GetAttribute("encoding") : null;
            // Some framework declaration decoders replace invalid bytes. Validate
            // the original bytes strictly before loading this same reader's DOM.
            _ = DetectedEncoding(body, declaredEncoding).GetCharCount(body);
            return XDocument.Load(reader, options);
        }
        // A TextReader keeps a conflicting XML declaration from overriding the
        // authoritative BOM or HTTP charset. No XML prolog is parsed twice.
        using var text = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false);
        using var encodedReader = XmlReader.Create(text, settings);
        return XDocument.Load(encodedReader, options);
    }

    internal static (Encoding? Encoding, int PreambleLength) SelectEncoding(ReadOnlySpan<byte> prefix, string? charset)
    {
        var (encoding, preambleLength) = DetectByteOrderMark(prefix);
        return (encoding ?? (charset is null ? null : CharsetEncoding(charset)), preambleLength);
    }

    internal static XmlReaderSettings SafeSettings(XmlReaderSettings settings)
    {
        var safe = settings.Clone();
        safe.DtdProcessing = DtdProcessing.Prohibit;
        safe.XmlResolver = null;
        safe.CloseInput = true;
        return safe;
    }

    internal static Encoding CharsetEncoding(string charset)
    {
        var name = charset.Trim();
        if (name.Length >= 2 && name[0] == '"' && name[^1] == '"')
            name = name[1..^1];
        return Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    internal static Encoding DetectedEncoding(ReadOnlySpan<byte> prefix, string? declaredEncoding)
    {
        var encoding = CharsetEncoding(declaredEncoding ?? "utf-8");
        if (prefix.StartsWith<byte>([0x3C, 0x00, 0x00, 0x00]))
            return Utf32LittleEndian;
        if (prefix.StartsWith<byte>([0x00, 0x00, 0x00, 0x3C]))
            return Utf32BigEndian;
        if (prefix.StartsWith<byte>([0x3C, 0x00]))
            return Utf16LittleEndian;
        return prefix.StartsWith<byte>([0x00, 0x3C]) ? Utf16BigEndian : encoding;
    }

    private static (Encoding? Encoding, int Length) DetectByteOrderMark(ReadOnlySpan<byte> body)
    {
        // Check UTF-32 first because its little-endian BOM begins like UTF-16.
        if (body.StartsWith<byte>([0xFF, 0xFE, 0x00, 0x00]))
            return (Utf32LittleEndian, 4);
        if (body.StartsWith<byte>([0x00, 0x00, 0xFE, 0xFF]))
            return (Utf32BigEndian, 4);
        if (body.StartsWith<byte>([0xEF, 0xBB, 0xBF]))
            return (Utf8, 3);
        if (body.StartsWith<byte>([0xFF, 0xFE]))
            return (Utf16LittleEndian, 2);
        return body.StartsWith<byte>([0xFE, 0xFF]) ? (Utf16BigEndian, 2) : (null, 0);
    }
}
