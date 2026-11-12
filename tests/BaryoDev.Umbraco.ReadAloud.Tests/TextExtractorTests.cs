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

    [Theory]
    [InlineData("<script ")]
    [InlineData("<style>")]
    [InlineData("<p ")]
    [InlineData("<a ")]
    public void Many_unclosed_openers_finish_quickly(string opener)
    {
        var html = string.Concat(Enumerable.Repeat(opener, 50_000)) + "tail";

        var clock = System.Diagnostics.Stopwatch.StartNew();
        TextExtractor.ToSpeakableText(html, 8000);
        clock.Stop();

        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void An_unclosed_style_opener_keeps_the_text_after_it()
    {
        TextExtractor.ToSpeakableText("<p>one</p><style>two", 8000).ShouldBe("one two");
    }

    [Fact]
    public void Script_blocks_are_removed_with_mixed_case_and_attributes()
    {
        TextExtractor.ToSpeakableText(
            "a<SCRIPT type=\"x\">1 < 2</Script>b<style media=\"all\">p{}</STYLE>c<scripted>d</scripted>", 8000)
            .ShouldBe("a b cd");
    }

    [Fact]
    public void Unspaced_text_after_an_early_space_is_cut_at_the_budget_not_the_space()
    {
        var result = TextExtractor.ToSpeakableText("Hi " + new string('\u5B57', 20000), 8000);

        result.Length.ShouldBe(8000);
    }

    [Fact]
    public void A_long_word_after_an_early_space_is_cut_at_the_budget()
    {
        var result = TextExtractor.ToSpeakableText("Hi " + new string('x', 100), 40);

        result.ShouldBe("Hi " + new string('x', 37));
    }

    [Fact]
    public void A_hard_cut_does_not_split_a_surrogate_pair()
    {
        // Each emoji is two UTF-16 chars, so a cut at 5 would land inside the third one.
        var result = TextExtractor.ToSpeakableText(string.Concat(Enumerable.Repeat("\U0001F600", 10)), 5);

        result.Length.ShouldBe(4);
        char.IsHighSurrogate(result[^1]).ShouldBeFalse();
    }
}
