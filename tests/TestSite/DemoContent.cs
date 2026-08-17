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
            _logger.LogInformation("The read-aloud demo is already seeded. Nothing to do.");
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
