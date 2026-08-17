using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

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
    /// <remarks>
    /// This one has to stay <c>const</c>, unlike the other public strings in this package, which
    /// are <c>static readonly</c> so they are not inlined into consumers. An attribute argument
    /// must be a compile-time constant, and this is used as
    /// <c>[EnableRateLimiting(ReadAloudRateLimiting.PolicyName)]</c> on the controller, so
    /// <c>static readonly</c> would not compile. The inlining is the price of being usable there.
    /// Treat the value as permanent: changing it would silently miss any consumer who had already
    /// built against it.
    /// </remarks>
    public const string PolicyName = "BaryoDev.ReadAloud";
}

/// <summary>
/// A fixed window per caller, sized from configuration.
/// </summary>
/// <remarks>
/// A policy class rather than a partition delegate, because the rejection status has to be set
/// here. The alternative, <c>RateLimiterOptions.RejectionStatusCode</c>, is host-global: setting it
/// would change the rejection status of any other rate limiting the site already has, which is not
/// a package's business.
/// </remarks>
internal sealed class ReadAloudRateLimiterPolicy : IRateLimiterPolicy<string>
{
    private readonly IOptionsMonitor<ReadAloudOptions> _options;

    public ReadAloudRateLimiterPolicy(IOptionsMonitor<ReadAloudOptions> options) => _options = options;

    /// <summary>
    /// Answers 429 rather than the framework default of 503.
    /// </summary>
    /// <remarks>
    /// Scoped to this policy, so it says "you asked too often" for read-aloud without claiming
    /// anything about the rest of the site.
    /// </remarks>
    public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } =
        (context, _) =>
        {
            context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return ValueTask.CompletedTask;
        };

    public RateLimitPartition<string> GetPartition(HttpContext httpContext)
    {
        var perMinute = _options.CurrentValue.RateLimitPerMinute;

        // Zero or less turns it off, for a site behind its own gateway that would rather rate
        // limit there than here.
        if (perMinute <= 0) return RateLimitPartition.GetNoLimiter("unlimited");

        // RemoteIpAddress is the immediate peer, which behind a proxy or CDN is the edge rather
        // than the reader. A site in that shape must enable forwarded headers (UseForwardedHeaders)
        // or every visitor shares one bucket and legitimate readers start seeing 429s. See the
        // RateLimitPerMinute note in README.md.
        //
        // The address is a partition key held in memory for the length of one window and nothing
        // more: never written down, never logged, never part of a cache key. This is the only
        // place in the package that touches anything about a listener at all.
        return RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = perMinute,
                Window = TimeSpan.FromMinutes(1),

                // Refused outright rather than queued. A reader wants to know now that the button
                // will not work, not to have the page hang while a queue drains.
                QueueLimit = 0,
            });
    }
}
