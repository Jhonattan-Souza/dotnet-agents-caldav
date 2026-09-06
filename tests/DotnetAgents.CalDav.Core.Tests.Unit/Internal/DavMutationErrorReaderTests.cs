using System.Net.Http.Headers;
using System.Text;
using DotnetAgents.CalDav.Core.Internal.Xml;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal;

public sealed class DavMutationErrorReaderTests
{
    private const string Conditions = "<c:no-uid-conflict/><c:supported-calendar-component/><d:responsedescription>café</d:responsedescription>";
    private const DavMutationErrorKind BothConditions = DavMutationErrorKind.NoUidConflict | DavMutationErrorKind.UnsupportedCapability;

    [Theory]
    [InlineData("utf-16", true, null, "utf-16")]
    [InlineData("utf-16BE", true, "unsupported-encoding", "us-ascii")]
    [InlineData("utf-16BE", false, "utf-16BE", null)]
    [InlineData("utf-16BE", false, null, "utf-16")]
    [InlineData("iso-8859-1", false, "iso-8859-1", "utf-8")]
    [InlineData("iso-8859-1", false, null, "iso-8859-1")]
    public async Task ValidEncodedDavConditionsRemainClassifiable(
        string encodingName,
        bool bom,
        string? charset,
        string? declaration)
    {
        var encoding = Encoding.GetEncoding(encodingName);
        var xml = declaration is null ? string.Empty : $"<?xml version='1.0' encoding='{declaration}'?>";
        var bytes = encoding.GetBytes(xml + Error(Conditions));
        using var content = Content([.. bom ? encoding.GetPreamble() : [], .. bytes], charset);

        (await ReadAsync(content)).ShouldBe(BothConditions);
    }

    [Theory]
    [InlineData("<c:no-uid-conflict/>", (int)DavMutationErrorKind.NoUidConflict)]
    [InlineData("<c:supported-calendar-data/>", (int)DavMutationErrorKind.UnsupportedCapability)]
    [InlineData("<d:supported-method/>", (int)DavMutationErrorKind.UnsupportedCapability)]
    [InlineData("<d:supported-report/>", (int)DavMutationErrorKind.UnsupportedCapability)]
    [InlineData("<d:no-uid-conflict/>", (int)DavMutationErrorKind.None)]
    [InlineData("<c:unrecognized/>", (int)DavMutationErrorKind.None)]
    public async Task BoundedXmlClassificationPreservesEachCondition(string condition, int expected)
    {
        using var content = Content(Encoding.UTF8.GetBytes(Error(condition)), "utf-8");

        (await ReadAsync(content)).ShouldBe((DavMutationErrorKind)expected);
    }

    [Theory]
    [MemberData(nameof(MalformedBodies))]
    public async Task MalformedOrUnsupportedEncodingDoesNotInventDavPreconditions(byte[] bytes, string? charset)
    {
        using var content = Content(bytes, charset);

        (await ReadAsync(content)).ShouldBe(DavMutationErrorKind.None);
    }

    [Fact]
    public async Task ExactlyBoundedBodyRetainsConditions()
    {
        var body = Error(Conditions);
        var padding = (64 * 1024) - Encoding.UTF8.GetByteCount(body);
        using var content = Content(Encoding.UTF8.GetBytes(body + new string(' ', padding)), "utf-8");

        (await ReadAsync(content)).ShouldBe(BothConditions);
    }

    [Fact]
    public async Task OversizedUnknownLengthBodyStopsAfterTheLimitAndOneByte()
    {
        var bytes = Encoding.UTF8.GetBytes(Error(Conditions) + new string(' ', 70 * 1024));
        using var stream = new ObservedStream(bytes);
        using var content = new StreamContent(stream);

        (await ReadAsync(content)).ShouldBe(DavMutationErrorKind.None);

        stream.ObservedBytes.ShouldBe((64 * 1024) + 1);
        stream.IsDisposed.ShouldBeTrue();
    }

    [Fact]
    public async Task KnownOversizedBodyDoesNotReadTheStream()
    {
        using var stream = new ObservedStream([]);
        using var content = new StreamContent(stream);
        content.Headers.ContentLength = (64 * 1024) + 1;

        (await ReadAsync(content)).ShouldBe(DavMutationErrorKind.None);

        stream.ObservedBytes.ShouldBe(0);
    }

    [Fact]
    public async Task FailedBodyReadRetainsBestEffortNone()
    {
        using var content = new StreamContent(new ObservedStream([], new IOException("Interrupted response.")));

        (await ReadAsync(content)).ShouldBe(DavMutationErrorKind.None);
    }

    [Fact]
    public async Task CancellationRetainsBestEffortNone()
    {
        using var content = Content(Encoding.UTF8.GetBytes(Error(Conditions)), "utf-8");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        (await DavMutationErrorReader.ReadAsync(content, cancellation.Token)).ShouldBe(DavMutationErrorKind.None);
    }

    public static TheoryData<byte[], string?> MalformedBodies => new()
    {
        { Encoding.UTF8.GetBytes(Error(Conditions)), "unsupported-encoding" },
        { Encoding.Latin1.GetBytes(Error(Conditions)), "utf-8" },
        { [.. "<?xml version='1.0' encoding='us-ascii'?><d:error xmlns:d='DAV:'>"u8.ToArray(), 0xff, .. "</d:error>"u8.ToArray()], null },
        { [.. "<?xml version='1.0' encoding='unicode-1-1-utf-8'?><d:error xmlns:d='DAV:'>"u8.ToArray(), 0xff, .. "</d:error>"u8.ToArray()], null },
        { Encoding.Unicode.GetBytes("<d:error xmlns:d='DAV:'>"), "utf-16" },
        { Encoding.UTF8.GetBytes("<!DOCTYPE d:error [<!ENTITY x 'private'>]>" + Error(Conditions)), null },
        { Encoding.UTF8.GetBytes(Error(string.Concat(Enumerable.Repeat("<d:nested>", 66)) + Conditions + string.Concat(Enumerable.Repeat("</d:nested>", 66)))), null },
        { Encoding.UTF8.GetBytes(Error(Conditions) + "<unclosed>"), null },
        { [], null }
    };

    private static string Error(string conditions) =>
        $"<d:error xmlns:d='DAV:' xmlns:c='urn:ietf:params:xml:ns:caldav'>{conditions}</d:error>";

    private static ByteArrayContent Content(byte[] bytes, string? charset)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = charset };
        return content;
    }

    private static Task<DavMutationErrorKind> ReadAsync(HttpContent content) =>
        DavMutationErrorReader.ReadAsync(content, TestContext.Current.CancellationToken);

    private sealed class ObservedStream(byte[] bytes, Exception? failure = null) : MemoryStream(bytes)
    {
        internal int ObservedBytes { get; private set; }
        internal bool IsDisposed { get; private set; }
        public override bool CanSeek => false;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is not null)
                return ValueTask.FromException<int>(failure);
            var read = base.Read(buffer.Span);
            ObservedBytes += read;
            return ValueTask.FromResult(read);
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
