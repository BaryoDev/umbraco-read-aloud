using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;
using Umbraco.Extensions;

namespace TestSite;

/// <summary>
/// Seeds the public demo with a published article on first boot, when configuration asks for it.
/// </summary>
/// <remarks>
/// Off unless <c>Demo:SeedContent</c> is set, which only the deployed container sets. The test
/// fixture boots this same host and publishes its own content under its own aliases, and a seeder
/// that ran there would either collide with it or quietly change what the tests are counting.
///
/// It exists at all because the sibling PWA demo shipped with nothing published, and an empty site
/// is exactly what hid a bug in it for days. This package reads a property off a published node,
/// so a demo with nothing published would demonstrate nothing at all.
/// </remarks>
public class DemoContentComposer : IComposer
{
    /// <inheritdoc />
    public void Compose(IUmbracoBuilder builder)
    {
        if (!builder.Config.GetValue<bool>("Demo:SeedContent"))
        {
            return;
        }

        builder.AddNotificationAsyncHandler<UmbracoApplicationStartedNotification, DemoContentSeeder>();
    }
}

/// <summary>
/// Creates the demo's document type, template and article, and publishes it.
/// </summary>
/// <remarks>
/// Through Umbraco's own services rather than by writing rows, because the endpoint reads the
/// published cache and only a real publish populates it.
///
/// Idempotent by the document type's existence: the database lives on a named volume that outlives
/// the container, so this runs again on every release and must not stack up duplicate articles.
/// </remarks>
public class DemoContentSeeder : INotificationAsyncHandler<UmbracoApplicationStartedNotification>
{
    private const string DocumentTypeAlias = "readAloudArticle";
    private const string TemplateAlias = "readAloudArticle";
    private const string PropertyAlias = "bodyText";
    private const string ArticleName = "How this page reads itself aloud";

    private readonly IServiceProvider _services;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DemoContentSeeder> _logger;

    /// <summary>Creates the seeder.</summary>
    public DemoContentSeeder(
        IServiceProvider services,
        IWebHostEnvironment environment,
        ILogger<DemoContentSeeder> logger)
    {
        _services = services;
        _environment = environment;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task HandleAsync(
        UmbracoApplicationStartedNotification notification,
        CancellationToken cancellationToken)
    {
        try
        {
            await SeedAsync();
        }
        catch (Exception ex)
        {
            // A demo that cannot seed is worth a loud log and a site that still boots, so the
            // backoffice stays reachable and the reason is diagnosable from outside.
            _logger.LogError(ex, "The read-aloud demo could not seed its content.");
        }
    }

    private async Task SeedAsync()
    {
        using var scope = _services.CreateScope();
        var services = scope.ServiceProvider;

        var contentTypeService = services.GetRequiredService<IContentTypeService>();

        var existing = contentTypeService.Get(DocumentTypeAlias);
        if (existing is not null)
        {
            RefreshArticleBody(services);
            return;
        }

        var shortStringHelper = services.GetRequiredService<IShortStringHelper>();
        var dataTypeService = services.GetRequiredService<IDataTypeService>();
        var templateService = services.GetRequiredService<ITemplateService>();
        var contentService = services.GetRequiredService<IContentService>();

        var template = await CreateTemplateAsync(templateService);

        var textarea = await dataTypeService.GetAsync(Constants.DataTypes.Guids.TextareaGuid)
            ?? throw new InvalidOperationException("The built-in Textarea data type is missing.");

        var documentType = new ContentType(shortStringHelper, -1)
        {
            Alias = DocumentTypeAlias,
            Name = "Read Aloud Article",
            Icon = "icon-microphone",
            AllowedAsRoot = true,
        };
        documentType.AddPropertyType(
            new PropertyType(shortStringHelper, textarea, PropertyAlias) { Name = "Body Text" });

        // Without an allowed template Umbraco has nothing to render the node with and the front
        // end answers 404, which is the state the sibling PWA demo shipped in.
        documentType.AllowedTemplates = [template];
        documentType.SetDefaultTemplate(template);

        var created = await contentTypeService.CreateAsync(documentType, Constants.Security.SuperUserKey);
        if (!created.Success)
        {
            throw new InvalidOperationException($"Could not create the document type: {created.Result}.");
        }

        var article = contentService.Create(ArticleName, Constants.System.Root, DocumentTypeAlias);
        article.SetValue(PropertyAlias, DemoArticle.BodyHtml);
        article.TemplateId = template.Id;

        var saved = contentService.Save(article);
        if (!saved.Success)
        {
            throw new InvalidOperationException($"Could not save the demo article: {saved.Result}.");
        }

        var published = contentService.Publish(article, ["*"]);
        if (!published.Success)
        {
            throw new InvalidOperationException($"Could not publish the demo article: {published.Result}.");
        }

        // Logged because it is the one value a visitor needs to call the endpoints by hand, and
        // reading it out of the container's log is faster than clicking through the backoffice.
        _logger.LogInformation(
            "The read-aloud demo published {Name} with key {Key}.",
            ArticleName,
            article.Key);
    }

    /// <summary>
    /// Republishes the article when the body in the image no longer matches the body in the
    /// database.
    /// </summary>
    /// <remarks>
    /// Without this, the demo's prose is code in the repository but data on the server, and the two
    /// drift apart silently. That is not hypothetical: a release that removed a factually wrong
    /// claim from <see cref="DemoArticle.BodyHtml"/> deployed cleanly, reported success, and left
    /// the wrong text serving to the public, because the volume carrying the database survives a
    /// release by design and the seeder above returns as soon as it sees its own document type.
    ///
    /// Compared rather than written unconditionally, so an ordinary release does not create a new
    /// version in Umbraco every time the site restarts. Only the body is reconciled: a demo whose
    /// name or template moved is a bigger change than this should make on its own.
    /// </remarks>
    private void RefreshArticleBody(IServiceProvider services)
    {
        var contentService = services.GetRequiredService<IContentService>();

        var matches = FindArticles(contentService);
        if (matches.Count != 1)
        {
            // Refusing to guess. Zero means the volume carries the document type but not the
            // article, which a re-seed fixes and this cannot. More than one means something else
            // created articles here, and picking one arbitrarily would edit whichever the database
            // happened to return first.
            _logger.LogWarning(
                "Expected exactly one demo article named {Name}, found {Count}. Leaving the content "
                + "alone. Clear the volume to seed from scratch.",
                ArticleName,
                matches.Count);
            return;
        }

        var article = matches[0];

        // Both versions, because they can disagree. GetValue reads the draft; a publish that failed
        // last boot leaves the new body in the draft and the old body live, and comparing only the
        // draft would call that current and never retry. That is the same silent divergence this
        // method exists to end, one version deeper.
        var draftIsCurrent = string.Equals(
            article.GetValue<string>(PropertyAlias), DemoArticle.BodyHtml, StringComparison.Ordinal);
        var publishedIsCurrent = string.Equals(
            article.GetValue<string>(PropertyAlias, published: true), DemoArticle.BodyHtml, StringComparison.Ordinal);

        if (draftIsCurrent && publishedIsCurrent)
        {
            _logger.LogInformation("The read-aloud demo is already seeded and its body is current.");
            return;
        }

        if (!draftIsCurrent)
        {
            article.SetValue(PropertyAlias, DemoArticle.BodyHtml);

            var saved = contentService.Save(article);
            if (!saved.Success)
            {
                throw new InvalidOperationException($"Could not save the refreshed demo article: {saved.Result}.");
            }
        }

        var published = contentService.Publish(article, ["*"]);
        if (!published.Success)
        {
            throw new InvalidOperationException($"Could not publish the refreshed demo article: {published.Result}.");
        }

        // The audio cache is keyed on the text, so the changed body simply misses and synthesizes
        // again. Nothing has to be evicted, and the recordings for the old text age out with it.
        _logger.LogInformation(
            "The read-aloud demo republished {Name} ({Key}) because its body had changed.",
            ArticleName,
            article.Key);
    }

    /// <summary>
    /// Every root article matching both this demo's document type and its name.
    /// </summary>
    /// <remarks>
    /// Pages through rather than reading the first hundred children, and matches on the name as
    /// well as the alias, so the caller can insist on exactly one. A single unpaged
    /// <c>FirstOrDefault</c> on the alias alone would silently edit an arbitrary article if the
    /// root ever held more than one, and would miss this one entirely on a site whose root has more
    /// than a hundred children.
    /// </remarks>
    private static List<IContent> FindArticles(IContentService contentService)
    {
        const int pageSize = 100;

        var matches = new List<IContent>();
        var page = 0;
        long total;

        do
        {
            var children = contentService.GetPagedChildren(Constants.System.Root, page, pageSize, out total);

            matches.AddRange(children.Where(x =>
                x.ContentType.Alias == DocumentTypeAlias
                && string.Equals(x.Name, ArticleName, StringComparison.Ordinal)));

            page++;
        }
        while ((long)page * pageSize < total);

        return matches;
    }

    /// <summary>
    /// Registers the template that renders the article, reusing one from an earlier boot if the
    /// volume already carries it.
    /// </summary>
    /// <remarks>
    /// The view itself ships in the image and is compiled into it, because the published output
    /// carries no Razor runtime compiler: Umbraco only pulls
    /// <c>Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation</c> in through its development mode
    /// package. A template created with content alone would write a .cshtml nobody ever compiles
    /// and render nothing. The content is read back off disk rather than duplicated here, so the
    /// backoffice shows the same file the site actually runs.
    /// </remarks>
    private async Task<ITemplate> CreateTemplateAsync(ITemplateService templateService)
    {
        var existing = await templateService.GetAsync(TemplateAlias);
        if (existing is not null)
        {
            return existing;
        }

        var viewPath = Path.Combine(_environment.ContentRootPath, "Views", $"{TemplateAlias}.cshtml");
        // Qualified because Umbraco.Cms.Core.Models has a File of its own and the using above
        // makes the bare name ambiguous.
        var content = System.IO.File.Exists(viewPath)
            ? await System.IO.File.ReadAllTextAsync(viewPath)
            : string.Empty;

        var created = await templateService.CreateAsync(
            "Read Aloud Article",
            TemplateAlias,
            content,
            Constants.Security.SuperUserKey);

        if (!created.Success || created.Result is null)
        {
            throw new InvalidOperationException($"Could not create the template: {created.Status}.");
        }

        return created.Result;
    }
}
