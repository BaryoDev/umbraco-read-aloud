using System.Net;
using System.Net.Http.Json;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
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
        routes.Any(text => Segments(text!) is ["read-aloud", "{key:guid}"]).ShouldBeTrue(
            $"no endpoint matches read-aloud/{{key:guid}}. Registered: {string.Join(", ", routes)}");

        routes.Any(text => Segments(text!) is ["read-aloud", "{key:guid}", "timings"]).ShouldBeTrue(
            $"no endpoint matches read-aloud/{{key:guid}}/timings. Registered: {string.Join(", ", routes)}");
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
    public async Task The_audio_response_carries_no_timings_header()
    {
        // The timings used to ride along on this response. They cannot: a long article's timings
        // run to tens of kilobytes, and past a couple of hundred once the serializer escapes
        // non-Latin text to \uXXXX, which is far beyond what CDN edges accept in a header. A
        // header also forces the client to use fetch to read it, which means buffering the whole
        // recording before playback rather than streaming it.
        var response = await _site.Client.GetAsync($"/read-aloud/{_site.PublishedNodeKey}");

        response.Headers.Contains("X-ReadAloud-Boundaries").ShouldBeFalse();
        response.Content.Headers.Contains("X-ReadAloud-Boundaries").ShouldBeFalse();
    }

    [Fact]
    public async Task Word_timings_are_served_as_json_on_their_own_route()
    {
        // The client highlights words as they are spoken, so it still has to be able to get them.
        var response = await _site.Client.GetAsync($"/read-aloud/{_site.PublishedNodeKey}/timings");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");

        var boundaries = await response.Content.ReadFromJsonAsync<WordBoundary[]>();
        boundaries.ShouldNotBeNull();
        boundaries!.ShouldHaveSingleItem().Text.ShouldBe("quick");
    }

    [Fact]
    public async Task A_member_protected_node_is_not_read_aloud_on_the_timings_route()
    {
        // The timings give away the article's text word by word, so a second route that resolved
        // content on its own would be a way straight around the protection the audio route has.
        var response = await _site.Client.GetAsync($"/read-aloud/{_site.ProtectedNodeKey}/timings");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_unknown_node_is_not_found_on_the_timings_route_too()
    {
        // The two routes answer alike, so a client does not have to learn two sets of rules.
        var response = await _site.Client.GetAsync($"/read-aloud/{Guid.NewGuid()}/timings");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Requests_beyond_the_configured_rate_are_refused()
    {
        // The endpoint is anonymous and synthesis is expensive, and RateLimitPerMinute tells a site
        // owner in the documented configuration block that this is handled. Until now it was not.
        var caller = IPAddress.Parse("203.0.113.10");

        for (var i = 0; i < UmbracoSiteFixture.RateLimitPerMinute; i++)
        {
            var allowed = await SendFromAsync(caller, $"/read-aloud/{_site.PublishedNodeKey}");
            allowed.ShouldNotBe(StatusCodes.Status429TooManyRequests,
                $"request {i + 1} is still inside the window of {UmbracoSiteFixture.RateLimitPerMinute}");
        }

        var refused = await SendFromAsync(caller, $"/read-aloud/{_site.PublishedNodeKey}");

        refused.ShouldBe(StatusCodes.Status429TooManyRequests);
    }

    [Fact]
    public async Task One_caller_exhausting_the_window_does_not_lock_everyone_else_out()
    {
        // Partitioned per caller, so one noisy reader cannot turn read-aloud off for the site.
        var noisy = IPAddress.Parse("203.0.113.20");
        var innocent = IPAddress.Parse("203.0.113.21");

        for (var i = 0; i <= UmbracoSiteFixture.RateLimitPerMinute; i++)
        {
            await SendFromAsync(noisy, $"/read-aloud/{_site.PublishedNodeKey}");
        }

        (await SendFromAsync(noisy, $"/read-aloud/{_site.PublishedNodeKey}"))
            .ShouldBe(StatusCodes.Status429TooManyRequests);

        (await SendFromAsync(innocent, $"/read-aloud/{_site.PublishedNodeKey}"))
            .ShouldNotBe(StatusCodes.Status429TooManyRequests);
    }

    /// <summary>Sends one request as if it came from a given address, which the client cannot set.</summary>
    private async Task<int> SendFromAsync(IPAddress caller, string path)
    {
        var context = await _site.Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = caller;
        });

        return context.Response.StatusCode;
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
