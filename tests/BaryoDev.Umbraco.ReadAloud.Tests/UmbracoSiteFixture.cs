using System.Globalization;
using BaryoDev.Umbraco.ReadAloud.Caching;
using BaryoDev.Umbraco.ReadAloud.Content;
using BaryoDev.Umbraco.ReadAloud.Engine;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

/// <summary>
/// Boots a real Umbraco against a throwaway SQLite database, once, for the whole suite, and
/// publishes one node for the endpoint to read.
/// </summary>
/// <remarks>
/// Deliberately not a mocked host. The things most likely to break here are the parts only a real
/// boot exercises: whether the composer's registrations resolve, whether the route is reachable at
/// the URL the client calls, and whether a published node's property can be read from a plain
/// attribute-routed controller. A test double would pass with all three broken.
///
/// The content is real too. An empty site can only ever prove the not-found paths, and a package
/// whose whole job is reading a property off a published node needs a published node to read.
/// </remarks>
public class UmbracoSiteFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>The document type and property the fixture publishes, matching the configured alias.</summary>
    public const string DocumentTypeAlias = "readAloudPage";
    public const string PropertyAlias = "bodyText";

    /// <summary>Markup with a tag and an entity in it, so text extraction is exercised rather than assumed.</summary>
    public const string BodyHtml = "<p>The <strong>quick</strong> brown fox &amp; the lazy dog.</p>";

    /// <summary>The body of the member-protected node, which must never be spoken to anyone.</summary>
    public const string ProtectedBodyHtml = "<p>The board votes on Thursday.</p>";

    /// <summary>Stand-in audio, seeded into the cache so no test ever calls out to a synthesis service.</summary>
    public static readonly byte[] SeededAudio = [0x49, 0x44, 0x33, 0x04, 0x00, 0x66, 0x69, 0x78];

    /// <summary>The protected node's stand-in audio, distinct so a leak is identifiable in the failure.</summary>
    public static readonly byte[] SeededProtectedAudio = [0x49, 0x44, 0x33, 0x04, 0x00, 0x73, 0x65, 0x63];

    /// <summary>The window the site under test is configured with.</summary>
    /// <remarks>
    /// The rate limit tests exhaust their own caller addresses, so they do not spend this. What
    /// does spend it is every ordinary test that goes through <see cref="Client"/>, since those
    /// carry no address and share one partition. There are roughly a dozen today, so a dozen or so
    /// of headroom is left. Adding many more endpoint tests means raising this first, or they will
    /// start failing with 429s that look like nothing to do with the test that broke.
    /// </remarks>
    public const int RateLimitPerMinute = 30;

    /// <summary>The word in the seeded timings, which identifies which voice's entry was read.</summary>
    public const string DefaultBoundaryText = "quick";

    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), $"readaloud-tests-{Guid.NewGuid():N}");

    public HttpClient Client { get; private set; } = default!;

    /// <summary>The key of the published node, which is what a client puts in the URL.</summary>
    public Guid PublishedNodeKey { get; private set; }

    /// <summary>The key of a published node that is behind Umbraco public access.</summary>
    public Guid ProtectedNodeKey { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dataDirectory);

        // Each run gets its own database file, so a test never inherits content from the last run.
        var dbPath = Path.Combine(_dataDirectory, "Umbraco.sqlite.db");

        builder.UseSetting(
            "ConnectionStrings:umbracoDbDSN",
            $"Data Source={dbPath};Cache=Shared;Foreign Keys=True;Pooling=True");
        builder.UseSetting("ConnectionStrings:umbracoDbDSN_ProviderName", "Microsoft.Data.Sqlite");

        builder.UseSetting("Umbraco:CMS:Unattended:InstallUnattended", "true");
        builder.UseSetting("Umbraco:CMS:Unattended:UnattendedUserName", "Test Admin");
        builder.UseSetting("Umbraco:CMS:Unattended:UnattendedUserEmail", "test@example.com");
        builder.UseSetting("Umbraco:CMS:Unattended:UnattendedUserPassword", "LocalOnly-ChangeMe-1234!");

        // Pinned rather than inherited, so these tests do not change when the demo config does.
        builder.UseSetting("BaryoDev:ReadAloud:PropertyAlias", PropertyAlias);
        builder.UseSetting("BaryoDev:ReadAloud:CachePath", Path.Combine(_dataDirectory, "cache"));

        // High enough that the ordinary tests never reach it, low enough that the rate limit
        // tests can exhaust a window without sending thousands of requests. Those tests use their
        // own caller addresses, so they consume their own budget rather than this one.
        builder.UseSetting(
            "BaryoDev:ReadAloud:RateLimitPerMinute",
            RateLimitPerMinute.ToString(CultureInfo.InvariantCulture));

        builder.UseEnvironment("Development");

        // The host's MVC serializer is deliberately set to the hostile setting: PascalCase, which
        // is what a site gets by configuring nothing outside ASP.NET's web defaults, and one of the
        // things Umbraco is free to change between majors. The browser client hard-codes
        // lower-camel property names, so a response that inherits the host's policy is a contract
        // this package does not control. Pinned here so the timings test proves the route pins it.
        builder.ConfigureServices(services =>
            services.Configure<MvcJsonOptions>(options =>
                options.JsonSerializerOptions.PropertyNamingPolicy = null));
    }

    public async Task InitializeAsync()
    {
        Client = CreateClient();

        // Force the host to build and the unattended install to finish before anything asks
        // Umbraco for a service. The status does not matter: the site has no template, so the
        // front end is entitled to answer this with a 404.
        using var response = await Client.GetAsync("/");
        _ = response.StatusCode;

        await PublishNodesAsync();

        await SeedAudioAsync(BodyHtml, SeededAudio);

        // The protected node's audio is seeded too, so that the only thing standing between a
        // caller and it is the public access check. A cache miss would refuse the request for the
        // wrong reason and the test would pass with the check deleted.
        await SeedAudioAsync(ProtectedBodyHtml, SeededProtectedAudio);
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch
        {
            // A locked SQLite file on a slow CI agent should not fail an otherwise green run.
        }
    }

    public T Resolve<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>
    /// Creates the document type and publishes both nodes through Umbraco's own services, then
    /// puts the second one behind public access.
    /// </summary>
    /// <remarks>
    /// Through the services rather than by writing rows, because the published cache is what the
    /// endpoint reads and only a real publish populates it. The protected node is published too:
    /// an unpublished node would be refused for the wrong reason and prove nothing about whether
    /// protection is honoured.
    /// </remarks>
    private async Task PublishNodesAsync()
    {
        using var scope = Services.CreateScope();
        var services = scope.ServiceProvider;

        var shortStringHelper = services.GetRequiredService<IShortStringHelper>();
        var dataTypeService = services.GetRequiredService<IDataTypeService>();
        var contentTypeService = services.GetRequiredService<IContentTypeService>();
        var contentService = services.GetRequiredService<IContentService>();
        var publicAccessService = services.GetRequiredService<IPublicAccessService>();

        var textarea = await dataTypeService.GetAsync(Constants.DataTypes.Guids.TextareaGuid)
            ?? throw new InvalidOperationException("The built-in Textarea data type is missing.");

        var documentType = new ContentType(shortStringHelper, -1)
        {
            Alias = DocumentTypeAlias,
            Name = "Read Aloud Page",
            AllowedAsRoot = true,
        };
        documentType.AddPropertyType(
            new PropertyType(shortStringHelper, textarea, PropertyAlias) { Name = "Body Text" });

        var created = await contentTypeService.CreateAsync(documentType, Constants.Security.SuperUserKey);
        if (!created.Success)
        {
            throw new InvalidOperationException($"Could not create the document type: {created.Result}.");
        }

        var node = PublishNode(contentService, "Read Aloud Page", BodyHtml);
        PublishedNodeKey = node.Key;

        var protectedNode = PublishNode(contentService, "Members Only", ProtectedBodyHtml);
        ProtectedNodeKey = protectedNode.Key;

        // The node stands in for its own login and no-access pages. Neither is ever followed here,
        // and pointing at itself avoids two more nodes that would prove nothing.
        var entry = new PublicAccessEntry(protectedNode, protectedNode, protectedNode, []);
        entry.AddRule("read-aloud-members", Constants.Conventions.PublicAccess.MemberRoleRuleType);

        var protectedResult = publicAccessService.Save(entry);
        if (!protectedResult.Success)
        {
            throw new InvalidOperationException($"Could not protect the node: {protectedResult.Result}.");
        }
    }

    private static IContent PublishNode(IContentService contentService, string name, string html)
    {
        var node = contentService.Create(name, Constants.System.Root, DocumentTypeAlias);
        node.SetValue(PropertyAlias, html);

        var saved = contentService.Save(node);
        if (!saved.Success)
        {
            throw new InvalidOperationException($"Could not save {name}: {saved.Result}.");
        }

        var published = contentService.Publish(node, ["*"]);
        if (!published.Success)
        {
            throw new InvalidOperationException($"Could not publish {name}: {published.Result}.");
        }

        return node;
    }

    /// <summary>
    /// Writes audio into the real cache under the key the controller will compute for this text
    /// and voice, so a test can tell which voice the controller chose by the bytes it gets back.
    /// </summary>
    /// <remarks>
    /// This is what keeps the suite off the network. It also makes the cache key part of the
    /// contract under test: if the controller builds a different request than the one seeded here,
    /// the lookup misses and the test fails rather than quietly reaching out to Microsoft.
    /// </remarks>
    public async Task SeedAudioAsync(
        string html,
        byte[] audio,
        string? voice = null,
        string boundaryText = DefaultBoundaryText)
    {
        var options = Resolve<IOptionsMonitor<ReadAloudOptions>>().CurrentValue;

        var request = new SynthesisRequest
        {
            Text = TextExtractor.ToSpeakableText(html, options.MaxChars),
            Voice = voice ?? options.DefaultVoice,
        };

        var result = new SynthesisResult(
            audio,
            [new WordBoundary(boundaryText, 100, 200)],
            "audio/mpeg");

        await Resolve<IAudioCache>().SetAsync(request.CacheKey(), result);
    }
}

[CollectionDefinition(Name)]
public class UmbracoCollection : ICollectionFixture<UmbracoSiteFixture>
{
    public const string Name = "umbraco";
}
