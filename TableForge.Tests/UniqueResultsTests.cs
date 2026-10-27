using TableForge.Domain;

namespace TableForge.Tests;

/// <summary>
/// Unique Results, the rules (<see cref="UniqueResults"/>): which values a table still accepts given its used Unique rows, which
/// rows an accepted value uses up, and when every value its own dice can reach is used — with situational modifiers, Clamp to
/// Range, d66, gaps, overlapping rows, open-ended rows and several result sets.
/// </summary>
public class UniqueResultsTests
{
    private static TableEntry Row(int min, int max, bool unique = true) =>
        new() { Min = min, Max = max, Text = $"{min}..{max}", IsUnique = unique, RowKey = Guid.NewGuid() };

    private static TableEntry Repeatable(int min, int max) => Row(min, max, unique: false);

    private static ResultSet Set(params TableEntry[] rows) => new() { Entries = [.. rows] };

    private static RollableTable Table(string dice, params ResultSet[] sets) =>
        new() { Name = "T", Dice = DiceExpression.Parse(dice), ResultSets = [.. sets] };

    private static RollableTable Table(string dice, params TableEntry[] rows) => Table(dice, Set(rows));

    private static RollableTable Clamped(RollableTable table)
    {
        table.ClampResultsToRange = true;
        return table;
    }

    private static HashSet<Guid> Used(params TableEntry[] rows) => rows.Select(r => r.RowKey).ToHashSet();

    private static HashSet<Guid> None => [];

    // ---- tables without Unique rows --------------------------------------------------------------------------------------

    [Fact]
    public void A_table_with_no_unique_rows_never_rejects_anything_and_is_never_exhausted()
    {
        var a = Repeatable(1, 3);
        var b = Repeatable(4, 6);
        var table = Table("d6", a, b);
        var used = Used(a, b); // even used state left over from rows that were once Unique

        Assert.False(UniqueResults.HasUniqueRows(table));
        Assert.All(Enumerable.Range(1, 6), v => Assert.False(UniqueResults.IsRejected(table, v, used)));
        Assert.Empty(UniqueResults.RowsUsedBy(table, 2));
        Assert.False(UniqueResults.IsExhausted(table, used));
        Assert.False(UniqueResults.IsExhausted(table, None));
    }

    // ---- which values are rejected ---------------------------------------------------------------------------------------

    [Fact]
    public void A_used_unique_row_rejects_every_value_it_covers_and_nothing_else()
    {
        var low = Row(1, 2);
        var mid = Row(3, 5);   // a range of three values: any of them uses it, then all three are rejected
        var high = Repeatable(6, 6);
        var table = Table("d8", low, mid, high); // 7 and 8 are gaps

        var used = Used(mid, high);
        Assert.True(UniqueResults.HasUniqueRows(table));
        Assert.Equal([false, false, true, true, true, false, false, false],
            Enumerable.Range(1, 8).Select(v => UniqueResults.IsRejected(table, v, used)).ToArray());
        Assert.All(Enumerable.Range(1, 8), v => Assert.False(UniqueResults.IsRejected(table, v, None)));
    }

    [Fact]
    public void A_used_row_that_is_no_longer_unique_rejects_nothing()
    {
        var dragon = Row(5, 6);
        var table = Table("d6", Row(1, 4), dragon);
        var used = Used(dragon);
        Assert.True(UniqueResults.IsRejected(table, 6, used));

        dragon.IsUnique = false;
        Assert.False(UniqueResults.IsRejected(table, 6, used));
    }

    [Fact]
    public void A_row_not_yet_saved_has_no_key_and_so_is_never_used()
    {
        var unsaved = Row(1, 6);
        unsaved.RowKey = Guid.Empty;
        Assert.False(UniqueResults.IsRejected(Table("d6", unsaved), 3, new HashSet<Guid> { Guid.Empty }));
    }

    [Fact]
    public void A_value_is_rejected_when_any_of_several_overlapping_rows_is_a_used_unique_row()
    {
        var everything = Repeatable(1, 6);
        var treasure = Row(3, 4);
        var table = Table("d6", everything, treasure);
        var used = Used(treasure);

        Assert.False(UniqueResults.IsRejected(table, 2, used));
        Assert.True(UniqueResults.IsRejected(table, 3, used));   // the repeatable row also covers 3; the roll is still rejected
        Assert.True(UniqueResults.IsRejected(table, 4, used));
    }

    [Fact]
    public void With_several_result_sets_one_used_unique_row_in_any_set_rejects_the_whole_roll()
    {
        var who = Set(Repeatable(1, 3), Repeatable(4, 6));
        var lair = Row(4, 6);
        var where = Set(Row(1, 3), lair);
        var table = Table("d6", who, where);
        var used = Used(lair);

        Assert.False(UniqueResults.IsRejected(table, 3, used));
        Assert.True(UniqueResults.IsRejected(table, 5, used));   // "who" alone would accept 5
    }

    // ---- which rows an accepted value uses up ----------------------------------------------------------------------------

    [Fact]
    public void An_accepted_value_uses_up_every_unique_row_it_lands_on_in_every_set_and_never_a_repeatable_one()
    {
        var a = Row(1, 3);
        var b = Repeatable(4, 6);
        var c = Row(1, 2);
        var d = Row(2, 6);   // overlaps c at 2
        var table = Table("d8", Set(a, b), Set(c, d));

        Assert.Equal([a.RowKey, c.RowKey, d.RowKey], UniqueResults.RowsUsedBy(table, 2));
        Assert.Equal([d.RowKey], UniqueResults.RowsUsedBy(table, 5));
        Assert.Empty(UniqueResults.RowsUsedBy(table, 7));        // a gap uses up nothing
    }

    // ---- exhaustion ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_table_is_exhausted_only_when_every_value_its_dice_can_reach_lands_on_a_used_unique_row()
    {
        var rows = new[] { Row(1, 2), Row(3, 4), Row(5, 6) };
        var table = Table("d6", rows);

        Assert.False(UniqueResults.IsExhausted(table, None));
        Assert.False(UniqueResults.IsExhausted(table, Used(rows[0], rows[2])));
        Assert.True(UniqueResults.IsExhausted(table, Used(rows)));
    }

    [Fact]
    public void A_repeatable_row_left_means_rolling_continues_even_when_every_unique_row_is_used()
    {
        var unique = new[] { Row(1, 2), Row(3, 4) };
        var table = Table("d6", unique[0], unique[1], Repeatable(5, 6));
        Assert.False(UniqueResults.IsExhausted(table, Used(unique)));
    }

    [Fact]
    public void A_gap_the_dice_can_reach_is_a_no_match_not_a_used_result_so_the_table_is_not_exhausted()
    {
        var rows = new[] { Row(1, 3), Row(4, 6) };
        Assert.False(UniqueResults.IsExhausted(Table("d8", rows), Used(rows)));   // 7 and 8 are gaps
        Assert.True(UniqueResults.IsExhausted(Table("d6", rows), Used(rows)));
    }

    [Fact]
    public void Rows_beyond_the_dice_do_not_count_and_a_gap_inside_the_dice_does()
    {
        var rows = new[] { Row(2, 3), Row(4, 12), Row(13, 20) };
        Assert.True(UniqueResults.IsExhausted(Table("2d6", rows), Used(rows[0], rows[1])));   // 13-20 cannot be rolled
        var gapped = new[] { Row(2, 6), Row(8, 12) };
        Assert.False(UniqueResults.IsExhausted(Table("2d6", gapped), Used(gapped)));          // 7 can
    }

    [Fact]
    public void The_dice_expressions_own_modifier_shifts_what_can_be_reached()
    {
        var low = Row(2, 4);
        var high = Row(5, 7);
        var table = Table("d6+1", low, high, Row(1, 1)); // reaches 2-7: row 1-1 is out of reach
        Assert.True(UniqueResults.IsExhausted(table, Used(low, high)));
    }

    [Fact]
    public void A_situational_modifier_changes_which_rows_can_be_reached()
    {
        var low = Row(1, 4);
        var high = new[] { Row(5, 7), Row(8, 10) };
        var table = Table("d6", low, high[0], high[1]);
        var used = Used(high);

        Assert.False(UniqueResults.IsExhausted(table, used));                  // 1-6 reaches 1-4
        Assert.True(UniqueResults.IsExhausted(table, used, situational: 4));    // 5-10 is all used
        Assert.False(UniqueResults.IsExhausted(table, used, situational: 3));   // 4-9 still reaches 4
        Assert.False(UniqueResults.IsExhausted(table, used, situational: -4));  // -3 to 2: gaps and 1-4
    }

    [Fact]
    public void Clamp_gathers_out_of_range_rolls_onto_the_first_and_last_rows()
    {
        var rows = new[] { Row(3, 10), Row(11, 18) };

        Assert.False(UniqueResults.IsExhausted(Table("d20", rows), Used(rows)));           // without Clamp 1, 2, 19 and 20 are gaps
        Assert.True(UniqueResults.IsExhausted(Clamped(Table("d20", rows)), Used(rows)));   // with it they land on used rows
        Assert.False(UniqueResults.IsExhausted(Clamped(Table("d20", rows)), Used(rows[1])));
    }

    [Fact]
    public void Clamp_and_a_modifier_together_can_leave_only_the_last_row_reachable()
    {
        var first = Row(3, 10);
        var last = Row(11, 18);
        var table = Clamped(Table("d20", first, last));

        // d20+10 is 11-30, looked up as 11-18: only the last row can come up, so using it exhausts the table for this roll.
        Assert.True(UniqueResults.IsExhausted(table, Used(last), situational: 10));
        Assert.False(UniqueResults.IsExhausted(table, Used(last)));
        Assert.False(UniqueResults.IsExhausted(table, Used(first), situational: 10));
    }

    [Fact]
    public void Clamp_that_cannot_apply_is_ignored()
    {
        // Result sets with different outer ranges: Clamp never takes effect, so 19 and 20 stay gaps.
        var a = new[] { Row(1, 10), Row(11, 18) };
        var b = Set(Repeatable(1, 18));
        var table = Clamped(Table("d20", Set(a), b));
        b.Entries[0].Max = 17;
        Assert.False(UniqueResults.IsExhausted(table, Used(a)));
    }

    [Fact]
    public void Open_ended_rows_count_as_the_values_they_cover()
    {
        var below = Row(RangeBounds.OpenBelow, 10);
        var middle = Row(11, 25);
        var above = Row(26, RangeBounds.OpenAbove);
        var table = Table("d20", below, middle, above);

        Assert.True(UniqueResults.IsRejected(table, -50, Used(below)));
        Assert.True(UniqueResults.IsExhausted(table, Used(middle, above), situational: 10));    // 11-30
        Assert.False(UniqueResults.IsExhausted(table, Used(middle), situational: 10));          // 26+ is unused
        Assert.True(UniqueResults.IsExhausted(table, Used(below, middle, above), situational: 1000));
        Assert.True(UniqueResults.IsExhausted(table, Used(below), situational: -1000));
    }

    [Fact]
    public void For_d66_only_its_36_legal_results_count_never_the_impossible_numbers_between_them()
    {
        var ones = Row(11, 16);
        var rest = Row(21, 66);
        Assert.True(UniqueResults.IsExhausted(Table("d66", ones, rest), Used(ones, rest)));    // 17-20 cannot be rolled: not gaps
        Assert.False(UniqueResults.IsExhausted(Table("d66", ones, rest), Used(ones)));

        var upToFifties = Row(11, 56);
        Assert.False(UniqueResults.IsExhausted(Table("d66", upToFifties), Used(upToFifties)));  // 61-66 are real gaps
        var last = Row(66, 66);
        Assert.False(UniqueResults.IsExhausted(Table("d66", upToFifties, Row(61, 65), last), Used(upToFifties, last)));
    }

    [Fact]
    public void A_d66_takes_no_situational_modifier()
    {
        var table = Table("d66", Row(11, 66));
        Assert.Throws<ArgumentException>(() => UniqueResults.IsExhausted(table, None, situational: 1));
    }

    [Fact]
    public void With_several_result_sets_one_set_whose_unique_rows_are_all_used_exhausts_the_table()
    {
        var where = new[] { Row(1, 3), Row(4, 6) };
        var table = Table("d6", Set(Repeatable(1, 6)), Set(where));

        Assert.True(UniqueResults.IsExhausted(table, Used(where)));
        Assert.False(UniqueResults.IsExhausted(table, Used(where[0])));
    }

    [Fact]
    public void Used_rows_from_different_sets_can_together_cover_every_value()
    {
        var a = Row(1, 3);
        var b = Row(4, 6);
        var table = Table("d6", Set(a, Repeatable(4, 6)), Set(Repeatable(1, 3), b));
        Assert.True(UniqueResults.IsExhausted(table, Used(a, b)));
    }

    [Fact]
    public void The_largest_dice_are_checked_without_rolling_or_enumerating_every_value()
    {
        // 100d1000 reaches 100 to 100,000; a thousand used rows of a hundred values each cover all of it.
        var rows = Enumerable.Range(0, 1000).Select(i => Row(i * 100 + 1, i * 100 + 100)).ToArray();
        var table = Table("100d1000", rows);
        Assert.True(UniqueResults.IsExhausted(table, Used(rows)));
        Assert.False(UniqueResults.IsExhausted(table, Used(rows[..^1])));
    }
}
