using TableForge.Domain;
using TableForge.Import;

namespace TableForge.Tests;

public class MultiSetParserTests
{
    /// <summary>The representative copy: a heading, then three headed groups of rows.</summary>
    public const string RoomFeatures =
        "D100 ROOM FEATURES\n" +
        "\n" +
        "AMBIENT\n" +
        "01-30 Cold stale air\n" +
        "31-65 Damp stone\n" +
        "66-70 Smell of burning flesh\n" +
        "71-00 Heavy incense\n" +
        "\n" +
        "NOISE\n" +
        "01-25 Silence\n" +
        "26-50 Distant scratching\n" +
        "51-66 Dripping water\n" +
        "67-71 Hissing\n" +
        "72-00 Low chanting\n" +
        "\n" +
        "GENERAL FEATURE\n" +
        "01-40 Cracked stone walls\n" +
        "41-67 Broken furniture\n" +
        "68-69 Grated floors reveal dozens of people below\n" +
        "70-00 Carved pillars";

    private static (string Name, (string Range, string Text)[] Rows)[] Shape(TableImportDraft d) =>
        d.ResultSets.Select(s => (s.Name, s.Entries.Select(e => (e.RangeText, e.Text)).ToArray())).ToArray();

    private static string[] Flat(TableImportDraft d) =>
        d.ResultSets.SelectMany(s => new[] { $"SET {s.Name}" }.Concat(s.Entries.Select(e => $"{e.RangeText} {e.Text}"))).ToArray();

    private static ParseIssueCode[] Codes(TableImportDraft d) => d.Issues.Select(i => i.Code).ToArray();

    // ---- Room Features ----------------------------------------------------------------------

    [Fact]
    public void Room_features_becomes_three_named_result_sets_with_their_own_rows_and_no_issues()
    {
        var draft = TableTextParser.Parse(RoomFeatures);

        Assert.Equal("Room Features", draft.TableName);
        Assert.Equal("d100", draft.DiceText);
        Assert.Equal(
            [
                ("Ambient", new[] { ("01-30", "Cold stale air"), ("31-65", "Damp stone"), ("66-70", "Smell of burning flesh"), ("71-00", "Heavy incense") }),
                ("Noise", new[] { ("01-25", "Silence"), ("26-50", "Distant scratching"), ("51-66", "Dripping water"), ("67-71", "Hissing"), ("72-00", "Low chanting") }),
                ("General Feature", new[] { ("01-40", "Cracked stone walls"), ("41-67", "Broken furniture"), ("68-69", "Grated floors reveal dozens of people below"), ("70-00", "Carved pillars") }),
            ],
            Shape(draft));
        Assert.Empty(draft.Issues); // clear structure needs no review
    }

    [Fact]
    public void Room_features_builds_numeric_ranges_and_keeps_the_d100_display_forms()
    {
        var table = Fixtures.ParseAndBuild(RoomFeatures);

        var ambient = table.ResultSets[0].Entries;
        Assert.Equal((71, 100, "71–00"), (ambient[3].Min, ambient[3].Max, ambient[3].DisplayRange));
        Assert.Equal((1, 30, "01–30"), (ambient[0].Min, ambient[0].Max, ambient[0].DisplayRange));
        Assert.Equal((31, 65, (string?)null), (ambient[1].Min, ambient[1].Max, ambient[1].DisplayRange));
        Assert.Equal((72, 100, "72–00"), (table.ResultSets[1].Entries[4].Min, table.ResultSets[1].Entries[4].Max, table.ResultSets[1].Entries[4].DisplayRange));
        Assert.Empty(TableValidator.Validate(table));
    }

    [Fact]
    public void Imported_saved_and_reloaded_room_features_resolves_68_across_all_three_sets()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("Dungeon");
        var id = db.SaveTable(Fixtures.ParseAndBuild(RoomFeatures, collection.Id)).Id;

        var loaded = db.LoadTable(id)!;
        var resolution = TableResolver.Resolve(loaded, 68);

        Assert.Equal(["Ambient", "Noise", "General Feature"], resolution.Results.Select(r => r.ResultSet.Name).ToArray());
        Assert.Equal(["Smell of burning flesh", "Hissing", "Grated floors reveal dozens of people below"],
            resolution.Results.Select(r => r.Entry!.Text).ToArray());
        Assert.Equal(["Heavy incense", "Low chanting", "Carved pillars"],
            TableResolver.Resolve(loaded, 100).Results.Select(r => r.Entry!.Text).ToArray()); // "00"
    }

    [Fact]
    public void Windows_line_endings_and_extra_blank_lines_give_the_same_result()
    {
        var messy = RoomFeatures.Replace("\n", "\r\n").Replace("NOISE", "\r\n\r\nNOISE") + "\r\n\r\n";
        Assert.Equal(Flat(TableTextParser.Parse(RoomFeatures)), Flat(TableTextParser.Parse(messy)));
        Assert.Empty(TableTextParser.Parse(messy).Issues);
    }

    [Fact]
    public void Single_set_tables_are_unchanged_by_heading_detection()
    {
        var draft = TableTextParser.Parse(Fixtures.RandomStartingGear);
        var set = Assert.Single(draft.ResultSets);
        Assert.Equal("", set.Name);
        Assert.Equal(8, set.Entries.Count);
        Assert.Equal([ParseIssueCode.ContinuationJoined], Codes(draft));
    }

    // ---- heading recognition ----------------------------------------------------------------

    [Fact]
    public void Headings_with_colons_and_no_blank_lines_are_recognized_when_numbering_restarts()
    {
        var draft = TableTextParser.Parse("d6 Weather\nDAY:\n1-3 Sun\n4-6 Cloud\nNIGHT:\n1-3 Moon\n4-6 Stars");

        Assert.Equal(
            [("Day", new[] { ("1-3", "Sun"), ("4-6", "Cloud") }), ("Night", new[] { ("1-3", "Moon"), ("4-6", "Stars") })],
            Shape(draft));
        Assert.Empty(draft.Issues);
    }

    [Fact]
    public void Heading_directly_under_the_table_title_names_the_first_set()
    {
        var draft = TableTextParser.Parse("d6 Weather\nAMBIENT\n1-6 Fog");
        Assert.Equal("Ambient", Assert.Single(draft.ResultSets).Name);
        Assert.Empty(draft.Issues);
    }

    [Fact]
    public void Title_case_headings_separated_by_blank_lines_are_recognized()
    {
        var draft = TableTextParser.Parse("d6 Weather\n\nDay Weather\n1-3 Sun\n4-6 Cloud\n\nNight Weather\n1-6 Moon");
        Assert.Equal(["Day Weather", "Night Weather"], draft.ResultSets.Select(s => s.Name).ToArray());
        Assert.Empty(draft.Issues);
    }

    [Fact]
    public void Title_case_heading_with_nothing_separating_it_splits_but_asks_for_review()
    {
        var draft = TableTextParser.Parse("d6 Weather\nDay\n1-3 Sun\n4-6 Cloud\nNight\n1-6 Moon");

        Assert.Equal(["Day", "Night"], draft.ResultSets.Select(s => s.Name).ToArray());
        var issue = Assert.Single(draft.Issues);
        Assert.Equal(ParseIssueCode.ProbableResultSetHeading, issue.Code);
        Assert.Equal(ParseIssueTarget.ResultSet, issue.Target);
        Assert.Equal(1, issue.ResultSetIndex);
        Assert.Equal(5, issue.SourceLine);
    }

    [Fact]
    public void Uppercase_row_text_is_never_a_heading()
    {
        var draft = TableTextParser.Parse("d6 Signs\n1 DANGER\n2 KEEP OUT\n3 WET FLOOR\n4-6 SILENCE");

        var set = Assert.Single(draft.ResultSets);
        Assert.Equal(4, set.Entries.Count);
        Assert.Equal(["DANGER", "KEEP OUT", "WET FLOOR", "SILENCE"], set.Entries.Select(e => e.Text).ToArray());
        Assert.Empty(draft.Issues);
    }

    [Fact]
    public void Uppercase_wrapped_line_is_a_continuation_not_a_heading_when_numbering_continues()
    {
        var draft = TableTextParser.Parse("d6 Signs\n1 Painted in red\nWARNING\n2-6 Nothing");

        var set = Assert.Single(draft.ResultSets);
        Assert.Equal("Painted in red WARNING", set.Entries[0].Text);
        Assert.Equal([ParseIssueCode.ContinuationJoined], Codes(draft));
    }

    [Fact]
    public void Uppercase_line_where_numbering_does_not_restart_is_flagged_and_nothing_is_lost_or_split()
    {
        var draft = TableTextParser.Parse("d6 Loot\n1-3 Coin\n\nSTRAY NOTE\n4-6 Gem");

        var set = Assert.Single(draft.ResultSets);
        Assert.Equal([("1-3", "Coin"), ("4-6", "Gem")], set.Entries.Select(e => (e.RangeText, e.Text)).ToArray());
        var issue = Assert.Single(draft.Issues);
        Assert.Equal(ParseIssueCode.AmbiguousSectionBreak, issue.Code);
        Assert.Equal(4, issue.SourceLine);
    }

    [Fact]
    public void Heading_with_no_rows_after_it_is_not_invented()
    {
        var draft = TableTextParser.Parse("d6 Loot\n1-3 Coin\n4-6 Gem\n\nNOTES");

        Assert.Single(draft.ResultSets);
        var issue = Assert.Single(draft.Issues);
        Assert.Equal(ParseIssueCode.UnrecognizedLine, issue.Code);
        Assert.Equal(5, issue.SourceLine);
    }

    [Fact]
    public void Two_headings_in_a_row_only_the_one_followed_by_rows_starts_a_set()
    {
        var draft = TableTextParser.Parse("d6 Loot\nAMBIENT\nNOISE\n1-6 Hiss");

        Assert.Equal("Noise", Assert.Single(draft.ResultSets).Name);
        var issue = Assert.Single(draft.Issues);
        Assert.Equal(ParseIssueCode.UnrecognizedLine, issue.Code);
        Assert.Equal(2, issue.SourceLine);
    }

    [Fact]
    public void Rows_before_the_first_heading_go_to_an_unnamed_first_set_and_are_flagged()
    {
        var draft = TableTextParser.Parse("d6 Loot\n1-3 Coin\n4-6 Gem\n\nNOISE\n1-6 Hiss");

        Assert.Equal(["", "Noise"], draft.ResultSets.Select(s => s.Name).ToArray());
        Assert.Equal(2, draft.ResultSets[0].Entries.Count);
        var issue = Assert.Single(draft.Issues);
        Assert.Equal((ParseIssueCode.UnnamedResultSet, ParseIssueTarget.ResultSet, 0), (issue.Code, issue.Target, issue.ResultSetIndex));
    }

    [Fact]
    public void Existing_stray_note_behaviour_is_preserved()
    {
        var draft = TableTextParser.Parse("d6 Loot\n1 Coin\n\nStray note\n2 Gem");
        Assert.Single(draft.ResultSets);
        Assert.Equal(ParseIssueCode.UnrecognizedLine, Assert.Single(draft.Issues).Code);
    }

    [Fact]
    public void Entry_issues_point_at_the_right_set_and_row()
    {
        var draft = TableTextParser.Parse("d6 Loot\nAMBIENT\n1-3 Fog\n4-6 Very\nthick\nNOISE\n1-3 Hiss\n4-6 Very\nloud");

        var issues = draft.Issues.Where(i => i.Code == ParseIssueCode.ContinuationJoined).ToList();
        Assert.Equal([(0, 1), (1, 1)], issues.Select(i => (i.ResultSetIndex!.Value, i.EntryIndex!.Value)).ToArray());
        Assert.Equal("Very thick", draft.ResultSets[0].Entries[1].Text);
        Assert.Equal("Very loud", draft.ResultSets[1].Entries[1].Text);
    }

    // ---- side-by-side columns ---------------------------------------------------------------

    [Fact]
    public void A_line_with_two_clear_pairs_becomes_two_entries()
    {
        var draft = TableTextParser.Parse("d100 Names\n01-05 Ash        51-55 Kel");

        var set = Assert.Single(draft.ResultSets);
        Assert.Equal([("01-05", "Ash"), ("51-55", "Kel")], set.Entries.Select(e => (e.RangeText, e.Text)).ToArray());

        var table = Fixtures.ParseAndBuild("d100 Names\n01-05 Ash        51-55 Kel");
        Assert.Equal([(1, 5, "01–05"), (51, 55, (string?)null)], table.ResultSets[0].Entries.Select(e => (e.Min, e.Max, e.DisplayRange)).ToArray());
    }

    [Fact]
    public void Split_lines_are_reported_once_per_block_and_read_column_by_column()
    {
        var draft = TableTextParser.Parse(
            "d100 Names\n" +
            "01-05 Ash        51-55 Kel\n" +
            "06-10 Bar        56-60 Lor\n" +
            "11-15 Cor        61-65 Mor");

        var rows = draft.ResultSets[0].Entries.Select(e => e.RangeText + " " + e.Text).ToArray();
        Assert.Equal(["01-05 Ash", "06-10 Bar", "11-15 Cor", "51-55 Kel", "56-60 Lor", "61-65 Mor"], rows);

        var issue = Assert.Single(draft.Issues);
        Assert.Equal((ParseIssueCode.SideBySideSplit, ParseIssueSeverity.Info, ParseIssueTarget.Entry), (issue.Code, issue.Severity, issue.Target));
        Assert.Equal((0, 0, 2), (issue.ResultSetIndex!.Value, issue.EntryIndex!.Value, issue.SourceLine!.Value));
        Assert.Contains("Lines 2–4", issue.Message);
    }

    [Fact]
    public void Gaps_between_range_and_text_and_tabs_between_columns_are_both_understood()
    {
        var spaced = TableTextParser.Parse("d100 Names\n01-05    Ash        51-55    Kel");
        Assert.Equal(["Ash", "Kel"], spaced.ResultSets[0].Entries.Select(e => e.Text).ToArray());

        var tabbed = TableTextParser.Parse("d100 Names\n01-05 Ash\t51-55 Kel\t80-99 Nim");
        Assert.Equal(["Ash", "Kel", "Nim"], tabbed.ResultSets[0].Entries.Select(e => e.Text).ToArray());
    }

    [Fact]
    public void A_short_last_row_stays_under_its_column()
    {
        var draft = TableTextParser.Parse("d100 Names\n01-05 Ash        51-55 Kel\n06-10 Bar        56-60 Lor\n11-15 Cor");

        Assert.Equal(["Ash", "Bar", "Cor", "Kel", "Lor"], draft.ResultSets[0].Entries.Select(e => e.Text).ToArray());
    }

    [Fact]
    public void Split_entries_resolve_by_their_own_ranges()
    {
        var table = Fixtures.ParseAndBuild("d100 Names\n01-50 Ash        51-00 Kel");

        Assert.Equal("Ash", TableResolver.Resolve(table, 50).Results[0].Entry!.Text);
        Assert.Equal("Kel", TableResolver.Resolve(table, 51).Results[0].Entry!.Text);
        Assert.Equal("Kel", TableResolver.Resolve(table, 100).Results[0].Entry!.Text);
    }

    [Fact]
    public void Columns_inside_a_headed_set_stay_in_that_set()
    {
        var draft = TableTextParser.Parse("d100 Names\n\nFIRST\n01-50 Ash        51-00 Kel\n\nSECOND\n01-50 Bar        51-00 Lor");

        Assert.Equal([("First", new[] { ("01-50", "Ash"), ("51-00", "Kel") }), ("Second", new[] { ("01-50", "Bar"), ("51-00", "Lor") })], Shape(draft));
    }

    [Fact]
    public void A_single_space_before_the_second_range_is_not_split_but_is_flagged()
    {
        var draft = TableTextParser.Parse("d100 Names\n01-05 Ash 51-55 Kel");

        var entry = Assert.Single(draft.ResultSets[0].Entries);
        Assert.Equal(("01-05", "Ash 51-55 Kel"), (entry.RangeText, entry.Text));
        var issue = Assert.Single(draft.Issues);
        Assert.Equal((ParseIssueCode.MultipleRangesOnLine, ParseIssueTarget.Entry, 0, 0), (issue.Code, issue.Target, issue.ResultSetIndex, issue.EntryIndex));
    }

    [Fact]
    public void Columns_whose_ranges_do_not_continue_are_not_split_but_are_flagged()
    {
        // Two parallel tables side by side (both start at 01) cannot be safely assigned to one set.
        var draft = TableTextParser.Parse("d100 Both\n01-30 Cold air        01-25 Silence");

        var entry = Assert.Single(draft.ResultSets[0].Entries);
        Assert.Equal("Cold air 01-25 Silence", entry.Text);
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.MultipleRangesOnLine && i.EntryIndex == 0);
    }

    [Fact]
    public void Ordinary_multi_space_text_and_prices_in_the_text_do_not_trigger_splitting_or_flags()
    {
        var draft = TableTextParser.Parse("d6 Shop\n1-2  Backpack\n3 Sword    of Doom\n4 Rope (15-20 m)\n5-6 Rations for 1 day");

        Assert.Equal(["Backpack", "Sword of Doom", "Rope (15-20 m)", "Rations for 1 day"], draft.ResultSets[0].Entries.Select(e => e.Text).ToArray());
        Assert.Empty(draft.Issues);
    }

    [Fact]
    public void A_wrapped_line_after_columns_is_not_joined_to_either_column()
    {
        var draft = TableTextParser.Parse("d100 Names\n01-50 Ash        51-00 Kel\nwrapped bit");

        Assert.Equal(["Ash", "Kel"], draft.ResultSets[0].Entries.Select(e => e.Text).ToArray());
        var issue = draft.Issues.Single(i => i.Code == ParseIssueCode.UnrecognizedLine);
        Assert.Equal(3, issue.SourceLine);
        Assert.Contains("side-by-side", issue.Message);
    }

    // ---- row-only parsing -------------------------------------------------------------------

    [Fact]
    public void Row_only_parsing_reads_rows_with_continuations_and_columns_and_reports_relative_lines()
    {
        var result = TableTextParser.ParseRows("01-30 Cold air\n31-65 Damp\nstone\n66-70 A        71-00 B", DiceExpression.Parse("d100"));

        Assert.Equal([("01-30", "Cold air"), ("31-65", "Damp stone"), ("66-70", "A"), ("71-00", "B")],
            result.Entries.Select(e => (e.RangeText, e.Text)).ToArray());
        Assert.All(result.Entries, e => Assert.Equal(0, e.SourceLineStart)); // pasted rows have no place in the original text
        Assert.Contains(result.Issues, i => i.Code == ParseIssueCode.ContinuationJoined && i.EntryIndex == 1 && i.Message.StartsWith("Pasted line 3"));
        Assert.Contains(result.Issues, i => i.Code == ParseIssueCode.SideBySideSplit && i.EntryIndex == 2);
    }

    [Fact]
    public void Row_only_parsing_never_treats_headings_or_dice_lines_as_structure()
    {
        var result = TableTextParser.ParseRows("D6 OTHER TABLE\nNOISE\n1-3 Hiss\n4-6 Drip", DiceExpression.Parse("d6"));

        Assert.Equal(["Hiss", "Drip"], result.Entries.Select(e => e.Text).ToArray());
        var unrecognized = result.Issues.Where(i => i.Code == ParseIssueCode.UnrecognizedLine).ToList();
        Assert.Equal([1, 2], unrecognized.Select(i => i.SourceLine!.Value).ToArray());
    }

    [Fact]
    public void Row_only_parsing_of_text_without_rows_reports_no_entries()
    {
        var result = TableTextParser.ParseRows("just some words\nand more", null);
        Assert.Empty(result.Entries);
        Assert.Contains(result.Issues, i => i.Code == ParseIssueCode.NoEntries);
        Assert.Empty(TableTextParser.ParseRows(null, null).Entries);
    }

    [Fact]
    public void Row_only_parsing_understands_00_on_a_d100_but_not_elsewhere()
    {
        var d100 = TableTextParser.ParseRows("96-00 Gold", DiceExpression.Parse("d100"));
        Assert.True(d100.Entries[0].TryParseRange(DiceExpression.Parse("d100"), out var range, out _));
        Assert.Equal((96, 100), (range.Min, range.Max));
    }
}
