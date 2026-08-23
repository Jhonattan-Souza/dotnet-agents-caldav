using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DotnetAgents.CalDav.Core.Internal.Xml;

/// <summary>Streams one bounded Calendar multiget batch without imposing the single-resource REPORT limit.</summary>
internal static class CalendarMultigetResponseParser
{
    private const int MaximumResourceBytes = 4 * 1024 * 1024;
    private const int MaximumResponseEnvelopeBytes = 64 * 1024;
    private const int MaximumRootEnvelopeBytes = 64 * 1024;
    private const int MaximumDepth = 64;
    private const long MaximumBatchBytes =
        CalendarQueryPolicy.MaximumMultigetBatchSize
        * ((5L * MaximumResourceBytes) + MaximumResponseEnvelopeBytes)
        + MaximumRootEnvelopeBytes;
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private static readonly XName ResponseName = Dav + "response";
    private static readonly XName MultistatusName = Dav + "multistatus";
    private static readonly XName CalendarDataName = CalDav + "calendar-data";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static async Task<IReadOnlyList<CalendarMultigetResource>> ParseAsync(
        HttpContent content,
        int requestedResourceCount,
        CancellationToken cancellationToken)
    {
        if (requestedResourceCount is < 1 or > CalendarQueryPolicy.MaximumMultigetBatchSize)
            throw new ArgumentOutOfRangeException(nameof(requestedResourceCount));
        try
        {
            return await ParseStrictUtf8Async(content, requestedResourceCount, cancellationToken).ConfigureAwait(false);
        }
        catch (DecoderFallbackException exception)
        {
            throw new XmlException("The Calendar multiget response was not valid UTF-8.", exception);
        }
    }

    private static async Task<IReadOnlyList<CalendarMultigetResource>> ParseStrictUtf8Async(
        HttpContent content,
        int requestedResourceCount,
        CancellationToken cancellationToken)
    {
        await using var encoded = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using Stream decompressed = content.Headers.ContentEncoding.Contains("gzip", StringComparer.OrdinalIgnoreCase)
            ? new GZipStream(encoded, CompressionMode.Decompress, leaveOpen: false)
            : encoded;
        await using var bounded = new MaximumReadStream(decompressed, MaximumBatchBytes);
        using var text = new StreamReader(
            bounded,
            StrictUtf8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: false);
        using var reader = XmlReader.Create(text, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
            MaxCharactersFromEntities = 0,
            MaxCharactersInDocument = MaximumBatchBytes
        });
        return await ReadResourcesAsync(reader, requestedResourceCount, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<CalendarMultigetResource>> ReadResourcesAsync(
        XmlReader reader,
        int requestedResourceCount,
        CancellationToken cancellationToken)
    {
        var resources = new List<CalendarMultigetResource>(requestedResourceCount);
        var root = new XmlEnvelopeBudget(MaximumRootEnvelopeBytes, "root");
        var rootSeen = false;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDepth(reader, "response");
            if (!rootSeen)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    root.AccountNode(reader);
                    continue;
                }
                EnsureRoot(reader, root);
                rootSeen = true;
                continue;
            }
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Depth != 1 || NameOf(reader) != ResponseName)
                    throw new XmlException("A Calendar multiget response must contain only direct DAV:response children.");
                if (resources.Count == requestedResourceCount)
                    throw new XmlException("A Calendar multiget response exceeded the requested response count.");
                resources.Add(await ReadResponseAsync(reader, cancellationToken).ConfigureAwait(false));
                continue;
            }
            root.AccountNode(reader);
        }
        if (!rootSeen)
            throw new XmlException("A Calendar multiget response is missing its DAV:multistatus root.");
        if (resources.Count != requestedResourceCount)
            throw new XmlException("A Calendar multiget response omitted a requested resource response.");
        return resources;
    }

    private static void EnsureRoot(XmlReader reader, XmlEnvelopeBudget budget)
    {
        if (reader.NodeType != XmlNodeType.Element || reader.Depth != 0 || NameOf(reader) != MultistatusName)
            throw new XmlException("A Calendar multiget response must have a DAV:multistatus root.");
        budget.AccountElement(reader);
    }

    private static async Task<CalendarMultigetResource> ReadResponseAsync(
        XmlReader outer,
        CancellationToken cancellationToken)
    {
        using var reader = outer.ReadSubtree();
        var budget = new XmlEnvelopeBudget(MaximumResponseEnvelopeBytes, "resource response");
        var state = new MultigetResponseState();
        if (!await reader.ReadAsync().ConfigureAwait(false) || NameOf(reader) != ResponseName)
            throw new XmlException("A Calendar multiget response is malformed.");
        budget.AccountElement(reader);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDepth(reader, "resource response");
            if (reader.NodeType != XmlNodeType.Element)
            {
                budget.AccountNode(reader);
                continue;
            }
            if (reader.Depth != 1)
                throw new XmlException("A Calendar multiget response contains invalid response structure.");
            var name = NameOf(reader);
            if (name == Dav + "href")
                state.SetHref(await ReadTextElementAsync(reader, budget, calendarData: false).ConfigureAwait(false));
            else if (name == Dav + "status")
                state.SetResponseStatus(await ReadStatusAsync(reader, budget).ConfigureAwait(false));
            else if (name == Dav + "propstat")
                state.Add(await ReadPropstatAsync(reader, budget, cancellationToken).ConfigureAwait(false));
            else
                await SkipElementAsync(reader, budget, cancellationToken).ConfigureAwait(false);
        }
        return state.Build();
    }

    private static async Task<MultigetPropstat> ReadPropstatAsync(
        XmlReader reader,
        XmlEnvelopeBudget budget,
        CancellationToken cancellationToken)
    {
        var depth = reader.Depth;
        budget.AccountElement(reader);
        var state = new MultigetPropstatState();
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDepth(reader, "resource response");
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;
            if (reader.NodeType != XmlNodeType.Element)
            {
                budget.AccountNode(reader);
                continue;
            }
            if (reader.Depth != depth + 1)
                throw new XmlException("A Calendar multiget propstat contains invalid structure.");
            await ReadPropstatElementAsync(reader, budget, state, cancellationToken).ConfigureAwait(false);
        }
        return state.Build();
    }

    private static async Task ReadPropstatElementAsync(
        XmlReader reader,
        XmlEnvelopeBudget budget,
        MultigetPropstatState state,
        CancellationToken cancellationToken)
    {
        var name = NameOf(reader);
        if (name == Dav + "prop")
        {
            state.SetProperties(await ReadPropertiesAsync(reader, budget, cancellationToken).ConfigureAwait(false));
        }
        else if (name == Dav + "status")
        {
            state.SetStatus(await ReadStatusAsync(reader, budget).ConfigureAwait(false));
        }
        else
        {
            await SkipElementAsync(reader, budget, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<MultigetProperties> ReadPropertiesAsync(
        XmlReader reader,
        XmlEnvelopeBudget budget,
        CancellationToken cancellationToken)
    {
        var depth = reader.Depth;
        budget.AccountElement(reader);
        if (reader.IsEmptyElement)
            return new MultigetProperties(null, null, 0, 0);
        string? entityTag = null;
        string? calendarData = null;
        var entityTagCount = 0;
        var calendarDataCount = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDepth(reader, "resource response");
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                break;
            if (reader.NodeType != XmlNodeType.Element)
            {
                budget.AccountNode(reader);
                continue;
            }
            if (reader.Depth != depth + 1)
                throw new XmlException("Calendar multiget properties contain invalid structure.");
            var name = NameOf(reader);
            if (name == Dav + "getetag")
            {
                entityTagCount++;
                entityTag = await ReadTextElementAsync(reader, budget, calendarData: false).ConfigureAwait(false);
            }
            else if (name == CalendarDataName)
            {
                calendarDataCount++;
                calendarData = await ReadTextElementAsync(reader, budget, calendarData: true).ConfigureAwait(false);
            }
            else
            {
                await SkipElementAsync(reader, budget, cancellationToken).ConfigureAwait(false);
            }
        }
        return new MultigetProperties(entityTag, calendarData, entityTagCount, calendarDataCount);
    }

    private static async Task<int> ReadStatusAsync(XmlReader reader, XmlEnvelopeBudget budget) =>
        DavResponseParser.ParseStatusCode(
            await ReadTextElementAsync(reader, budget, calendarData: false).ConfigureAwait(false));

    private static async Task<string> ReadTextElementAsync(
        XmlReader reader,
        XmlEnvelopeBudget budget,
        bool calendarData)
    {
        var depth = reader.Depth;
        budget.AccountElement(reader);
        if (reader.IsEmptyElement)
            return string.Empty;
        var value = new StringBuilder();
        var buffer = new char[4096];
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            EnsureDepth(reader, "resource response");
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                return value.ToString();
            if (reader.NodeType == XmlNodeType.Element)
                throw new XmlException("A Calendar multiget scalar property contained nested markup.");
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA
                or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                await ReadTextChunksAsync(reader, value, buffer, budget, calendarData).ConfigureAwait(false);
            }
            else
            {
                budget.AccountNode(reader);
            }
        }
        throw new XmlException("A Calendar multiget scalar property was not closed.");
    }

    private static async Task ReadTextChunksAsync(
        XmlReader reader,
        StringBuilder value,
        char[] buffer,
        XmlEnvelopeBudget budget,
        bool calendarData)
    {
        int read;
        do
        {
            read = await reader.ReadValueChunkAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read == 0)
                continue;
            var chunk = buffer.AsSpan(0, read);
            budget.AccountText(chunk, calendarData);
            value.Append(chunk);
        } while (read != 0);
    }

    private static async Task SkipElementAsync(
        XmlReader reader,
        XmlEnvelopeBudget budget,
        CancellationToken cancellationToken)
    {
        var depth = reader.Depth;
        budget.AccountElement(reader);
        if (reader.IsEmptyElement)
            return;
        var buffer = new char[4096];
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDepth(reader, "resource response");
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
                return;
            if (reader.NodeType == XmlNodeType.Element)
                budget.AccountElement(reader);
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA
                     or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
                await ReadTextChunksAsync(reader, new StringBuilder(), buffer, budget, calendarData: false)
                    .ConfigureAwait(false);
            else
                budget.AccountNode(reader);
        }
        throw new XmlException("A Calendar multiget response element was not closed.");
    }

    private static void EnsureDepth(XmlReader reader, string scope)
    {
        if (reader.Depth > MaximumDepth)
            throw new XmlException($"The Calendar multiget {scope} exceeded the safe XML depth.");
    }

    private static XName NameOf(XmlReader reader) => XName.Get(reader.LocalName, reader.NamespaceURI);

    private sealed class XmlEnvelopeBudget(long maximumBytes, string scope)
    {
        private long _envelopeBytes;
        private long _calendarDataBytes;

        internal void AccountElement(XmlReader reader)
        {
            _envelopeBytes += Encoding.UTF8.GetByteCount(reader.Name) + 8;
            if (reader.HasAttributes)
            {
                while (reader.MoveToNextAttribute())
                {
                    _envelopeBytes += Encoding.UTF8.GetByteCount(reader.Name);
                    _envelopeBytes += Encoding.UTF8.GetByteCount(reader.Value) + 4;
                }
                reader.MoveToElement();
            }
            EnsureBounded();
        }

        internal void AccountNode(XmlReader reader)
        {
            if (reader.NodeType is XmlNodeType.Comment or XmlNodeType.ProcessingInstruction)
                throw new XmlException($"The Calendar multiget {scope} contained unsupported XML markup.");
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA
                or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                AccountText(reader.Value.AsSpan(), calendarData: false);
            }
        }

        internal void AccountText(ReadOnlySpan<char> value, bool calendarData)
        {
            var bytes = Encoding.UTF8.GetByteCount(value);
            if (calendarData)
                _calendarDataBytes += bytes;
            else
                _envelopeBytes += bytes;
            EnsureBounded();
        }

        private void EnsureBounded()
        {
            if (_envelopeBytes > maximumBytes)
                throw new XmlException($"The Calendar multiget {scope} exceeded its bounded envelope.");
            if (_calendarDataBytes > MaximumResourceBytes)
                throw new XmlException("A Calendar multiget resource response exceeded its resource payload bound.");
        }
    }

    private sealed class MultigetResponseState
    {
        private readonly List<MultigetPropstat> _propstats = [];
        private string? _href;
        private int? _responseStatus;

        internal void SetHref(string href)
        {
            if (_href is not null)
                throw new XmlException("A Calendar multiget response contained duplicate href truth.");
            _href = string.IsNullOrWhiteSpace(href)
                ? throw new XmlException("A Calendar multiget response is missing its href.")
                : href.Trim();
        }

        internal void SetResponseStatus(int status)
        {
            if (_responseStatus is not null)
                throw new XmlException("A Calendar multiget response contained duplicate response status truth.");
            _responseStatus = status;
        }

        internal void Add(MultigetPropstat propstat) => _propstats.Add(propstat);

        internal CalendarMultigetResource Build()
        {
            var href = _href ?? throw new XmlException("A Calendar multiget response is missing its href.");
            var successful = _propstats.Where(item => item.Status is >= 200 and <= 299).ToArray();
            if (successful.Length == 0)
                return new CalendarMultigetResource(href, GetFailureStatus(), null, null);
            if (_responseStatus is not null)
                throw new XmlException("A Calendar multiget response mixed response and successful propstat truth.");
            var entityTags = successful.Sum(item => item.Properties.EntityTagCount);
            var calendarData = successful.Sum(item => item.Properties.CalendarDataCount);
            if (entityTags > 1 || calendarData > 1)
                throw new XmlException("A Calendar multiget response contained conflicting authoritative property truth.");
            return new CalendarMultigetResource(
                href,
                200,
                successful.Select(item => item.Properties.EntityTag).SingleOrDefault(value => value is not null)?.Trim(),
                successful.Select(item => item.Properties.CalendarData).SingleOrDefault(value => value is not null));
        }

        private int GetFailureStatus()
        {
            var statuses = _propstats.Select(item => (int?)item.Status)
                .Append(_responseStatus)
                .OfType<int>()
                .Distinct()
                .ToArray();
            return statuses.Length switch
            {
                0 => throw new XmlException("A Calendar multiget response is missing its status."),
                1 => statuses[0],
                _ => throw new XmlException("A Calendar multiget response contained inconsistent failure status truth.")
            };
        }
    }

    private sealed record MultigetPropstat(int Status, MultigetProperties Properties);

    private sealed class MultigetPropstatState
    {
        private MultigetProperties? _properties;
        private int? _status;

        internal void SetProperties(MultigetProperties properties)
        {
            if (_properties is not null)
                throw new XmlException("A Calendar multiget propstat contained duplicate DAV:prop truth.");
            _properties = properties;
        }

        internal void SetStatus(int status)
        {
            if (_status is not null)
                throw new XmlException("A Calendar multiget propstat contained duplicate DAV:status truth.");
            _status = status;
        }

        internal MultigetPropstat Build() => new(
            _status ?? throw new XmlException("A Calendar multiget propstat is missing its status."),
            _properties ?? throw new XmlException("A Calendar multiget propstat is missing its properties."));
    }

    private sealed record MultigetProperties(
        string? EntityTag,
        string? CalendarData,
        int EntityTagCount,
        int CalendarDataCount);

    private sealed class MaximumReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long _observed;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _observed; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("The bounded multiget parser uses asynchronous reads only.");
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var remainingPlusOne = maximumBytes - _observed + 1;
            if (remainingPlusOne <= 0)
                throw new XmlException("The Calendar multiget response exceeded its bounded batch payload.");
            var read = await inner.ReadAsync(
                buffer[..(int)Math.Min(buffer.Length, remainingPlusOne)],
                cancellationToken).ConfigureAwait(false);
            _observed += read;
            if (_observed > maximumBytes)
                throw new XmlException("The Calendar multiget response exceeded its bounded batch payload.");
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }
    }
}
