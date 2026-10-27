namespace TableForge.Domain;

/// <summary>
/// Unique Results (rolling without replacement): which values a table can still accept, given which of its Unique rows
/// (<see cref="TableEntry.IsUnique"/>) have been used. Pure rules: nothing here rolls, stores or changes a table, and nothing
/// here ever changes the dice. A table is always rolled with its own expression; a value that lands on a used Unique row is
/// rejected and the same expression is rolled again, so every value still available keeps exactly its original weight.
/// <list type="bullet">
/// <item>A value is rejected when ANY row it is looked up against, in ANY result set, is a used Unique row — including one of
/// several overlapping rows. One roll is one outcome for every result set, so it is accepted or rejected as a whole.</item>
/// <item>A value no row covers (a gap) is never rejected: it is the No Match it always was.</item>
/// <item>A used row that is no longer Unique never rejects anything, and a used state only refers to a row by its
/// <see cref="TableEntry.RowKey"/>, never its text or range.</item>
/// </list>
/// A table with no Unique rows behaves exactly as it always did: nothing is ever rejected and it is never exhausted.
/// </summary>
public static class UniqueResults
{
    public const string ExhaustedMessage = "All results used — Reset to roll again.";
    public const string AlreadyUsedMessage = "Result already used — choose another result.";

    /// <summary>"Rerolled 2×" for a result that took rerolls past used Unique results; empty for one accepted on its first throw.</summary>
    public static string RerollNote(int rerolledCount) => rerolledCount > 0 ? $"Rerolled {rerolledCount}×" : "";

    /// <summary>Whether any row of any result set is a Unique row.</summary>
    public static bool HasUniqueRows(RollableTable table) => table.ResultSets.Any(s => s.Entries.Any(e => e.IsUnique));

    /// <summary>
    /// True when <paramref name="lookupValue"/> (the value the table is looked up with: after any situational modifier and Clamp,
    /// or a manual entry as typed) lands on a used Unique row in any result set, so it cannot be this table's result.
    /// </summary>
    public static bool IsRejected(RollableTable table, int lookupValue, IReadOnlySet<Guid> usedRowKeys) =>
        usedRowKeys.Count > 0 && table.ResultSets.Any(s => s.Entries.Any(e => IsUsedUnique(e, usedRowKeys) && e.Covers(lookupValue)));

    /// <summary>
    /// The Unique rows an accepted <paramref name="lookupValue"/> uses up: every Unique row it is looked up against, in every result
    /// set (each of several overlapping rows included). Repeatable rows are never in it. Empty for a gap. A row not yet saved has
    /// no key to record, so it is never in it either (see <see cref="IsRejected"/>).
    /// </summary>
    public static IReadOnlyList<Guid> RowsUsedBy(RollableTable table, int lookupValue) =>
        table.ResultSets.SelectMany(s => s.Entries).Where(e => e.IsUnique && e.RowKey != Guid.Empty && e.Covers(lookupValue))
            .Select(e => e.RowKey).Distinct().ToList();

    /// <summary>
    /// True when no roll of the table's own dice, with <paramref name="situational"/> added and Clamp to Range applied as a roll
    /// would apply it (see <see cref="TableClamp.LookupValue"/>), could be accepted: every value the dice can reach lands on a used
    /// Unique row. Checked before anything is rolled. A value that reaches a repeatable row, an unused Unique row or a gap means the
    /// table is not exhausted. For a d66 only its 36 legal results count, never the impossible numbers between them, and it takes
    /// no situational modifier.
    /// </summary>
    public static bool IsExhausted(RollableTable table, IReadOnlySet<Guid> usedRowKeys, int situational = 0)
    {
        var dice = table.Dice;
        if (dice.IsD66 && situational != 0) throw new ArgumentException("A d66 takes no situational modifier.", nameof(situational));

        var blocked = table.ResultSets.SelectMany(s => s.Entries).Where(e => IsUsedUnique(e, usedRowKeys))
            .Select(e => (Min: (long)e.Min, Max: (long)e.Max)).OrderBy(r => r.Min).ToList();
        if (blocked.Count == 0) return false;

        if (dice.IsD66)
        {
            for (int? v = dice.Min; v is { } value; v = dice.NextLegal(value))
                if (!blocked.Any(r => value >= r.Min && value <= r.Max)) return false;
            return true;
        }

        // Every number from Min to Max is a possible total of ordinary dice, so the values a roll can be looked up with form one
        // unbroken run: Clamp never moves a value past another, it only gathers the ends onto the table's first and last value.
        long low = TableClamp.LookupValue(table, dice.Min + situational);
        long high = TableClamp.LookupValue(table, dice.Max + situational);
        var next = low; // the lowest reachable value not yet known to be rejected
        foreach (var (min, max) in blocked)
        {
            if (min > next) break; // next is reachable and no used row covers it
            if (max >= next) next = max + 1;
            if (next > high) return true;
        }
        return next > high;
    }

    private static bool IsUsedUnique(TableEntry entry, IReadOnlySet<Guid> usedRowKeys) =>
        entry.IsUnique && entry.RowKey != Guid.Empty && usedRowKeys.Contains(entry.RowKey);
}
