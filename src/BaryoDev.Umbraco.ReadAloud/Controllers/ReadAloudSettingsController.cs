using Asp.Versioning;
using BaryoDev.Umbraco.ReadAloud.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Web.Common.Authorization;

namespace BaryoDev.Umbraco.ReadAloud.Controllers;

/// <summary>
/// Backs the Read Aloud dashboard in the Settings section. Backoffice users with Settings access
/// only, enforced here and not just by hiding the dashboard.
/// </summary>
/// <remarks>
/// The key goes in and never comes out: no action returns it or any part of it, and nothing here
/// logs it. The work is done by <see cref="IReadAloudAzureAdmin"/>, which the Azure package
/// registers. Without that package every action reports that it is not installed.
/// </remarks>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("baryodev/read-aloud")]
[ApiExplorerSettings(GroupName = "Read Aloud")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
public class ReadAloudSettingsController : ManagementApiControllerBase
{
    private static readonly AzureStatusModel NotInstalled =
        new(false, false, "none", null, false, false, "browser");

    private readonly IReadAloudAzureAdmin? _admin;
    private readonly IBackOfficeSecurityAccessor _security;

    /// <summary>Creates the controller.</summary>
    public ReadAloudSettingsController(
        IBackOfficeSecurityAccessor security,
        IEnumerable<IReadAloudAzureAdmin> admins)
    {
        _security = security;
        _admin = admins.LastOrDefault();
    }

    /// <summary>Whether a key is set, where it comes from and which voice readers get.</summary>
    [HttpGet("azure")]
    [MapToApiVersion("1.0")]
    [ProducesResponseType(typeof(AzureStatusModel), StatusCodes.Status200OK)]
    public IActionResult Status() => Ok(_admin?.GetStatus() ?? NotInstalled);

    /// <summary>Synthesizes a very short phrase with the values typed in, or the key in use. Saves nothing.</summary>
    [HttpPost("azure/test")]
    [MapToApiVersion("1.0")]
    [ProducesResponseType(typeof(AzureTestModel), StatusCodes.Status200OK)]
    public async Task<IActionResult> Test([FromBody] AzureCredentialsRequest request, CancellationToken ct)
    {
        if (_admin is null) return Conflict(NotInstalledMessage());

        return Ok(await _admin.TestAsync(request.Region, request.Key, ct));
    }

    /// <summary>Stores the credentials. Takes effect on the next request.</summary>
    [HttpPut("azure")]
    [MapToApiVersion("1.0")]
    [ProducesResponseType(typeof(AzureSaveModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AzureSaveModel), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(AzureSaveModel), StatusCodes.Status409Conflict)]
    public IActionResult Save([FromBody] AzureCredentialsRequest request)
    {
        if (_admin is null) return Conflict(NotInstalledMessage());

        return Respond(_admin.Save(request.Region, request.Key, CurrentUser()));
    }

    /// <summary>Removes the stored key. Readers get the browser voice from the next request.</summary>
    [HttpDelete("azure")]
    [MapToApiVersion("1.0")]
    [ProducesResponseType(typeof(AzureSaveModel), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AzureSaveModel), StatusCodes.Status409Conflict)]
    public IActionResult Remove()
    {
        if (_admin is null) return Conflict(NotInstalledMessage());

        return Respond(_admin.Remove(CurrentUser()));
    }

    private IActionResult Respond(AzureSaveModel result) => result.Reason switch
    {
        "ok" => Ok(result),
        "locked" => Conflict(result),
        _ => BadRequest(result),
    };

    private static AzureSaveModel NotInstalledMessage() => new(
        false,
        "notInstalled",
        "Install the BaryoDev.Umbraco.ReadAloud.Azure package to use Azure Speech.");

    private string CurrentUser() =>
        _security.BackOfficeSecurity?.CurrentUser?.Username ?? "unknown";
}
