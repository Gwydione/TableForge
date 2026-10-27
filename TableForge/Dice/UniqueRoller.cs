using TableForge.Domain;

namespace TableForge.Dice;

public enum UniqueRollStatus
{
    /// <summary>A roll was accepted: <see cref="UniqueRollResult.Roll"/> is the provider's number.</summary>
    Accepted,

    /// <summary>Nothing was rolled: every value the dice can reach lands on a used Unique row.</summary>
    Exhausted,

    /// <summary>Every allowed attempt landed on a used Unique row. Nothing was accepted, so nothing is used.</summary>
    AttemptLimitReached,

    /// <summary>A throw failed or was cancelled, or Stop was pressed between throws. The caller already knows why.</summary>
    NotRolled,
}

/// <param name="Roll">The provider's number for the accepted roll, before any situational modifier; 0 unless accepted.</param>
/// <param name="RerolledCount">How many throws were rejected before the accepted one (for a limit reached: every throw).</param>
/// <param name="RowsUsed">The Unique rows the accepted roll uses up (see <see cref="UniqueResults.RowsUsedBy"/>); empty otherwise.</param>
public sealed record UniqueRollResult(UniqueRollStatus Status, int Roll = 0, int RerolledCount = 0, IReadOnlyList<Guid>? RowsUsed = null)
{
    public IReadOnlyList<Guid> RowsUsed { get; } = RowsUsed ?? [];
}

/// <summary>
/// Rolling a table that has Unique rows, by rejection sampling: the table's own dice expression is thrown, the situational modifier
/// added and Clamp to Range applied exactly as for any roll, and a value landing on a used Unique row (see
/// <see cref="UniqueResults.IsRejected"/>) is thrown away and the same expression thrown again. The dice are never changed and no
/// remaining result is ever picked directly, so each available value keeps exactly its original weight relative to the others.
/// Exhaustion is checked before anything is thrown. Nothing here records anything: the caller uses up
/// <see cref="UniqueRollResult.RowsUsed"/> only once it accepts the result.
/// </summary>
public static class UniqueRoller
{
    /// <summary>The built-in dice roll instantly, so a result may take many throws before TableForge gives up.</summary>
    public const int BuiltInMaxAttempts = 10_000;

    /// <summary>Any other provider's limit unless it says otherwise: 10 throws per result, the first included (agreed for dddice).</summary>
    public const int DefaultMaxAttempts = 10;

    /// <param name="throwDice">Throws the table's own dice once and gives the provider's number, or null when the throw failed or was
    /// cancelled (the caller reports why). Called at most <paramref name="maxAttempts"/> times.</param>
    /// <param name="stopRequested">Checked before every throw, so Stop ends a run of rerolls (or a batch) even when each throw completes at once,
    /// and after every throw, so a result that comes back once Stop has been pressed (a visual provider's dice settling just as the
    /// person stops) is never accepted, shown or used up.</param>
    public static async Task<UniqueRollResult> RollAsync(RollableTable table, int situational, IReadOnlySet<Guid> usedRowKeys, int maxAttempts,
        Func<Task<int?>> throwDice, Func<bool> stopRequested)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        if (UniqueResults.IsExhausted(table, usedRowKeys, situational)) return new(UniqueRollStatus.Exhausted);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (stopRequested()) return new(UniqueRollStatus.NotRolled, RerolledCount: attempt - 1);
            if (await throwDice() is not { } roll || stopRequested()) return new(UniqueRollStatus.NotRolled, RerolledCount: attempt - 1);

            var lookup = TableClamp.LookupValue(table, roll + situational);
            if (!UniqueResults.IsRejected(table, lookup, usedRowKeys))
                return new(UniqueRollStatus.Accepted, roll, attempt - 1, UniqueResults.RowsUsedBy(table, lookup));
        }
        return new(UniqueRollStatus.AttemptLimitReached, RerolledCount: maxAttempts);
    }
}
