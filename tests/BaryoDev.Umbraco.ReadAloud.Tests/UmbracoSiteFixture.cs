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

    /// <summary>Stand-in audio, seeded into the cache so no test ever calls out to a synthesis service.</summary>
    public static readonly byte[] SeededAudio = [0x49, 0x44, 0x33, 0x04, 0x00, 0x66, 0x69, 0x78];

    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), $"readaloud-tests-{Guid.NewGuid():N}");

    public HttpClient Client { get; private set; } = default!;

    /// <summary>The key of the published node, which is what a client puts in the URL.</summary>
    public Guid PublishedNodeKey { get; private set; }

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

        builder.UseEnvironment("Development");
    }

    public async Task InitializeAsync()
    {
        Client = CreateClient();

        // Force the host to build and the unattended install to finish before anything asks
        // Umbraco for a service. The status does not matter: the site has no template, so the
        // front end is entitled to answer this with a 404.
        using var response = await Client.GetAsync("/");
        _ = response.StatusCode;

        PublishedNodeKey = await PublishNodeAsync();
        await SeedAudioAsync();
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
    /// Creates the document type and publishes one node through Umbraco's own services.
    /// </summary>
    /// <remarks>
    /// Through the services rather than by writing rows, because the published cache is what the
    /// endpoint reads and only a real publish populates it.
    /// </remarks>
    private async Task<Guid> PublishNodeAsync()
    {
        using var scope = Services.CreateScope();
        var services = scope.ServiceProvider;

        var shortStringHelper = services.GetRequiredService<IShortStringHelper>();
        var dataTypeService = services.GetRequiredService<IDataTypeService>();
        var contentTypeService = services.GetRequiredService<IContentTypeService>();
        var contentService = services.GetRequiredService<IContentService>();

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

        var node = contentService.Create("Read Aloud Page", Constants.System.Root, DocumentTypeAlias);
        node.SetValue(PropertyAlias, BodyHtml);

        var saved = contentService.Save(node);
        if (!saved.Success)
        {
            throw new InvalidOperationException($"Could not save the node: {saved.Result}.");
        }

        var published = contentService.Publish(node, ["*"]);
        if (!published.Success)
        {
            throw new InvalidOperationException($"Could not publish the node: {published.Result}.");
        }

        return node.Key;
    }

    /// <summary>
    /// Writes the audio the endpoint will look for into the real cache, under the key the
    /// controller will compute.
    /// </summary>
    /// <remarks>
    /// This is what keeps the suite off the network. It also makes the cache key part of the
    /// contract under test: if the controller builds a different request than the one seeded here,
    /// the lookup misses and the test fails rather than quietly reaching out to Microsoft.
    /// </remarks>
    private async Task SeedAudioAsync()
    {
        var options = Resolve<IOptionsMonitor<ReadAloudOptions>>().CurrentValue;

        var request = new SynthesisRequest
        {
            Text = TextExtractor.ToSpeakableText(BodyHtml, options.MaxChars),
            Voice = options.DefaultVoice,
        };

        var result = new SynthesisResult(
            SeededAudio,
            [new WordBoundary("quick", 100, 200)],
            "audio/mpeg");

        await Resolve<IAudioCache>().SetAsync(request.CacheKey(), result);
    }
}

[CollectionDefinition(Name)]
public class UmbracoCollection : ICollectionFixture<UmbracoSiteFixture>
{
    public const string Name = "umbraco";
}
