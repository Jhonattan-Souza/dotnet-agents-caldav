using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using DotnetAgents.CalDav.Core.Internal.Xml;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class CalendarMultigetEncodingTests
{
    private const string Href = "https://cal.example/calendars/work/café.ics";
    private const string CalendarData = "BEGIN:VCALENDAR\nSUMMARY:café\nEND:VCALENDAR\n";

    [Theory]
    [MemberData(nameof(ValidEncodings))]
    public async Task EncodingPrecedencePreservesUnicodeScalarsAcrossOneByteReads(
        string encodingName,
        bool includeBom,
        string? charset,
        string? declaration)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        var bytes = EncodedBody(encoding, includeBom, declaration, SuccessResponse(CalendarData));
        using var source = new FragmentedStream(bytes, maximumRead: 1);
        using var content = Content(source, charset);

        var resource = (await ParseAsync(content)).Single();

        resource.Href.ShouldBe(Href);
        resource.EntityTag.ShouldBe("\"café\"");
        resource.CalendarData.ShouldBe(CalendarData);
        source.FirstRequestedLength.ShouldBe(4);
        source.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task GzipIsDecompressedBeforeReadingTheXmlByteOrderMark()
    {
        var bytes = EncodedBody(Encoding.BigEndianUnicode, true, "iso-8859-1", SuccessResponse(CalendarData));
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            gzip.Write(bytes);
        using var source = new FragmentedStream(compressed.ToArray(), 3);
        using var content = Content(source, "us-ascii");
        content.Headers.ContentEncoding.Add("gzip");

        (await ParseAsync(content)).Single().CalendarData.ShouldBe(CalendarData);
        source.IsDisposed.ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(InvalidEncodings))]
    public async Task MalformedOrUnsupportedEncodingFailsWithoutPublishingResources(byte[] bytes, string? charset)
    {
        using var source = new FragmentedStream(bytes, 7);
        using var content = Content(source, charset);

        await Should.ThrowAsync<XmlException>(() => ParseAsync(content));

        source.IsDisposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("us-ascii")]
    [InlineData("unicode-1-1-utf-8")]
    public async Task InvalidDeclaredBytesAfterAValidResourceAndInitialReadAheadRejectTheWholeBatch(string declaration)
    {
        var prefix = $"<?xml version='1.0' encoding='{declaration}'?>" + MultistatusStart
            + SuccessResponse("opaque")
            + new string(' ', 12 * 1024);
        var bytes = Encoding.ASCII.GetBytes(prefix).Concat([byte.MaxValue])
            .Concat("</d:multistatus>"u8.ToArray()).ToArray();
        using var source = new FragmentedStream(bytes, 4096);
        using var content = Content(source, null);

        await Should.ThrowAsync<XmlException>(() => ParseAsync(content));
        source.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task DeclaredUtf16DecoderRetainsSplitSurrogatesAcrossReads()
    {
        const string summary = "SUMMARY:🗓 café";
        var bytes = EncodedBody(Encoding.BigEndianUnicode, false, "utf-16", SuccessResponse(summary));
        using var content = Content(new FragmentedStream(bytes, 1), null);

        (await ParseAsync(content)).Single().CalendarData.ShouldBe(summary);
    }

    [Fact]
    public async Task ResourceLimitRemainsMeasuredInCanonicalUtf8Bytes()
    {
        var oversized = new string('é', (2 * 1024 * 1024) + 1);
        var bytes = EncodedBody(Encoding.Latin1, false, null, SuccessResponse(oversized));
        using var content = Content(new FragmentedStream(bytes, 4096), "iso-8859-1");

        await Should.ThrowAsync<XmlException>(() => ParseAsync(content));
    }

    [Fact]
    public async Task MultipleResourcesAboveTheSingleResourceBodyLimitRemainStreamed()
    {
        var data = new string('é', (5 * 1024 * 1024) / 4);
        var responses = SuccessResponse(data)
            + SuccessResponse(data).Replace("café.ics", "other.ics", StringComparison.Ordinal);
        var bytes = EncodedBody(Encoding.Unicode, true, "utf-16", responses);
        using var source = new FragmentedStream(bytes, 4096);
        using var content = Content(source, null);

        var resources = await CalendarMultigetResponseParser.ParseAsync(content, 2, TestContext.Current.CancellationToken);

        resources.Count.ShouldBe(2);
        resources.ShouldAllBe(resource => resource.CalendarData == data);
        source.MaximumRequestedLength.ShouldBeLessThanOrEqualTo(4096);
        source.ObservedBytes.ShouldBe(bytes.Length);
        source.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task EncodedResponseStillRejectsOverdeepExtensions()
    {
        var nested = string.Concat(Enumerable.Repeat("<x:child xmlns:x='urn:test'>", 66))
            + string.Concat(Enumerable.Repeat("</x:child>", 66));
        var bytes = EncodedBody(Encoding.Unicode, true, null, nested + SuccessResponse(CalendarData));
        using var content = Content(new FragmentedStream(bytes, 4096), null);

        await Should.ThrowAsync<XmlException>(() => ParseAsync(content));
    }

    [Fact]
    public async Task UndecidedEncodingCannotBufferAnUnboundedXmlDeclaration()
    {
        var xml = "<?xml version='1.0' " + new string(' ', 512 * 1024)
            + "encoding='us-ascii'?>" + MultistatusStart + SuccessResponse("opaque") + "</d:multistatus>";
        using var source = new FragmentedStream(Encoding.ASCII.GetBytes(xml), 4096);
        using var content = Content(source, null);

        var exception = await Should.ThrowAsync<XmlException>(() => ParseAsync(content));

        exception.Message.ShouldContain("bounded encoding declaration envelope");
        source.ObservedBytes.ShouldBeLessThan(280 * 1024);
        source.IsDisposed.ShouldBeTrue();
    }

    [Theory]
    [InlineData(1, null)]
    [InlineData(128, null)]
    [InlineData(128, "utf-8")]
    public async Task CancellationInterruptsProbeAndLaterNetworkReads(int blockedAfter, string? charset)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var bytes = EncodedBody(Encoding.UTF8, false, "utf-8", SuccessResponse(CalendarData));
        using var source = new FragmentedStream(bytes, 1, blockedAfter);
        using var content = Content(source, charset);
        var parsing = CalendarMultigetResponseParser.ParseAsync(content, 1, cancellation.Token);
        await source.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => parsing);
        source.IsDisposed.ShouldBeTrue();
    }

    public static TheoryData<string, bool, string?, string?> ValidEncodings => new()
    {
        { "utf-8", false, null, null },
        { "utf-8", false, null, "UTF-8" },
        { "utf-8", false, null, "unicode-1-1-utf-8" },
        { "iso-8859-1", false, null, "iso-8859-1" },
        { "iso-8859-1", false, "iso-8859-1", "utf-8" },
        { "utf-16", true, null, "utf-16" },
        { "utf-16BE", true, "unsupported-encoding", "us-ascii" },
        { "utf-8", true, "iso-8859-1", "us-ascii" },
        { "utf-16", false, "utf-16LE", "us-ascii" },
        { "utf-16BE", false, "utf-16BE", null },
        { "utf-16BE", false, null, "utf-16" },
        { "utf-16BE", false, null, null },
        { "utf-32", true, "us-ascii", "us-ascii" },
        { "utf-32BE", true, null, "utf-32" },
        { "utf-32BE", false, null, "utf-32BE" }
    };

    public static TheoryData<byte[], string?> InvalidEncodings => new()
    {
        { Encoding.UTF8.GetBytes(MultistatusStart + SuccessResponse(CalendarData) + "</d:multistatus>"), "unsupported-encoding" },
        { Encoding.Latin1.GetBytes(MultistatusStart + SuccessResponse(CalendarData) + "</d:multistatus>"), "utf-8" },
        { InvalidByteBody("us-ascii"), null },
        { InvalidByteBody("unicode-1-1-utf-8"), null },
        { InvalidByteBody("utf-8"), null },
        { [.. "<d:multistatus xmlns:d='DAV:'>"u8.ToArray(), 0xc3], null },
        { [.. Encoding.Unicode.GetBytes("<?xml version='1.0' encoding='utf-16'?><r>"), 0x00, 0xd8, .. Encoding.Unicode.GetBytes("</r>")], null },
        { [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes("<r/>"), 0x00], null },
        { Encoding.UTF8.GetBytes("<!DOCTYPE d:multistatus [<!ENTITY x 'private'>]>" + MultistatusStart + SuccessResponse("&x;") + "</d:multistatus>"), "utf-8" },
        { [], null },
        { [0xff], null }
    };

    private const string MultistatusStart = "<d:multistatus xmlns:d='DAV:' xmlns:c='urn:ietf:params:xml:ns:caldav'>";

    private static string SuccessResponse(string data) =>
        $"<d:response><d:href>{Href}</d:href><d:propstat><d:prop><d:getetag>&quot;café&quot;</d:getetag>"
        + $"<c:calendar-data>{data}</c:calendar-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";

    private static byte[] EncodedBody(Encoding encoding, bool bom, string? declaration, string responses)
    {
        var xml = declaration is null ? string.Empty : $"<?xml version='1.0' encoding='{declaration}'?>";
        xml += MultistatusStart + responses + "</d:multistatus>";
        return [.. bom ? encoding.GetPreamble() : [], .. encoding.GetBytes(xml)];
    }

    private static byte[] InvalidByteBody(string declaration) =>
        [.. Encoding.ASCII.GetBytes($"<?xml version='1.0' encoding='{declaration}'?>" + MultistatusStart),
            0xff, .. Encoding.ASCII.GetBytes("</d:multistatus>")];

    private static StreamContent Content(Stream stream, string? charset)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = charset };
        return content;
    }

    private static Task<IReadOnlyList<CalendarMultigetResource>> ParseAsync(HttpContent content) =>
        CalendarMultigetResponseParser.ParseAsync(content, 1, TestContext.Current.CancellationToken);

    private sealed class FragmentedStream(byte[] bytes, int maximumRead, int? blockedAfter = null)
        : MemoryStream(bytes, 0, bytes.Length, writable: false, publiclyVisible: true)
    {
        internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool IsDisposed { get; private set; }
        internal int ObservedBytes { get; private set; }
        internal int? FirstRequestedLength { get; private set; }
        internal int MaximumRequestedLength { get; private set; }
        public override bool CanSeek => false;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            FirstRequestedLength ??= buffer.Length;
            MaximumRequestedLength = Math.Max(MaximumRequestedLength, buffer.Length);
            if (blockedAfter is not null && ObservedBytes >= blockedAfter)
            {
                Blocked.TrySetResult();
                await new TaskCompletionSource().Task.WaitAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var position = (int)base.Position;
            var read = Math.Min(Math.Min(buffer.Length, maximumRead), (int)base.Length - position);
            base.GetBuffer().AsMemory(position, read).CopyTo(buffer);
            base.Position = position + read;
            ObservedBytes += read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("The response must be read asynchronously.");

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
