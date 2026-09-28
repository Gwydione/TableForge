using TableForge.Domain;

namespace TableForge.Import;

/// <summary>An editable interpretation of pasted text, or of a saved table being edited. Nothing is saved until it is built into a table.</summary>
public sealed class TableImportDraft
{
    /// <summary>Zero for a new table; otherwise the saved table this draft will replace.</summary>
    public long TableId { get; set; }

    public string SourceText { get; set; } = "";
    public string TableName { get; set; } = "";

    /// <summary>Dice as text so a bad or unsupported expression can be shown and corrected.</summary>
    public string DiceText { get; set; } = "";

    /// <summary>Zero or one folder in the table's collection. Null means Unfiled.</summary>
    public long? FolderId { get; set; }

    /// <summary>
    /// What the user asked for (<see cref="RollableTable.ClampResultsToRange"/>). It is kept through edits that make clamping
    /// unavailable for a while, but a built table only ever gets it where <see cref="TableClamp.TryGetRange"/> finds a range.
    /// </summary>
    public bool ClampResultsToRange { get; set; }

    public List<ResultSetDraft> ResultSets { get; set; } = [];
    public List<ParseIssue> Issues { get; set; } = [];

    /// <summary>Starts an edit of a saved table. Range text keeps the stored display form (for example "96–00").</summary>
    public static TableImportDraft FromTable(RollableTable table) => new()
    {
        TableId = table.Id,
        TableName = table.Name,
        DiceText = table.Dice.ToString(),
        FolderId = table.FolderId,
        ClampResultsToRange = table.ClampResultsToRange,
        ResultSets = table.ResultSets.Select(s => new ResultSetDraft
        {
            Name = s.Name,
            Entries = s.Entries.Select(e => new EntryDraft
            {
                RangeText = e.DisplayRange ?? (e.IsOpenBelow || e.IsOpenAbove ? RangeBounds.Label(e.Min, e.Max)
                    : e.Min == e.Max ? $"{e.Min}" : $"{e.Min}-{e.Max}"),
                Text = e.Text,
                LinkedTableId = e.LinkedTableId,
                UnresolvedLinkName = e.UnresolvedLinkName,
            }).ToList(),
        }).ToList(),
    };

    /// <summary>
    /// Builds a table from the current draft values. Returns false, with every reason, if the draft
    /// cannot form a valid table (this is about structure, not gaps or overlaps).
    /// </summary>
    public bool TryBuildTable(long collectionId, out RollableTable? table, out List<string> errors)
    {
        table = null;
        errors = [];

        if (string.IsNullOrWhiteSpace(TableName)) errors.Add("Table name is required.");

        var haveDice = DiceExpression.TryParse(DiceText, out var dice);
        if (!haveDice) errors.Add($"Dice expression '{DiceText}' is not supported. Use a dice expression such as d20, 2d6, or 2d6+1.{DiceExpression.UnsupportedHint(DiceText)}");

        if (ResultSets.Count == 0) errors.Add("There are no result sets to save.");

        var built = new List<ResultSet>();
        for (var s = 0; s < ResultSets.Count; s++)
        {
            var draft = ResultSets[s];
            var label = string.IsNullOrWhiteSpace(draft.Name) ? $"{s + 1}" : $"'{draft.Name.Trim()}'";
            var where = ResultSets.Count > 1 ? $" in result set {label}" : "";
            var set = new ResultSet { Name = draft.Name.Trim(), SortOrder = s };

            if (draft.Entries.Count == 0) errors.Add($"Result set {label} has no entries.");

            for (var i = 0; i < draft.Entries.Count; i++)
            {
                var entry = draft.Entries[i];
                if (!entry.TryParseRange(haveDice ? dice : null, out var range, out var error))
                {
                    errors.Add($"Row {i + 1}{where}: {error}");
                    continue;
                }

                var unresolved = entry.LinkedTableId is null ? entry.UnresolvedLinkName?.Trim() : null;
                if (unresolved is { Length: 0 })
                {
                    errors.Add($"Row {i + 1}{where}: enter the name of the unresolved link, or choose no link.");
                    continue;
                }

                set.Entries.Add(new TableEntry
                {
                    Min = range.Min,
                    Max = range.Max,
                    DisplayRange = range.DisplayRange,
                    Text = entry.Text.Trim(),
                    LinkedTableId = entry.LinkedTableId,
                    UnresolvedLinkName = unresolved,
                    SortOrder = i,
                });
            }
            built.Add(set);
        }

        if (errors.Count > 0) return false;

        table = new RollableTable
        {
            Id = TableId,
            CollectionId = collectionId,
            Name = TableName.Trim(),
            Dice = dice,
            FolderId = FolderId,
            ResultSets = built,
        };
        // A table that cannot be clamped (d66, result sets with different ranges) is saved with Clamp off, never a dormant "on".
        table.ClampResultsToRange = ClampResultsToRange && TableClamp.TryGetRange(table, out _, out _);
        return true;
    }
}

public sealed class ResultSetDraft
{
    public string Name { get; set; } = "";
    public List<EntryDraft> Entries { get; set; } = [];
}

public sealed class EntryDraft
{
    /// <summary>Range as written, e.g. "9-10" or "96-00". Numeric values are derived from it.</summary>
    public string RangeText { get; set; } = "";
    public string Text { get; set; } = "";

    /// <summary>Resolved link to another table (stable id). At most one of the two link fields is meaningful.</summary>
    public long? LinkedTableId { get; set; }

    /// <summary>Intended destination name for a link with no table yet. Empty (not null) means "unresolved, name not yet entered".</summary>
    public string? UnresolvedLinkName { get; set; }

    /// <summary>One-based lines of the original source this entry came from; zero when the entry was not parsed from text.</summary>
    public int SourceLineStart { get; set; }
    public int SourceLineEnd { get; set; }

    public bool TryParseRange(DiceExpression? dice, out ParsedRange range, out string? error) =>
        Import.RangeText.TryParse(RangeText, dice, out range, out error);
}
