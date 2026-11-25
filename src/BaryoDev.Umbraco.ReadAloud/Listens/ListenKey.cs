namespace BaryoDev.Umbraco.ReadAloud.Listens;

/// <summary>The identity of one counter: article, voice and UTC day.</summary>
internal readonly record struct ListenKey(Guid NodeKey, string Voice, int DayUtc)
{
    /// <summary>The UTC day as <c>yyyymmdd</c>.</summary>
    internal static int DayOf(DateTimeOffset utc) => utc.Year * 10000 + utc.Month * 100 + utc.Day;
}
