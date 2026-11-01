using Microsoft.CognitiveServices.Speech;

namespace BaryoDev.Umbraco.ReadAloud.Azure;

internal enum ProbeOutcome
{
    Ok,
    Rejected,
    Forbidden,
    Quota,
    Network,
    Failed,
}

/// <summary>Makes one very short real synthesis. A seam so tests need no network and no key.</summary>
internal interface IAzureProbe
{
    Task<ProbeOutcome> RunAsync(string region, string key, CancellationToken ct);
}

/// <summary>
/// Synthesizes a single word with the Speech SDK and reports only a coarse outcome. The SDK's own
/// messages and details are dropped, because they can carry request ids and echo what was sent.
/// </summary>
internal sealed class AzureProbe : IAzureProbe
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    public async Task<ProbeOutcome> RunAsync(string region, string key, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Limit);

        try
        {
            var config = SpeechConfig.FromSubscription(key, region);
            config.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Audio24Khz48KBitRateMonoMp3);

            // Null audio config: the audio comes back in the result and is thrown away.
            using var synthesizer = new SpeechSynthesizer(config, null);
            using var stop = limit.Token.Register(() => _ = synthesizer.StopSpeakingAsync());

            using var result = await synthesizer.SpeakTextAsync("Ready.").WaitAsync(limit.Token);

            if (result.Reason == ResultReason.SynthesizingAudioCompleted && result.AudioData.Length > 0)
            {
                return ProbeOutcome.Ok;
            }

            if (result.Reason != ResultReason.Canceled) return ProbeOutcome.Failed;

            return SpeechSynthesisCancellationDetails.FromResult(result).ErrorCode switch
            {
                CancellationErrorCode.AuthenticationFailure => ProbeOutcome.Rejected,
                CancellationErrorCode.Forbidden => ProbeOutcome.Forbidden,
                CancellationErrorCode.TooManyRequests => ProbeOutcome.Quota,
                CancellationErrorCode.ConnectionFailure => ProbeOutcome.Network,
                CancellationErrorCode.ServiceTimeout => ProbeOutcome.Network,
                CancellationErrorCode.ServiceUnavailable => ProbeOutcome.Network,
                _ => ProbeOutcome.Failed,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ProbeOutcome.Network;
        }
        catch (Exception)
        {
            return ProbeOutcome.Failed;
        }
    }
}
