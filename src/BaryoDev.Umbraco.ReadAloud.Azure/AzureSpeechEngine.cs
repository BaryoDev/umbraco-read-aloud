using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.CognitiveServices.Speech;
using Microsoft.Extensions.Logging;

namespace BaryoDev.Umbraco.ReadAloud.Azure;

/// <summary>
/// Synthesizes with Azure AI Speech through the official Speech SDK.
/// </summary>
/// <remarks>
/// The SDK rather than the REST endpoint, because only the SDK raises WordBoundary events, and
/// word highlighting needs real timings. The key is resolved on each call, from configuration or
/// from the backoffice, and is never logged, never put in a message and never part of a cache key.
///
/// A failure surfaces as an exception, as it does for Edge, so the controller answers 503 and the
/// browser client falls back to <c>speechSynthesis</c>.
/// </remarks>
internal sealed class AzureSpeechEngine : IReadAloudEngine
{
    private static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger<AzureSpeechEngine> _logger;
    private readonly IAzureCredentialSource _credentials;
    private readonly TimeSpan _idleTimeout;
    private readonly Func<SpeechSynthesizer, string, Task<SpeechSynthesisResult>> _speak;

    // One SpeechConfig per region and key, reused until either changes. A changed key builds one
    // new config. The old one is left to the finalizer, since a request may still be using it.
    private readonly object _configGate = new();
    private (string Region, string Key, SpeechConfig Config)? _config;

    public AzureSpeechEngine(ILogger<AzureSpeechEngine> logger, IAzureCredentialSource credentials)
        : this(logger, credentials, DefaultIdleTimeout)
    {
    }

    internal AzureSpeechEngine(
        ILogger<AzureSpeechEngine> logger,
        IAzureCredentialSource credentials,
        TimeSpan idleTimeout,
        Func<SpeechSynthesizer, string, Task<SpeechSynthesisResult>>? speak = null)
    {
        _speak = speak ?? ((synthesizer, ssml) => synthesizer.SpeakSsmlAsync(ssml));
        _logger = logger;
        _credentials = credentials;
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

        var current = _credentials.Current
            ?? throw new InvalidOperationException("Azure Speech has no key to use.");
        var credential = current with { Region = current.Region.Trim(), Key = current.Key.Trim() };

        // Checked again here, not only at startup, because the settings can be reloaded or saved
        // from the backoffice. The messages name the setting and never carry the key.
        var problems = AzureSpeechProviderSettings.Problems(
            new AzureSpeechOptions { Region = credential.Region, Key = credential.Key });
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", problems));
        }

        var config = ConfigFor(credential);

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

    private SpeechConfig ConfigFor(AzureCredential credential)
    {
        lock (_configGate)
        {
            if (_config is { } current && current.Region == credential.Region && current.Key == credential.Key)
            {
                return current.Config;
            }

            var config = SpeechConfig.FromSubscription(credential.Key, credential.Region);

            // The same format the Edge engine asks for, so the cache and the client see one kind of file.
            config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Audio24Khz48KBitRateMonoMp3);

            _config = (credential.Region, credential.Key, config);
            return config;
        }
    }
}
