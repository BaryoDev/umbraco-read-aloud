using BaryoDev.Umbraco.ReadAloud.Content;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class TextExtractorTests
{
    [Fact]
    public void Tags_are_removed_and_words_keep_their_spacing()
    {
        TextExtractor.ToSpeakableText("<p>Hello <strong>world</strong>.</p>", 8000)
            .ShouldBe("Hello world.");
    }

    [Fact]
    public void Block_elements_do_not_run_words_together()
    {
        // Without a space at the boundary this reads as "onetwo", which sounds like a mistake.
        TextExtractor.ToSpeakableText("<p>One</p><p>Two</p>", 8000).ShouldBe("One Two");
    }

    [Fact]
    public void Script_and_style_content_is_never_read_aloud()
    {
        TextExtractor.ToSpeakableText(
            "<p>Hello</p><script>var x = 1;</script><style>.a{color:red}</style>", 8000)
            .ShouldBe("Hello");
    }

    [Fact]
    public void Html_entities_are_decoded()
    {
        // "&amp;" must be spoken as "and", not as "ampersand".
        TextExtractor.ToSpeakableText("<p>Tom &amp; Jerry &mdash; friends</p>", 8000)
            .ShouldContain("Tom & Jerry");
    }

    [Fact]
    public void Text_is_truncated_at_a_word_boundary()
    {
        var result = TextExtractor.ToSpeakableText("<p>alpha beta gamma delta</p>", 12);

        result.Length.ShouldBeLessThanOrEqualTo(12);
        result.ShouldNotEndWith("gam");
        result.ShouldBe("alpha beta");
    }

    [Fact]
    public void Empty_and_null_input_give_an_empty_string_rather_than_throwing()
    {
        TextExtractor.ToSpeakableText(null, 8000).ShouldBe("");
        TextExtractor.ToSpeakableText("   ", 8000).ShouldBe("");
        TextExtractor.ToSpeakableText("<p></p>", 8000).ShouldBe("");
    }
}
