using TableForge.Data;
using TableForge.Domain;
using TableForge.Import;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>The PDF copy/paste cleanup pass: Join With Previous Row, Remove Empty Rows, Normalize Text, Dehyphenate,
/// the bad-replacement-character warning, and their single-level Undo.</summary>
public class PdfCleanupTests
{
    private static (TempDatabase Temp, AppDatabase Db, MainViewModel Main) Start()
    {
        var temp = new TempDatabase();
        var db = temp.Open();
        db.CreateCollection("Dungeon");
        return (temp, db, new MainViewModel(db, new FixedDice(6)));
    }

    private static ReviewViewModel Import(MainViewModel main, string text)
    {
        main.PasteTableCommand.Execute(null);
        var paste = (PasteViewModel)main.Current!;
        paste.SourceText = text;
        paste.InterpretCommand.Execute(null);
        return (ReviewViewModel)main.Current!;
    }

    // ---- Join With Previous Row ----------------------------------------------------------------

    [Fact]
    public void Join_combines_a_continuation_row_into_the_previous_row_and_removes_it()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1 First\n2 Second\n3 Third\n4 Fourth\n5 Fifth\n6 Sixth");
        var set = review.SelectedResultSet;
        var countBefore = set.Rows.Count;

        // Row 1 holds the first sentence; row 2, with no range of its own, is the wrapped continuation.
        set.Rows[0].RangeText = "1";
        set.Rows[0].Text = "Construction supplies are stored";
        set.Rows[1].RangeText = "";
        set.Rows[1].Text = "inside a damaged wooden crate.";
        review.SelectedRow = set.Rows[1];

        review.JoinWithPreviousRowCommand.Execute(null);

        Assert.Equal("Construction supplies are stored inside a damaged wooden crate.", set.Rows[0].Text);
        Assert.Equal("1", set.Rows[0].RangeText);
        Assert.Equal(countBefore - 1, set.Rows.Count);
        Assert.Same(set.Rows[0], review.SelectedRow);
    }

    [Fact]
    public void Join_trims_and_joins_with_exactly_one_space()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1 First\n2 Second\n3 Third\n4 Fourth\n5 Fifth\n6 Sixth");
        var set = review.SelectedResultSet;
        set.Rows[0].Text = "You find an abandoned shrine containing   ";
        set.Rows[1].RangeText = "";
        set.Rows[1].Text = "   several offerings and a damaged statue.";
        review.SelectedRow = set.Rows[1];

        review.JoinWithPreviousRowCommand.Execute(null);

        Assert.Equal("You find an abandoned shrine containing several offerings and a damaged statue.", set.Rows[0].Text);
    }

    [Fact]
    public void Join_refuses_when_the_selected_row_has_its_own_range()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1 Coin\n2 Gem\n3 Rope\n4 Torch\n5 Knife\n6 Bandage");
        var set = review.SelectedResultSet;
        review.SelectedRow = set.Rows[1]; // "2 Gem" has a legitimate range: refuse rather than destroy it

        review.JoinWithPreviousRowCommand.Execute(null);

        Assert.Equal(6, set.Rows.Count);
        Assert.Equal("Coin", set.Rows[0].Text);
        Assert.Equal("Gem", set.Rows[1].Text);
        Assert.Contains("its own range", review.CleanupMessage);
    }

    [Fact]
    public void Join_refuses_on_the_first_row_of_a_result_set()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1 Coin\n2 Gem\n3 Rope\n4 Torch\n5 Knife\n6 Bandage");
        var set = review.SelectedResultSet;
        set.Rows[0].RangeText = "";
        review.SelectedRow = set.Rows[0];

        review.JoinWithPreviousRowCommand.Execute(null);

        Assert.Equal(6, set.Rows.Count);
        Assert.Contains("no previous row", review.CleanupMessage);
    }

    [Fact]
    public void Join_never_crosses_a_result_set_boundary()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d100 Room Features\nAMBIENT\n1-50 Cold air\n51-100 Damp stone");
        review.AddResultSetCommand.Execute(null);
        var second = review.SelectedResultSet;
        second.Rows[0].RangeText = "";
        second.Rows[0].Text = "spillover text";
        review.SelectedRow = second.Rows[0];

        review.JoinWithPreviousRowCommand.Execute(null); // no previous row in THIS set, even though set 1 has rows

        Assert.Single(second.Rows);
        Assert.Equal(2, review.ResultSets[0].Rows.Count); // untouched
        Assert.Contains("no previous row", review.CleanupMessage);
    }

    [Fact]
    public void Join_can_be_undone()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1 First\n2 Second\n3 Third\n4 Fourth\n5 Fifth\n6 Sixth");
        var set = review.SelectedResultSet;
        set.Rows[0].Text = "First part";
        set.Rows[1].RangeText = "";
        set.Rows[1].Text = "second part";
        review.SelectedRow = set.Rows[1];

        review.JoinWithPreviousRowCommand.Execute(null);
        Assert.Equal(5, set.Rows.Count);
        Assert.True(review.CanUndoCleanup);

        review.UndoCleanupCommand.Execute(null);

        Assert.Equal(6, set.Rows.Count);
        Assert.Equal("First part", set.Rows[0].Text);
        Assert.Equal("second part", set.Rows[1].Text);
        Assert.False(review.CanUndoCleanup);
    }

    // ---- Remove Empty Rows ----------------------------------------------------------------------

    [Fact]
    public void Remove_empty_rows_removes_only_truly_empty_rows()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1 Coin\n2 Gem\n3 Rope\n4 Torch\n5 Knife\n6 Bandage");
        var set = review.SelectedResultSet;
        set.Rows[1].RangeText = "";
        set.Rows[1].Text = "";                 // truly empty: removed
        set.Rows[3].RangeText = "   ";
        set.Rows[3].Text = "  ";                // whitespace-only counts as empty too: removed
        set.Rows[4].RangeText = "";
        set.Rows[4].Text = "Knife (has text, no range)"; // partially populated: kept

        review.RemoveEmptyRowsCommand.Execute(null);

        Assert.Equal(["Coin", "Rope", "Knife (has text, no range)", "Bandage"], set.Rows.Select(r => r.Text).ToArray());
        Assert.Equal([1, 2, 3, 4], set.Rows.Select(r => r.Number).ToArray());
        Assert.Contains("Removed 2 empty rows", review.CleanupMessage);
    }

    [Fact]
    public void Remove_empty_rows_can_be_undone_preserving_original_order()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1 Coin\n2 Gem\n3 Rope\n4 Torch\n5 Knife\n6 Bandage");
        var set = review.SelectedResultSet;
        set.Rows[1].RangeText = "";
        set.Rows[1].Text = "";
        set.Rows[4].RangeText = "";
        set.Rows[4].Text = "";
        var beforeRemoval = set.Rows.Select(r => (r.RangeText, r.Text)).ToArray(); // the state Undo must restore, blanks included

        review.RemoveEmptyRowsCommand.Execute(null);
        Assert.Equal(4, set.Rows.Count);

        review.UndoCleanupCommand.Execute(null);

        Assert.Equal(beforeRemoval, set.Rows.Select(r => (r.RangeText, r.Text)).ToArray());
    }

    [Fact]
    public void Remove_empty_rows_reports_when_there_is_nothing_to_remove()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1 Coin\n2 Gem\n3 Rope\n4 Torch\n5 Knife\n6 Bandage");

        review.RemoveEmptyRowsCommand.Execute(null);

        Assert.Equal(6, review.SelectedResultSet.Rows.Count);
        Assert.Equal("No empty rows were found in this result set.", review.CleanupMessage);
    }

    [Fact]
    public void Remove_empty_rows_does_not_remove_a_multi_result_row_that_still_has_content_in_one_set()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d100 Room Features\nAMBIENT\n1-50 Cold air\n51-100 Damp stone");
        review.AddResultSetCommand.Execute(null);
        review.SelectedResultSet.Rows[0].RangeText = "1-50";
        review.SelectedResultSet.Rows[0].Text = ""; // blank text but a real range: not empty

        review.RemoveEmptyRowsCommand.Execute(null);

        Assert.Single(review.SelectedResultSet.Rows);
    }

    // ---- Normalize Text -------------------------------------------------------------------------

    [Fact]
    public void Normalize_text_fixes_whitespace_and_ligatures_without_touching_dice_text_in_prose()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1 Coin\n2 Gem\n3 Rope\n4 Torch\n5 Knife\n6 Bandage");
        var set = review.SelectedResultSet;
        set.Rows[0].Text = "D4 Bandages";                 // non-breaking space
        set.Rows[1].Text = "4D6+5\tmagniﬁcent gem";        // tab + ligature, dice text alongside
        set.Rows[2].Text = "  UD6   extra   spaces  ";          // leading/trailing + repeated spaces
        set.Rows[3].Text = "2d6+1 charge";

        review.NormalizeTextCommand.Execute(null);

        Assert.Equal("D4 Bandages", set.Rows[0].Text);
        Assert.Equal("4D6+5 magnificent gem", set.Rows[1].Text);
        Assert.Equal("UD6 extra spaces", set.Rows[2].Text);
        Assert.Equal("2d6+1 charge", set.Rows[3].Text); // already clean: untouched
    }

    [Fact]
    public void Normalize_text_does_not_touch_ordinary_hyphenated_words_or_punctuation()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1 Coin\n2 Gem\n3 Rope\n4 Torch\n5 Knife\n6 Bandage");
        var set = review.SelectedResultSet;
        set.Rows[0].Text = "A well-made half-orc two-handed axe — “fine work”.";

        review.NormalizeTextCommand.Execute(null);

        Assert.Equal("A well-made half-orc two-handed axe — “fine work”.", set.Rows[0].Text);
    }

    [Fact]
    public void Normalize_text_cleans_range_field_spacing_and_dash_variants()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d20 Loot\n1 Coin\n2 Gem\n3 Rope\n4 Torch\n5 Knife\n6 Bandage");
        var set = review.SelectedResultSet;
        set.Rows[5].RangeText = "7 - 8";
        set.AddRowCommand.Execute(null);
        set.Rows[6].RangeText = "9 – 10";  // en dash
        set.AddRowCommand.Execute(null);
        set.Rows[7].RangeText = "11 − 12"; // real minus sign

        review.NormalizeTextCommand.Execute(null);

        Assert.Equal("7-8", set.Rows[5].RangeText);
        Assert.Equal("9-10", set.Rows[6].RangeText);
        Assert.Equal("11-12", set.Rows[7].RangeText);
    }

    [Fact]
    public void Normalize_text_preserves_d100_and_d66_semantics()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d100 Loot\n01-05 Coin\n06-95 Filler\n96 – 00 Gem");

        review.NormalizeTextCommand.Execute(null);
        Assert.Empty(review.ValidationNotes);
        Assert.True(review.CanSave);
        review.SaveCommand.Execute(null);

        var saved = db.LoadTable(main.Tables.Single(t => t.Name == "Loot").Id)!.ResultSets[0].Entries;
        Assert.Equal([(1, 5, "Coin"), (6, 95, "Filler"), (96, 100, "Gem")], saved.Select(e => (e.Min, e.Max, e.Text)).ToArray());

        var d66Review = Import(main, "d66 Encounters\n11-16 Goblins\n21-26 Wolves\n31-36 Bandits\n41-46 Spider\n51-56 Ghoul\n61-66 Dragon");
        d66Review.SelectedResultSet.Rows[2].RangeText = "31 – 36"; // en dash, extra spacing

        d66Review.NormalizeTextCommand.Execute(null);

        Assert.Equal("31-36", d66Review.SelectedResultSet.Rows[2].RangeText);
        Assert.Empty(d66Review.ValidationNotes);
        Assert.True(d66Review.CanSave);
    }

    [Fact]
    public void Normalize_text_can_be_undone()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1 Coin\n2 Gem\n3 Rope\n4 Torch\n5 Knife\n6 Bandage");
        var set = review.SelectedResultSet;
        set.Rows[0].Text = "D4 Bandages";

        review.NormalizeTextCommand.Execute(null);
        Assert.Equal("D4 Bandages", set.Rows[0].Text);

        review.UndoCleanupCommand.Execute(null);

        Assert.Equal("D4 Bandages", set.Rows[0].Text);
    }

    [Fact]
    public void Normalize_text_reports_when_nothing_needed_fixing()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1 Coin\n2 Gem\n3 Rope\n4 Torch\n5 Knife\n6 Bandage");

        review.NormalizeTextCommand.Execute(null);

        Assert.Equal("Nothing needed normalizing.", review.CleanupMessage);
        Assert.False(review.CanUndoCleanup);
    }

    // ---- Dehyphenate ----------------------------------------------------------------------------

    [Fact]
    public void Dehyphenate_joins_a_pdf_line_wrap_hyphenated_word()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1 A magnifi- cent gem\n2 Gem\n3 Rope\n4 Torch\n5 Knife\n6 Bandage");
        var set = review.SelectedResultSet;
        review.SelectedRow = set.Rows[0];

        review.DehyphenateSelectedCommand.Execute(null);

        Assert.Equal("A magnificent gem", set.Rows[0].Text);
    }

    [Fact]
    public void Dehyphenate_does_not_alter_legitimate_hyphenated_words()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1 A well-made half-orc two-handed axe\n2 Gem\n3 Rope\n4 Torch\n5 Knife\n6 Bandage");
        var set = review.SelectedResultSet;
        review.SelectedRow = set.Rows[0];

        review.DehyphenateSelectedCommand.Execute(null);

        Assert.Equal("A well-made half-orc two-handed axe", set.Rows[0].Text);
        Assert.Equal("No line-wrap hyphenation was found in the selected row.", review.CleanupMessage);
    }

    [Fact]
    public void Dehyphenate_can_be_undone()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1 A magnifi- cent gem\n2 Gem\n3 Rope\n4 Torch\n5 Knife\n6 Bandage");
        var set = review.SelectedResultSet;
        review.SelectedRow = set.Rows[0];

        review.DehyphenateSelectedCommand.Execute(null);
        Assert.Equal("A magnificent gem", set.Rows[0].Text);

        review.UndoCleanupCommand.Execute(null);

        Assert.Equal("A magnifi- cent gem", set.Rows[0].Text);
    }

    // ---- Replacement character warning -----------------------------------------------------------

    [Fact]
    public void A_replacement_character_is_flagged_and_never_silently_changed()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1 magn�icent gem\n2 Gem\n3 Rope\n4 Torch\n5 Knife\n6 Bandage");
        var set = review.SelectedResultSet;

        Assert.Equal("magn�icent gem", set.Rows[0].Text); // never altered
        Assert.True(set.Rows[0].HasWarning);
        Assert.Contains("could not be copied correctly", set.Rows[0].Notes);

        review.NormalizeTextCommand.Execute(null); // normalizing does not convert or remove it either

        Assert.Contains('�', set.Rows[0].Text);
    }

    // ---- Undo scope: only the single most recent cleanup ------------------------------------------

    [Fact]
    public void Undo_only_reverses_the_single_most_recent_cleanup_action()
    {
        var (temp, db, main) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1 Coin\n2 Gem\n3 Rope\n4 Torch\n5 Knife\n6 Bandage");
        var set = review.SelectedResultSet;

        set.Rows[1].RangeText = "";
        set.Rows[1].Text = "";
        review.RemoveEmptyRowsCommand.Execute(null);
        Assert.Equal(5, set.Rows.Count);

        set.Rows[0].Text = "Coin piece";
        review.NormalizeTextCommand.Execute(null); // a second cleanup action supersedes the first undo slot

        review.UndoCleanupCommand.Execute(null);
        Assert.Equal("Coin piece", set.Rows[0].Text); // the normalize is undone...
        Assert.Equal(5, set.Rows.Count);                    // ...but the earlier removal is not
        Assert.False(review.CanUndoCleanup);
    }
}
