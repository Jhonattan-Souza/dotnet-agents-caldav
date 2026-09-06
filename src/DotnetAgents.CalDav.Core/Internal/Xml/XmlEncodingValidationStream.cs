using System.Text;
using System.Xml;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

/// <summary>Replays the encoding probe and validates declaration-selected bytes without buffering a response batch.</summary>
internal sealed class XmlEncodingValidationStream(
    Stream source,
    ReadOnlyMemory<byte> prefix,
    int maximumDetectionBytes,
    CancellationToken operationCancellation) : Stream
{
    private ReadOnlyMemory<byte> _prefix = prefix;
    private MemoryStream? _detection = maximumDetectionBytes > 0 ? new MemoryStream() : null;
    private Decoder? _decoder;
    private bool _endOfStream;

    public override bool CanRead => source.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("The XML response parser uses asynchronous reads only.");

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        operationCancellation.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty)
            return 0;
        var read = ReplayPrefix(buffer.Span);
        if (read == 0)
        {
            read = await ReadSourceAsync(buffer, cancellationToken).ConfigureAwait(false);
            _endOfStream = read == 0;
        }
        Observe(buffer.Span[..read]);
        return read;
    }

    internal void CompleteEncodingDetection(string? declaredEncoding)
    {
        if (_detection is null)
            return;
        var captured = _detection.GetBuffer().AsSpan(0, (int)_detection.Length);
        var encoding = XmlResponseReader.DetectedEncoding(captured, declaredEncoding);
        // XmlReader's default and exact UTF-8 name use a strict decoder; aliases
        // such as unicode-1-1-utf-8 can instead select a replacement fallback.
        if (!HasNativeStrictDecoder(encoding, declaredEncoding))
        {
            _decoder = encoding.GetDecoder();
            Validate(captured);
        }
        _detection.Dispose();
        _detection = null;
    }

    private static bool HasNativeStrictDecoder(Encoding encoding, string? declaredEncoding) =>
        encoding.CodePage == Encoding.UTF8.CodePage
        && (declaredEncoding is null || declaredEncoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase));

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _detection?.Dispose();
            source.Dispose();
        }
        base.Dispose(disposing);
    }

    private int ReplayPrefix(Span<byte> destination)
    {
        var read = Math.Min(destination.Length, _prefix.Length);
        _prefix.Span[..read].CopyTo(destination);
        _prefix = _prefix[read..];
        return read;
    }

    private async ValueTask<int> ReadSourceAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled || cancellationToken == operationCancellation)
            return await source.ReadAsync(buffer, operationCancellation).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(operationCancellation, cancellationToken);
        return await source.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
    }

    private void Observe(ReadOnlySpan<byte> bytes)
    {
        if (_detection is null)
        {
            Validate(bytes);
            return;
        }
        if (_detection.Length + bytes.Length > maximumDetectionBytes)
            throw new XmlException("The XML response exceeded its bounded encoding declaration envelope.");
        _detection.Write(bytes);
    }

    private void Validate(ReadOnlySpan<byte> bytes)
    {
        if (_decoder is null)
            return;
        Span<char> characters = stackalloc char[512];
        bool complete;
        do
        {
            _decoder.Convert(bytes, characters, _endOfStream, out var consumed, out _, out complete);
            bytes = bytes[consumed..];
        } while (!complete);
    }
}
