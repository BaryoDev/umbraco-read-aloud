using BaryoDev.Umbraco.ReadAloud.Caching;
using BaryoDev.Umbraco.ReadAloud.Content;
using BaryoDev.Umbraco.ReadAloud.Controllers;
using BaryoDev.Umbraco.ReadAloud.Engine;
using BaryoDev.Umbraco.ReadAloud.Listens;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using global::Umbraco.Cms.Core;
using global::Umbraco.Cms.Core.Services;
using global::Umbraco.Cms.Infrastructure.Scoping;
using global::Umbraco.Cms.Core.Web;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

/// <summary>
/// Drives the controller directly with its own counter and clock, so each test reads exactly what
/// its own call added. The real pipeline cases at the bottom go through the booted site.
/// </summary>
[Collection(UmbracoCollection.Name)]
public class ListenCountingTests
{
    private readonly UmbracoSiteFixture _site;
    private readonly ListenClock _clock = new();
    private readonly ListenOptions _options = new();
    private readonly ListenCounter _counter;

    public ListenCountingTests(UmbracoSiteFixture site)
    {
        _site = site;
        _counter = new ListenCounter(_options, _clock);
    }

    private sealed class OkEngine : IReadAloudEngine
    {
        public Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct = default) =>
            Task.FromResult(new SynthesisResult([1, 2, 3], [], "audio/mpeg"));
    }

    private sealed class ThrowingEngine : IReadAloudEngine
    {
        public Task<SynthesisResult> SynthesizeAsync(SynthesisRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("provider down");
    }

    private sealed class NoCache : IAudioCache
    {
        public Task<SynthesisResult?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<SynthesisResult?>(null);

        public Task SetAsync(string key, SynthesisResult result, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class Rig(
        ReadAloudController controller,
        CoalescingAudioSource audio,
        HttpContext http,
        IServiceScope scope,
        IDisposable context) : IDisposable
    {
        public ReadAloudController Controller => controller;
        public CoalescingAudioSource Audio => audio;
        public HttpContext Http => http;

        public void Dispose()
        {
            context.Dispose();
            scope.Dispose();
        }
    }

    private Rig Build(IReadAloudEngine engine, ReadAloudOptions? configured = null, string? range = null, string? ifNoneMatch = null)
    {
        var scope = _site.Services.CreateScope();
        var services = scope.ServiceProvider;
        var context = services.GetRequiredService<IUmbracoContextFactory>().EnsureUmbracoContext();

        configured ??= new ReadAloudOptions { PropertyAlias = UmbracoSiteFixture.PropertyAlias };
        var options = new FixedOptions(configured);
        var audio = new CoalescingAudioSource(engine, new NoCache(), options, NullLogger<CoalescingAudioSource>.Instance);

        var http = new DefaultHttpContext();
        if (range is not null) http.Request.Headers.Range = range;
        if (ifNoneMatch is not null) http.Request.Headers.IfNoneMatch = ifNoneMatch;

        var controller = new ReadAloudController(
            audio,
            services.GetRequiredService<IPublishedContentQuery>(),
            services.GetRequiredService<IPublicAccessService>(),
            options,
            null,
            _counter)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };
        return new Rig(controller, audio, http, scope, context);
    }

    private string EtagFor(Rig rig, string html, string voice, ReadAloudOptions options)
    {
        var request = new SynthesisRequest
        {
            Text = TextExtractor.ToSpeakableText(html, options.MaxChars),
            Voice = voice,
        };
        return $"\"{rig.Audio.KeyFor(request)}-audio-{ReadAloudController.ETagVersion}\"";
    }

    private int Held(Guid node, string voice) =>
        _counter.Snapshot().GetValueOrDefault(new ListenKey(node, voice, ListenClock.Day));

    private static string DefaultVoice => new ReadAloudOptions().DefaultVoice;

    [Fact]
    public async Task A_200_adds_one_for_the_node_the_resolved_voice_and_the_utc_day()
    {
        using var rig = Build(new OkEngine());

        (await rig.Controller.Get(_site.PublishedNodeKey, null, default)).ShouldBeOfType<FileContentResult>();

        _counter.Snapshot().ShouldBe(new Dictionary<ListenKey, int>
        {
            [new ListenKey(_site.PublishedNodeKey, DefaultVoice, ListenClock.Day)] = 1,
        });
    }

    [Fact]
    public async Task A_304_adds_one()
    {
        var options = new ReadAloudOptions { PropertyAlias = UmbracoSiteFixture.PropertyAlias };
        string tag;
        using (var probe = Build(new OkEngine(), options)) tag = EtagFor(probe, UmbracoSiteFixture.BodyHtml, DefaultVoice, options);

        using var rig = Build(new OkEngine(), options, ifNoneMatch: tag);
        var result = await rig.Controller.Get(_site.PublishedNodeKey, null, default);

        result.ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(304);
        Held(_site.PublishedNodeKey, DefaultVoice).ShouldBe(1);
    }

    [Fact]
    public async Task The_timings_route_adds_nothing()
    {
        using var rig = Build(new OkEngine());

        (await rig.Controller.Timings(_site.PublishedNodeKey, null, default)).ShouldBeOfType<JsonResult>();

        _counter.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_page_adds_nothing()
    {
        using var rig = Build(new OkEngine());

        foreach (var key in new[]
                 {
                     Guid.NewGuid(), _site.ProtectedNodeKey, _site.DisabledNodeKey, _site.UnpublishedNodeKey,
                 })
        {
            (await rig.Controller.Get(key, null, default)).ShouldBeOfType<NotFoundResult>();
        }

        _counter.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_synthesis_failure_adds_nothing()
    {
        using var rig = Build(new ThrowingEngine());

        (await rig.Controller.Get(_site.PublishedNodeKey, null, default))
            .ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(503);

        _counter.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task Browser_mode_adds_nothing()
    {
        var options = new ReadAloudOptions
        {
            PropertyAlias = UmbracoSiteFixture.PropertyAlias,
            Provider = ReadAloudOptions.BrowserProvider,
        };
        using var rig = Build(new BrowserVoiceEngine(), options);

        (await rig.Controller.Get(_site.PublishedNodeKey, null, default))
            .ShouldBeOfType<StatusCodeResult>().StatusCode.ShouldBe(503);

        _counter.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_request_cancelled_before_the_audio_is_ready_adds_nothing()
    {
        using var rig = Build(new OkEngine());
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        (await rig.Controller.Get(_site.PublishedNodeKey, null, cancelled.Token)).ShouldBeOfType<EmptyResult>();

        _counter.Snapshot().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("bytes=0-", 1)]
    [InlineData("bytes=0-1", 0)]
    [InlineData("bytes=500-", 0)]
    [InlineData("bytes=-500", 0)]
    [InlineData("bytes=0-1,5-6", 0)]
    [InlineData("bytes=junk", 0)]
    public async Task Only_a_request_without_a_range_or_with_an_open_range_from_zero_adds_a_listen(
        string? range,
        int expected)
    {
        using var rig = Build(new OkEngine(), range: range);

        (await rig.Controller.Get(_site.PublishedNodeKey, null, default)).ShouldBeOfType<FileContentResult>();

        Held(_site.PublishedNodeKey, DefaultVoice).ShouldBe(expected);
    }

    [Fact]
    public async Task A_ranged_304_adds_nothing_like_a_ranged_200()
    {
        var options = new ReadAloudOptions { PropertyAlias = UmbracoSiteFixture.PropertyAlias };
        string tag;
        using (var probe = Build(new OkEngine(), options)) tag = EtagFor(probe, UmbracoSiteFixture.BodyHtml, DefaultVoice, options);

        using var rig = Build(new OkEngine(), options, range: "bytes=0-1", ifNoneMatch: tag);
        (await rig.Controller.Get(_site.PublishedNodeKey, null, default)).ShouldBeOfType<StatusCodeResult>();

        _counter.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_voice_stored_is_the_one_used_and_not_the_one_asked_for()
    {
        var options = new ReadAloudOptions { PropertyAlias = UmbracoSiteFixture.PropertyAlias };
        using var rig = Build(new OkEngine(), options);

        await rig.Controller.Get(_site.PublishedNodeKey, "en-US-NotOnTheList", default);

        Held(_site.PublishedNodeKey, DefaultVoice).ShouldBe(1);
        Held(_site.PublishedNodeKey, "en-US-NotOnTheList").ShouldBe(0);
    }

    [Fact]
    public async Task An_allowed_voice_is_stored_as_that_voice()
    {
        var options = new ReadAloudOptions
        {
            PropertyAlias = UmbracoSiteFixture.PropertyAlias,
            AllowedVoices = ["en-GB-RyanNeural"],
        };
        using var rig = Build(new OkEngine(), options);

        await rig.Controller.Get(_site.PublishedNodeKey, "en-GB-RyanNeural", default);

        Held(_site.PublishedNodeKey, "en-GB-RyanNeural").ShouldBe(1);
    }

    [Fact]
    public async Task A_page_voice_is_stored_as_the_page_voice()
    {
        var options = new ReadAloudOptions
        {
            PropertyAlias = UmbracoSiteFixture.PropertyAlias,
            AllowedVoices = [UmbracoSiteFixture.PageVoice],
        };
        using var rig = Build(new OkEngine(), options);

        await rig.Controller.Get(_site.OverrideNodeKey, null, default);

        Held(_site.OverrideNodeKey, UmbracoSiteFixture.PageVoice).ShouldBe(1);
    }

    [Fact]
    public async Task A_voice_refused_on_an_unpublished_key_writes_nothing()
    {
        var options = new ReadAloudOptions
        {
            PropertyAlias = UmbracoSiteFixture.PropertyAlias,
            AllowedVoices = ["en-GB-RyanNeural"],
        };
        using var rig = Build(new OkEngine(), options);

        (await rig.Controller.Get(_site.UnpublishedNodeKey, "en-GB-RyanNeural", default))
            .ShouldBeOfType<NotFoundResult>();

        _counter.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task With_the_setting_off_a_play_adds_nothing()
    {
        _options.CurrentValue.CountListens = false;
        using var rig = Build(new OkEngine());

        (await rig.Controller.Get(_site.PublishedNodeKey, null, default)).ShouldBeOfType<FileContentResult>();

        _counter.Snapshot().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_controller_built_without_a_counter_still_serves()
    {
        using var rig = Build(new OkEngine());
        var bare = new ReadAloudController(
            rig.Audio,
            _site.Services.CreateScope().ServiceProvider.GetRequiredService<IPublishedContentQuery>(),
            _site.Services.CreateScope().ServiceProvider.GetRequiredService<IPublicAccessService>(),
            new FixedOptions(new ReadAloudOptions { PropertyAlias = UmbracoSiteFixture.PropertyAlias }))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        (await bare.Get(_site.PublishedNodeKey, null, default)).ShouldBeOfType<FileContentResult>();
    }

    [Fact]
    public async Task A_rate_limited_request_adds_nothing_and_a_served_one_lands_in_the_table()
    {
        var counter = _site.Resolve<ListenCounter>();
        var caller = System.Net.IPAddress.Parse("203.0.113.77");
        var path = $"/read-aloud/{_site.NotDisabledNodeKey}";
        var voice = _site.Resolve<Microsoft.Extensions.Options.IOptionsMonitor<ReadAloudOptions>>().CurrentValue.DefaultVoice;
        var key = new ListenKey(_site.NotDisabledNodeKey, voice, ListenKey.DayOf(DateTimeOffset.UtcNow));

        // Discard what other tests in the shared site left held, then count only this caller.
        FlushDiscard(counter);
        for (var i = 0; i < UmbracoSiteFixture.RateLimitPerMinute; i++)
        {
            await SendAsync(caller, path);
        }

        var held = counter.Snapshot().GetValueOrDefault(key);
        var limited = await SendAsync(caller, path);

        limited.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        counter.Snapshot().GetValueOrDefault(key).ShouldBe(held);
        held.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task One_played_request_goes_from_the_endpoint_through_a_flush_into_the_table()
    {
        var counter = _site.Resolve<ListenCounter>();
        var flush = _site.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .OfType<ListenFlushService>().Single();
        var scopes = _site.Resolve<IScopeProvider>();
        var voice = _site.Resolve<Microsoft.Extensions.Options.IOptionsMonitor<ReadAloudOptions>>().CurrentValue.DefaultVoice;
        var node = _site.EmptyOverrideNodeKey;

        int Stored()
        {
            using var scope = scopes.CreateScope();
            var sum = scope.Database.ExecuteScalar<int?>(
                "SELECT SUM(Listens) FROM baryoReadAloudListens WHERE NodeKey = @0 AND Voice = @1", node, voice);
            scope.Complete();
            return sum ?? 0;
        }

        flush.Flush().ShouldBeTrue();
        var before = Stored();

        var response = await SendAsync(System.Net.IPAddress.Parse("203.0.113.78"), $"/read-aloud/{node}");
        response.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        flush.Flush().ShouldBeTrue();

        Stored().ShouldBe(before + 1);
        counter.Snapshot().ShouldBeEmpty();
    }

    private static void FlushDiscard(ListenCounter counter) => counter.Clear();

    private Task<HttpContext> SendAsync(System.Net.IPAddress caller, string path) =>
        _site.Server.SendAsync(c =>
        {
            c.Request.Method = HttpMethods.Get;
            c.Request.Path = path;
            c.Connection.RemoteIpAddress = caller;
        });

    private sealed class FixedOptions(ReadAloudOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<ReadAloudOptions>
    {
        public ReadAloudOptions CurrentValue { get; } = value;

        public ReadAloudOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<ReadAloudOptions, string?> listener) => null;
    }
}
