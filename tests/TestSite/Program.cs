// Stated explicitly rather than relying on implicit global usings: which ones the Umbraco
// SDK injects differs between majors, and this host is built against 16, 17 and 18.
using Microsoft.AspNetCore.HttpOverrides;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Extensions;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// nginx terminates TLS and proxies to this container over plain HTTP, so without this the app
// sees every request as http no matter what the browser did. Two things break. OpenIddict, which
// backs the Umbraco login, refuses the whole flow with "This server only accepts HTTPS requests"
// (ID2083). And this package's rate limiter partitions on Connection.RemoteIpAddress, which
// without the header is the proxy for every visitor alike, so the whole site shares one bucket
// and a listen costs two requests against a default of 20 a minute.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    // The container sees nginx as a Docker bridge address rather than loopback, so the defaults
    // reject it. Safe here because nginx sets both headers on every request rather than appending
    // to whatever a client sent. A site exposing this container directly must not clear these.
    //
    // The property was renamed between the two runtimes this host targets: net9.0, which is
    // Umbraco 16, has only KnownNetworks, and net10.0 obsoletes that name in favour of
    // KnownIPNetworks. Neither name compiles on both, so the conditional is unavoidable.
#if NET10_0_OR_GREATER
    options.KnownIPNetworks.Clear();
#else
    options.KnownNetworks.Clear();
#endif
    options.KnownProxies.Clear();
});

// The public demo is mounted under a path on a shared host rather than at a root of its own, so
// the app has to know its own prefix: routing has to match after it is removed, and every URL the
// app generates has to carry it. Unset by default, which is the case the tests and a local run
// take, so nothing here changes unless a deployment says otherwise.
var pathBase = builder.Configuration["Demo:PathBase"]?.TrimEnd('/');
if (!string.IsNullOrWhiteSpace(pathBase))
{
    // UsePathBase alone is not enough. It moves the prefix off Request.Path, but Umbraco routes
    // published content by the absolute URL, prefix included, so the front end answers 404 for
    // every page while the backoffice and the static assets are perfectly happy. This is the
    // setting that tells Umbraco the prefix is not part of any content route. Written here rather
    // than in the deployment's environment so the two cannot drift apart.
    builder.Configuration["Umbraco:CMS:Hosting:ApplicationVirtualPath"] = pathBase;
}

builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddComposers()
    .Build();

WebApplication app = builder.Build();

await app.BootUmbracoAsync();

// First in the pipeline on purpose. Everything after this point, Umbraco's own middleware
// included, reads the scheme and the client address from the request, so the rewrite has to
// happen before any of it runs.
app.UseForwardedHeaders();

if (!string.IsNullOrWhiteSpace(pathBase))
{
    app.UsePathBase(pathBase);
}

app.UseUmbraco()
    .WithMiddleware(u =>
    {
        u.UseBackOffice();
        u.UseWebsite();
    })
    .WithEndpoints(u =>
    {
        u.UseBackOfficeEndpoints();
        u.UseWebsiteEndpoints();
    });

await app.RunAsync();

// Top-level statements generate an internal Program class. WebApplicationFactory<Program> needs it
// public, otherwise a public test fixture cannot derive from the factory.
public partial class Program { }
