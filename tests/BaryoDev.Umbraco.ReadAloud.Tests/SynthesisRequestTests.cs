using BaryoDev.Umbraco.ReadAloud.Engine;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class SynthesisRequestTests
{
    private static SynthesisRequest Request(string text = "Hello world.") =>
        new() { Text = text };

    [Fact]
    public void The_same_text_and_voice_produce_the_same_key()
    {
        // The whole cache depends on this. The spike proved synthesis is deterministic: the same
        // sentence gave byte-identical output on Ubuntu, Windows and macOS, 20,736 bytes each.
        Request().CacheKey().ShouldBe(Request().CacheKey());
    }

    [Fact]
    public void Different_text_produces_a_different_key()
    {
        // This is what makes the cache self-invalidating. Editing a page changes the text, which
        // changes the key, so a stale recording can never be served.
        Request("Hello world.").CacheKey().ShouldNotBe(Request("Goodbye world.").CacheKey());
    }

    [Fact]
    public void Every_field_that_changes_the_audio_changes_the_key()
    {
        var baseline = Request().CacheKey();

        (Request() with { Voice = "en-GB-SoniaNeural" }).CacheKey().ShouldNotBe(baseline);
        (Request() with { Rate = "+20%" }).CacheKey().ShouldNotBe(baseline);
        (Request() with { Pitch = "-2st" }).CacheKey().ShouldNotBe(baseline);
        (Request() with { Volume = "-10%" }).CacheKey().ShouldNotBe(baseline);
    }

    [Fact]
    public void The_key_is_safe_to_use_as_a_file_name()
    {
        // It becomes a path under App_Data, so anything outside hex would be a traversal risk.
        var key = Request("../../etc/passwd \\ : * ?").CacheKey();

        key.Length.ShouldBe(64);
        key.ShouldAllBe(c => Uri.IsHexDigit(c));
    }

    [Fact]
    public void Fields_that_run_together_do_not_collide()
    {
        // Without a separator between the fields these two hash the same material, and one article's
        // recording would be served in answer to a different article's request.
        var a = Request("BC") with { Volume = "+0%A" };
        var b = Request("C") with { Volume = "+0%AB" };

        a.CacheKey().ShouldNotBe(b.CacheKey());
    }
}
