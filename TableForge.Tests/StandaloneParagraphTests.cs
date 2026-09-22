using TableForge.Domain;
using TableForge.Import;

namespace TableForge.Tests;

/// <summary>Part A: a row number (or range) on its own line takes the paragraph that follows it as its result.</summary>
public class StandaloneParagraphTests
{
    /// <summary>The real Broken Shores source that RC1 turned into two blank rows.</summary>
    public const string WhyDoYouGoOn =
        "D12 WHY DO YOU GO ON?\n" +
        "\n" +
        "1\n" +
        "\n" +
        "You are terrified of reality tearing itself apart as the result of\n" +
        "someone casting the wrong spell. You seek for a permanent\n" +
        "solution to the danger of sorcery, to safely cast magic\n" +
        "without risking madness or demonic mutation.\n" +
        "\n" +
        "2\n" +
        "\n" +
        "In a brutal world where fresh water is worth far more than\n" +
        "blood, your goal is to find an unpolluted, newly emerged\n" +
        "island with a natural spring where you can settle down and\n" +
        "never worry about dehydration again.";

    private const string Row1 =
        "You are terrified of reality tearing itself apart as the result of someone casting the wrong spell. " +
        "You seek for a permanent solution to the danger of sorcery, to safely cast magic without risking madness or demonic mutation.";

    private const string Row2 =
        "In a brutal world where fresh water is worth far more than blood, your goal is to find an unpolluted, newly emerged " +
        "island with a natural spring where you can settle down and never worry about dehydration again.";

    private static string[] Rows(TableImportDraft d) => d.ResultSets[0].Entries.Select(e => $"{e.RangeText} | {e.Text}").ToArray();

    [Fact]
    public void The_broken_shores_example_pairs_each_number_with_its_paragraph()
    {
        var draft = TableTextParser.Parse(WhyDoYouGoOn);

        Assert.Equal("Why Do You Go On?", draft.TableName);
        Assert.Equal("d12", draft.DiceText);
        Assert.Equal([$"1 | {Row1}", $"2 | {Row2}"], Rows(draft));
        Assert.Single(draft.ResultSets);
    }

    [Fact]
    public void The_pairing_is_reported_once_as_information_not_as_a_warning_per_line()
    {
        var draft = TableTextParser.Parse(WhyDoYouGoOn);

        var issue = Assert.Single(draft.Issues);
        Assert.Equal((ParseIssueCode.ParagraphsAttached, ParseIssueSeverity.Info, ParseIssueTarget.Entry), (issue.Code, issue.Severity, issue.Target));
        Assert.Equal((0, 0), (issue.ResultSetIndex, issue.EntryIndex));
    }

    [Fact]
    public void Source_lines_span_the_number_and_its_whole_paragraph()
    {
        var entries = TableTextParser.Parse(WhyDoYouGoOn).ResultSets[0].Entries;

        Assert.Equal((3, 8), (entries[0].SourceLineStart, entries[0].SourceLineEnd));
        Assert.Equal((10, 15), (entries[1].SourceLineStart, entries[1].SourceLineEnd));
    }

    [Fact]
    public void A_range_on_its_own_line_works_like_a_single_number()
    {
        var draft = TableTextParser.Parse("d8 Weather\n\n1-3\n\nFair skies all day long.\n\n4-8\nA slow grey drizzle.");

        Assert.Equal(["1-3 | Fair skies all day long.", "4-8 | A slow grey drizzle."], Rows(draft));
    }

    [Fact]
    public void Any_number_of_blank_lines_before_the_text_is_fine_and_none_is_fine_too()
    {
        var draft = TableTextParser.Parse("d6 Loot\n1\n\n\n\nA gem\n2\nA coin\n\n3\n\n\nA ring");

        Assert.Equal(["1 | A gem", "2 | A coin", "3 | A ring"], Rows(draft));
    }

    [Fact]
    public void A_multi_line_paragraph_is_joined_with_single_spaces()
    {
        var draft = TableTextParser.Parse("d6 Loot\n1\n\nfirst line\nsecond line\nthird line\n2\n\nx");

        Assert.Equal("first line second line third line", draft.ResultSets[0].Entries[0].Text);
    }

    [Fact]
    public void The_next_number_or_range_stops_accumulation_and_inline_rows_still_work()
    {
        var draft = TableTextParser.Parse("d6 Loot\n1\n\nFirst paragraph.\n\n2\n\nSecond paragraph.\n\n3 Third, inline\n4-6\n\nFourth.");

        Assert.Equal(["1 | First paragraph.", "2 | Second paragraph.", "3 | Third, inline", "4-6 | Fourth."], Rows(draft));
    }

    [Fact]
    public void A_row_with_its_text_on_the_same_line_does_not_accumulate_later_paragraphs()
    {
        // Existing behavior: text after a blank line does not belong to a row that already has its text.
        var draft = TableTextParser.Parse("d6 Loot\n1 Coin\n\nStray note\n2 Gem");

        Assert.Equal(["1 | Coin", "2 | Gem"], Rows(draft));
        Assert.Equal(ParseIssueCode.UnrecognizedLine, Assert.Single(draft.Issues).Code);
    }

    [Fact]
    public void Prose_before_the_first_numbered_row_stays_reviewable_and_is_not_attached_to_anything()
    {
        var draft = TableTextParser.Parse("D12 WHY DO YOU GO ON?\n\nChoose the reason that suits your character best.\n\n1\n\nFirst paragraph.\n\n2\n\nSecond.");

        Assert.Equal(["1 | First paragraph.", "2 | Second."], Rows(draft));
        var stray = Assert.Single(draft.Issues, i => i.Code == ParseIssueCode.UnrecognizedLine);
        Assert.Equal(3, stray.SourceLine);
        Assert.Contains("Choose the reason", stray.Message);
    }

    [Fact]
    public void A_second_paragraph_is_kept_after_a_blank_line_and_flagged_for_review()
    {
        var draft = TableTextParser.Parse("d6 Loot\n1\n\nFirst paragraph.\n\nBroken Shores 12\n\n2\n\nSecond.");

        Assert.Equal("First paragraph.\n\nBroken Shores 12", draft.ResultSets[0].Entries[0].Text);
        var issue = Assert.Single(draft.Issues, i => i.Code == ParseIssueCode.MultipleParagraphs);
        Assert.Equal((ParseIssueSeverity.Warning, ParseIssueTarget.Entry, 0, 0), (issue.Severity, issue.Target, issue.ResultSetIndex, issue.EntryIndex));
    }

    [Fact]
    public void A_line_starting_with_a_number_that_does_not_continue_the_numbering_is_kept_as_text_and_flagged()
    {
        var draft = TableTextParser.Parse("d12 Reasons\n1\n\nYou will wait\n10 days for the rain to stop.\n\n2\n\nThe next reason.");

        Assert.Equal(["1 | You will wait 10 days for the rain to stop.", "2 | The next reason."], Rows(draft));
        var issue = Assert.Single(draft.Issues, i => i.Code == ParseIssueCode.NumberedLineKeptAsText);
        Assert.Equal(5, issue.SourceLine);
        Assert.Equal(ParseIssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void A_lone_number_always_starts_a_new_row_even_if_it_does_not_continue_the_numbering()
    {
        var draft = TableTextParser.Parse("d100 Reasons\n01\n\nOne.\n\n50\n\nFifty.");

        Assert.Equal(["01 | One.", "50 | Fifty."], Rows(draft));
    }

    [Fact]
    public void A_number_that_continues_the_numbering_is_a_row_even_with_text_on_its_line()
    {
        var draft = TableTextParser.Parse("d6 Loot\n1\n\nFirst.\n2 Second, inline.");

        Assert.Equal(["1 | First.", "2 | Second, inline."], Rows(draft));
    }

    [Fact]
    public void Rows_that_never_receive_text_are_still_reported()
    {
        var draft = TableTextParser.Parse("d6 Loot\n1\n2 Gem");   // existing behavior

        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.EmptyEntryText && i.EntryIndex == 0);
        Assert.Equal(["1 | ", "2 | Gem"], Rows(draft));
    }

    [Fact]
    public void Headings_still_win_over_paragraph_accumulation_in_standalone_number_tables()
    {
        var draft = TableTextParser.Parse("d6 Weather\n\nDAY\n1\n\nSunny and warm.\n\nNIGHT\n1\n\nClear and cold.");

        Assert.Equal(["Day", "Night"], draft.ResultSets.Select(s => s.Name).ToArray());
        Assert.Equal("Sunny and warm.", draft.ResultSets[0].Entries[0].Text);
        Assert.Equal("Clear and cold.", draft.ResultSets[1].Entries[0].Text);
    }

    [Fact]
    public void Pasting_rows_uses_the_same_rule_and_reports_pasted_lines()
    {
        var result = TableTextParser.ParseRows("1\n\nFirst paragraph\nwrapped.\n\n2\n\nSecond.", DiceExpression.Parse("d12"));

        Assert.Equal(["First paragraph wrapped.", "Second."], result.Entries.Select(e => e.Text).ToArray());
        Assert.Contains(result.Issues, i => i.Code == ParseIssueCode.ParagraphsAttached && i.Message.Contains("row 1"));
    }

    [Fact]
    public void The_imported_table_saves_and_rolls_the_paragraph()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var table = db.SaveTable(Fixtures.ParseAndBuild(WhyDoYouGoOn, db.CreateCollection("C").Id));

        var loaded = db.LoadTable(table.Id)!;

        Assert.Equal(new DiceExpression(1, 12), loaded.Dice);
        Assert.Equal(Row2, TableResolver.Resolve(loaded, 2).Results[0].Entry!.Text);
        Assert.Equal(ResolutionStatus.NoMatch, TableResolver.Resolve(loaded, 7).Results[0].Status);   // rows 3-12 were not in the excerpt
    }
}
