using System.Text.Json;
using BaryoDev.Umbraco.ReadAloud.Caching;
using BaryoDev.Umbraco.ReadAloud.Content;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Services.OperationStatus;
using Umbraco.Extensions;

namespace BaryoDev.Umbraco.ReadAloud.Controllers;

/// <summary>
/// Serves the audio for one published node.
/// </summary>
/// <remarks>
/// Anonymous by necessity, since every visitor's browser calls it. It accepts no text: the server
/// reads the configured property itself, which is why an arbitrary-text endpoint and its abuse
/// surface are not needed here at all.
/// </remarks>
[Route("read-aloud")]
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
    /// <param name="key">The key of the published node to read.</param>
    /// <param name="voice">An optional voice, honoured only if the site allows it.</param>
    /// <param name="ct">Cancels when the reader navigates away.</param>
    [HttpGet("{key:guid}")]
    public async Task<IActionResult> Get(Guid key, [FromQuery] string? voice, CancellationToken ct)
    {
        var options = _options.CurrentValue;
        if (!options.Enabled) return NotFound();

        var node = _content.Content(key);
        if (node is null) return NotFound();

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
        if (!unprotected) return NotFound();

        var html = node.Value<string>(options.PropertyAlias);
        var text = TextExtractor.ToSpeakableText(html, options.MaxChars);
        if (string.IsNullOrEmpty(text)) return NotFound();

        // An empty allow-list means the default voice only, which is what the option documents.
        // The voice is interpolated into the SSML document the engine sends, so a caller-supplied
        // one that the site never listed is an injection point rather than a preference.
        var chosen = voice ?? options.DefaultVoice;
        if (options.AllowedVoices.Count == 0 || !options.AllowedVoices.Contains(chosen))
        {
            chosen = options.DefaultVoice;
        }

        try
        {
            var result = await _audio.GetOrCreateAsync(
                new SynthesisRequest { Text = text, Voice = chosen }, ct);

            Response.Headers.Append("X-ReadAloud-Boundaries", JsonSerializer.Serialize(result.Boundaries));

            return File(result.Audio, result.ContentType);
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
