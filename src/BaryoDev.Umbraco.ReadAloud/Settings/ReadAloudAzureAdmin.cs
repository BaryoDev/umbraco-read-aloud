namespace BaryoDev.Umbraco.ReadAloud.Settings;

/// <summary>
/// What the backoffice dashboard can do with the Azure Speech credentials. The Azure package
/// registers the implementation. Without it the dashboard only says to install the package.
/// </summary>
/// <remarks>
/// Nothing here returns the key. It goes in through <see cref="Save"/> and
/// <see cref="TestAsync"/> and never comes out.
/// </remarks>
public interface IReadAloudAzureAdmin
{
    /// <summary>Whether a key is set, where it comes from and which voice is in use.</summary>
    AzureStatusModel GetStatus();

    /// <summary>
    /// A very short real synthesis. A blank region or key falls back to the one in use, so the
    /// saved key can be tested without typing it again. Nothing is saved.
    /// </summary>
    Task<AzureTestModel> TestAsync(string? region, string? key, CancellationToken ct);

    /// <summary>Stores the credentials. A blank key keeps the saved one and changes only the region.</summary>
    AzureSaveModel Save(string? region, string? key, string savedBy);

    /// <summary>Removes the stored key. The site goes back to the browser voice.</summary>
    AzureSaveModel Remove(string removedBy);
}

/// <summary>The state of the Azure key, as the dashboard shows it. Never carries the key.</summary>
/// <param name="Installed">False when the Azure package is not installed.</param>
/// <param name="KeySaved">True when a key is set, in configuration or in the backoffice.</param>
/// <param name="Source"><c>none</c>, <c>config</c> or <c>dashboard</c>.</param>
/// <param name="Region">The region in use, or the last saved one when the key cannot be read.</param>
/// <param name="Unreadable">A key was saved but cannot be decrypted, so it has to be entered again.</param>
/// <param name="LockedByConfig">Configuration supplies the key, so Save and Remove are off.</param>
/// <param name="Voice"><c>azure</c>, <c>browser</c> or <c>edge</c>: what readers get now.</param>
public sealed record AzureStatusModel(
    bool Installed,
    bool KeySaved,
    string Source,
    string? Region,
    bool Unreadable,
    bool LockedByConfig,
    string Voice);

/// <summary>The result of a test synthesis.</summary>
/// <param name="Ok">True when Azure returned audio.</param>
/// <param name="Reason"><c>ok</c>, <c>rejected</c>, <c>network</c>, <c>quota</c>, <c>invalid</c>, <c>missing</c> or <c>failed</c>.</param>
/// <param name="Message">A plain sentence for the dashboard. Written here, never copied from Azure.</param>
public sealed record AzureTestModel(bool Ok, string Reason, string Message);

/// <summary>The result of a save or remove.</summary>
/// <param name="Ok">True when the change was made.</param>
/// <param name="Reason"><c>ok</c>, <c>locked</c>, <c>invalid</c> or <c>missing</c>.</param>
/// <param name="Message">A plain sentence for the dashboard.</param>
public sealed record AzureSaveModel(bool Ok, string Reason, string Message);

/// <summary>The body of the test and save requests.</summary>
/// <param name="Region">The region of the Speech resource.</param>
/// <param name="Key">The key. Write-only: no response ever carries it.</param>
public sealed record AzureCredentialsRequest(string? Region, string? Key);
