using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

public class EdgeTtsEngineTests
{
    private static EdgeTtsEngine Engine() => new(NullLogger<EdgeTtsEngine>.Instance);

    [Fact]
    public async Task Empty_text_is_rejected_before_a_socket_is_opened()
    {
        // Cheap guard. Opening a connection to send nothing is rude and the server just hangs.
        await Should.ThrowAsync<ArgumentException>(async () =>
            await Engine().SynthesizeAsync(new SynthesisRequest { Text = "   " }));
    }

    [Fact]
    public async Task A_cancelled_request_does_not_hang()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await Engine().SynthesizeAsync(new SynthesisRequest { Text = "Hello." }, cts.Token));
    }

    [Fact(Skip = "Hits the live Microsoft endpoint. Run manually, never in CI.")]
    [Trait("Category", "Live")]
    public async Task Live_synthesis_returns_mp3_and_word_timings()
    {
        var result = await Engine().SynthesizeAsync(new SynthesisRequest
        {
            Text = "There is a listen button on this article.",
            Voice = "en-US-JennyNeural",
        });

        result.ContentType.ShouldBe("audio/mpeg");
        result.Audio.Length.ShouldBeGreaterThan(1000);
        result.Boundaries.ShouldNotBeEmpty();
        result.Boundaries[0].DurationMs.ShouldBeGreaterThan(0);
    }
}
