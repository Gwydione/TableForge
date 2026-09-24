namespace TableForge.Domain;

public sealed class Collection
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }

    /// <summary>What a screen reader or UI Automation reads for this collection in the collection chooser.</summary>
    public override string ToString() => Name;
}

/// <summary>One-level grouping of tables inside a single Collection. Folders never nest and never span collections.</summary>
public sealed class Folder
{
    public long Id { get; set; }
    public long CollectionId { get; set; }
    public string Name { get; set; } = "";

    public override string ToString() => Name;
}

/// <summary>What one roll showed, as text: the data stored for Recent Rolls. It never refers to entries.</summary>
/// <param name="RollValue">The final value the table was resolved with, after any <paramref name="SituationalModifier"/>.</param>
/// <param name="SituationalModifier">The temporary modifier this roll used (see <see cref="Domain.SituationalModifier"/>); 0 for none.</param>
public sealed record RollSnapshot(long? TableId, string TableName, string DiceText, int RollValue, string ResultText, int SituationalModifier = 0);

/// <summary>A stored roll snapshot. It stays readable after its table is renamed, edited or deleted (then <see cref="TableId"/> is null).</summary>
public sealed record RollHistoryItem(long Id, long? TableId, string TableName, string DiceText, int RollValue, string ResultText, DateTime RolledUtc,
    int SituationalModifier = 0)
{
    /// <summary>
    /// The final roll. Unmodified, it reads as the dice show it (numeric 100 on a d100 reads "00"); with a situational
    /// modifier it is a calculated number, not a die face, so it is shown plainly.
    /// </summary>
    public string RollDisplay => SituationalModifier == 0 && DiceExpression.TryParse(DiceText, out var dice) ? dice.FormatValue(RollValue) : RollValue.ToString();

    /// <summary>How a modified roll was reached ("11 +3 situational"); empty for an unmodified one.</summary>
    public string SituationalBreakdown => SituationalModifier == 0 ? ""
        : $"{RollValue - SituationalModifier} {Domain.SituationalModifier.Signed(SituationalModifier)} situational";

    /// <summary>Readable snapshot: table, dice and roll (and how a modifier reached it), then each result set's output.</summary>
    public string FullText => SituationalModifier == 0
        ? $"{TableName}\n{DiceText} → {RollDisplay}\n\n{ResultText}"
        : $"{TableName}\n{DiceText} → {RollDisplay} ({SituationalBreakdown})\n\n{ResultText}";
}

/// <summary>Lightweight row for listing tables without loading their entries. <see cref="FolderName"/> is "Unfiled" when <see cref="FolderId"/> is null.</summary>
public sealed record TableSummary(long Id, string Name, DiceExpression Dice, long? FolderId, string FolderName)
{
    /// <summary>What a screen reader or UI Automation reads for a row in a table list.</summary>
    public override string ToString() => $"{Name} ({Dice})";
}

public sealed class RollableTable
{
    public long Id { get; set; }
    public long CollectionId { get; set; }
    public string Name { get; set; } = "";
    public DiceExpression Dice { get; set; }

    /// <summary>Zero or one folder in the same collection. Null means Unfiled.</summary>
    public long? FolderId { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public List<ResultSet> ResultSets { get; set; } = [];
}

/// <summary>Owns its entries independently; result sets never share rows.</summary>
public sealed class ResultSet
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }
    public List<TableEntry> Entries { get; set; } = [];
}

public sealed class TableEntry
{
    public long Id { get; set; }

    /// <summary>Numeric bounds are what resolution uses. Display strings never are.</summary>
    public int Min { get; set; }
    public int Max { get; set; }
    public string Text { get; set; } = "";

    /// <summary>How the range is shown when it differs from plain numbers, e.g. "96–00" or "08".</summary>
    public string? DisplayRange { get; set; }

    /// <summary>At most one link target. Resolved links use the stable table id.</summary>
    public long? LinkedTableId { get; set; }
    public string? UnresolvedLinkName { get; set; }

    public int SortOrder { get; set; }

    public string RangeLabel => DisplayRange ?? (Min == Max ? $"{Min}" : $"{Min}–{Max}");

    public bool Covers(int value) => value >= Min && value <= Max;
}
