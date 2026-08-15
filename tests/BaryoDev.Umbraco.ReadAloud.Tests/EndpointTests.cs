using System.Net;
using Microsoft.AspNetCore.Routing;
using Shouldly;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

/// <summary>
/// Boots a real Umbraco rather than mocking one, because route registration, DI and options
/// binding are only exercised by a real boot and a test double passes with all three broken.
/// </summary>
[Collection(UmbracoCollection.Name)]
public class EndpointTests
{
    private readonly UmbracoSiteFixture _site;

    public EndpointTests(UmbracoSiteFixture site) => _site = site;

    [Fact]
    public void The_route_is_registered_at_the_url_the_client_calls()
    {
        // A rename here breaks every site silently: the client keeps requesting and the server
        // keeps 404ing, and read-aloud just stops working with nothing in the logs.
        //
        // Asserted against the endpoint table rather than by requesting a URL, because a missing
        // route and a missing node both answer 404 and a request cannot tell them apart.
        var routes = _site.Resolve<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(text => text is not null)
            .ToList();

        // Matched exactly rather than by substring: "read-aloud-renamed" would satisfy a Contains
        // on the first segment, and "{keyword:guid}" would satisfy one on the second, while both
        // break every client.
        var registered = routes.Any(text => Segments(text!) is ["read-aloud", "{key:guid}"]);

        registered.ShouldBeTrue(
            $"no endpoint matches read-aloud/{{key:guid}}. Registered: {string.Join(", ", routes)}");
    }

    private static string[] Segments(string routePattern) =>
        routePattern.Split('/', StringSplitOptions.RemoveEmptyEntries);

    [Fact]
    public async Task An_unknown_node_is_not_found_rather_than_a_server_error()
    {
        var response = await _site.Client.GetAsync($"/read-aloud/{Guid.NewGuid()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_malformed_key_does_not_reach_the_handler()
    {
        var response = await _site.Client.GetAsync("/read-aloud/not-a-guid");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_published_node_is_read_from_its_configured_property_and_served()
    {
        // The whole chain in one request: the route matches, the node is found in the published
        // cache, its property is extracted to text, and the cache key that text produces is the
        // one the audio was stored under. Any link breaking gives a 404 or different bytes.
        var response = await _site.Client.GetAsync($"/read-aloud/{_site.PublishedNodeKey}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("audio/mpeg");
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(UmbracoSiteFixture.SeededAudio);
    }

    [Fact]
    public async Task Word_timings_travel_with_the_audio()
    {
        // The client highlights words as they are spoken, and it has one response to learn the
        // timings from. Without the header the audio plays and nothing highlights.
        var response = await _site.Client.GetAsync($"/read-aloud/{_site.PublishedNodeKey}");

        response.Headers.GetValues("X-ReadAloud-Boundaries").ShouldHaveSingleItem()
            .ShouldContain("quick");
    }

    [Fact]
    public async Task A_member_protected_node_is_not_read_aloud()
    {
        // Public access is enforced by Umbraco's routing pipeline, which this controller never
        // runs. Without an explicit check the endpoint reads a protected page's body straight out
        // of the published cache and speaks it to anyone holding the key, and the key is in the
        // page markup by design. The audio is already sitting in the cache, so nothing but the
        // check itself stands in the way.
        var response = await _site.Client.GetAsync($"/read-aloud/{_site.ProtectedNodeKey}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound,
            "a protected node must not be readable, and 404 rather than 403 so the endpoint does "
            + "not confirm that it exists");
    }

    [Fact]
    public async Task An_unprotected_node_is_still_served()
    {
        // The other half of the protection test. Refusing everything would satisfy it alone.
        var response = await _site.Client.GetAsync($"/read-aloud/{_site.PublishedNodeKey}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(UmbracoSiteFixture.SeededAudio);
    }

    [Fact]
    public async Task A_voice_the_site_does_not_allow_falls_back_to_the_default()
    {
        // AllowedVoices is empty here, as it is on a site that configures nothing, and an empty
        // list means only the default voice. A caller-supplied voice reaches the SSML document the
        // engine builds, so anything but the configured default getting through is an injection
        // point rather than a preference.
        var response = await _site.Client.GetAsync(
            $"/read-aloud/{_site.PublishedNodeKey}?voice=en-US-NotOnTheList");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(UmbracoSiteFixture.SeededAudio,
            "only the default voice's audio is cached, so anything else means the requested voice "
            + "was used");
    }

    [Fact]
    public void The_composer_registers_everything_the_controller_needs()
    {
        // Resolving proves the DI graph is complete. A missing registration otherwise appears as
        // a 500 on first use in production rather than at boot.
        _site.Resolve<BaryoDev.Umbraco.ReadAloud.Engine.IReadAloudEngine>().ShouldNotBeNull();
        _site.Resolve<BaryoDev.Umbraco.ReadAloud.Caching.IAudioCache>().ShouldNotBeNull();
        _site.Resolve<BaryoDev.Umbraco.ReadAloud.Caching.CoalescingAudioSource>().ShouldNotBeNull();
    }
}
