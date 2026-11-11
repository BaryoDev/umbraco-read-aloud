using System.Net;
using System.Text.Json;
using BaryoDev.Umbraco.ReadAloud.Azure;
using BaryoDev.Umbraco.ReadAloud.Engine;
using BaryoDev.Umbraco.ReadAloud.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using static BaryoDev.Umbraco.ReadAloud.Tests.AzureTestSupport;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

/// <summary>
/// The Test button and the cached voice list. A fake handler stands in for Azure, so no test calls
/// it and none needs a key.
/// </summary>
public class AzureVoiceTests
{
    private static readonly string Azure = AzureSpeechOptions.SectionName;

    [Fact]
    public async Task A_working_key_counts_the_voices_and_calls_the_regional_list_with_the_key_header()
    {
        var handler = FakeVoiceHandler.Voices("en-GB-SoniaNeural", "en-PH-RosaNeural", "fil-PH-BlessicaNeural");
        using var site = new Site(handler);

        var result = await site.Admin.TestAsync("southeastasia", FakeKey, default);

        result.Ok.ShouldBeTrue();
        result.Message.ShouldBe("Azure accepted the key for southeastasia. 3 voices available.");
        handler.Calls.Count.ShouldBe(1);
        handler.Calls[0].Uri.ToString()
            .ShouldBe("https://southeastasia.tts.speech.microsoft.com/cognitiveservices/voices/list");
        handler.Calls[0].Key.ShouldBe(FakeKey);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_refused_key_names_the_region_and_never_the_key(int code)
    {
        using var site = new Site(FakeVoiceHandler.Status((HttpStatusCode)code));

        var result = await site.Admin.TestAsync("westeurope", FakeKey, default);

        result.Ok.ShouldBeFalse();
        result.Message.ShouldBe(
            "Azure refused this key for westeurope. Check the key and that the region matches the resource's Location.");
        JsonSerializer.Serialize(result).ShouldNotContain(FakeKey);
    }

    [Fact]
    public async Task Another_status_reports_only_the_code()
    {
        using var site = new Site(FakeVoiceHandler.Status(HttpStatusCode.TooManyRequests));

        (await site.Admin.TestAsync("westeurope", FakeKey, default)).Message
            .ShouldBe("Azure answered with status 429.");
    }

    [Fact]
    public async Task An_unreachable_service_says_so_and_the_exception_text_is_dropped()
    {
        using var site = new Site(FakeVoiceHandler.Throws());

        var result = await site.Admin.TestAsync("westeurope", FakeKey, default);

        result.Message.ShouldBe("Could not reach Azure.");
        site.AllText.ShouldNotContain(FakeKey);
        JsonSerializer.Serialize(result).ShouldNotContain(FakeKey);
    }

    [Fact]
    public async Task A_request_that_runs_past_the_timeout_reads_as_unreachable()
    {
        var client = new AzureVoiceClient(new FakeHttpFactory(new HangingHandler()), TimeSpan.FromMilliseconds(50));

        var result = await client.FetchAsync("southeastasia", FakeKey, default);

        result.Status.ShouldBe(VoiceFetchStatus.Network);
    }

    [Theory]
    [InlineData("evil.example.com/x?")]
    [InlineData("a.b")]
    [InlineData("southeastasia@evil.com")]
    [InlineData("a-b")]
    [InlineData("UPPER")]
    public async Task A_bad_region_is_refused_before_any_request(string region)
    {
        var handler = FakeVoiceHandler.Voices("x");
        var client = new AzureVoiceClient(new FakeHttpFactory(handler));

        (await client.FetchAsync(region, FakeKey, default)).Status.ShouldBe(VoiceFetchStatus.Invalid);
        handler.Calls.ShouldBeEmpty();

        using var site = new Site(handler);
        (await site.Admin.TestAsync(region, FakeKey, default)).Ok.ShouldBeFalse();
        handler.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_key_is_in_no_log_line_and_no_response_even_when_the_handler_throws_with_it()
    {
        using var site = new Site(FakeVoiceHandler.Throws());

        var responses = new List<string>
        {
            JsonSerializer.Serialize(await site.Admin.TestAsync("southeastasia", FakeKey, default)),
            JsonSerializer.Serialize(site.Admin.Save("southeastasia", FakeKey, "admin")),
            JsonSerializer.Serialize(await site.Admin.TestAsync("", "", default)),
            JsonSerializer.Serialize(await site.Admin.GetVoicesAsync(default)),
            JsonSerializer.Serialize(site.Admin.GetStatus()),
            JsonSerializer.Serialize(site.Admin.Remove("admin")),
        };

        site.Lines.ShouldNotBeEmpty();
        site.AllText.ShouldNotContain(FakeKey);
        foreach (var response in responses) response.ShouldNotContain(FakeKey);
    }

    [Fact]
    public async Task The_list_is_fetched_once_and_a_second_lookup_comes_from_the_cache()
    {
        var handler = FakeVoiceHandler.Voices("en-GB-SoniaNeural");
        using var site = new Site(handler);
        site.Admin.Save("southeastasia", FakeKey, "admin");
        var list = site.Services.GetRequiredService<IAzureVoiceList>();

        var first = await list.GetAsync(default);
        var second = await list.GetAsync(default);

        handler.Calls.Count.ShouldBe(1);
        first!.Voices!.ShouldContain("en-GB-SoniaNeural");
        second.ShouldBeSameAs(first);
    }

    [Fact]
    public async Task Concurrent_lookups_share_one_fetch_while_it_is_in_flight()
    {
        var handler = new GatedHandler();
        using var site = new Site(handler);
        site.Admin.Save("southeastasia", FakeKey, "admin");
        var list = site.Services.GetRequiredService<IAzureVoiceList>();

        var lookups = Enumerable.Range(0, 8).Select(_ => list.GetAsync(default)).ToList();
        await Task.Delay(300);

        handler.Calls.ShouldBe(1);

        handler.Release();
        var snapshots = await Task.WhenAll(lookups);

        handler.Calls.ShouldBe(1);
        snapshots.ShouldAllBe(x => x != null && x.Voices != null && x.Voices.Contains("en-GB-SoniaNeural"));
    }

    [Fact]
    public async Task A_green_test_clears_a_cached_failure_for_that_credential()
    {
        var down = true;
        var handler = new FakeVoiceHandler(_ => down ? throw new HttpRequestException() : FakeVoiceHandler.Json("en-GB-SoniaNeural"));
        using var site = new Site(handler);
        site.Admin.Save("southeastasia", FakeKey, "admin");
        var list = site.Services.GetRequiredService<IAzureVoiceList>();

        (await list.GetAsync(default))!.Voices.ShouldBeNull();

        down = false;
        (await site.Admin.TestAsync("", "", default)).Ok.ShouldBeTrue();

        (await list.GetAsync(default))!.Voices.ShouldNotBeNull();
    }

    [Fact]
    public async Task Saving_clears_a_cached_failure()
    {
        var down = true;
        var handler = new FakeVoiceHandler(_ => down ? throw new HttpRequestException() : FakeVoiceHandler.Json("en-GB-SoniaNeural"));
        using var site = new Site(handler);
        site.Admin.Save("southeastasia", FakeKey, "admin");
        var list = site.Services.GetRequiredService<IAzureVoiceList>();
        (await list.GetAsync(default))!.Voices.ShouldBeNull();

        down = false;
        site.Admin.Save("southeastasia", FakeKey, "admin");

        (await list.GetAsync(default))!.Voices.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_failure_after_the_fetch_still_does_not_throw()
    {
        using var site = new Site(FakeVoiceHandler.Voices("en-GB-SoniaNeural"), configure: s =>
            s.AddSingleton<Microsoft.Extensions.Caching.Memory.IMemoryCache>(new ThrowingCache()));
        site.Admin.Save("southeastasia", FakeKey, "admin");

        var list = site.Services.GetRequiredService<IAzureVoiceList>();

        await Should.NotThrowAsync(async () => await list.GetAsync(default));
        (await list.GetAsync(default)).ShouldBeNull();
    }

    [Fact]
    public async Task A_new_key_or_region_is_a_new_cache_entry()
    {
        var handler = FakeVoiceHandler.Voices("en-GB-SoniaNeural");
        using var site = new Site(handler);
        var list = site.Services.GetRequiredService<IAzureVoiceList>();

        site.Admin.Save("southeastasia", FakeKey, "admin");
        await list.GetAsync(default);
        site.Admin.Save("westeurope", "", "admin");
        await list.GetAsync(default);

        handler.Calls.Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_unreachable_list_does_not_throw_and_the_failure_is_cached_briefly()
    {
        var handler = FakeVoiceHandler.Throws();
        using var site = new Site(handler);
        site.Admin.Save("southeastasia", FakeKey, "admin");
        var list = site.Services.GetRequiredService<IAzureVoiceList>();

        var first = await list.GetAsync(default);
        await list.GetAsync(default);

        first!.Voices.ShouldBeNull();
        handler.Calls.Count.ShouldBe(1);

        var status = await site.Admin.GetVoicesAsync(default);
        status.Available.ShouldBeFalse();
    }

    [Fact]
    public async Task No_credential_means_no_call_and_no_list()
    {
        var handler = FakeVoiceHandler.Voices("x");
        using var site = new Site(handler);

        (await site.Services.GetRequiredService<IAzureVoiceList>().GetAsync(default)).ShouldBeNull();
        handler.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_configured_voice_missing_from_the_list_logs_one_warning_that_names_it()
    {
        var handler = FakeVoiceHandler.Voices("en-GB-SoniaNeural");
        using var site = new Site(handler, new Dictionary<string, string?>
        {
            [$"{ReadAloudOptions.SectionName}:Provider"] = "Azure",
            [$"{ReadAloudOptions.SectionName}:DefaultVoice"] = "en-GB-SoniaNeural",
            [$"{ReadAloudOptions.SectionName}:AllowedVoices:0"] = "en-GB-SoniaNeural",
            [$"{ReadAloudOptions.SectionName}:AllowedVoices:1"] = "xx-XX-NoSuchNeural",
        });
        site.Admin.Save("southeastasia", FakeKey, "admin");

        await site.Services.GetRequiredService<IAzureVoiceList>().GetAsync(default);

        var warnings = site.Warnings();
        warnings.Count.ShouldBe(1);
        warnings[0].ShouldContain("xx-XX-NoSuchNeural");
        warnings[0].ShouldContain("southeastasia");
        warnings[0].ShouldNotContain("en-GB-SoniaNeural,");

        // A second lookup, and a refetch after the entry is evicted, do not repeat it.
        await site.Services.GetRequiredService<IAzureVoiceList>().GetAsync(default);
        site.Services.GetRequiredService<IAzureVoiceList>().Evict("southeastasia", FakeKey);
        await site.Services.GetRequiredService<IAzureVoiceList>().GetAsync(default);

        site.Warnings().Count.ShouldBe(1);
    }

    [Fact]
    public async Task No_warning_when_every_configured_voice_is_in_the_list()
    {
        using var site = new Site(FakeVoiceHandler.Voices("en-GB-SoniaNeural"));
        site.Admin.Save("southeastasia", FakeKey, "admin");

        await site.Services.GetRequiredService<IAzureVoiceList>().GetAsync(default);

        site.Warnings().ShouldBeEmpty();
    }

    [Fact]
    public async Task The_dashboard_status_marks_voices_the_region_does_not_offer()
    {
        using var site = new Site(FakeVoiceHandler.Voices("en-GB-SoniaNeural", "a", "b"), new Dictionary<string, string?>
        {
            [$"{ReadAloudOptions.SectionName}:AllowedVoices:0"] = "xx-XX-NoSuchNeural",
        });
        site.Admin.Save("southeastasia", FakeKey, "admin");

        var status = await site.Admin.GetVoicesAsync(default);

        status.Available.ShouldBeTrue();
        status.Count.ShouldBe(3);
        status.Region.ShouldBe("southeastasia");
        status.Configured.ShouldBe(
        [
            new AzureConfiguredVoice("en-GB-SoniaNeural", true),
            new AzureConfiguredVoice("xx-XX-NoSuchNeural", false),
        ]);
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }

    [Fact]
    public void The_voice_client_does_not_follow_redirects_so_the_key_header_stays_on_one_host()
    {
        using var site = new Site(FakeVoiceHandler.Voices(), fakeHttp: false);

        HttpMessageHandler? handler = site.Services
            .GetRequiredService<System.Net.Http.IHttpMessageHandlerFactory>()
            .CreateHandler(AzureVoiceClient.HttpClientName);
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler;

        handler.ShouldBeOfType<SocketsHttpHandler>().AllowAutoRedirect.ShouldBeFalse();
    }

    private sealed class GatedHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public void Release() => _gate.SetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            await _gate.Task;
            return FakeVoiceHandler.Json("en-GB-SoniaNeural");
        }
    }

    private sealed class ThrowingCache : Microsoft.Extensions.Caching.Memory.IMemoryCache
    {
        public bool TryGetValue(object key, out object? value)
        {
            value = null;
            return false;
        }

        public Microsoft.Extensions.Caching.Memory.ICacheEntry CreateEntry(object key) =>
            throw new InvalidOperationException("cache is broken");

        public void Remove(object key)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class Site : IDisposable
    {
        private readonly string _keys = TempFolder();
        private readonly ServiceProvider _provider;

        public Site(
            HttpMessageHandler handler,
            Dictionary<string, string?>? values = null,
            Action<IServiceCollection>? configure = null,
            bool fakeHttp = true)
        {
            Provider = new CapturingProvider();

            values ??= new Dictionary<string, string?>();
            values[$"{ReadAloudOptions.SectionName}:KeysPath"] = _keys;

            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Provider));
            services.AddSingleton<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>(new ReadAloudComposerTests.StubEnvironment());
            services.AddSingleton<ICredentialRecords>(new MemoryRecords());

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var builder = new ReadAloudComposerTests.StubUmbracoBuilder(services, configuration);

            new ReadAloudComposer().Compose(builder);
            new AzureSpeechComposer().Compose(builder);

            if (fakeHttp) services.AddSingleton<IHttpClientFactory>(new FakeHttpFactory(handler));
            configure?.Invoke(services);

            _provider = services.BuildServiceProvider();
        }

        public CapturingProvider Provider { get; }

        public IReadOnlyList<(LogLevel Level, string Category, string Text)> Lines => [.. Provider.Lines];

        public string AllText => string.Join("\n", Lines.Select(l => l.Text));

        public List<string> Warnings() =>
            Lines.Where(l => l.Level == LogLevel.Warning && l.Category.EndsWith(nameof(AzureVoiceList)))
                .Select(l => l.Text).ToList();

        public IServiceProvider Services => _provider;

        public IReadAloudAzureAdmin Admin => _provider.GetRequiredService<IReadAloudAzureAdmin>();

        public void Dispose()
        {
            _provider.Dispose();
            try { Directory.Delete(_keys, recursive: true); } catch (IOException) { }
        }
    }
}
