using BaryoDev.Umbraco.ReadAloud.Caching;
using BaryoDev.Umbraco.ReadAloud.Engine;
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

    /// <summary>Seeded against the alternative voice only, so the two entries are distinguishable.</summary>
    private const string AlternativeBoundaryText = "alternative";

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

    private async Task<IActionResult> TimingsAsync(
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

        return await controller.Timings(key, voice, ct);
    }

    private static ReadAloudOptions Options() => new()
    {
        PropertyAlias = UmbracoSiteFixture.PropertyAlias,
        DefaultVoice = new ReadAloudOptions().DefaultVoice,
    };

    private sealed class CountingEngine : IReadAloudEngine
    {
        public int Calls { get; private set; }

        public Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct = default)
        {
            Calls++;
            throw new InvalidOperationException("should not be called");
        }
    }

    private sealed class EmptyCache : BaryoDev.Umbraco.ReadAloud.Caching.IAudioCache
    {
        public Task<SynthesisResult?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<SynthesisResult?>(null);

        public Task SetAsync(string key, SynthesisResult result, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task The_browser_engine_answers_503_for_a_readable_page_and_404_for_one_that_is_not()
    {
        var controller = BrowserModeController(new BrowserVoiceEngine(), out var scope, out var context);
        using var _ = scope;
        using var __ = context;

        // The guards run first now, so a page that cannot be read is a 404 that removes the
        // button, and a readable one is the 503 that sends the reader to the browser voice. The
        // engine is never called in either case.
        (await controller.Get(_site.PublishedNodeKey, null, default))
            .ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(503);
        (await controller.Timings(_site.PublishedNodeKey, null, default))
            .ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(503);

        foreach (var key in new[] { Guid.NewGuid(), _site.ProtectedNodeKey, _site.DisabledNodeKey })
        {
            (await controller.Get(key, null, default)).ShouldBeOfType<NotFoundResult>();
            (await controller.Timings(key, null, default)).ShouldBeOfType<NotFoundResult>();
        }
    }

    [Fact]
    public async Task A_registered_engine_still_runs_under_the_default_browser_provider()
    {
        // A 0.1.x site with its own engine and no Provider set must keep using it. The engine
        // here throws, so the 503 is its failure, and the call count proves it was reached.
        var engine = new CountingEngine();
        var controller = BrowserModeController(engine, out var scope, out var context);
        using var _ = scope;
        using var __ = context;

        (await controller.Get(_site.PublishedNodeKey, null, default))
            .ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(503);

        engine.Calls.ShouldBe(1);
    }

    private ReadAloudController BrowserModeController(
        IReadAloudEngine engine,
        out IServiceScope scope,
        out IDisposable context)
    {
        scope = _site.Services.CreateScope();
        var services = scope.ServiceProvider;
        context = services.GetRequiredService<IUmbracoContextFactory>().EnsureUmbracoContext();

        var configured = Options();
        configured.Provider = ReadAloudOptions.BrowserProvider;
        var options = new FixedOptions(configured);

        var audio = new CoalescingAudioSource(
            engine,
            new EmptyCache(),
            options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CoalescingAudioSource>.Instance);

        return new ReadAloudController(
            audio,
            services.GetRequiredService<IPublishedContentQuery>(),
            services.GetRequiredService<IPublicAccessService>(),
            options)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

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
            UmbracoSiteFixture.BodyHtml, SeededAlternativeAudio, AllowedAlternative,
            AlternativeBoundaryText);

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
    public async Task A_document_type_the_site_did_not_list_is_not_read_aloud()
    {
        // A site that sets DocumentTypes to ["article"] means it, and today gets audio for every
        // published node with a bodyText property and no way to tell that the option did nothing.
        var options = Options();
        options.DocumentTypes = ["article"];

        var result = await GetAsync(options, _site.PublishedNodeKey);

        result.ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task A_listed_document_type_is_read_aloud()
    {
        var options = Options();
        options.DocumentTypes = ["article", UmbracoSiteFixture.DocumentTypeAlias];

        var result = await GetAsync(options, _site.PublishedNodeKey);

        result.ShouldBeOfType<FileContentResult>();
    }

    [Fact]
    public async Task A_document_type_alias_is_matched_without_case()
    {
        // Umbraco aliases are camelCase by convention, and configuration is typed by hand, which
        // is exactly where that convention gets missed. Refusing on case would look like the
        // feature being broken rather than a typo.
        var options = Options();
        options.DocumentTypes = [UmbracoSiteFixture.DocumentTypeAlias.ToUpperInvariant()];

        var result = await GetAsync(options, _site.PublishedNodeKey);

        result.ShouldBeOfType<FileContentResult>();
    }

    [Fact]
    public async Task An_empty_document_type_list_means_every_type()
    {
        var options = Options();
        options.DocumentTypes = [];

        var result = await GetAsync(options, _site.PublishedNodeKey);

        result.ShouldBeOfType<FileContentResult>();
    }

    [Fact]
    public async Task The_timings_route_runs_the_same_guards_as_the_audio_route()
    {
        // Every guard, checked through the second route rather than assumed to be shared.
        var disabled = Options();
        disabled.Enabled = false;
        (await TimingsAsync(disabled, _site.PublishedNodeKey)).ShouldBeOfType<NotFoundResult>();

        var filtered = Options();
        filtered.DocumentTypes = ["article"];
        (await TimingsAsync(filtered, _site.PublishedNodeKey)).ShouldBeOfType<NotFoundResult>();

        (await TimingsAsync(Options(), _site.ProtectedNodeKey)).ShouldBeOfType<NotFoundResult>();
        (await TimingsAsync(Options(), Guid.NewGuid())).ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task The_timings_route_honours_the_voice_rules_too()
    {
        // Timings are per voice, so the route that serves them has to clamp the voice the same way
        // the audio route does. The two cached entries carry different words, which is what lets
        // this test tell which one was read: seeding both with the same word made it unfailable.
        await _site.SeedAudioAsync(
            UmbracoSiteFixture.BodyHtml, SeededAlternativeAudio, AllowedAlternative,
            AlternativeBoundaryText);

        var options = Options();
        options.AllowedVoices = [];

        var result = await TimingsAsync(options, _site.PublishedNodeKey, AllowedAlternative);

        var boundaries = result.ShouldBeOfType<JsonResult>()
            .Value.ShouldBeAssignableTo<IReadOnlyList<WordBoundary>>();

        boundaries!.ShouldHaveSingleItem().Text.ShouldBe(UmbracoSiteFixture.DefaultBoundaryText,
            $"an empty allow-list means the default voice, so the timings must not be the "
            + $"'{AlternativeBoundaryText}' entry cached against {AllowedAlternative}");
    }

    [Fact]
    public async Task A_reader_navigating_away_is_not_reported_as_a_failure()
    {
        // RequestAborted fires whenever someone closes the tab mid-article, which is ordinary
        // behaviour rather than an outage. Answering 503 writes a failure to a response nobody is
        // listening to, and makes the endpoint look unreliable in exactly the numbers an operator
        // reads when deciding whether to keep the feature switched on.
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var result = await GetAsync(Options(), _site.PublishedNodeKey, ct: aborted.Token);

        result.ShouldBeOfType<EmptyResult>();
    }

    [Fact]
    public async Task A_voice_set_on_the_page_is_used()
    {
        var options = Options();
        options.AllowedVoices = [UmbracoSiteFixture.PageVoice];

        var result = await GetAsync(options, _site.OverrideNodeKey);

        result.ShouldBeOfType<FileContentResult>().FileContents.ShouldBe(UmbracoSiteFixture.SeededOverrideAudio);
    }

    [Fact]
    public async Task A_page_voice_wins_over_the_voice_the_caller_asks_for()
    {
        var options = Options();
        options.AllowedVoices = [UmbracoSiteFixture.PageVoice, AllowedAlternative];

        var result = await GetAsync(options, _site.OverrideNodeKey, AllowedAlternative);

        result.ShouldBeOfType<FileContentResult>().FileContents.ShouldBe(UmbracoSiteFixture.SeededOverrideAudio);
    }

    [Fact]
    public async Task A_page_voice_is_used_for_the_timings_too()
    {
        var options = Options();
        options.AllowedVoices = [UmbracoSiteFixture.PageVoice];

        var result = await TimingsAsync(options, _site.OverrideNodeKey);

        result.ShouldBeOfType<JsonResult>().Value.ShouldBeAssignableTo<IReadOnlyList<WordBoundary>>()!
            .ShouldHaveSingleItem().Text.ShouldBe(UmbracoSiteFixture.OverrideBoundaryText);
    }

    [Fact]
    public async Task An_empty_page_voice_inherits_the_site_voice()
    {
        var options = Options();
        options.AllowedVoices = [UmbracoSiteFixture.PageVoice];

        var result = await GetAsync(options, _site.EmptyOverrideNodeKey);

        result.ShouldBeOfType<FileContentResult>().FileContents.ShouldBe(UmbracoSiteFixture.SeededAudio);
    }

    [Fact]
    public async Task A_page_voice_outside_AllowedVoices_is_ignored()
    {
        var options = Options();
        options.AllowedVoices = [UmbracoSiteFixture.PageVoice];

        var result = await GetAsync(options, _site.UnlistedOverrideNodeKey);

        result.ShouldBeOfType<FileContentResult>().FileContents.ShouldBe(UmbracoSiteFixture.SeededAudio,
            "only the default voice is cached for this page, so any other bytes mean the unlisted voice was used");
    }

    [Fact]
    public async Task A_page_voice_is_ignored_when_AllowedVoices_is_empty()
    {
        // An empty list means the default voice only, for a page the same as for a caller.
        var result = await GetAsync(Options(), _site.OverrideNodeKey);

        result.ShouldBeOfType<FileContentResult>().FileContents.ShouldBe(UmbracoSiteFixture.SeededAudio);
    }

    [Fact]
    public async Task A_page_voice_is_not_read_when_the_alias_is_turned_off()
    {
        var options = Options();
        options.AllowedVoices = [UmbracoSiteFixture.PageVoice];
        options.VoicePropertyAlias = "";

        var result = await GetAsync(options, _site.OverrideNodeKey);

        result.ShouldBeOfType<FileContentResult>().FileContents.ShouldBe(UmbracoSiteFixture.SeededAudio);
    }

    [Fact]
    public async Task A_page_switched_off_is_refused_like_an_unlisted_document_type()
    {
        (await GetAsync(Options(), _site.DisabledNodeKey)).ShouldBeOfType<NotFoundResult>();
        (await TimingsAsync(Options(), _site.DisabledNodeKey)).ShouldBeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task The_disable_switch_is_ignored_when_its_alias_is_turned_off()
    {
        var options = Options();
        options.DisablePropertyAlias = "";

        (await GetAsync(options, _site.DisabledNodeKey)).ShouldBeOfType<FileContentResult>();
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
