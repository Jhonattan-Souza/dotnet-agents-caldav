using System.Text;
using System.Xml;
using System.Xml.Linq;
using DotnetAgents.CalDav.Core.Internal.Xml;
using Shouldly;
using Xunit;

namespace DotnetAgents.CalDav.Core.Tests.Unit.Internal.Xml;

public sealed class XmlResponseReaderTests
{
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16be")]
    [InlineData("utf-32")]
    [InlineData("utf-32be")]
    public void BomOverridesConflictingOrUnknownHttpCharsetAndDeclaration(string name)
    {
        var encoding = BomEncoding(name);
        var body = encoding.GetPreamble().Concat(encoding.GetBytes("<?xml version='1.0' encoding='us-ascii'?><root>café</root>")).ToArray();

        foreach (var charset in new[] { "us-ascii", "x-unknown-charset" })
            Load(body, charset).Root!.Value.ShouldBe("café");
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16be")]
    [InlineData("iso-8859-1")]
    public void HttpCharsetWithoutBomOverridesXmlDeclaration(string charset)
    {
        var body = Encoding.GetEncoding(charset).GetBytes("<?xml version='1.0' encoding='us-ascii'?><root>café</root>");

        Load(body, '"' + charset + '"').Root!.Value.ShouldBe("café");
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-16be")]
    [InlineData("iso-8859-1")]
    public void DeclarationOnlyUsesAutodetectionAndKeepsTheOriginalDeclaration(string encoding)
    {
        var body = Encoding.GetEncoding(encoding).GetBytes("<?xml version='1.0' encoding='" + encoding + "'?><root>café</root>");

        var document = Load(body);

        document.Root!.Value.ShouldBe("café");
        document.Declaration!.Encoding.ShouldBe(encoding);
    }

    [Fact]
    public void UnlabelledXmlUsesUtf8WithoutReplacingUnicode()
    {
        Load(Encoding.UTF8.GetBytes("<root>São Paulo 😀</root>")).Root!.Value.ShouldBe("São Paulo 😀");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("utf-8")]
    [InlineData("us-ascii")]
    public void InvalidBytesCannotBeReplacedByTheDefaultOrHttpDecoder(string? charset)
    {
        var body = Encoding.ASCII.GetBytes("<root>").Concat(new byte[] { 0xFF }).Concat(Encoding.ASCII.GetBytes("</root>")).ToArray();

        Should.Throw<XmlException>(() => Load(body, charset));
    }

    [Theory]
    [InlineData("us-ascii")]
    [InlineData("unicode-1-1-utf-8")]
    public void DeclarationOnlyDecodersCannotReplaceInvalidBytes(string encoding)
    {
        var body = Encoding.ASCII.GetBytes("<?xml version='1.0' encoding='" + encoding + "'?><root>")
            .Concat(new byte[] { 0xFF }).Concat(Encoding.ASCII.GetBytes("</root>")).ToArray();

        Should.Throw<XmlException>(() => Load(body));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Utf16MalformedSurrogatesFailWithOrWithoutBom(bool bom)
    {
        var encoding = new UnicodeEncoding(false, true);
        var body = (bom ? encoding.GetPreamble() : []).Concat(encoding.GetBytes("<root>"))
            .Concat(new byte[] { 0x00, 0xD8 }).Concat(encoding.GetBytes("</root>")).ToArray();

        Should.Throw<XmlException>(() => Load(body, bom ? "us-ascii" : "utf-16"));
    }

    [Theory]
    [InlineData("x-unknown-charset")]
    [InlineData("")]
    public void UnsupportedHttpCharsetFailsAsXmlProtocolEvidence(string charset)
    {
        Should.Throw<XmlException>(() => Load(Encoding.UTF8.GetBytes("<root/>"), charset));
    }

    [Fact]
    public void DtdAndCallerCharacterLimitsArePreserved()
    {
        var dtd = Encoding.UTF8.GetBytes("<!DOCTYPE root [<!ENTITY value 'expanded'>]><root>&value;</root>");
        Should.Throw<XmlException>(() => Load(dtd, "utf-8"));
        Should.Throw<XmlException>(() => XmlResponseReader.Load(Encoding.UTF8.GetBytes("<root>" + new string('a', 50) + "</root>"),
            null, new XmlReaderSettings { MaxCharactersInDocument = 32 }));
    }

    [Fact]
    public void WhitespaceOptionsAreKeptWithoutMutatingCallerSettings()
    {
        var settings = new XmlReaderSettings { MaxCharactersInDocument = 1024, CloseInput = false };

        var document = XmlResponseReader.Load(Encoding.UTF8.GetBytes("<root>\n <child/>\n</root>"), "utf-8", settings, LoadOptions.PreserveWhitespace);

        document.Root!.Nodes().OfType<XText>().Select(text => text.Value).ShouldBe(["\n ", "\n"]);
        settings.CloseInput.ShouldBeFalse();
    }

    private static XDocument Load(byte[] body, string? charset = null) => XmlResponseReader.Load(body, charset,
        new XmlReaderSettings { MaxCharactersInDocument = 4 * 1024 * 1024 });

    private static Encoding BomEncoding(string name) => name switch
    {
        "utf-8" => new UTF8Encoding(true),
        "utf-16" => new UnicodeEncoding(false, true),
        "utf-16be" => new UnicodeEncoding(true, true),
        "utf-32" => new UTF32Encoding(false, true),
        _ => new UTF32Encoding(true, true)
    };
}
