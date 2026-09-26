namespace TableForge.Domain;

/// <summary>
/// Clamp to Range: an explicit per-table option (<see cref="RollableTable.ClampResultsToRange"/>) that changes only a
/// provider-driven primary roll whose final number falls below the table's common minimum or above its common maximum.
/// That roll is looked up at the nearest boundary; the calculated number itself is never changed or hidden.
/// It never fills internal gaps, never picks between overlapping rows, never applies to Manual Entry or inline rolls,
/// and is never available for d66.
/// </summary>
public static class TableClamp
{
    public const string D66Message = "Clamp is not available for d66 tables.";
    public const string IncompatibleMessage = "Clamp requires all Result Sets to share the same minimum and maximum.";
    public const string NoRowsMessage = "Clamp needs at least one row with a range.";

    /// <summary>
    /// The table's range for clamping: the lowest and highest values its rows actually cover (never the dice's theoretical
    /// range). Every result set with rows must have the same outer minimum and maximum, so that one roll clamps to one
    /// number for all of them. False, with the reason, for d66, for result sets that disagree, or when there are no rows.
    /// </summary>
    public static bool TryGetRange(RollableTable table, out int min, out int max, out string? unavailableReason)
    {
        min = max = 0;
        unavailableReason = null;
        if (table.Dice.IsD66)
        {
            unavailableReason = D66Message;
            return false;
        }

        var ranges = table.ResultSets
            .Where(s => s.Entries.Count > 0)
            .Select(s => (Min: s.Entries.Min(e => e.Min), Max: s.Entries.Max(e => e.Max)))
            .Distinct()
            .ToList();
        if (ranges.Count == 0)
        {
            unavailableReason = NoRowsMessage;
            return false;
        }
        if (ranges.Count > 1)
        {
            unavailableReason = IncompatibleMessage;
            return false;
        }

        (min, max) = ranges[0];
        return true;
    }

    public static bool TryGetRange(RollableTable table, out int min, out int max) => TryGetRange(table, out min, out max, out _);

    /// <summary>
    /// The value to look <paramref name="finalValue"/> up with. It differs from <paramref name="finalValue"/> only when the table
    /// has Clamp turned on, has a clamp range, and the value lies outside it; a value inside the range (even in a gap) is unchanged.
    /// </summary>
    public static int LookupValue(RollableTable table, int finalValue)
    {
        if (!table.ClampResultsToRange || !TryGetRange(table, out var min, out var max)) return finalValue;
        return finalValue < min ? min : finalValue > max ? max : finalValue;
    }
}
