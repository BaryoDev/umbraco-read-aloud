using System.Text.Json;
using BaryoDev.Umbraco.ReadAloud.Caching;
using BaryoDev.Umbraco.ReadAloud.Content;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
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
    private readonly IOptionsMonitor<ReadAloudOptions> _options;
    private readonly ILogger<ReadAloudController> _logger;

    /// <summary>Creates the controller.</summary>
    public ReadAloudController(
        CoalescingAudioSource audio,
        IPublishedContentQuery content,
        IOptionsMonitor<ReadAloudOptions> options,
        ILogger<ReadAloudController> logger)
    {
        _audio = audio;
        _content = content;
        _options = options;
        _logger = logger;
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

        var html = node.Value<string>(options.PropertyAlias);
        var text = TextExtractor.ToSpeakableText(html, options.MaxChars);
        if (string.IsNullOrEmpty(text)) return NotFound();

        var chosen = voice ?? options.DefaultVoice;
        if (options.AllowedVoices.Count > 0 && !options.AllowedVoices.Contains(chosen))
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
        catch (Exception ex)
        {
            // 503 rather than 500, because the client treats it as "try browser speech instead"
            // and a reader gets a working, if worse, experience rather than a dead button.
            _logger.LogWarning(ex, "Read-aloud synthesis failed for node {Key}.", key);
            return StatusCode(503);
        }
    }
}
