namespace BaryoDev.Umbraco.ReadAloud.Caching;

/// <summary>
/// Tells the audio source to forget its failure backoff. It has no dependencies, so the Azure
/// admin can raise it without resolving the audio pipeline.
/// </summary>
internal sealed class BackoffResetSignal
{
    private int _generation;

    public int Generation => Volatile.Read(ref _generation);

    public void Reset() => Interlocked.Increment(ref _generation);
}
