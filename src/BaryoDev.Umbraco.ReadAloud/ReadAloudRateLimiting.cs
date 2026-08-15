namespace BaryoDev.Umbraco.ReadAloud;

/// <summary>
/// Names the rate limiting policy the endpoint runs under.
/// </summary>
/// <remarks>
/// A constant rather than a literal in two files, because the attribute on the controller and the
/// policy registered in the composer have to agree exactly. If they do not, ASP.NET throws at the
/// first request rather than at boot, and only for the route nobody tests under load.
/// </remarks>
public static class ReadAloudRateLimiting
{
    /// <summary>The policy name shared by the composer's registration and the controller.</summary>
    public const string PolicyName = "BaryoDev.ReadAloud";
}
