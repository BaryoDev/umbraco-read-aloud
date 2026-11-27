namespace BaryoDev.Umbraco.ReadAloud.Listens;

/// <summary>Where counts are written. A seam so the flush service can be tested without a database.</summary>
internal interface IListenStore
{
    /// <summary>Adds <paramref name="count"/> to the row for the key, creating it when it is missing.</summary>
    void Add(ListenKey key, int count);
}
