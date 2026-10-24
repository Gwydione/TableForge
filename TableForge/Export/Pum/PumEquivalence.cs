using TableForge.Domain;

namespace TableForge.Export.Pum;

/// <summary>
/// The final word on whether a PUM table rolls like its TableForge source. It compares, for every value, what TableForge shows
/// (<see cref="TableResolver"/> after <see cref="TableClamp.LookupValue"/>, exactly as a provider roll is resolved) with what PUM
/// shows, using PUM's observed lookup (F09/F10):
/// <code>
///     v' = clamp(v, 1, last key)        PUM also displays v' as the roll
///     the row is the first whose key is at least v'
/// </code>
/// Both outcomes only change at a row bound, a key, a clamp bound, a dice bound, 1 or the last key, so one value from each
/// stretch between those points stands for the whole stretch: the check costs as much as the table has rows, never as much as
/// its dice have values. (A d66 has only 36 legal values, which are checked one by one.)
/// </summary>
public static class PumEquivalence
{
    /// <param name="Normal">The first roll of the table's own dice that differs; null when every one matches.</param>
    /// <param name="BelowDice">The difference closest below the dice's range (PUM overrides or the situational modifier only).</param>
    /// <param name="AboveDice">The difference closest above the dice's range (PUM overrides or the situational modifier only).</param>
    public sealed record Result(PumRollMismatch? Normal, PumRollMismatch? BelowDice, PumRollMismatch? AboveDice);

    /// <summary>
    /// Compares <paramref name="table"/> with <paramref name="rows"/> (ascending keys, the first at least 1).
    /// <paramref name="render"/> turns what each result set matched (null for No Match) into the text the row would hold.
    /// </summary>
    public static Result Check(RollableTable table, IReadOnlyList<PumRow> rows, Func<IReadOnlyList<TableEntry?>, string> render)
    {
        if (rows.Count == 0) throw new ArgumentException("A PUM table needs at least one row.", nameof(rows));
        var dice = table.Dice;

        var points = new SortedSet<long> { 1, rows[^1].Key + 1L, dice.Min, dice.Max + 1L };
        foreach (var row in rows) points.Add(row.Key + 1L);
        foreach (var entry in table.ResultSets.SelectMany(s => s.Entries))
        {
            if (!entry.IsOpenBelow) points.Add(entry.Min);
            if (!entry.IsOpenAbove) points.Add(entry.Max + 1L);
        }
        if (table.ClampResultsToRange && TableClamp.TryGetRange(table, out var clampMin, out var clampMax))
        {
            if (!RangeBounds.IsOpen(clampMin)) points.Add(clampMin);
            if (!RangeBounds.IsOpen(clampMax)) points.Add(clampMax + 1L);
        }

        // The stretches: each point starts one that runs to just before the next point; one more runs below the first point.
        var starts = points.ToList();
        var stretches = new List<(long Start, long End)>(starts.Count + 1) { (long.MinValue, starts[0] - 1) };
        for (var i = 0; i < starts.Count; i++) stretches.Add((starts[i], i + 1 < starts.Count ? starts[i + 1] - 1 : long.MaxValue));

        PumRollMismatch? normal = null, below = null, above = null;
        if (dice.IsD66)
        {
            for (var v = dice.Min; v <= dice.Max && normal is null; v++)
                if (dice.IsLegal(v)) normal = Compare(table, rows, render, v);
        }
        else
        {
            foreach (var (start, end) in stretches.Where(s => s.End >= dice.Min && s.Start <= dice.Max))
                if ((normal = Compare(table, rows, render, (int)Math.Max(start, dice.Min))) is not null) break;
        }

        // Outside the dice: the difference nearest the dice on each side, which is the one a modest override or modifier meets.
        foreach (var (_, end) in stretches.Where(s => s.Start < dice.Min).Reverse())
            if ((below = Compare(table, rows, render, (int)Math.Min(end, dice.Min - 1L))) is not null) break;
        foreach (var (start, _) in stretches.Where(s => s.End > dice.Max))
            if ((above = Compare(table, rows, render, (int)Math.Max(start, dice.Max + 1L))) is not null) break;

        return new(normal, below, above);
    }

    /// <summary>The row PUM shows for a value, by its observed lookup.</summary>
    public static PumRow PumLookup(IReadOnlyList<PumRow> rows, long value)
    {
        var lookup = Math.Clamp(value, 1, rows[^1].Key);
        int low = 0, high = rows.Count - 1;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (rows[mid].Key >= lookup) high = mid;
            else low = mid + 1;
        }
        return rows[low];
    }

    /// <summary>The text TableForge shows for a provider roll of <paramref name="value"/>, in the same form as a PUM row.</summary>
    public static string TableForgeShows(RollableTable table, int value, Func<IReadOnlyList<TableEntry?>, string> render)
    {
        var results = TableResolver.Resolve(table, TableClamp.LookupValue(table, value)).Results;
        if (results.Any(r => r.Status == ResolutionStatus.Ambiguous)) return "(ambiguous: two rows cover this roll)";
        return render(results.Select(r => r.Entry).ToList());
    }

    private static PumRollMismatch? Compare(RollableTable table, IReadOnlyList<PumRow> rows, Func<IReadOnlyList<TableEntry?>, string> render, int value)
    {
        var expected = TableForgeShows(table, value, render);
        var actual = PumLookup(rows, value).Text;
        return expected == actual ? null : new PumRollMismatch(value, OneLine(expected), OneLine(actual));
    }

    /// <summary>A text short enough for a report line: lines joined with " / ", "(blank)" for no text, and "…" past 80 characters.</summary>
    internal static string OneLine(string text)
    {
        var line = string.Join(" / ", text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        if (line.Length == 0) return "(blank)";
        return line.Length <= 80 ? line : line[..79].TrimEnd() + "…";
    }
}
