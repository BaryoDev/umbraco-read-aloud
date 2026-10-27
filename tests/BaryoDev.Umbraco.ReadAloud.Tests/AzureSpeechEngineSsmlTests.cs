using BaryoDev.Umbraco.ReadAloud.Azure;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

/// <summary>
/// Runs the engine up to the SDK call with the call replaced, so no network is used. The point is
/// that the document the engine hands the SDK is the one Ssml.Build makes.
/// </summary>
public class AzureSpeechEngineSsmlTests
{
    private const string FakeKey = "test-key-do-not-leak-0123456789";

    [Fact]
    public async Task The_engine_sends_the_built_ssml_to_the_sdk()
    {
        string? sent = null;
        var engine = Engine((_, ssml) =>
        {
            sent = ssml;
            throw new InvalidOperationException("stop here");
        });

        var request = new SynthesisRequest { Text = "Tom & Jerry.", Voice = "en-PH-RosaNeural" };

        await Should.ThrowAsync<InvalidOperationException>(() => engine.SynthesizeAsync(request));

        sent.ShouldBe(Ssml.Build(request));
        sent.ShouldContain("en-PH-RosaNeural");
        sent.ShouldContain("Tom &amp; Jerry.");
    }

    [Fact]
    public async Task A_call_that_never_answers_ends_at_the_idle_timeout()
    {
        var engine = Engine((_, _) => new TaskCompletionSource<SpeechSynthesisResult>().Task, TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<TimeoutException>(
            () => engine.SynthesizeAsync(new SynthesisRequest { Text = "Hello." }));
    }

    [Fact]
    public async Task The_failure_message_does_not_carry_the_key()
    {
        var engine = Engine((_, _) => throw new InvalidOperationException(FakeKey));

        var error = await Should.ThrowAsync<InvalidOperationException>(
            () => engine.SynthesizeAsync(new SynthesisRequest { Text = "Hello." }));

        error.Message.ShouldNotContain(FakeKey);
    }

    private static AzureSpeechEngine Engine(
        Func<SpeechSynthesizer, string, Task<SpeechSynthesisResult>> speak,
        TimeSpan? idle = null) =>
        new(
            NullLogger<AzureSpeechEngine>.Instance,
            new Fixed(new AzureSpeechOptions { Region = " southeastasia ", Key = $" {FakeKey} " }),
            idle ?? TimeSpan.FromSeconds(30),
            speak);

    private sealed class Fixed(AzureSpeechOptions value) : IOptionsMonitor<AzureSpeechOptions>
    {
        public AzureSpeechOptions CurrentValue { get; } = value;

        public AzureSpeechOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<AzureSpeechOptions, string?> listener) => null;
    }
}
