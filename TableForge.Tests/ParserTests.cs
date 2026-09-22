using TableForge.Import;

namespace TableForge.Tests;

public class ParserTests
{
    [Fact]
    public void Random_starting_gear_parses_into_expected_rows()
    {
        var draft = TableTextParser.Parse(Fixtures.RandomStartingGear);

        Assert.Equal("Random Starting Gear", draft.TableName);
        Assert.Equal("d10", draft.DiceText);
        Assert.Equal(Fixtures.RandomStartingGear, draft.SourceText);

        var set = Assert.Single(draft.ResultSets);
        var rows = set.Entries.Select(e => (e.RangeText, e.Text)).ToArray();
        Assert.Equal(
            [
                ("1-2", "Backpack"),
                ("3", "Knife"),
                ("4", "1x Torch (UD6)"),
                ("5", "Fishing rod"),
                ("6", "Rope (15 m)"),
                ("7", "Tinderbox"),
                ("8", "D4 Bandages"),
                ("9-10", "D20 Construction Supplies"),
            ],
            rows);
    }

    [Fact]
    public void Random_starting_gear_builds_numeric_ranges()
    {
        var table = Fixtures.ParseAndBuild(Fixtures.RandomStartingGear);

        Assert.Equal("Random Starting Gear", table.Name);
        Assert.Equal(new(1, 10), table.Dice);
        var entries = Assert.Single(table.ResultSets).Entries;
        Assert.Equal([(1, 2), (3, 3), (4, 4), (5, 5), (6, 6), (7, 7), (8, 8), (9, 10)],
            entries.Select(e => (e.Min, e.Max)).ToArray());
        Assert.All(entries, e => Assert.Null(e.DisplayRange));
        Assert.Equal("D20 Construction Supplies", entries[^1].Text);
    }

    [Fact]
    public void Wrapped_final_line_produces_a_targeted_review_issue_and_nothing_else()
    {
        var draft = TableTextParser.Parse(Fixtures.RandomStartingGear);

        var issue = Assert.Single(draft.Issues);
        Assert.Equal(ParseIssueCode.ContinuationJoined, issue.Code);
        Assert.Equal(ParseIssueSeverity.Warning, issue.Severity);
        Assert.Equal(ParseIssueTarget.Entry, issue.Target);
        Assert.Equal(0, issue.ResultSetIndex);
        Assert.Equal(7, issue.EntryIndex);      // the 9-10 row
        Assert.Equal(10, issue.SourceLine);     // "Supplies"
        Assert.Equal((9, 10), (draft.ResultSets[0].Entries[7].SourceLineStart, draft.ResultSets[0].Entries[7].SourceLineEnd));
    }

    [Fact]
    public void Dice_like_text_inside_results_is_plain_text()
    {
        var entries = TableTextParser.Parse(Fixtures.RandomStartingGear).ResultSets[0].Entries;
        Assert.Equal("1x Torch (UD6)", entries[2].Text);
        Assert.Equal("D4 Bandages", entries[6].Text);
        Assert.StartsWith("D20 ", entries[7].Text);
    }

    [Fact]
    public void Windows_line_endings_and_odd_whitespace_give_the_same_result()
    {
        var messy = "﻿D10 RANDOM STARTING GEAR\r\n\r\n1-2 Backpack \r\n3\tKnife\r\n\r\n";
        var draft = TableTextParser.Parse(messy);

        Assert.Equal("Random Starting Gear", draft.TableName);
        Assert.Equal("d10", draft.DiceText);
        Assert.Equal([("1-2", "Backpack"), ("3", "Knife")], draft.ResultSets[0].Entries.Select(e => (e.RangeText, e.Text)).ToArray());
        Assert.Empty(draft.Issues);
    }

    [Fact]
    public void En_dash_ranges_and_separators_after_the_range_are_understood()
    {
        var draft = TableTextParser.Parse("d6 Loot\n1–2. Coin\n3) Gem\n4: Sword\n5 - Shield\n6 – Ring");
        Assert.Equal(
            [("1-2", "Coin"), ("3", "Gem"), ("4", "Sword"), ("5", "Shield"), ("6", "Ring")],
            draft.ResultSets[0].Entries.Select(e => (e.RangeText, e.Text)).ToArray());
        Assert.Empty(draft.Issues);
    }

    [Fact]
    public void D100_table_keeps_display_range_forms()
    {
        var table = Fixtures.ParseAndBuild("d100 Treasure\n01-50 Copper\n51-95 Silver\n96-00 Gold");
        var entries = table.ResultSets[0].Entries;

        Assert.Equal((1, 50), (entries[0].Min, entries[0].Max));
        Assert.Equal("01–50", entries[0].DisplayRange);
        Assert.Equal((96, 100), (entries[2].Min, entries[2].Max));
        Assert.Equal("96–00", entries[2].DisplayRange);
    }

    [Fact]
    public void Two_dice_heading_and_trailing_dice_heading_are_understood()
    {
        Assert.Equal("2d6", TableTextParser.Parse("2d6 Reaction\n2-12 Whatever").DiceText);

        var trailing = TableTextParser.Parse("Reaction Roll (d20)\n1-20 Whatever");
        Assert.Equal("Reaction Roll", trailing.TableName);
        Assert.Equal("d20", trailing.DiceText);
    }

    [Fact]
    public void Mixed_case_heading_is_not_altered()
    {
        Assert.Equal("Random Starting Gear of Doom", TableTextParser.Parse("d10 Random Starting Gear of Doom\n1 x").TableName);
    }

    [Fact]
    public void All_caps_heading_keeps_small_words_lower_after_the_first_word()
    {
        Assert.Equal("Treasure of the Dragon", TableTextParser.Parse("D100 TREASURE OF THE DRAGON\n1 x").TableName);
    }

    [Fact]
    public void Missing_dice_is_reported_and_not_guessed()
    {
        var draft = TableTextParser.Parse("Loot\n1-2 Coin\n3-4 Gem");

        Assert.Equal("Loot", draft.TableName);
        Assert.Equal("", draft.DiceText);
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.NoDiceExpression && i.Target == ParseIssueTarget.Dice);
    }

    [Fact]
    public void Unsupported_dice_in_heading_is_kept_for_correction_and_reported()
    {
        var draft = TableTextParser.Parse("d0 Loot\n1 Coin");
        Assert.Equal("d0", draft.DiceText);
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.UnsupportedDice && i.SourceLine == 1);
    }

    [Fact]
    public void No_heading_reports_missing_name_and_dice()
    {
        var draft = TableTextParser.Parse("1-2 Coin\n3-4 Gem");

        Assert.Equal("", draft.TableName);
        Assert.Equal(2, draft.ResultSets[0].Entries.Count);
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.NoTableName);
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.NoDiceExpression);
    }

    [Fact]
    public void Text_after_a_blank_line_is_not_joined_to_the_previous_row()
    {
        var draft = TableTextParser.Parse("d6 Loot\n1 Coin\n\nStray note\n2 Gem");

        Assert.Equal([("1", "Coin"), ("2", "Gem")], draft.ResultSets[0].Entries.Select(e => (e.RangeText, e.Text)).ToArray());
        var issue = Assert.Single(draft.Issues);
        Assert.Equal(ParseIssueCode.UnrecognizedLine, issue.Code);
        Assert.Equal(4, issue.SourceLine);
    }

    [Fact]
    public void Multi_line_continuation_joins_every_line_and_reports_each()
    {
        var draft = TableTextParser.Parse("d6 Loot\n1 A very\nlong\nwrapped row\n2 Gem");

        Assert.Equal("A very long wrapped row", draft.ResultSets[0].Entries[0].Text);
        Assert.Equal(2, draft.Issues.Count(i => i.Code == ParseIssueCode.ContinuationJoined));
        Assert.Equal((2, 4), (draft.ResultSets[0].Entries[0].SourceLineStart, draft.ResultSets[0].Entries[0].SourceLineEnd));
    }

    [Fact]
    public void Row_without_text_is_reported()
    {
        var draft = TableTextParser.Parse("d6 Loot\n1\n2 Gem");
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.EmptyEntryText && i.EntryIndex == 0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    [InlineData("just some prose\nwith no rows")]
    public void Input_with_no_rows_reports_no_entries_and_does_not_throw(string text)
    {
        var draft = TableTextParser.Parse(text);
        Assert.Empty(draft.ResultSets[0].Entries);
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.NoEntries);
        Assert.False(draft.TryBuildTable(1, out _, out var errors));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Null_input_does_not_throw()
    {
        Assert.Contains(TableTextParser.Parse(null).Issues, i => i.Code == ParseIssueCode.NoEntries);
    }

    [Fact]
    public void Build_reports_every_structural_problem()
    {
        var draft = TableTextParser.Parse("d10 Loot\n1-2 Coin\n3 Gem");
        draft.TableName = " ";
        draft.DiceText = "d6*1";
        draft.ResultSets[0].Entries[0].RangeText = "x";
        draft.ResultSets[0].Entries[1].RangeText = "9-3";

        Assert.False(draft.TryBuildTable(1, out var table, out var errors));
        Assert.Null(table);
        Assert.Equal(4, errors.Count);
    }
}
