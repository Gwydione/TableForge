using TableForge.Data;
using TableForge.Domain;
using TableForge.Import;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>
/// The raw Paste Table editor's cleanup tools, added as a follow-up to the Review cleanup pass so obvious clipboard
/// damage can be repaired before parsing, instead of only after: Ctrl+J (raw line join), Normalize Text, Dehyphenate,
/// and the replacement-character warning. These are the pure, WPF-free parts; <c>PasteRawCleanupViewTests</c> covers
/// the buttons and warning banner through the real view.
/// </summary>
public class RawLineJoinTests
{
    [Fact]
    public void Joins_the_current_line_into_the_previous_line_with_one_space()
    {
        var text = "7 You find a damaged chest containing\nseveral old coins and a silver key.";
        var caret = text.IndexOf("several", StringComparison.Ordinal) + 3; // caret is anywhere on the second line

        var joined = TextCleanup.TryJoinLineWithPrevious(text, caret, out var result, out var newCaret, out var message);

        Assert.True(joined);
        Assert.Equal("7 You find a damaged chest containing several old coins and a silver key.", result);
        Assert.Equal("Joined the current line with the previous line.", message);
        Assert.InRange(newCaret, 0, result.Length);
    }

    [Fact]
    public void Trims_trailing_and_leading_whitespace_at_the_join()
    {
        var text = "First part   \n   second part.";
        var caret = text.IndexOf("second", StringComparison.Ordinal);

        TextCleanup.TryJoinLineWithPrevious(text, caret, out var result, out _, out _);

        Assert.Equal("First part second part.", result);
    }

    [Fact]
    public void Refuses_and_changes_nothing_when_the_caret_is_on_the_first_line()
    {
        var text = "Only one line\nand a second one";

        var joined = TextCleanup.TryJoinLineWithPrevious(text, 4, out var result, out var newCaret, out var message);

        Assert.False(joined);
        Assert.Equal(text, result);
        Assert.Equal(4, newCaret);
        Assert.Contains("no previous line", message);
    }

    [Fact]
    public void A_blank_continuation_line_is_simply_removed()
    {
        var text = "Heading\n   \nBody";
        var caret = text.IndexOf("   ", StringComparison.Ordinal) + 1;

        var joined = TextCleanup.TryJoinLineWithPrevious(text, caret, out var result, out _, out _);

        Assert.True(joined);
        Assert.Equal("Heading\nBody", result);
    }

    [Fact]
    public void A_blank_previous_line_is_simply_removed()
    {
        var text = "\nBody text";
        var caret = text.IndexOf("Body", StringComparison.Ordinal);

        TextCleanup.TryJoinLineWithPrevious(text, caret, out var result, out _, out _);

        Assert.Equal("Body text", result);
    }

    [Theory]
    [InlineData("Line one\r\nLine two")]
    [InlineData("Line one\nLine two")]
    public void CRLF_and_LF_line_breaks_both_work(string text)
    {
        var caret = text.IndexOf("Line two", StringComparison.Ordinal) + 2;

        var joined = TextCleanup.TryJoinLineWithPrevious(text, caret, out var result, out _, out _);

        Assert.True(joined);
        Assert.Equal("Line one Line two", result);
    }

    [Fact]
    public void Surrounding_lines_are_left_exactly_as_they_were()
    {
        var text = "Row A\nRow B first\nRow B second\nRow C";
        var caret = text.IndexOf("Row B second", StringComparison.Ordinal) + 3;

        var joined = TextCleanup.TryJoinLineWithPrevious(text, caret, out var result, out _, out _);

        Assert.True(joined);
        Assert.Equal("Row A\nRow B first Row B second\nRow C", result);
    }

    [Fact]
    public void The_caret_lands_at_the_join_point_between_the_two_pieces()
    {
        var text = "Alpha\nBeta";

        TextCleanup.TryJoinLineWithPrevious(text, 7, out var result, out var newCaret, out _);

        Assert.Equal("Alpha Beta", result);
        Assert.Equal("Alpha ".Length, newCaret); // right before "Beta"
    }

    [Fact]
    public void Caret_exactly_at_the_start_of_the_second_line_still_counts_as_the_second_line()
    {
        var text = "Alpha\nBeta";
        var caret = text.IndexOf('\n') + 1; // the very first character of line two

        var joined = TextCleanup.TryJoinLineWithPrevious(text, caret, out var result, out _, out _);

        Assert.True(joined);
        Assert.Equal("Alpha Beta", result);
    }
}

/// <summary>Ctrl+J with a selection across several raw lines (<see cref="TextCleanup.TryJoinSelectedLines"/>): joins them all at once.</summary>
public class BulkLineJoinTests
{
    /// <summary>Selects <paramref name="selected"/> (its first occurrence) in <paramref name="text"/> and applies the bulk join, if it happens.</summary>
    private static (bool Joined, string Result, int Caret) Join(string text, string selected) =>
        Join(text, text.IndexOf(selected, StringComparison.Ordinal), selected.Length);

    private static (bool Joined, string Result, int Caret) Join(string text, int start, int length)
    {
        Assert.True(start >= 0, "the selection must be in the text");
        if (!TextCleanup.TryJoinSelectedLines(text, start, length, out var at, out var count, out var replacement, out _))
            return (false, text, start);
        return (true, text[..at] + replacement + text[(at + count)..], at + replacement.Length);
    }

    [Fact]
    public void Two_selected_lines_become_one()
    {
        Assert.Equal((true, "Alpha Beta", 10), Join("Alpha\nBeta", "Alpha\nBeta"));
    }

    [Fact]
    public void Three_or_more_selected_lines_become_one()
    {
        Assert.Equal("Alpha Beta Gamma", Join("Alpha\nBeta\nGamma", "Alpha\nBeta\nGamma").Result);
        Assert.Equal("Alpha Beta Gamma Delta", Join("Alpha\nBeta\nGamma\nDelta", "Alpha\nBeta\nGamma\nDelta").Result);
    }

    [Fact]
    public void The_wrapped_pdf_row_joins_into_one_logical_line()
    {
        var text = "01-02 Kael Dravorn\nthe scarred scout\nfrom the north\n03-04 Morthan Vex";

        var (joined, result, _) = Join(text, "01-02 Kael Dravorn\nthe scarred scout\nfrom the north");

        Assert.True(joined);
        Assert.Equal("01-02 Kael Dravorn the scarred scout from the north\n03-04 Morthan Vex", result);
    }

    [Fact]
    public void Whitespace_at_each_line_boundary_is_trimmed_to_exactly_one_space()
    {
        var text = "Alpha   \n   Beta";
        Assert.Equal("Alpha Beta", Join(text, text).Result);

        var tabs = "\tAlpha\t\n\t Beta \t";
        Assert.Equal("Alpha Beta", Join(tabs, tabs).Result);
    }

    [Fact]
    public void Spacing_inside_a_line_is_left_as_it_was()
    {
        var text = "Alpha  one\nBeta";
        Assert.Equal("Alpha  one Beta", Join(text, text).Result); // only the join itself is normalized; Normalize Text is separate
    }

    [Theory]
    [InlineData("Alpha\n\nBeta")]
    [InlineData("Alpha\n   \nBeta")]
    [InlineData("Alpha\n\n\n\t\nBeta")]
    [InlineData("\nAlpha\nBeta\n")]
    public void Blank_selected_lines_are_dropped_without_extra_spaces(string text)
    {
        Assert.Equal("Alpha Beta", Join(text, text).Result.Trim('\n'));
    }

    [Fact]
    public void Text_outside_the_selected_lines_is_preserved_exactly()
    {
        var (joined, result, caret) = Join("KEEP\nAlpha\nBeta\nKEEP2", "Alpha\nBeta");

        Assert.True(joined);
        Assert.Equal("KEEP\nAlpha Beta\nKEEP2", result);
        Assert.Equal("KEEP\nAlpha Beta".Length, caret); // at the end of the joined text
    }

    [Fact]
    public void CRLF_and_mixed_line_breaks_outside_the_selection_are_kept()
    {
        var text = "KEEP\r\nAlpha\r\nBeta\nGamma\r\nKEEP2";
        Assert.Equal("KEEP\r\nAlpha Beta Gamma\r\nKEEP2", Join(text, "Alpha\r\nBeta\nGamma").Result);
    }

    [Fact]
    public void Hyphens_are_joined_literally_not_dehyphenated()
    {
        Assert.Equal("well- made weapon", Join("well-\nmade weapon", "well-\nmade weapon").Result);
    }

    [Fact]
    public void Replacement_characters_are_left_untouched()
    {
        var text = "Caf� in the\nwoods �";
        Assert.Equal("Caf� in the woods �", Join(text, text).Result);
    }

    [Fact]
    public void Nothing_happens_without_a_selection_so_the_single_line_join_applies()
    {
        Assert.False(Join("Alpha\nBeta", 7, 0).Joined);
    }

    [Fact]
    public void A_selection_within_one_line_is_not_a_bulk_join()
    {
        Assert.False(Join("Alpha\nBeta words", "words").Joined);
        Assert.False(Join("Alpha\nBeta", "Beta").Joined);
    }

    [Fact]
    public void One_whole_line_selected_with_its_line_break_is_not_a_bulk_join()
    {
        // Selecting "Alpha\n" ends at the very start of "Beta": nothing of Beta's line is selected.
        Assert.False(Join("Alpha\nBeta", "Alpha\n").Joined);
    }

    [Fact]
    public void A_selection_ending_at_the_start_of_the_next_line_does_not_pull_that_line_in()
    {
        Assert.Equal("Alpha Beta\nGamma", Join("Alpha\nBeta\nGamma", "Alpha\nBeta\n").Result);
    }

    // ---- partial-line selections: the whole touched lines are joined, and the unselected parts are kept -------------

    [Fact]
    public void A_selection_starting_and_ending_mid_line_joins_the_whole_touched_lines()
    {
        var text = "KEEP\n01-02 Kael Dravorn\nthe scarred scout\nKEEP2";

        var (joined, result, _) = Join(text, "Dravorn\nthe scarred");

        Assert.True(joined);
        Assert.Equal("KEEP\n01-02 Kael Dravorn the scarred scout\nKEEP2", result); // "01-02 Kael " and " scout" are kept
    }

    [Fact]
    public void A_selection_from_the_end_of_one_line_takes_that_line_break_and_joins_across_it()
    {
        var text = "Alpha\nBeta\nGamma";
        var start = text.IndexOf('\n'); // right after "Alpha", before its line break

        Assert.Equal("Alpha Beta\nGamma", Join(text, start, 2).Result); // "\nB"
    }

    [Fact]
    public void A_partial_selection_never_loses_or_reorders_any_non_whitespace_text()
    {
        var text = "one two\nthree four\nfive six";
        for (var start = 0; start < text.Length; start++)
            for (var length = 1; start + length <= text.Length; length++)
            {
                var (joined, result, caret) = Join(text, start, length);
                if (!joined) continue;
                Assert.Equal(string.Concat(text.Where(c => !char.IsWhiteSpace(c))), string.Concat(result.Where(c => !char.IsWhiteSpace(c))));
                Assert.DoesNotContain("  ", result);
                Assert.InRange(caret, 0, result.Length);
            }
    }
}

public class NormalizePastedTextTests
{
    [Fact]
    public void Normalizes_nbsp_tabs_repeated_spaces_and_ligatures_one_line_at_a_time()
    {
        var text = "D4 Bandages\r\n4D6+5\tmagniﬁcent gem\n  UD6   extra   spaces  \nAlready clean";

        var result = TextCleanup.NormalizePastedText(text);

        Assert.Equal("D4 Bandages\r\n4D6+5 magnificent gem\nUD6 extra spaces\nAlready clean", result);
    }

    [Fact]
    public void Preserves_mixed_CRLF_and_LF_line_breaks_exactly()
    {
        var text = "One\r\nTwo\nThree";

        Assert.Equal(text, TextCleanup.NormalizePastedText(text));
    }

    [Fact]
    public void Does_not_flatten_the_pasted_table_into_one_paragraph()
    {
        var text = "1 Coin\n2 Gem\n3 Rope";

        var result = TextCleanup.NormalizePastedText(text);

        Assert.Equal(text, result);
        Assert.Equal(3, result.Split('\n').Length);
    }

    [Fact]
    public void Does_not_alter_prose_punctuation_or_dice_expressions()
    {
        var text = "A well-made half-orc two-handed axe — “fine work”.\n2d6+1 charge";

        Assert.Equal(text, TextCleanup.NormalizePastedText(text));
    }
}

public class RawDehyphenateTests
{
    [Fact]
    public void Removes_a_pdf_line_wrap_hyphen_across_a_real_line_break()
    {
        var text = "The gem is magnifi-\ncent and old.";

        var dehyphenated = TextCleanup.TryDehyphenate(text, out var result);

        Assert.True(dehyphenated);
        Assert.Equal("The gem is magnificent and old.", result);
    }

    [Fact]
    public void Works_across_a_CRLF_line_break_too()
    {
        var dehyphenated = TextCleanup.TryDehyphenate("magnifi-\r\ncent", out var result);

        Assert.True(dehyphenated);
        Assert.Equal("magnificent", result);
    }

    [Fact]
    public void Leaves_legitimate_hyphenated_words_untouched()
    {
        var text = "A well-made half-orc\ntwo-handed axe";

        var dehyphenated = TextCleanup.TryDehyphenate(text, out var result);

        Assert.False(dehyphenated);
        Assert.Equal(text, result);
    }

    [Fact]
    public void Does_not_cross_a_blank_line_paragraph_break()
    {
        var text = "Section one ends here-\n\ncontinues wrongly";

        var dehyphenated = TextCleanup.TryDehyphenate(text, out var result);

        Assert.False(dehyphenated);
        Assert.Equal(text, result);
    }

    [Fact]
    public void Multiple_line_wrap_hyphens_in_one_paste_are_all_joined()
    {
        var text = "1 A magnifi-\ncent gem\n2 A won-\nderful ring";

        TextCleanup.TryDehyphenate(text, out var result);

        Assert.Equal("1 A magnificent gem\n2 A wonderful ring", result);
    }
}

public class ReplacementCharacterCountTests
{
    [Fact]
    public void Counts_every_replacement_character_in_the_text()
    {
        Assert.Equal(0, TextCleanup.CountReplacementCharacters("clean text"));
        Assert.Equal(1, TextCleanup.CountReplacementCharacters("one � mark"));
        Assert.Equal(3, TextCleanup.CountReplacementCharacters("� two � three �"));
        Assert.Equal(0, TextCleanup.CountReplacementCharacters(null));
    }
}

public class PasteRawCleanupViewModelTests
{
    private static (TempDatabase Temp, AppDatabase Db, PasteViewModel Paste) StartPaste()
    {
        var temp = new TempDatabase();
        var db = temp.Open();
        db.CreateCollection("Dungeon");
        var main = new MainViewModel(db, new FixedDice(1));
        main.PasteTableCommand.Execute(null);
        return (temp, db, (PasteViewModel)main.Current!);
    }

    [Fact]
    public void The_replacement_character_warning_reports_a_count_and_never_changes_the_text()
    {
        var (temp, db, paste) = StartPaste();
        using var _1 = temp; using var _2 = db;

        Assert.False(paste.HasDamagedCharacters);
        Assert.Equal("", paste.DamagedCharacterWarning);

        paste.SourceText = "1 magn�icent gem\n2 wea�on\n3 ro�e";

        Assert.True(paste.HasDamagedCharacters);
        Assert.Equal(3, paste.DamagedCharacterCount);
        Assert.Contains("3 damaged characters detected.", paste.DamagedCharacterWarning);
        Assert.Contains("could not be copied correctly", paste.DamagedCharacterWarning);
        Assert.Contains('�', paste.SourceText); // never altered
    }

    [Fact]
    public void The_warning_clears_once_the_damaged_characters_are_gone()
    {
        var (temp, db, paste) = StartPaste();
        using var _1 = temp; using var _2 = db;
        paste.SourceText = "1 magn�icent gem";
        Assert.True(paste.HasDamagedCharacters);

        paste.SourceText = "1 magnificent gem";

        Assert.False(paste.HasDamagedCharacters);
        Assert.Equal(0, paste.DamagedCharacterCount);
        Assert.Equal("", paste.DamagedCharacterWarning);
    }

    [Fact]
    public void Singular_wording_is_used_for_exactly_one_damaged_character()
    {
        var (temp, db, paste) = StartPaste();
        using var _1 = temp; using var _2 = db;

        paste.SourceText = "one � mark";

        Assert.StartsWith("1 damaged character detected.", paste.DamagedCharacterWarning);
    }

    [Fact]
    public void Cleanup_message_starts_empty_until_a_raw_cleanup_action_sets_it()
    {
        var (temp, db, paste) = StartPaste();
        using var _1 = temp; using var _2 = db;

        Assert.Equal("", paste.CleanupMessage);
    }
}

/// <summary>Confirms the whole point of pre-parse cleanup: what Ctrl+J joins on the Paste screen survives parsing.</summary>
public class RawJoinParserPreservationTests
{
    private static (TempDatabase Temp, AppDatabase Db, MainViewModel Main) Start()
    {
        var temp = new TempDatabase();
        var db = temp.Open();
        db.CreateCollection("Dungeon");
        return (temp, db, new MainViewModel(db, new FixedDice(1)));
    }

    [Fact]
    public void A_continuation_line_joined_before_parsing_survives_as_part_of_the_intended_row()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        main.PasteTableCommand.Execute(null);
        var paste = (PasteViewModel)main.Current!;

        // "several old coins..." has no leading number of its own, so the parser would actually join this particular
        // shape on its own — the point here is only that Ctrl+J's result, once pasted back, parses cleanly.
        var raw = "d6 Loot\n1 You find a damaged chest containing\nseveral old coins and a silver key.\n2 Gem\n3 Rope\n4 Torch\n5 Knife\n6 Bandage";
        var caret = raw.IndexOf("several", StringComparison.Ordinal);

        var joined = TextCleanup.TryJoinLineWithPrevious(raw, caret, out var joinedText, out _, out _);
        Assert.True(joined);
        paste.SourceText = joinedText;

        paste.InterpretCommand.Execute(null);
        var review = (ReviewViewModel)main.Current!;

        Assert.Equal(6, review.SelectedResultSet.Rows.Count);
        Assert.Equal("You find a damaged chest containing several old coins and a silver key.", review.SelectedResultSet.Rows[0].Text);
    }

    [Fact]
    public void A_continuation_line_that_starts_with_its_own_number_would_otherwise_be_split_into_a_bogus_row()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        main.PasteTableCommand.Execute(null);
        var paste = (PasteViewModel)main.Current!;

        // Without joining first, the parser reads "12 gleaming coins." as a brand-new numbered row (out of range for
        // a d6 table) rather than as the rest of row 6 — exactly the kind of continuation Review-only cleanup is too
        // late for, because by the time it reaches Review the text has already been split apart.
        var raw = "d6 Loot\n1 Coin\n2 Knife\n3 Rope\n4 Torch\n5 Bandage\n6 The chest also holds\n12 gleaming coins.";
        var caret = raw.IndexOf("12 gleaming coins.", StringComparison.Ordinal);

        var joined = TextCleanup.TryJoinLineWithPrevious(raw, caret, out var joinedText, out _, out _);
        Assert.True(joined);
        paste.SourceText = joinedText;

        paste.InterpretCommand.Execute(null);
        var review = (ReviewViewModel)main.Current!;

        Assert.Equal(6, review.SelectedResultSet.Rows.Count);
        Assert.Equal("The chest also holds 12 gleaming coins.", review.SelectedResultSet.Rows[5].Text);
        Assert.Empty(review.ValidationNotes);
        Assert.True(review.CanSave);
    }
}
