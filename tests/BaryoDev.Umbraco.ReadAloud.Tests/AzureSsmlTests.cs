using System.Xml.Linq;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class AzureSsmlTests
{
    private static readonly XNamespace Synthesis = "http://www.w3.org/2001/10/synthesis";

    [Fact]
    public void The_voice_and_prosody_are_in_the_document()
    {
        var ssml = Ssml.Build(new SynthesisRequest
        {
            Text = "Hello.",
            Voice = "en-PH-RosaNeural",
            Rate = "+10%",
            Pitch = "-2st",
            Volume = "+5%",
        });

        var root = XDocument.Parse(ssml).Root!;
        var voice = root.Element(Synthesis + "voice")!;
        var prosody = voice.Element(Synthesis + "prosody")!;

        voice.Attribute("name")!.Value.ShouldBe("en-PH-RosaNeural");
        prosody.Attribute("rate")!.Value.ShouldBe("+10%");
        prosody.Attribute("pitch")!.Value.ShouldBe("-2st");
        prosody.Attribute("volume")!.Value.ShouldBe("+5%");
        prosody.Value.ShouldBe("Hello.");
    }

    [Fact]
    public void Text_and_attributes_are_escaped()
    {
        // An unescaped quote in the voice would close the attribute and let a caller add elements.
        var ssml = Ssml.Build(new SynthesisRequest
        {
            Text = "Tom & Jerry <b>said</b> hi",
            Voice = "x'/><audio src='http://evil.example/a.mp3'/><voice name='y",
        });

        var root = XDocument.Parse(ssml).Root!;

        root.Descendants(Synthesis + "audio").ShouldBeEmpty();
        root.Descendants(Synthesis + "voice").Count().ShouldBe(1);
        root.Descendants(Synthesis + "prosody").Single().Value.ShouldBe("Tom & Jerry <b>said</b> hi");
    }

    [Fact]
    public void Characters_xml_cannot_carry_are_dropped_not_escaped()
    {
        // A form feed, a NUL and a lone surrogate would each make the document unparseable.
        var ssml = Ssml.Build(new SynthesisRequest
        {
            Text = "a\u000Cb\u0000c\uD800d \U0001F600 e\tf",
            Voice = "en-US-JennyNeural\u0001",
        });

        var root = XDocument.Parse(ssml).Root!;

        root.Descendants(Synthesis + "prosody").Single().Value.ShouldBe("abcd \U0001F600 e\tf");
        root.Element(Synthesis + "voice")!.Attribute("name")!.Value.ShouldBe("en-US-JennyNeural");
    }

    [Fact]
    public void The_edge_escaper_drops_them_too()
    {
        EdgeTtsProtocol.EscapeXml("x\u0008y&").ShouldBe("xy&amp;");
    }
}
