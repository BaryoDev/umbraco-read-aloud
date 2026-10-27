namespace BaryoDev.Umbraco.ReadAloud.Engine;

/// <summary>
/// The engine for the Browser provider. It synthesizes nothing: the reader's browser speaks the
/// text itself, so there is no remote engine to call.
/// </summary>
/// <remarks>
/// The controller answers 503 before it reaches an engine in Browser mode, so this is never
/// called in normal use. It exists so the registered services still resolve, and it throws if
/// something does call it, rather than quietly returning no audio.
/// </remarks>
public sealed class BrowserVoiceEngine : IReadAloudEngine
{
    /// <inheritdoc />
    public string? Provider => ReadAloudOptions.BrowserProvider;

    /// <inheritdoc />
    public Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "The Browser provider does not synthesize on the server. Set "
            + $"{ReadAloudOptions.SectionName}:Provider to \"Azure\" for server-made audio.");
}
