using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace BaryoDev.Umbraco.ReadAloud.Listens;

/// <summary>
/// One row per article, voice and UTC day. Four columns and nothing else: there is no column
/// that could say who listened, and adding one is a design change, not a schema tweak.
/// </summary>
[TableName(TableName)]
[ExplicitColumns]
public sealed class ListenRow
{
    /// <summary>The table name.</summary>
    public const string TableName = "baryoReadAloudListens";

    /// <summary>The longest voice name the table holds.</summary>
    public const int MaxVoiceLength = 100;

    /// <summary>The key of the published node that was read.</summary>
    [Column("NodeKey")]
    [PrimaryKeyColumn(AutoIncrement = false, Clustered = true, Name = "PK_baryoReadAloudListens", OnColumns = "NodeKey, Voice, DayUtc")]
    public Guid NodeKey { get; set; }

    /// <summary>The voice actually used, after the site's allow-list.</summary>
    [Column("Voice")]
    [Length(MaxVoiceLength)]
    public string Voice { get; set; } = string.Empty;

    /// <summary>The UTC day as <c>yyyymmdd</c>.</summary>
    [Column("DayUtc")]
    public int DayUtc { get; set; }

    /// <summary>How many plays were served.</summary>
    [Column("Listens")]
    public int Listens { get; set; }
}
