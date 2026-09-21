using TableForge.Domain;
using TableForge.Import;

namespace TableForge.Tests;

/// <summary>
/// A split into columns needs block-level evidence. Pieces that merely look like entries ("4 Rope    10 gp")
/// stay together and are flagged, without the parser knowing anything about prices or equipment.
/// </summary>
public class ColumnSafeguardTests
{
    private static string[] Rows(TableImportDraft d) => d.ResultSets[0].Entries.Select(e => $"{e.RangeText} {e.Text}").ToArray();

    // ---- what must not split ------------------------------------------------------------------

    [Fact]
    public void An_isolated_row_with_a_number_after_a_gap_is_not_silently_split()
    {
        var draft = TableTextParser.Parse("d20 Shop\n4 Rope    10 gp");

        var entry = Assert.Single(draft.ResultSets[0].Entries);
        Assert.Equal(("4", "Rope 10 gp"), (entry.RangeText, entry.Text));
        var issue = Assert.Single(draft.Issues);
        Assert.Equal((ParseIssueCode.MultipleRangesOnLine, ParseIssueSeverity.Warning, ParseIssueTarget.Entry, 0, 0),
            (issue.Code, issue.Severity, issue.Target, issue.ResultSetIndex, issue.EntryIndex));
        Assert.Contains("(4, 10)", issue.Message);
        Assert.Equal(2, issue.SourceLine);
    }

    [Fact]
    public void A_number_beyond_the_dice_is_never_treated_as_a_column()
    {
        var draft = TableTextParser.Parse("d6 Shop\n4 Rope    10 gp");

        Assert.Equal(["4 Rope 10 gp"], Rows(draft));
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.MultipleRangesOnLine);
    }

    [Fact]
    public void A_list_where_every_line_has_a_price_is_not_mistaken_for_columns()
    {
        var draft = TableTextParser.Parse("d20 Shop\n4 Rope    10 gp\n5 Lamp    12 gp\n6 Oil    15 gp");

        Assert.Equal(["4 Rope 10 gp", "5 Lamp 12 gp", "6 Oil 15 gp"], Rows(draft));      // three rows, not six
        Assert.Equal(3, draft.Issues.Count(i => i.Code == ParseIssueCode.MultipleRangesOnLine));
        Assert.DoesNotContain(draft.Issues, i => i.Code == ParseIssueCode.SideBySideSplit);
    }

    [Fact]
    public void A_lone_line_needs_every_piece_to_be_an_explicit_span()
    {
        var draft = TableTextParser.Parse("d100 Names\n01-05 Ash        51 Kel");

        Assert.Equal(["01-05 Ash 51 Kel"], Rows(draft));
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.MultipleRangesOnLine && i.EntryIndex == 0);
    }

    [Fact]
    public void Adjacent_lines_that_do_not_run_on_down_each_column_are_not_a_block()
    {
        // The left column skips 06-10, so these are not two clean columns.
        var draft = TableTextParser.Parse("d100 Names\n01-05 Ash        51-55 Kel\n11-15 Bar        56-60 Lor");

        Assert.Equal(["01-05 Ash 51-55 Kel", "11-15 Bar 56-60 Lor"], Rows(draft));
        Assert.Equal(2, draft.Issues.Count(i => i.Code == ParseIssueCode.MultipleRangesOnLine));
    }

    [Fact]
    public void Adjacent_lines_with_different_numbers_of_pieces_do_not_vouch_for_each_other()
    {
        var draft = TableTextParser.Parse("d20 Shop\n4 Rope    10 gp\n5 Lamp    12 gp    14 sp\n6 Oil");

        Assert.Equal(3, draft.ResultSets[0].Entries.Count);
        Assert.Equal("Rope 10 gp", draft.ResultSets[0].Entries[0].Text);
    }

    [Fact]
    public void Pasting_a_lone_price_style_line_into_a_result_set_is_kept_together_and_flagged_too()
    {
        var result = TableTextParser.ParseRows("4 Rope    10 gp", DiceExpression.Parse("d20"));

        var entry = Assert.Single(result.Entries);
        Assert.Equal("Rope 10 gp", entry.Text);
        Assert.Contains(result.Issues, i => i.Code == ParseIssueCode.MultipleRangesOnLine && i.EntryIndex == 0 && i.Message.StartsWith("Pasted line 1"));
    }

    // ---- single exact values need three lines (locked rule) -----------------------------------

    [Fact]
    public void Two_lines_of_single_values_stay_together_even_when_both_columns_run_on_consecutively()
    {
        // 4,5 and 10,11 are each consecutive, which is exactly what makes this look like columns. Two lines are not enough.
        var draft = TableTextParser.Parse("d20 Shop\n4 Rope    10 gp\n5 Lamp    11 gp");

        Assert.Equal(["4 Rope 10 gp", "5 Lamp 11 gp"], Rows(draft));
        Assert.Equal(2, draft.Issues.Count(i => i.Code == ParseIssueCode.MultipleRangesOnLine));
        Assert.DoesNotContain(draft.Issues, i => i.Code == ParseIssueCode.SideBySideSplit);
    }

    [Fact]
    public void Three_adjacent_lines_with_consistent_structure_may_split_single_values()
    {
        var draft = TableTextParser.Parse("d6 Names\n1 Ash    4 Kel\n2 Bar    5 Lor\n3 Cor    6 Mor");

        Assert.Equal(["1 Ash", "2 Bar", "3 Cor", "4 Kel", "5 Lor", "6 Mor"], Rows(draft));
        Assert.Equal(ParseIssueCode.SideBySideSplit, Assert.Single(draft.Issues).Code);
    }

    [Fact]
    public void One_single_value_among_spans_is_enough_to_require_three_lines()
    {
        // Left column are spans, right column single values: the single values are the weak evidence.
        var twoLines = TableTextParser.Parse("d20 Names\n1-2 Ash     9 Kel\n3-4 Bar    10 Lor");
        Assert.Equal(["1-2 Ash 9 Kel", "3-4 Bar 10 Lor"], Rows(twoLines));

        var threeLines = TableTextParser.Parse("d20 Names\n1-2 Ash     9 Kel\n3-4 Bar    10 Lor\n5-6 Cor    11 Mor");
        Assert.Equal(6, threeLines.ResultSets[0].Entries.Count);
    }

    [Fact]
    public void Two_lines_of_explicit_spans_still_split_as_before()
    {
        var draft = TableTextParser.Parse("d100 Names\n01-25 Ash        51-75 Kel\n26-50 Bar        76-00 Lor");

        Assert.Equal(["01-25 Ash", "26-50 Bar", "51-75 Kel", "76-00 Lor"], Rows(draft));
    }

    [Fact]
    public void A_short_third_row_does_not_count_as_a_third_line_of_evidence()
    {
        // Only two lines have two pieces; the third has one. That is not three lines showing the same structure.
        var draft = TableTextParser.Parse("d6 Names\n1 Ash    4 Kel\n2 Bar    5 Lor\n3 Cor");

        Assert.Equal(["1 Ash 4 Kel", "2 Bar 5 Lor", "3 Cor"], Rows(draft));
    }

    [Fact]
    public void Pasting_rows_follows_the_same_single_value_rule()
    {
        var two = TableTextParser.ParseRows("4 Rope    10 gp\n5 Lamp    11 gp", DiceExpression.Parse("d20"));
        Assert.Equal(["Rope 10 gp", "Lamp 11 gp"], two.Entries.Select(e => e.Text).ToArray());

        var three = TableTextParser.ParseRows("4 Rope    10 gp\n5 Lamp    11 gp\n6 Oil    12 gp", DiceExpression.Parse("d20"));
        Assert.Equal(6, three.Entries.Count);
    }

    // ---- what still splits --------------------------------------------------------------------

    [Fact]
    public void A_multi_line_sequential_block_still_splits_column_by_column()
    {
        var draft = TableTextParser.Parse(
            "d100 Names\n01-05 Ash        51-55 Kel\n06-10 Bar        56-60 Lor\n11-15 Cor        61-65 Mor");

        Assert.Equal(["01-05 Ash", "06-10 Bar", "11-15 Cor", "51-55 Kel", "56-60 Lor", "61-65 Mor"], Rows(draft));
        Assert.Equal(ParseIssueCode.SideBySideSplit, Assert.Single(draft.Issues).Code);
    }

    [Fact]
    public void Single_value_columns_split_when_adjacent_lines_prove_the_layout()
    {
        var draft = TableTextParser.Parse("d6 Names\n1 Ash    4 Kel\n2 Bar    5 Lor\n3 Cor    6 Mor");

        Assert.Equal(["1 Ash", "2 Bar", "3 Cor", "4 Kel", "5 Lor", "6 Mor"], Rows(draft));
    }

    [Fact]
    public void Three_columns_with_consistent_structure_split_into_three()
    {
        var draft = TableTextParser.Parse("d100 Names\n01-10 Ash    34-43 Kel    67-76 Nim\n11-20 Bar    44-53 Lor    77-86 Oda");

        Assert.Equal(["01-10 Ash", "11-20 Bar", "34-43 Kel", "44-53 Lor", "67-76 Nim", "77-86 Oda"], Rows(draft));
    }

    [Fact]
    public void An_isolated_line_of_explicit_spans_still_splits()
    {
        var draft = TableTextParser.Parse("d100 Names\n01-50 Ash        51-00 Kel");

        Assert.Equal(["01-50 Ash", "51-00 Kel"], Rows(draft));
        Assert.Equal(ParseIssueCode.SideBySideSplit, Assert.Single(draft.Issues).Code);
    }

    [Fact]
    public void A_short_final_row_continues_a_proven_block()
    {
        var draft = TableTextParser.Parse("d100 Names\n01-05 Ash        51-55 Kel\n06-10 Bar        56-60 Lor\n11-15 Cor");

        Assert.Equal(["01-05 Ash", "06-10 Bar", "11-15 Cor", "51-55 Kel", "56-60 Lor"], Rows(draft));
    }

    [Fact]
    public void A_proven_block_in_a_multi_set_import_splits_within_its_set_and_a_price_line_elsewhere_does_not()
    {
        var draft = TableTextParser.Parse(
            "d20 Mixed\n\nNAMES\n1-5 Ash        11-15 Kel\n6-10 Bar        16-20 Lor\n\nSHOP\n4 Rope    10 gp");

        Assert.Equal(["Names", "Shop"], draft.ResultSets.Select(s => s.Name).ToArray());
        Assert.Equal(["1-5 Ash", "6-10 Bar", "11-15 Kel", "16-20 Lor"], draft.ResultSets[0].Entries.Select(e => $"{e.RangeText} {e.Text}").ToArray());
        Assert.Equal(["4 Rope 10 gp"], draft.ResultSets[1].Entries.Select(e => $"{e.RangeText} {e.Text}").ToArray());
    }
}
