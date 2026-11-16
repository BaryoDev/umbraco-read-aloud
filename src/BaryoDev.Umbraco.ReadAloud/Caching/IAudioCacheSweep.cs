namespace BaryoDev.Umbraco.ReadAloud.Caching;

/// <summary>
/// Optional. A cache that can delete old entries implements this next to <see cref="IAudioCache"/>.
/// The age sweep only runs when the registered cache implements it.
/// </summary>
public interface IAudioCacheSweep
{
    /// <summary>
    /// Deletes entries last written before <paramref name="olderThan"/>, at most
    /// <paramref name="maxFiles"/> files per call. Returns how many files were deleted.
    /// </summary>
    Task<int> SweepAsync(DateTimeOffset olderThan, int maxFiles, CancellationToken ct = default);
}
