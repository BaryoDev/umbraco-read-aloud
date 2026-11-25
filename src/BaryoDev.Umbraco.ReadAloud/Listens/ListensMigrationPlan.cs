using Umbraco.Cms.Core.Packaging;
using Umbraco.Cms.Infrastructure.Migrations;
using Umbraco.Cms.Infrastructure.Persistence;

namespace BaryoDev.Umbraco.ReadAloud.Listens;

/// <summary>
/// Creates the listens table. Umbraco finds a plan by type scan, so the composer registers nothing
/// for it. With <c>PackageMigrationsUnattended</c> on, which is the default, it runs at startup.
/// </summary>
public sealed class ListensMigrationPlan() : PackageMigrationPlan("BaryoDev.Umbraco.ReadAloud")
{
    /// <inheritdoc />
    protected override void DefinePlan()
    {
        To<CreateListensTable>("6c1e4b0a-2f7d-4d58-9a13-5e8b7c3f9d24");
    }
}

/// <summary>Creates <see cref="ListenRow.TableName"/>, only when it does not exist.</summary>
public sealed class CreateListensTable(IMigrationContext context) : AsyncMigrationBase(context)
{
    /// <inheritdoc />
    protected override Task MigrateAsync()
    {
        if (IsMissing(Database))
        {
            Create.Table<ListenRow>().Do();
        }

        return Task.CompletedTask;
    }

    /// <summary>Whether the table has to be created. The guard that makes a second run harmless.</summary>
    internal static bool IsMissing(IUmbracoDatabase database, string tableName = ListenRow.TableName) =>
        !database.SqlContext.SqlSyntax.DoesTableExist(database, tableName);
}
