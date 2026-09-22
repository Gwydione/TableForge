namespace TableForge.Domain;

/// <summary>
/// <see cref="ImpossibleValue"/> is a row covering values that lie between the dice's lowest and highest result but that the dice can never
/// produce (17 or 20 on a d66). It is reported as a stretch of such values; it is never reported as missing coverage.
/// </summary>
public enum ValidationKind { Gap, Overlap, BelowMinimum, AboveMaximum, ImpossibleValue }

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
        if (dice.IsD66)
        {
            ValidateDiscreteSet(setIndex, entries, dice, findings);
            return;
        }

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

    /// <summary>
    /// Dice whose legal results are a set, not a range (d66). The same facts are reported, but against <see cref="DiceExpression.IsLegal"/>:
    ///  - below/above the lowest/highest result, as for any dice;
    ///  - <see cref="ValidationKind.ImpossibleValue"/> for values in between that cannot occur (one finding per stretch, so 15-22 reports 17-20);
    ///  - overlaps only where the shared values are possible ones;
    ///  - gaps only for possible results nobody covers, one finding per run of consecutive numbers (a missing 16 and 21 are two findings).
    /// Impossible values are never gaps.
    /// </summary>
    private static void ValidateDiscreteSet(int setIndex, List<TableEntry> entries, DiceExpression dice, List<ValidationFinding> findings)
    {
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.Min < dice.Min)
                findings.Add(new(setIndex, ValidationKind.BelowMinimum, e.Min, Math.Min(e.Max, dice.Min - 1), [i]));
            if (e.Max > dice.Max)
                findings.Add(new(setIndex, ValidationKind.AboveMaximum, Math.Max(e.Min, dice.Max + 1), e.Max, [i]));

            var runStart = int.MinValue;
            for (var v = Math.Max(e.Min, dice.Min); v <= Math.Min(e.Max, dice.Max); v++)
            {
                if (!dice.IsLegal(v))
                {
                    if (runStart == int.MinValue) runStart = v;
                }
                else if (runStart != int.MinValue)
                {
                    findings.Add(new(setIndex, ValidationKind.ImpossibleValue, runStart, v - 1, [i]));
                    runStart = int.MinValue;
                }
            }
            if (runStart != int.MinValue)
                findings.Add(new(setIndex, ValidationKind.ImpossibleValue, runStart, Math.Min(e.Max, dice.Max), [i]));
        }

        for (var i = 0; i < entries.Count; i++)
        for (var j = i + 1; j < entries.Count; j++)
        {
            var start = Math.Max(entries[i].Min, entries[j].Min);
            var end = Math.Min(entries[i].Max, entries[j].Max);
            if (start > end) continue;

            // Report the stretch of shared POSSIBLE values (an overlap only in impossible values is already reported as such).
            int? first = null, last = null;
            for (var v = start; v <= end; v++)
                if (dice.IsLegal(v)) { first ??= v; last = v; }
            if (first is not null) findings.Add(new(setIndex, ValidationKind.Overlap, first.Value, last!.Value, [i, j]));
        }

        // Missing possible results, grouped into runs of consecutive numbers (impossible values are skipped, and end a run).
        int? runFirst = null, runLast = null;
        for (var v = dice.Min; v <= dice.Max; v++)
        {
            if (!dice.IsLegal(v) || entries.Any(e => e.Min <= v && v <= e.Max)) continue;
            if (runLast is not null && v == runLast + 1)
            {
                runLast = v;
                continue;
            }
            if (runFirst is not null) findings.Add(new(setIndex, ValidationKind.Gap, runFirst.Value, runLast!.Value, []));
            runFirst = runLast = v;
        }
        if (runFirst is not null) findings.Add(new(setIndex, ValidationKind.Gap, runFirst.Value, runLast!.Value, []));
    }
}
