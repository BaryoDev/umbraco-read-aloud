using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.ReadAloud.Azure;

/// <summary>
/// Synthesizes with Azure AI Speech through the official Speech SDK.
/// </summary>
/// <remarks>
/// The SDK rather than the REST endpoint, because only the SDK raises WordBoundary events, and
/// word highlighting needs real timings. The key is read from <see cref="AzureSpeechOptions"/> on
/// each call and is never logged, never put in a message and never part of a cache key.
///
/// A failure surfaces as an exception, as it does for Edge, so the controller answers 503 and the
/// browser client falls back to <c>speechSynthesis</c>.
/// </remarks>
public sealed class AzureSpeechEngine : IReadAloudEngine
{
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<AzureSpeechEngine> _logger;
    private readonly IOptionsMonitor<AzureSpeechOptions> _options;
    private readonly TimeSpan _idleTimeout;
    private readonly Func<SpeechSynthesizer, string, Task<SpeechSynthesisResult>> _speak;

    public AzureSpeechEngine(ILogger<AzureSpeechEngine> logger, IOptionsMonitor<AzureSpeechOptions> options)
        : this(logger, options, DefaultIdleTimeout)
    {
    }

    internal AzureSpeechEngine(
        ILogger<AzureSpeechEngine> logger,
        IOptionsMonitor<AzureSpeechOptions> options,
        TimeSpan idleTimeout,
        Func<SpeechSynthesizer, string, Task<SpeechSynthesisResult>>? speak = null)
    {
        _speak = speak ?? ((synthesizer, ssml) => synthesizer.SpeakSsmlAsync(ssml));
        _logger = logger;
        _options = options;
        _idleTimeout = idleTimeout;
    }

    /// <inheritdoc />
    public string? Provider => ReadAloudOptions.AzureProvider;

    public async Task<SynthesisResult> SynthesizeAsync(
        SynthesisRequest request,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Text))
        {
            throw new ArgumentException("Text is empty.", nameof(request));
        }

        ct.ThrowIfCancellationRequested();

        var options = _options.CurrentValue;

        // Checked again here, not only at startup, because the settings can be reloaded. The
        // messages name the setting and never carry the key.
        var problems = AzureSpeechProviderSettings.Problems(options);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", problems));
        }

        var config = SpeechConfig.FromSubscription(options.Key.Trim(), options.Region.Trim());

        // The same format the Edge engine asks for, so the cache and the client see one kind of file.
        config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Audio24Khz48KBitRateMonoMp3);

        // Declared before the synthesizer so it is disposed after it: a late Synthesizing event
        // must never reach a disposed source. Idle, not total-duration, as for Edge: every audio
        // chunk resets the clock, so a long article is not punished and only silence is.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(_idleTimeout);

        // Null audio config: the audio comes back in the result instead of going to a speaker.
        using var synthesizer = new SpeechSynthesizer(config, null);

        var boundaries = new List<WordBoundary>();

        EventHandler<SpeechSynthesisWordBoundaryEventArgs> onWord = (_, e) =>
        {
            if (!AzureWordBoundaries.IsWord(e.BoundaryType)) return;
            lock (boundaries) boundaries.Add(AzureWordBoundaries.ToBoundary(e.Text, e.AudioOffset, e.Duration));
        };

        EventHandler<SpeechSynthesisEventArgs> onChunk = (_, _) =>
        {
            try { idle.CancelAfter(_idleTimeout); }
            catch (ObjectDisposedException) { }
        };

        if (request.WordBoundaries) synthesizer.WordBoundary += onWord;
        synthesizer.Synthesizing += onChunk;

        using var stop = idle.Token.Register(() => _ = synthesizer.StopSpeakingAsync());

        SpeechSynthesisResult result;

        try
        {
            // The SDK call takes no token, so the idle timeout is enforced on the await. The
            // stop above still ends the underlying work.
            result = await _speak(synthesizer, Ssml.Build(request)).WaitAsync(idle.Token);
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Azure Speech sent nothing for {_idleTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The type only. The SDK's message is not ours to forward.
            _logger.LogWarning("Azure Speech threw {ExceptionType} during synthesis.", ex.GetType().Name);
            throw new InvalidOperationException("Azure Speech synthesis failed.");
        }
        finally
        {
            synthesizer.Synthesizing -= onChunk;
            synthesizer.WordBoundary -= onWord;
        }

        using (result)
        {
            ct.ThrowIfCancellationRequested();

            if (idle.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Azure Speech sent nothing for {_idleTimeout.TotalSeconds:0} seconds.");
            }

            if (result.Reason == ResultReason.Canceled)
            {
                var details = SpeechSynthesisCancellationDetails.FromResult(result);

                // The error code names the cause (a bad key is AuthenticationFailure, a spent quota
                // is TooManyRequests). The free-text details are left out of the message.
                throw new InvalidOperationException(
                    $"Azure Speech refused the synthesis: {details.ErrorCode}.");
            }

            if (result.Reason != ResultReason.SynthesizingAudioCompleted || result.AudioData.Length == 0)
            {
                throw new InvalidOperationException("Azure Speech returned no audio.");
            }

            lock (boundaries)
            {
                return new SynthesisResult(result.AudioData, boundaries.ToList(), "audio/mpeg");
            }
        }
    }
}
