using TableForge.Domain;
using TableForge.Import;

namespace TableForge.Tests;

/// <summary>
/// One table printed as repeated "range | result" column pairs across the page ("D100 FOLLOWER NAME D100 FOLLOWER NAME").
/// The second range column is another slice of the same roll, so the rows flatten column by column into one result set —
/// unlike aligned outputs ("D8 DIFFICULTY MODIFIER"), where one range column feeds several result sets.
/// </summary>
public class RepeatedRangeColumnTests
{
    /// <summary>The follower-name table: 01-02 … 49-50 down the left, 51-52 … 99-100 down the right.</summary>
    private static string Followers(string separator = " ", string dash = "-")
    {
        string Left(int i) => i switch { 0 => "Kael Dravorn", 1 => "Morthan Vex", 2 => "Ragor Thul", 24 => "Saelith", _ => $"Left Follower {i + 1}" };
        string Right(int i) => i switch { 0 => "Korva", 1 => "Thalen", 24 => "Sorneth Vale", _ => $"Right Follower {i + 1}" };

        var rows = Enumerable.Range(0, 25).Select(i =>
            string.Join(separator, $"{i * 2 + 1:00}{dash}{i * 2 + 2:00}", Left(i), $"{51 + i * 2}{dash}{52 + i * 2}", Right(i)));
        return string.Join(separator, "D100", "FOLLOWER NAME", "D100", "FOLLOWER NAME") + "\n" + string.Join("\n", rows);
    }

    private static string Lines(string heading, IEnumerable<string> rows) => heading + "\n" + string.Join("\n", rows);

    private static string[] Rows(TableImportDraft d) => d.ResultSets[0].Entries.Select(e => $"{e.RangeText} {e.Text}").ToArray();

    private static void AssertNotFlattened(TableImportDraft draft, int lines)
    {
        Assert.Equal(lines, draft.ResultSets.Sum(s => s.Entries.Count));   // still one row per printed line
        Assert.DoesNotContain(draft.Issues, i => i.Code == ParseIssueCode.SideBySideSplit);
    }

    // ---- the follower-name table ---------------------------------------------------------------

    [Theory]
    [InlineData(" ", "-")]        // PDF extraction: single spaces only
    [InlineData(" ", " - ")]      // ...with spaces around the dashes (was left as 25 combined rows and flagged)
    [InlineData(" ", " – ")]
    [InlineData("\t", "-")]       // copied from a table: tab-separated cells
    [InlineData("  ", " - ")]
    public void The_follower_table_flattens_into_one_logical_d100_table(string separator, string dash)
    {
        var draft = TableTextParser.Parse(Followers(separator, dash));

        Assert.Equal(("Follower Name", "d100"), (draft.TableName, draft.DiceText));
        var set = Assert.Single(draft.ResultSets);
        Assert.Equal(50, set.Entries.Count);
        Assert.Equal("01-02 Kael Dravorn", Rows(draft)[0]);
        Assert.Equal(["03-04 Morthan Vex", "05-06 Ragor Thul"], Rows(draft)[1..3]);
        Assert.Equal(["49-50 Saelith", "51-52 Korva", "53-54 Thalen"], Rows(draft)[24..27]);
        Assert.Equal("99-100 Sorneth Vale", Rows(draft)[^1]);
        Assert.DoesNotContain(draft.Issues, i => i.Severity != ParseIssueSeverity.Info);
    }

    [Fact]
    public void The_follower_table_covers_1_to_100_once_in_order_and_rolls_from_both_halves()
    {
        var table = Fixtures.ParseAndBuild(Followers(" ", " - "));
        var entries = table.ResultSets[0].Entries;

        Assert.Equal(Enumerable.Range(1, 100), entries.SelectMany(e => Enumerable.Range(e.Min, e.Max - e.Min + 1)));
        Assert.Empty(TableValidator.Validate(table));
        Assert.Equal((99, 100, (string?)null), (entries[^1].Min, entries[^1].Max, entries[^1].DisplayRange));   // plain numeric 99-100

        string At(int roll) => TableResolver.Resolve(table, roll).Results[0].Entry!.Text;
        Assert.Equal(["Kael Dravorn", "Saelith", "Korva", "Sorneth Vale"], new[] { 1, 50, 51, 100 }.Select(At).ToArray());
    }

    // ---- single values, proven by the repeated heading ------------------------------------------

    private static IEnumerable<string> D20Rows(string separator = " ") =>
        Enumerable.Range(0, 10).Select(i => string.Join(separator, $"{i + 1}", $"{(char)('A' + i)}", $"{i + 11}", $"{(char)('K' + i)}"));

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Single_values_under_a_repeated_heading_flatten_into_one_20_entry_table(string separator)
    {
        var draft = TableTextParser.Parse(Lines("D20 RESULT D20 RESULT", D20Rows(separator)));

        Assert.Equal(("Result", "d20"), (draft.TableName, draft.DiceText));
        var set = Assert.Single(draft.ResultSets);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"{i + 1} {(char)('A' + i)}"), Rows(draft));
    }

    [Theory]
    [InlineData("D20 EVENT D20 EVENT")]
    [InlineData("d20 Event d20 Event")]
    public void The_heading_text_does_not_matter_only_that_it_repeats(string heading)
    {
        var draft = TableTextParser.Parse(Lines(heading, D20Rows()));

        Assert.Equal(20, Assert.Single(draft.ResultSets).Entries.Count);
    }

    [Fact]
    public void Single_values_without_a_repeated_heading_are_not_flattened()
    {
        AssertNotFlattened(TableTextParser.Parse(Lines("D20 RESULT", D20Rows())), 10);
    }

    [Fact]
    public void Single_values_must_cover_the_whole_dice_range()
    {
        // 1-8 on the left and 9-16 on the right: coherent, but a d20 table that stops at 16 is not proof enough for single values.
        var rows = Enumerable.Range(0, 8).Select(i => $"{i + 1} L{i} {i + 9} R{i}");
        AssertNotFlattened(TableTextParser.Parse(Lines("D20 RESULT D20 RESULT", rows)), 8);
    }

    [Fact]
    public void Pasting_rows_never_flattens_single_values_because_there_is_no_heading()
    {
        var result = TableTextParser.ParseRows(string.Join("\n", D20Rows()), DiceExpression.Parse("d20"));

        Assert.Equal(10, result.Entries.Count);
        Assert.Equal("A 11 K", result.Entries[0].Text);
    }

    // ---- aligned outputs are not continuation ---------------------------------------------------

    [Fact]
    public void Aligned_outputs_stay_aligned_result_sets()
    {
        var draft = TableTextParser.Parse(
            "D8 DIFFICULTY MODIFIER\n1 Easy +20\n2 Routine +10\n3 Average +0\n4 Challenging -10\n5 Hard -20\n6 Very Hard -30\n7 Daunting -40\n8 Heroic -50");

        Assert.Equal(["Difficulty", "Modifier"], draft.ResultSets.Select(s => s.Name).ToArray());
        Assert.All(draft.ResultSets, s => Assert.Equal(8, s.Entries.Count));
        Assert.Equal(("1", "Easy", "+20"), (draft.ResultSets[0].Entries[0].RangeText, draft.ResultSets[0].Entries[0].Text, draft.ResultSets[1].Entries[0].Text));
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.ParallelOutputsSplit);
        Assert.DoesNotContain(draft.Issues, i => i.Code == ParseIssueCode.SideBySideSplit);
    }

    // ---- what must not flatten ------------------------------------------------------------------

    [Fact]
    public void A_second_number_column_that_is_not_a_range_of_the_same_roll_is_not_flattened()
    {
        var rows = Enumerable.Range(1, 10).Select(i => $"{i} Rope{i} {i * 5} m");   // lengths, not rolls: 5, 10, 15 …
        AssertNotFlattened(TableTextParser.Parse(Lines("D10 GEAR D10 GEAR", rows)), 10);
    }

    [Fact]
    public void Overlapping_continuation_columns_are_not_flattened()
    {
        var rows = Enumerable.Range(0, 4).Select(i => $"{i * 5 + 1}-{i * 5 + 5} L{i} {i * 5 + 6}-{i * 5 + 10} R{i}");   // 1-5 | 6-10, 6-10 | 11-15 …
        var draft = TableTextParser.Parse(Lines("D20 RESULT D20 RESULT", rows));

        AssertNotFlattened(draft, 4);
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.MultipleRangesOnLine);
    }

    [Fact]
    public void A_right_column_in_a_larger_domain_than_the_dice_is_not_flattened()
    {
        // d20-style single values on the left, d100-style spans on the right that run past 20.
        var rows = Enumerable.Range(0, 10).Select(i => $"{i + 1} L{i} {i * 10 + 11}-{i * 10 + 20} R{i}");
        AssertNotFlattened(TableTextParser.Parse(Lines("D20 RESULT D20 RESULT", rows)), 10);
    }

    [Fact]
    public void A_heading_whose_dice_differ_per_column_does_not_prove_continuation()
    {
        var draft = TableTextParser.Parse(Lines("D20 EVENT D100 EVENT", D20Rows()));

        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.AmbiguousHeading);
        AssertNotFlattened(draft, 10);
    }

    // ---- d66 ----------------------------------------------------------------------------------

    private static readonly int[] D66Values = [.. Enumerable.Range(11, 56).Where(DiceExpression.D66.IsLegal)];

    [Fact]
    public void A_repeated_d66_table_flattens_through_legal_d66_values_only()
    {
        var rows = Enumerable.Range(0, 18).Select(i => $"{D66Values[i]} L{D66Values[i]} {D66Values[i + 18]} R{D66Values[i + 18]}");
        var table = Fixtures.ParseAndBuild(Lines("D66 RESULT D66 RESULT", rows));

        Assert.Equal(D66Values, table.ResultSets[0].Entries.Select(e => e.Min));
        Assert.Equal(["L16", "L21", "L36", "R41", "R66"], new[] { 5, 6, 17, 18, 35 }.Select(i => table.ResultSets[0].Entries[i].Text).ToArray());
        Assert.Empty(TableValidator.Validate(table));
    }

    [Fact]
    public void Arbitrary_two_digit_numbers_under_a_d66_heading_are_not_flattened()
    {
        // 11-28 down the left runs straight through 17-20, which a d66 can never roll.
        var rows = Enumerable.Range(0, 18).Select(i => $"{11 + i} L{i} {29 + i} R{i}");
        AssertNotFlattened(TableTextParser.Parse(Lines("D66 RESULT D66 RESULT", rows)), 18);
    }

    // ---- more than two columns of single values, proven by a separator or by the heading --------------

    /// <summary>
    /// Single values laid out in columns: <paramref name="columns"/> lists each column's values top to bottom, and each group is
    /// written as value, separator, text ("26 – W26"; with an attached mark, "26. W26"; with no separator, "26 W26").
    /// </summary>
    private static string Grid(string heading, int[][] columns, string separator = " – ", Func<int, int, bool>? keep = null)
    {
        var rows = Enumerable.Range(0, columns.Max(c => c.Length)).Select(r => string.Join(" ",
            Enumerable.Range(0, columns.Length)
                .Where(c => r < columns[c].Length && (keep?.Invoke(r, c) ?? true))
                .Select(c => $"{columns[c][r]}{separator}W{columns[c][r]}")));
        return Lines(heading, rows);
    }

    private static int[] Run(int from, int count) => [.. Enumerable.Range(from, count)];

    private static readonly int[][] ActionColumns = [Run(1, 25), Run(26, 25), Run(51, 25), Run(76, 25)];

    [Fact]
    public void The_action_table_in_four_columns_flattens_into_100_sequential_rows()
    {
        var draft = TableTextParser.Parse(Grid("ACTION (D100)", ActionColumns));

        Assert.Equal(("Action", "d100"), (draft.TableName, draft.DiceText));
        var set = Assert.Single(draft.ResultSets);
        Assert.Equal(Enumerable.Range(1, 100).Select(v => $"{v} W{v}"), Rows(draft));   // column by column: 1-25, 26-50, 51-75, 76-100
        Assert.All(draft.Issues, i => Assert.Equal(ParseIssueSeverity.Info, i.Severity));
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.SideBySideSplit);

        var table = Fixtures.ParseAndBuild(Grid("ACTION (D100)", ActionColumns));
        Assert.Equal(Enumerable.Range(1, 100), table.ResultSets[0].Entries.SelectMany(e => Enumerable.Range(e.Min, e.Max - e.Min + 1)));
        Assert.Empty(TableValidator.Validate(table));
        string At(int roll) => TableResolver.Resolve(table, roll).Results[0].Entry!.Text;
        Assert.Equal(["W1", "W25", "W26", "W76", "W100"], new[] { 1, 25, 26, 76, 100 }.Select(At).ToArray());
    }

    [Theory]
    [InlineData("ACTION (D100)", " - ")]
    [InlineData("ACTION (D100)", " — ")]
    [InlineData("ACTION (D100)", ". ")]
    [InlineData("ACTION (D100)", ") ")]
    [InlineData("ACTION (D100)", ": ")]
    [InlineData("D100 ACTION", " – ")]
    [InlineData("Action (d100)", " – ")]
    public void Any_one_consistent_separator_and_heading_form_is_enough(string heading, string separator)
    {
        var draft = TableTextParser.Parse(Grid(heading, ActionColumns, separator));

        Assert.Equal(Enumerable.Range(1, 100).Select(v => $"{v} W{v}"), Rows(draft));
    }

    [Fact]
    public void Two_separated_columns_need_no_repeated_heading()
    {
        var draft = TableTextParser.Parse(Grid("D20 EVENT", [Run(1, 10), Run(11, 10)]));

        Assert.Equal(Enumerable.Range(1, 20).Select(v => $"{v} W{v}"), Rows(draft));
    }

    [Fact]
    public void A_heading_repeated_once_per_column_proves_three_columns_without_a_separator()
    {
        var draft = TableTextParser.Parse(Grid("D30 EVENT D30 EVENT D30 EVENT", [Run(1, 10), Run(11, 10), Run(21, 10)], separator: " "));

        Assert.Equal(("Event", "d30"), (draft.TableName, draft.DiceText));
        Assert.Equal(Enumerable.Range(1, 30).Select(v => $"{v} W{v}"), Rows(draft));
    }

    [Fact]
    public void A_heading_repeated_fewer_times_than_the_columns_does_not_prove_them()
    {
        AssertNotFlattened(TableTextParser.Parse(Grid("D30 EVENT D30 EVENT", [Run(1, 10), Run(11, 10), Run(21, 10)], separator: " ")), 10);
    }

    [Fact]
    public void A_separated_d66_table_flattens_through_legal_d66_values()
    {
        var table = Fixtures.ParseAndBuild(Grid("D66 OMEN", [D66Values[..18], D66Values[18..]]));

        Assert.Equal(D66Values, table.ResultSets[0].Entries.Select(e => e.Min));
        Assert.Empty(TableValidator.Validate(table));
    }

    [Fact]
    public void Pasting_separated_rows_that_cover_the_dice_flattens_them_too()
    {
        var result = TableTextParser.ParseRows(Grid("", ActionColumns).TrimStart('\n'), DiceExpression.Parse("d100"));

        Assert.Equal(Enumerable.Range(1, 100).Select(v => $"W{v}"), result.Entries.Select(e => e.Text));
    }

    // ---- what must not flatten ------------------------------------------------------------------

    [Fact]
    public void Numbers_inside_ordinary_row_text_are_not_columns()
    {
        // Covers d8 exactly, column by column, but nothing marks the second number as a roll value.
        var draft = TableTextParser.Parse("D8 LOOT\n1 Gain 5 gold\n2 Lose 6 hp\n3 Find 7 torches\n4 Take 8 damage");

        Assert.Equal(["1 Gain 5 gold", "2 Lose 6 hp", "3 Find 7 torches", "4 Take 8 damage"], Rows(draft));
        Assert.DoesNotContain(draft.Issues, i => i.Code == ParseIssueCode.SideBySideSplit);
    }

    public static TheoryData<string, string, int> BrokenGrids => new()
    {
        { "a column skips a value", Grid("ACTION (D100)", [Run(1, 25), [.. Run(26, 12), .. Run(39, 13)], Run(51, 25), Run(76, 25)]), 25 },
        { "columns overlap", Grid("ACTION (D100)", [Run(1, 25), Run(20, 25), Run(45, 25), Run(70, 25)]), 25 },
        { "the columns stop short of the dice", Grid("ACTION (D100)", [Run(1, 25), Run(26, 25), Run(51, 25)]), 25 },
        { "a line has fewer groups", Grid("ACTION (D100)", ActionColumns, keep: (r, c) => !(r == 4 && c == 3)), 25 },
        { "values run past the dice", Grid("ACTION (D100)", [Run(1, 30), Run(31, 30), Run(61, 30), Run(91, 30)]), 30 },
        { "values a d66 cannot roll", Grid("D66 OMEN", [Run(11, 18), Run(29, 18)]), 18 },
        { "the heading holds different dice", Grid("D20 EVENT D100 EVENT", [Run(1, 10), Run(11, 10)]), 10 },
        { "only three lines", Grid("D12 OMEN", [Run(1, 3), Run(4, 3), Run(7, 3), Run(10, 3)]), 3 },
        { "the separator changes", Grid("D20 EVENT", [Run(1, 10), Run(11, 10)]).Replace("11 – ", "11. "), 10 },
    };

    [Theory]
    [MemberData(nameof(BrokenGrids))]
    public void Separated_columns_that_do_not_prove_one_whole_table_stay_one_row_per_line_and_are_flagged(string why, string source, int lines)
    {
        var draft = TableTextParser.Parse(source);

        AssertNotFlattened(draft, lines);
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.MultipleRangesOnLine);   // never a silent 25-row import
        _ = why;
    }
}
