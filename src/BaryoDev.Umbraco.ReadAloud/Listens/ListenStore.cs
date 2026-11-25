using System.Data.Common;
using Umbraco.Cms.Infrastructure.Scoping;

namespace BaryoDev.Umbraco.ReadAloud.Listens;

/// <summary>Writes counts to <see cref="ListenRow.TableName"/>, one statement per row.</summary>
/// <remarks>
/// An update first, since nearly every write is to a row that exists. When it touches nothing, an
/// insert in a fresh scope. When the insert throws a <see cref="DbException"/>, another server won
/// the race to create the row, so the update runs again in a fresh scope. No provider error codes
/// are read, because they differ between SQLite and SQL Server.
/// </remarks>
internal sealed class ListenStore(IScopeProvider scopes) : IListenStore
{
    private const string UpdateSql =
        "UPDATE baryoReadAloudListens SET Listens = Listens + @0 WHERE NodeKey = @1 AND Voice = @2 AND DayUtc = @3";

    private const string InsertSql =
        "INSERT INTO baryoReadAloudListens (NodeKey, Voice, DayUtc, Listens) VALUES (@0, @1, @2, @3)";

    /// <summary>Runs just before the insert, so a test can make another server win the race.</summary>
    internal Action<ListenKey>? BeforeInsert { get; set; }

    public void Add(ListenKey key, int count)
    {
        if (Update(key, count)) return;

        try
        {
            BeforeInsert?.Invoke(key);
            Insert(key, count);
            return;
        }
        catch (DbException)
        {
        }

        if (!Update(key, count))
        {
            throw new InvalidOperationException(
                "The listens row could not be inserted and was not there to update.");
        }
    }

    private bool Update(ListenKey key, int count)
    {
        using var scope = scopes.CreateScope();
        var rows = scope.Database.Execute(UpdateSql, count, key.NodeKey, key.Voice, key.DayUtc);
        scope.Complete();
        return rows > 0;
    }

    private void Insert(ListenKey key, int count)
    {
        using var scope = scopes.CreateScope();
        scope.Database.Execute(InsertSql, key.NodeKey, key.Voice, key.DayUtc, count);
        scope.Complete();
    }
}
