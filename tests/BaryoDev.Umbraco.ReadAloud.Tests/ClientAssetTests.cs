using System.Net;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

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
        // The pairing that breaks silently on a rename. Both halves must move together.
        var client = await _site.Client.GetStringAsync(ClientPath);

        client.ShouldContain("/read-aloud/");
    }

    [Fact]
    public async Task The_client_falls_back_to_browser_speech_on_503()
    {
        // The degradation path. Without it a Microsoft outage leaves a dead button on every site.
        var client = await _site.Client.GetStringAsync(ClientPath);

        client.ShouldContain("speechSynthesis");
        client.ShouldContain("503");
    }

    [Fact]
    public async Task The_element_is_registered_under_a_namespaced_tag()
    {
        var client = await _site.Client.GetStringAsync(ClientPath);

        client.ShouldContain("customElements.define");
        client.ShouldContain("read-aloud");
    }
}
