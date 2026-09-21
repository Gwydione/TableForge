namespace TableForge.Domain;

public enum ValidationKind { Gap, Overlap, BelowMinimum, AboveMaximum }

/// <param name="ResultSetIndex">Position of the result set within the table.</param>
/// <param name="Start">First affected numeric value.</param>
/// <param name="End">Last affected numeric value.</param>
/// <param name="EntryIndexes">Positions (within the result set) of the entries involved; empty for a gap.</param>
public sealed record ValidationFinding(
    int ResultSetIndex, ValidationKind Kind, int Start, int End, IReadOnlyList<int> EntryIndexes);

public static class TableValidator
{
    /// <summary>
    /// Reports facts about each result set independently against the dice's legal range.
    /// It does not decide whether saving is allowed. Assumes each entry has Min &lt;= Max.
    /// </summary>
    public static IReadOnlyList<ValidationFinding> Validate(RollableTable table)
    {
        var findings = new List<ValidationFinding>();
        for (var s = 0; s < table.ResultSets.Count; s++)
            ValidateSet(s, table.ResultSets[s].Entries, table.Dice, findings);
        return findings;
    }

    private static void ValidateSet(int setIndex, List<TableEntry> entries, DiceExpression dice, List<ValidationFinding> findings)
    {
        var legalMin = dice.Min;
        var legalMax = dice.Max;

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Min < legalMin)
                findings.Add(new(setIndex, ValidationKind.BelowMinimum, e.Min, Math.Min(e.Max, legalMin - 1), [i]));
            if (e.Max > legalMax)
                findings.Add(new(setIndex, ValidationKind.AboveMaximum, Math.Max(e.Min, legalMax + 1), e.Max, [i]));
        }

        for (var i = 0; i < entries.Count; i++)
        for (var j = i + 1; j < entries.Count; j++)
        {
            var start = Math.Max(entries[i].Min, entries[j].Min);
            var end = Math.Min(entries[i].Max, entries[j].Max);
            if (start <= end)
                findings.Add(new(setIndex, ValidationKind.Overlap, start, end, [i, j]));
        }

        // Gaps: uncovered stretches within the legal range, found by sweeping clipped intervals.
        var covered = entries
            .Select(e => (Min: Math.Max(e.Min, legalMin), Max: Math.Min(e.Max, legalMax)))
            .Where(r => r.Min <= r.Max)
            .OrderBy(r => r.Min)
            .ToList();

        var next = legalMin; // lowest value not yet known to be covered
        foreach (var (min, max) in covered)
        {
            if (min > next)
                findings.Add(new(setIndex, ValidationKind.Gap, next, min - 1, []));
            next = Math.Max(next, max + 1);
        }
        if (next <= legalMax)
            findings.Add(new(setIndex, ValidationKind.Gap, next, legalMax, []));
    }
}
