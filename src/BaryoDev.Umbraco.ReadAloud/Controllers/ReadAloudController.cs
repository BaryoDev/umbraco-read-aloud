using System.Text.Json;
using BaryoDev.Umbraco.ReadAloud.Caching;
using BaryoDev.Umbraco.ReadAloud.Content;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.OperationStatus;
using Umbraco.Extensions;

namespace BaryoDev.Umbraco.ReadAloud.Controllers;

/// <summary>
/// Serves the audio for one published node, and its word timings separately.
/// </summary>
/// <remarks>
/// Anonymous by necessity, since every visitor's browser calls it. It accepts no text: the server
/// reads the configured property itself, which is why an arbitrary-text endpoint and its abuse
/// surface are not needed here at all. Rate limited for the same reason: synthesis is expensive
/// and nobody has to sign in to ask for it.
/// </remarks>
[Route("read-aloud")]
[EnableRateLimiting(ReadAloudRateLimiting.PolicyName)]
public class ReadAloudController : Controller
{
    private readonly CoalescingAudioSource _audio;
    private readonly IPublishedContentQuery _content;
    private readonly IPublicAccessService _publicAccess;
    private readonly IOptionsMonitor<ReadAloudOptions> _options;

    /// <summary>Creates the controller.</summary>
    public ReadAloudController(
        CoalescingAudioSource audio,
        IPublishedContentQuery content,
        IPublicAccessService publicAccess,
        IOptionsMonitor<ReadAloudOptions> options)
    {
        _audio = audio;
        _content = content;
        _publicAccess = publicAccess;
        _options = options;
    }

    /// <summary>Returns the audio for a node, synthesizing it on the first request.</summary>
    /// <remarks>
    /// Plain audio and nothing else, so the response is something a bare <c>audio</c> element can
    /// play and a cache can hold whole. The timings live on their own route.
    ///
    /// The origin does not answer range requests: <c>File(byte[], string)</c> leaves range
    /// processing off, so there is no <c>Accept-Ranges</c> and a <c>Range</c> request gets the
    /// whole recording with a 200. Seeking works only where the browser already holds the file.
    /// This version sends no cache headers, so nothing downstream is asked to keep a copy.
    /// </remarks>
    /// <param name="key">The key of the published node to read.</param>
    /// <param name="voice">An optional voice, honoured only if the site allows it.</param>
    /// <param name="ct">Cancels when the reader navigates away.</param>
    [HttpGet("{key:guid}")]
    public async Task<IActionResult> Get(Guid key, [FromQuery] string? voice, CancellationToken ct)
    {
        var (refusal, request) = await ResolveAsync(key, voice);
        if (refusal is not null) return refusal;

        return await SynthesizeAsync(request!, ct, result => File(result.Audio, result.ContentType));
    }

    /// <summary>Returns the word timings that drive highlighting, for the same node and voice.</summary>
    /// <remarks>
    /// A separate resource rather than a response header on the audio. The timings for a long
    /// article run to tens of kilobytes, and past a couple of hundred with non-Latin text once the
    /// serializer escapes it, which is beyond what proxies and CDN edges accept in a header. A
    /// header also forces the client to fetch the audio with <c>fetch</c> to read it, which means
    /// buffering the whole recording before playback instead of streaming it.
    /// </remarks>
    /// <param name="key">The key of the published node to read.</param>
    /// <param name="voice">An optional voice, honoured only if the site allows it.</param>
    /// <param name="ct">Cancels when the reader navigates away.</param>
    [HttpGet("{key:guid}/timings")]
    public async Task<IActionResult> Timings(Guid key, [FromQuery] string? voice, CancellationToken ct)
    {
        var (refusal, request) = await ResolveAsync(key, voice);
        if (refusal is not null) return refusal;

        return await SynthesizeAsync(request!, ct, result => Json(result.Boundaries, TimingsJson));
    }

    /// <summary>The wire format of the timings, pinned rather than inherited from the host.</summary>
    /// <remarks>
    /// readaloud.js reads <c>boundaries[i].text</c> and <c>boundaries[i + 1].offsetMs</c> by those
    /// exact names off a plain <c>response.json()</c>. Without this, the names come from the site's
    /// MVC JsonOptions, which this package does not own and which Umbraco is free to change between
    /// majors. If they ever came out PascalCase, every <c>.text</c> would be undefined, every word
    /// would fail to align, and the comparison that advances the highlight would be
    /// <c>undefined &lt;= ms</c>, which is false forever. Audio would still play and nothing would
    /// appear in the console: highlighting would simply never happen, on every site at once.
    /// </remarks>
    private static readonly JsonSerializerOptions TimingsJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Runs every guard and works out exactly what to synthesize, or why not to.
    /// </summary>
    /// <remarks>
    /// Shared by both routes on purpose. A second route that resolved content on its own is how
    /// the timings endpoint would quietly become a way around the checks the audio route makes,
    /// and the timings of a protected article give away its text just as surely as its audio does.
    /// </remarks>
    private async Task<(IActionResult? Refusal, SynthesisRequest? Request)> ResolveAsync(
        Guid key,
        string? voice)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled) return (NotFound(), null);

        var node = _content.Content(key);
        if (node is null) return (NotFound(), null);

        // Public access is enforced by Umbraco's routing pipeline, which an attribute-routed
        // controller never runs. Without this the endpoint reads a member-protected page straight
        // out of the published cache and speaks it to anyone holding the key, and the key is in
        // the page markup by design. Not found rather than forbidden, so a refusal does not
        // confirm that the node exists.
        //
        // The lookup includes protection inherited from ancestors. Read only when it positively
        // reports no entry: an unexpected status must not be taken as permission.
        var access = await _publicAccess.GetEntryByContentKeyAsync(key);
        var unprotected = access.Status == PublicAccessOperationStatus.EntryNotFound
            || (access.Success && access.Result is null);
        if (!unprotected) return (NotFound(), null);

        // An empty list means every document type, which is what the option documents. Compared
        // without case because Umbraco aliases are camelCase by convention and typing one by hand
        // into configuration is exactly where that convention gets missed.
        if (options.DocumentTypes.Count > 0
            && !options.DocumentTypes.Contains(node.ContentType.Alias, StringComparer.OrdinalIgnoreCase))
        {
            return (NotFound(), null);
        }

        var html = node.Value<string>(options.PropertyAlias);
        var text = TextExtractor.ToSpeakableText(html, options.MaxChars);
        if (string.IsNullOrEmpty(text)) return (NotFound(), null);

        // An empty allow-list means the default voice only, which is what the option documents.
        // The voice is interpolated into the SSML document the engine sends, so a caller-supplied
        // one that the site never listed is an injection point rather than a preference.
        var chosen = voice ?? options.DefaultVoice;
        if (options.AllowedVoices.Count == 0 || !options.AllowedVoices.Contains(chosen))
        {
            chosen = options.DefaultVoice;
        }

        return (null, new SynthesisRequest { Text = text, Voice = chosen });
    }

    private async Task<IActionResult> SynthesizeAsync(
        SynthesisRequest request,
        CancellationToken ct,
        Func<SynthesisResult, IActionResult> respond)
    {
        try
        {
            return respond(await _audio.GetOrCreateAsync(request, ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The reader navigated away. Nobody is listening, and writing a failure status to a
            // dead response would report ordinary browsing as an outage.
            return new EmptyResult();
        }
        catch (Exception)
        {
            // 503 rather than 500, because the client treats it as "try browser speech instead"
            // and a reader gets a working, if worse, experience rather than a dead button. The
            // cause is logged once by CoalescingAudioSource, where the shared work runs, rather
            // than once per waiting reader here.
            return StatusCode(503);
        }
    }
}
