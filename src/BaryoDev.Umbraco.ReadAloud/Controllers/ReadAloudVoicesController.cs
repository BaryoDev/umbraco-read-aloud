using Asp.Versioning;
using BaryoDev.Umbraco.ReadAloud.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Web.Common.Authorization;

namespace BaryoDev.Umbraco.ReadAloud.Controllers;

/// <summary>
/// The voices an editor may pick for a page. Backs the voice picker property editor.
/// </summary>
/// <remarks>
/// A separate controller from the settings one because the audience differs. The settings
/// endpoints hold the Azure key and need Settings access. Editors work in the Content section and
/// may not have Settings, so this uses the Content section policy, which is what Umbraco's own
/// document endpoints use. It returns voice names from configuration and nothing else, and the
/// check is on the server, not just on where the editor is shown.
/// </remarks>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("baryodev/read-aloud")]
[ApiExplorerSettings(GroupName = "Read Aloud")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
public class ReadAloudVoicesController : ManagementApiControllerBase
{
    private readonly IOptionsMonitor<ReadAloudOptions> _options;
    private readonly IReadAloudAzureAdmin? _admin;

    /// <summary>Creates the controller.</summary>
    public ReadAloudVoicesController(
        IOptionsMonitor<ReadAloudOptions> options,
        IEnumerable<IReadAloudAzureAdmin> admins)
    {
        _options = options;
        _admin = admins.LastOrDefault();
    }

    /// <summary>The voices a page may use: the allowed ones, minus any the Azure region lacks.</summary>
    [HttpGet("voices")]
    [MapToApiVersion("1.0")]
    [ProducesResponseType(typeof(PageVoiceOptionsModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> Voices(CancellationToken ct)
    {
        var options = _options.CurrentValue;
        var voices = options.AllowedVoices
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct()
            .ToList();

        // With an Azure list, only voices the region offers. Without one (no Azure package, no key,
        // or the list could not be fetched) every allowed voice is shown, since nothing says
        // otherwise.
        var filtered = false;
        if (_admin is not null)
        {
            var status = await _admin.GetVoicesAsync(ct);
            if (status.Available)
            {
                var missing = status.Configured
                    .Where(c => c.InRegion == false)
                    .Select(c => c.Name)
                    .ToHashSet();
                voices = voices.Where(v => !missing.Contains(v)).ToList();
                filtered = true;
            }
        }

        return Ok(new PageVoiceOptionsModel(options.DefaultVoice, voices, filtered));
    }
}

/// <summary>The choices for the page voice picker.</summary>
/// <param name="DefaultVoice">The site default, shown next to "Use the site default".</param>
/// <param name="Voices">The voices an editor may pick.</param>
/// <param name="FilteredByRegion">True when the list was narrowed to what the Azure region offers.</param>
public sealed record PageVoiceOptionsModel(string DefaultVoice, IReadOnlyList<string> Voices, bool FilteredByRegion);
