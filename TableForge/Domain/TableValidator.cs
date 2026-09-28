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
/// <param name="IsAuthoredExtension">
/// For <see cref="ValidationKind.BelowMinimum"/> and <see cref="ValidationKind.AboveMaximum"/> only: the result set covers every
/// value its dice can roll, so a row beyond them is taken as authored for modified rolls (-10-0 or 26+ on a d20) rather than as a
/// mistake. It is still reported, as information.
/// </param>
/// <remarks><see cref="Start"/> and <see cref="End"/> may be <see cref="RangeBounds"/> sentinels; show them with <see cref="RangeBounds.Label"/>.</remarks>
public sealed record ValidationFinding(
    int ResultSetIndex, ValidationKind Kind, int Start, int End, IReadOnlyList<int> EntryIndexes, bool IsAuthoredExtension = false);

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
        var first = findings.Count;

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

        // A set that covers every value its dice can roll, and has rows beyond them, was authored for modified rolls: its
        // out-of-range rows are information, and its gaps are looked for across everything its rows reach, not only the dice.
        var extends = entries.Any(e => e.Min < legalMin || e.Max > legalMax);
        var naturalGap = findings.Skip(first).Any(f => f.Kind == ValidationKind.Gap);
        if (!extends || naturalGap) return;

        for (var k = first; k < findings.Count; k++)
            if (findings[k].Kind is ValidationKind.BelowMinimum or ValidationKind.AboveMaximum)
                findings[k] = findings[k] with { IsAuthoredExtension = true };
        AddAuthoredGaps(setIndex, entries, legalMin, legalMax, findings);
    }

    /// <summary>
    /// Gaps outside the dice's range, within the authored domain: from the lowest to the highest finite number any row names (or
    /// the dice's own bounds, if wider). Open-ended rows reach the domain's edge. Works on clipped intervals, never value by value,
    /// so an open bound is never scanned toward. Only called when the dice's own range has no gap, so every gap found lies outside it.
    /// </summary>
    private static void AddAuthoredGaps(int setIndex, List<TableEntry> entries, int legalMin, int legalMax, List<ValidationFinding> findings)
    {
        var finite = entries.SelectMany(e => new[] { e.Min, e.Max }).Where(v => !RangeBounds.IsOpen(v)).ToList();
        long low = Math.Min(legalMin, finite.DefaultIfEmpty(legalMin).Min());
        long high = Math.Max(legalMax, finite.DefaultIfEmpty(legalMax).Max());

        var covered = entries
            .Select(e => (Min: Math.Max(e.Min, low), Max: Math.Min(e.Max, high)))
            .Where(r => r.Min <= r.Max)
            .OrderBy(r => r.Min)
            .ToList();

        var next = low; // lowest value not yet known to be covered
        foreach (var (min, max) in covered)
        {
            if (min > next) AddOutside(next, min - 1);
            next = Math.Max(next, max + 1);
        }
        if (next <= high) AddOutside(next, high);

        void AddOutside(long start, long end)
        {
            if (start < legalMin) findings.Add(new(setIndex, ValidationKind.Gap, (int)start, (int)Math.Min(end, legalMin - 1L), []));
            if (end > legalMax) findings.Add(new(setIndex, ValidationKind.Gap, (int)Math.Max(start, legalMax + 1L), (int)end, []));
        }
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
            for (var v = Math.Max(start, dice.Min); v <= Math.Min(end, dice.Max); v++) // only possible values count, and the loop stays finite
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
