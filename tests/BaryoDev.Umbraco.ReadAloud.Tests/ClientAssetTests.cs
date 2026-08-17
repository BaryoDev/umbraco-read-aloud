using System.Net;
using System.Text.RegularExpressions;
using BaryoDev.Umbraco.ReadAloud.Controllers;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

/// <summary>
/// Checks the browser client as the site actually serves it, against the routes this same booted
/// site actually registers.
/// </summary>
/// <remarks>
/// Every assertion here runs over <see cref="WithoutComments"/> rather than the raw file. Three of
/// these tests used to read the file whole, and every substring they looked for also appears in the
/// file's opening documentation block, so deleting the entire program and keeping the doc block
/// left them green. A test that its own subject's comments can satisfy reads as coverage while
/// providing none, which is worse than no test: the next person to change the route name sees four
/// green asset tests and believes the pairing is guarded.
///
/// What is deliberately not here: how the client behaves. That belongs to the Node suite in
/// tests/client, which runs the real file and drives it. These tests cover the seam that suite
/// cannot see, which is whether the asset is served at all and whether the literals in it still
/// match the server booted beside it.
/// </remarks>
[Collection(UmbracoCollection.Name)]
public class ClientAssetTests
{
    private readonly UmbracoSiteFixture _site;

    public ClientAssetTests(UmbracoSiteFixture site) => _site = site;

    private const string ClientPath = "/App_Plugins/BaryoDev.ReadAloud/readaloud.js";

    [Fact]
    public async Task The_client_is_served_as_javascript()
    {
        // Static web assets are served from a manifest, never copied to disk, so a filesystem
        // check would pass while the asset 404s in a real site.
        var response = await _site.Client.GetAsync(ClientPath);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/javascript");
    }

    [Fact]
    public async Task The_client_requests_the_route_the_server_actually_serves()
    {
        // The pairing that breaks silently on a rename: the client keeps requesting and the server
        // keeps 404ing, and read-aloud just stops working with nothing in the logs.
        //
        // The literals are taken from the route this very site registered rather than written out
        // here, so renaming the controller's route and leaving readaloud.js alone fails this test
        // instead of quietly agreeing with a copy of the old name kept in the test.
        var code = WithoutComments(await _site.Client.GetStringAsync(ClientPath));

        var literals = LiteralSegmentsOf(nameof(ReadAloudController.Timings));

        literals.Length.ShouldBe(2, "read-aloud/{key:guid}/timings has two literal segments");

        code.ShouldContain($"\"/{literals[0]}/\"", Case.Sensitive,
            $"the client builds its URLs from a literal, and the server now serves /{literals[0]}/");
        code.ShouldContain($"\"/{literals[1]}\"", Case.Sensitive,
            $"the timings route's own segment is /{literals[1]}");
    }

    [Fact]
    public async Task The_client_falls_back_to_browser_speech_on_503()
    {
        // The degradation path. Without it a Microsoft outage leaves a dead button on every site.
        //
        // Both halves are asserted as code: the status the server sends when synthesis is
        // unavailable has to be the status the client recognises, and recognising it has to lead
        // somewhere. Deleting the speak() call, or widening the check so 503 no longer routes to
        // it, fails here.
        var code = WithoutComments(await _site.Client.GetStringAsync(ClientPath));

        code.ShouldMatch(@"status\s*===\s*503",
            "the client has to recognise the status the controller sends when synthesis fails");
        code.ShouldMatch(@"speechSynthesis\s*\.\s*speak\s*\(",
            "recognising it has to actually lead to the browser speaking");
    }

    [Fact]
    public async Task The_element_is_registered_under_a_namespaced_tag()
    {
        // The tag is the package's public markup contract: every site's templates and the README
        // spell <read-aloud>, so the name in the define call is not free to move.
        var code = WithoutComments(await _site.Client.GetStringAsync(ClientPath));

        code.ShouldMatch(@"customElements\s*\.\s*define\(\s*""read-aloud""",
            "the element has to be defined, and defined under the tag the markup uses");
    }

    /// <summary>The literal (non-parameter) segments of one of this controller's registered routes.</summary>
    private string[] LiteralSegmentsOf(string actionName)
    {
        var endpoint = _site.Resolve<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(candidate =>
                candidate.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action
                && action.ControllerTypeInfo.AsType() == typeof(ReadAloudController)
                && action.ActionName == actionName);

        return (endpoint.RoutePattern.RawText ?? string.Empty)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !segment.StartsWith('{'))
            .ToArray();
    }

    /// <summary>
    /// The served script with its comments removed, so nothing here can be satisfied by prose.
    /// </summary>
    /// <remarks>
    /// Deliberately simple: block comments, then everything from a <c>//</c> to the end of its
    /// line. That is exact for this file, which contains no string literal holding <c>//</c>. If
    /// one is ever added, the stripping truncates that line and a test here fails; it cannot pass
    /// for the wrong reason, which is the direction that matters.
    /// </remarks>
    private static string WithoutComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        return Regex.Replace(withoutBlocks, @"//.*$", string.Empty, RegexOptions.Multiline);
    }
}
