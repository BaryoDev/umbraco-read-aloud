using BaryoDev.Umbraco.ReadAloud.Caching;
using BaryoDev.Umbraco.ReadAloud.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

/// <summary>
/// Drives the action method directly, with the real content query, public access service and
/// audio source taken from the booted site, and only the options faked.
/// </summary>
/// <remarks>
/// Through the real services rather than test doubles, because a hand-rolled
/// <c>IPublishedContent</c> would be twenty-odd members of guesswork whose <c>Value&lt;T&gt;()</c>
/// path does not resemble the real one, and it would pass while the real thing was broken.
/// Faking only the options is what makes per-test configuration possible without a second Umbraco
/// boot, which is the one thing this fixture cannot cheaply give.
/// </remarks>
[Collection(UmbracoCollection.Name)]
public class ReadAloudControllerTests
{
    private const string AllowedAlternative = "en-GB-RyanNeural";

    private readonly UmbracoSiteFixture _site;

    public ReadAloudControllerTests(UmbracoSiteFixture site) => _site = site;

    /// <summary>Runs the action under a scope and an Umbraco context, the way a request would.</summary>
    /// <remarks>
    /// <c>IPublishedContentQuery</c> is scoped and reaches the published cache through
    /// <c>IUmbracoContextAccessor</c>, so outside a request it needs both a scope and a context
    /// established by hand. In a real request the middleware does this.
    /// </remarks>
    private async Task<IActionResult> GetAsync(
        ReadAloudOptions options,
        Guid key,
        string? voice = null,
        CancellationToken ct = default)
    {
        using var scope = _site.Services.CreateScope();
        var services = scope.ServiceProvider;

        using var context = services.GetRequiredService<IUmbracoContextFactory>().EnsureUmbracoContext();

        var controller = new ReadAloudController(
            services.GetRequiredService<CoalescingAudioSource>(),
            services.GetRequiredService<IPublishedContentQuery>(),
            services.GetRequiredService<IPublicAccessService>(),
            new FixedOptions(options))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        return await controller.Get(key, voice, ct);
    }

    private static ReadAloudOptions Options() => new()
    {
        PropertyAlias = UmbracoSiteFixture.PropertyAlias,
        DefaultVoice = new ReadAloudOptions().DefaultVoice,
    };

    [Fact]
    public async Task A_disabled_site_serves_nothing()
    {
        // The kill switch. A site that turns the feature off and still has the endpoint answering
        // has no way to stop it short of uninstalling the package.
        var options = Options();
        options.Enabled = false;

        var result = await GetAsync(options, _site.PublishedNodeKey);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task With_no_allow_list_only_the_default_voice_is_used()
    {
        // AllowedVoices empty is the default, and it means only DefaultVoice. Anything else lets a
        // caller put a string of their choosing into the SSML document the engine builds.
        var options = Options();
        options.AllowedVoices = [];

        var result = await GetAsync(options, _site.PublishedNodeKey, "en-US-AnythingILike");

        var file = result.ShouldBeOfType<FileContentResult>();
        file.FileContents.ShouldBe(UmbracoSiteFixture.SeededAudio);
    }

    [Fact]
    public async Task A_voice_the_site_allows_is_honoured()
    {
        // The other half. A blanket fallback to the default would satisfy the test above while
        // making AllowedVoices meaningless.
        await _site.SeedAudioAsync(
            UmbracoSiteFixture.BodyHtml, SeededAlternativeAudio, AllowedAlternative);

        var options = Options();
        options.AllowedVoices = [AllowedAlternative];

        var result = await GetAsync(options, _site.PublishedNodeKey, AllowedAlternative);

        var file = result.ShouldBeOfType<FileContentResult>();
        file.FileContents.ShouldBe(SeededAlternativeAudio);
    }

    [Fact]
    public async Task A_voice_outside_a_configured_allow_list_is_replaced_by_the_default()
    {
        var options = Options();
        options.AllowedVoices = [AllowedAlternative];

        var result = await GetAsync(options, _site.PublishedNodeKey, "en-US-SomethingElse");

        var file = result.ShouldBeOfType<FileContentResult>();
        file.FileContents.ShouldBe(UmbracoSiteFixture.SeededAudio);
    }

    [Fact]
    public async Task A_member_protected_node_is_refused_even_with_its_audio_already_cached()
    {
        var result = await GetAsync(Options(), _site.ProtectedNodeKey);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task A_reader_navigating_away_is_not_reported_as_a_failure()
    {
        // RequestAborted fires whenever someone closes the tab mid-article, which is ordinary
        // behaviour rather than an outage. Answering 503 writes a failure to a response nobody is
        // listening to, and makes the endpoint look unreliable in exactly the numbers an operator
        // reads when deciding whether to pay for Azure Speech instead.
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var result = await GetAsync(Options(), _site.PublishedNodeKey, ct: aborted.Token);

        result.ShouldBeOfType<EmptyResult>();
    }

    private static readonly byte[] SeededAlternativeAudio =
        [0x49, 0x44, 0x33, 0x04, 0x00, 0x61, 0x6C, 0x74];

    /// <summary>An options monitor over one fixed value, so a test can configure the controller.</summary>
    private sealed class FixedOptions(ReadAloudOptions value) : IOptionsMonitor<ReadAloudOptions>
    {
        public ReadAloudOptions CurrentValue { get; } = value;

        public ReadAloudOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ReadAloudOptions, string?> listener) => null;
    }
}
