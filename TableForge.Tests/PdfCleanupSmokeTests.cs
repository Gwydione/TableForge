using TableForge.Data;
using TableForge.Domain;
using TableForge.Import;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>
/// The real-world smoke walkthrough for the PDF Copy/Paste Cleanup Pass milestone: paste a table with a wrapped
/// continuation row and PDF artifacts, clean it up on the Review screen exactly as a person would, save it to a real
/// SQLite file in a scratch folder (never the user's actual database — see <see cref="TempFolder"/>), close and
/// reopen that database to simulate an application restart, then roll it and confirm d66 tables are unaffected.
/// </summary>
public class PdfCleanupSmokeTests
{
    [Fact]
    public void Paste_clean_up_save_restart_and_roll_survives_the_whole_workflow()
    {
        using var folder = new TempFolder();
        long tableId;

        // ---- session 1: paste, clean up on the Review screen, save --------------------------------
        using (var db = new AppDatabase(folder.DatabasePath()))
        {
            db.CreateCollection("Smoke Test");
            var main = new MainViewModel(db, new FixedDice(3));

            // 1. Paste a table containing a wrapped continuation row.
            main.PasteTableCommand.Execute(null);
            var paste = (PasteViewModel)main.Current!;
            paste.SourceText = "d6 Loot\n1 Coin\n2 Knife\n3 A magnifi- cent gem\n4 Bandages\n5 Rope\n6 Torch";
            paste.InterpretCommand.Execute(null);
            var review = (ReviewViewModel)main.Current!;
            var set = review.SelectedResultSet;

            // Reshape the last row into "row holding the first sentence" + "continuation row with no range of its
            // own", appended after it — the shape a PDF copy leaves behind when the parser could not safely join
            // it automatically (Add Row always appends, so the continuation must be built after the row it follows).
            set.Rows[5].Text = "A sturdy torch that burns for";
            set.AddRowCommand.Execute(null);
            var continuation = set.Rows[^1];
            continuation.RangeText = "";
            continuation.Text = "several hours before going out.";
            review.SelectedRow = continuation;

            // 2. Use Ctrl+J (the Join With Previous Row command).
            review.JoinWithPreviousRowCommand.Execute(null);

            // 3. Verify row count and result text.
            Assert.Equal(6, set.Rows.Count);
            Assert.Equal("A sturdy torch that burns for several hours before going out.", set.Rows[5].Text);

            // 4. Normalize text containing ligatures and extra whitespace (as a person would get pasting into the
            // cell directly from a PDF that used a non-breaking space and the "fi" ligature glyph).
            set.Rows[3].Text = "D4 Sterile  ﬁrst-aid kit";
            review.NormalizeTextCommand.Execute(null);
            Assert.Equal("D4 Sterile first-aid kit", set.Rows[3].Text);

            // 5. Verify a replacement character is visibly flagged but left unchanged.
            set.Rows[4].Text = "Rope (frayed�end)";
            review.NormalizeTextCommand.Execute(null); // re-run: must not touch the replacement character
            Assert.Equal("Rope (frayed�end)", set.Rows[4].Text);
            Assert.True(set.Rows[4].HasWarning);
            Assert.Contains("could not be copied correctly", set.Rows[4].Notes);
            set.Rows[4].Text = "Rope"; // fix it by hand, as a real user would, so the table can be saved clean

            // Dehyphenate the row the join above did not touch.
            review.SelectedRow = set.Rows[2];
            Assert.Equal("A magnifi- cent gem", set.Rows[2].Text);
            review.DehyphenateSelectedCommand.Execute(null);
            Assert.Equal("A magnificent gem", set.Rows[2].Text);

            // 6. Remove blank rows.
            set.AddRowCommand.Execute(null);
            set.Rows[^1].RangeText = ""; // a genuinely blank leftover row, as a stray "Add row" or bad split can leave
            Assert.Equal(7, set.Rows.Count);
            review.RemoveEmptyRowsCommand.Execute(null);
            Assert.Equal(6, set.Rows.Count);

            Assert.Empty(review.ValidationNotes);
            Assert.True(review.CanSave);

            // 7. Save.
            review.SaveCommand.Execute(null);
            tableId = main.Tables.Single(t => t.Name == "Loot").Id;
        }

        // 8. Restart: reopen the same database file as a fresh process would.
        using (var db = new AppDatabase(folder.DatabasePath()))
        {
            var reloaded = db.LoadTable(tableId)!;
            Assert.Equal(
                ["Coin", "Knife", "A magnificent gem", "D4 Sterile first-aid kit", "Rope", "A sturdy torch that burns for several hours before going out."],
                reloaded.ResultSets[0].Entries.Select(e => e.Text).ToArray());

            // ...and roll it.
            var main = new MainViewModel(db, new FixedDice(3));
            main.SelectedTable = main.Tables.Single(t => t.Id == tableId);
            var roll = (RollViewModel)main.Current!;
            roll.RollCommand.Execute(null);
            Assert.Equal("A magnificent gem", Assert.Single(roll.Results).Text);
        }
    }

    [Fact]
    public void D66_tables_are_unaffected_by_the_cleanup_pass()
    {
        using var folder = new TempFolder();
        using var db = new AppDatabase(folder.DatabasePath());
        db.CreateCollection("Smoke Test");
        var main = new MainViewModel(db, new FixedDice(36));

        main.PasteTableCommand.Execute(null);
        var paste = (PasteViewModel)main.Current!;
        paste.SourceText = "d66 Encounters\n11-16 Goblins\n21-26 Wolves\n31-36 Bandits\n41-46 Spider\n51-56 Ghoul\n61-66 Dragon";
        paste.InterpretCommand.Execute(null);
        var review = (ReviewViewModel)main.Current!;
        review.SelectedResultSet.Rows[2].RangeText = "31 – 36"; // PDF-style en dash spacing damage

        review.NormalizeTextCommand.Execute(null);
        Assert.Equal("31-36", review.SelectedResultSet.Rows[2].RangeText);
        Assert.Empty(review.ValidationNotes); // no impossible-value or gap warnings introduced
        Assert.True(review.CanSave);

        review.SaveCommand.Execute(null);
        var roll = (RollViewModel)main.Current!;
        roll.RollCommand.Execute(null);
        Assert.Equal("Rolled 36 (d66)", roll.RollDisplay);
        Assert.Equal("Bandits", Assert.Single(roll.Results).Text);
    }

    /// <summary>
    /// The follow-up milestone: raw-text cleanup on the Paste screen itself, before parsing, for damage that Review-only
    /// cleanup is too late for — a continuation line that starts with its own number reads to the parser as a brand-new
    /// (out-of-range) row rather than as the rest of the row above it, so it must be joined before Interpret is pressed.
    /// </summary>
    [Fact]
    public void Raw_paste_screen_cleanup_repairs_clipboard_damage_before_parsing_and_the_table_still_rolls()
    {
        using var folder = new TempFolder();
        using var db = new AppDatabase(folder.DatabasePath());
        db.CreateCollection("Smoke Test");
        var main = new MainViewModel(db, new FixedDice(6));

        // 1. open Paste Table.
        main.PasteTableCommand.Execute(null);
        var paste = (PasteViewModel)main.Current!;

        // 2. paste malformed multi-line PDF text: a line-wrap hyphen, a continuation row the parser cannot safely
        //    auto-join because it starts with a number of its own, non-breaking-space damage, and a bad glyph.
        var raw = "d6 Loot\n1 Coin\n2 Knife\n3 A magnifi-\ncent gem\n4 D4 Bandages\n5 Rope (frayed�end)\n" +
                  "6 The chest also holds\n12 gleaming coins.";
        paste.SourceText = raw;

        // 3. use Ctrl+J before parsing, on row 6's continuation.
        var caret = paste.SourceText.IndexOf("12 gleaming coins.", StringComparison.Ordinal);
        var joined = TextCleanup.TryJoinLineWithPrevious(paste.SourceText, caret, out var afterJoin, out _, out _);
        Assert.True(joined);
        paste.SourceText = afterJoin;

        // 4. use Normalize Text.
        paste.SourceText = TextCleanup.NormalizePastedText(paste.SourceText);
        Assert.DoesNotContain(' ', paste.SourceText);

        // 5. use Dehyphenate.
        var dehyphenated = TextCleanup.TryDehyphenate(paste.SourceText, out var afterDehyphenate);
        Assert.True(dehyphenated);
        paste.SourceText = afterDehyphenate;

        // 6. verify the replacement-character warning, then fix it by hand as a real user would.
        Assert.True(paste.HasDamagedCharacters);
        Assert.Equal(1, paste.DamagedCharacterCount);
        Assert.Contains("1 damaged character detected.", paste.DamagedCharacterWarning);
        paste.SourceText = paste.SourceText.Replace("Rope (frayed�end)", "Rope");
        Assert.False(paste.HasDamagedCharacters);

        // 7. parse.
        paste.InterpretCommand.Execute(null);
        var review = (ReviewViewModel)main.Current!;

        // 8. verify the joined text reaches Review intact, as one clean row per number rather than a split, lost row.
        Assert.Equal(
            ["Coin", "Knife", "A magnificent gem", "D4 Bandages", "Rope", "The chest also holds 12 gleaming coins."],
            review.SelectedResultSet.Rows.Select(r => r.Text).ToArray());
        Assert.Empty(review.ValidationNotes);
        Assert.True(review.CanSave);

        // 9. save (a real, temporary SQLite file — never the user's own database).
        review.SaveCommand.Execute(null);

        // 10. roll successfully.
        var roll = (RollViewModel)main.Current!;
        roll.RollCommand.Execute(null);
        Assert.Equal("The chest also holds 12 gleaming coins.", Assert.Single(roll.Results).Text);
    }
}
