using TableForge.Data;
using TableForge.Domain;
using TableForge.Import;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>Multi-set import through the view models, and the "paste rows into this result set" recovery tool.</summary>
public class PasteRowsTests
{
    private const string NoiseColumn = "01-25 Silence\n26-50 Distant scratching\n51-66 Dripping water\n67-71 Hissing\n72-00 Low chanting";

    private static ReviewViewModel Import(MainViewModel main, string text)
    {
        main.PasteTableCommand.Execute(null);
        var paste = (PasteViewModel)main.Current!;
        paste.SourceText = text;
        paste.InterpretCommand.Execute(null);
        return (ReviewViewModel)main.Current!;
    }

    private static (TempDatabase Temp, AppDatabase Db, MainViewModel Main, FixedDice Dice) Start()
    {
        var temp = new TempDatabase();
        var db = temp.Open();
        db.CreateCollection("Dungeon");
        var dice = new FixedDice(68);
        return (temp, db, new MainViewModel(db, dice), dice);
    }

    private static string[] Snapshot(ResultSetEditorViewModel set) => set.Rows.Select(r => $"{r.RangeText}|{r.Text}").ToArray();

    // ---- multi-set import to a saved, rollable table ------------------------------------------

    [Fact]
    public void Pasted_room_features_reviews_as_three_sets_saves_and_rolls_68_across_all_of_them()
    {
        var (temp, db, main, dice) = Start();
        using var _ = temp; using var __ = db;

        var review = Import(main, MultiSetParserTests.RoomFeatures);

        Assert.Equal("Room Features", review.TableName);
        Assert.Equal("d100", review.DiceText);
        Assert.Equal(["Ambient", "Noise", "General Feature"], review.ResultSets.Select(s => s.DisplayName).ToArray());
        Assert.Equal([4, 5, 4], review.ResultSets.Select(s => s.Rows.Count).ToArray());
        Assert.All(review.ResultSets, s => Assert.All(s.Rows, r => Assert.False(r.HasNotes)));
        Assert.Empty(review.TableNotes);
        Assert.Empty(review.ValidationNotes);
        Assert.True(review.CanSave);

        review.SaveCommand.Execute(null);

        var roll = Assert.IsType<RollViewModel>(main.Current);
        roll.RollCommand.Execute(null);
        Assert.Equal("Rolled 68", roll.RollDisplay);
        Assert.Equal(
            [("Ambient", "Smell of burning flesh"), ("Noise", "Hissing"), ("General Feature", "Grated floors reveal dozens of people below")],
            roll.Results.Select(r => (r.Heading, r.Text)).ToArray());
    }

    [Fact]
    public void Side_by_side_import_can_be_saved_and_rolled_and_the_split_is_visible_in_review()
    {
        var (temp, db, main, dice) = Start();
        using var _ = temp; using var __ = db;

        var review = Import(main, "d100 Names\n01-25 Ash        51-75 Kel\n26-50 Bar        76-00 Lor");

        Assert.Equal(["01-25 Ash", "26-50 Bar", "51-75 Kel", "76-00 Lor"], review.Rows.Select(r => $"{r.RangeText} {r.Text}").ToArray());
        Assert.True(review.Rows[0].HasInfo);                    // the split note sits on the first row of the block,
        Assert.False(review.Rows[0].HasWarning);                // as information rather than something to correct
        Assert.Contains("side-by-side", review.Rows[0].Notes);
        Assert.Empty(review.ValidationNotes);

        review.SaveCommand.Execute(null);
        var roll = (RollViewModel)main.Current!;
        dice.Value = 100;
        roll.RollCommand.Execute(null);
        Assert.Equal("Lor", Assert.Single(roll.Results).Text);
    }

    // ---- paste rows into the selected result set ----------------------------------------------

    [Fact]
    public void Rows_pasted_into_the_selected_set_are_appended_and_nothing_else_changes()
    {
        var (temp, db, main, _) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, MultiSetParserTests.RoomFeatures);
        var noise = review.ResultSets[1];
        noise.Rows[4].DeleteCommand.Execute(null);                      // drop "72-00 Low chanting"
        var others = new[] { Snapshot(review.ResultSets[0]), Snapshot(review.ResultSets[2]) };
        var (name, dice) = (review.TableName, review.DiceText);

        review.SelectedResultSet = noise;
        review.TogglePasteRowsCommand.Execute(null);
        Assert.True(review.IsPasteRowsOpen);
        review.PasteRowsText = "72-80 Faint singing\n81-00 Wailing";
        review.AppendPastedRowsCommand.Execute(null);

        Assert.Equal(["01-25|Silence", "26-50|Distant scratching", "51-66|Dripping water", "67-71|Hissing", "72-80|Faint singing", "81-00|Wailing"], Snapshot(noise));
        Assert.Equal([1, 2, 3, 4, 5, 6], noise.Rows.Select(r => r.Number).ToArray());
        Assert.Equal(others[0], Snapshot(review.ResultSets[0]));
        Assert.Equal(others[1], Snapshot(review.ResultSets[2]));
        Assert.Equal((name, dice), (review.TableName, review.DiceText));
        Assert.Equal(3, review.ResultSets.Count);
        Assert.Equal("Added 2 pasted rows to Noise.", review.PasteRowsMessage);
        Assert.Equal("", review.PasteRowsText);
        Assert.Empty(review.ValidationNotes);                            // the pasted rows completed the set
        Assert.Same(noise, review.SelectedResultSet);
    }

    [Fact]
    public void Replacing_swaps_the_selected_sets_rows_for_the_pasted_ones_and_saves()
    {
        var (temp, db, main, _) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, MultiSetParserTests.RoomFeatures);
        var ambientBefore = Snapshot(review.ResultSets[0]);

        review.SelectedResultSet = review.ResultSets[1];
        review.PasteRowsText = "01-50 Quiet\n51-00 Loud";
        review.ReplacePastedRowsCommand.Execute(null);

        Assert.Equal(["01-50|Quiet", "51-00|Loud"], Snapshot(review.ResultSets[1]));
        Assert.Equal("Replaced the rows of Noise with 2 pasted rows.", review.PasteRowsMessage);
        Assert.Equal(ambientBefore, Snapshot(review.ResultSets[0]));
        Assert.Empty(review.ValidationNotes);

        review.SaveCommand.Execute(null);
        var saved = db.LoadTable(main.SelectedTable!.Id)!;
        Assert.Equal(["Quiet", "Loud"], saved.ResultSets[1].Entries.Select(e => e.Text).ToArray());
        Assert.Equal(["Smell of burning flesh", "Loud", "Grated floors reveal dozens of people below"],
            TableResolver.Resolve(saved, 68).Results.Select(r => r.Entry!.Text).ToArray());
    }

    [Fact]
    public void Text_with_no_usable_rows_changes_nothing_and_reports_what_was_not_understood()
    {
        var (temp, db, main, _) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, MultiSetParserTests.RoomFeatures);
        var before = Snapshot(review.ResultSets[0]);

        review.PasteRowsText = "hello there\nnot a row";
        review.ReplacePastedRowsCommand.Execute(null);   // even "replace" must not wipe rows when nothing was understood

        Assert.Equal(before, Snapshot(review.ResultSets[0]));
        Assert.Equal("No numbered rows were found in the pasted text, so nothing was changed.", review.PasteRowsMessage);
        Assert.Contains(review.TableNotes, n => n.StartsWith("Pasted line 1") && n.Contains("hello there"));
        Assert.Contains(review.TableNotes, n => n.StartsWith("Pasted line 2"));
        Assert.Equal("hello there\nnot a row", review.PasteRowsText);   // kept so it can be corrected
        Assert.True(review.CanSave);
    }

    [Fact]
    public void Row_only_paste_does_not_change_table_identity_dice_or_the_number_of_sets_even_if_the_text_has_them()
    {
        var (temp, db, main, _) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, MultiSetParserTests.RoomFeatures);
        review.ResultSets[1].Rows[4].DeleteCommand.Execute(null);
        review.SelectedResultSet = review.ResultSets[1];

        review.PasteRowsText = "D6 SOMETHING ELSE\nNEW SECTION\n72-80 Faint\n81-00 Loud";
        review.AppendPastedRowsCommand.Execute(null);

        Assert.Equal(("Room Features", "d100", 3), (review.TableName, review.DiceText, review.ResultSets.Count));
        Assert.Equal(["Faint", "Loud"], review.ResultSets[1].Rows.Skip(4).Select(r => r.Text).ToArray());
        Assert.Equal(2, review.TableNotes.Count(n => n.StartsWith("Pasted line")));    // the heading-like lines are flagged, not obeyed
    }

    [Fact]
    public void Issues_in_pasted_rows_attach_to_the_new_rows()
    {
        var (temp, db, main, _) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, MultiSetParserTests.RoomFeatures);
        review.ResultSets[1].Rows[4].DeleteCommand.Execute(null); // make room so validation stays quiet
        review.SelectedResultSet = review.ResultSets[1];

        review.PasteRowsText = "72-80 Faint\nsinging\n81-00 Wail";
        review.AppendPastedRowsCommand.Execute(null);

        var rows = review.ResultSets[1].Rows;
        Assert.Equal("Faint singing", rows[4].Text);
        Assert.True(rows[4].HasWarning);
        Assert.Contains("Pasted line 2", rows[4].Notes);
        Assert.False(rows[5].HasNotes);   // only the row with the joined line is flagged
        Assert.False(rows[3].HasNotes);
    }

    [Fact]
    public void Pasted_columns_are_split_like_import_and_row_ranges_use_the_tables_dice()
    {
        var (temp, db, main, _) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d100 Names\n01-50 Ash");
        review.PasteRowsText = "51-75 Kel        76-00 Lor";

        review.AppendPastedRowsCommand.Execute(null);

        Assert.Equal(["01-50|Ash", "51-75|Kel", "76-00|Lor"], Snapshot(review.ResultSets[0]));
        Assert.Empty(review.ValidationNotes);   // 76-00 was read as 76-100 because the table is a d100
    }

    [Fact]
    public void Paste_commands_wait_for_text()
    {
        var (temp, db, main, _) = Start();
        using var _1 = temp; using var _2 = db;
        var review = Import(main, "d6 Loot\n1-6 Coin");

        Assert.False(review.AppendPastedRowsCommand.CanExecute(null));
        Assert.False(review.ReplacePastedRowsCommand.CanExecute(null));
        review.PasteRowsText = "  \n ";
        Assert.False(review.AppendPastedRowsCommand.CanExecute(null));
        review.PasteRowsText = "7 x";
        Assert.True(review.AppendPastedRowsCommand.CanExecute(null));
    }

    [Fact]
    public void An_imperfect_copy_can_be_repaired_into_a_full_table_by_pasting_each_missing_set()
    {
        var (temp, db, main, dice) = Start();
        using var _1 = temp; using var _2 = db;
        // Only the first block came through with a heading; the rest arrives as separate copies.
        var review = Import(main, "D100 ROOM FEATURES\n\nAMBIENT\n01-30 Cold stale air\n31-65 Damp stone\n66-70 Smell of burning flesh\n71-00 Heavy incense");
        Assert.Single(review.ResultSets);

        review.AddResultSetCommand.Execute(null);
        review.SelectedResultSet.Name = "Noise";
        review.PasteRowsText = NoiseColumn;
        review.ReplacePastedRowsCommand.Execute(null);

        review.AddResultSetCommand.Execute(null);
        review.SelectedResultSet.Name = "General Feature";
        review.PasteRowsText = "01-40 Cracked stone walls\n41-67 Broken furniture\n68-69 Grated floors reveal dozens of people below\n70-00 Carved pillars";
        review.ReplacePastedRowsCommand.Execute(null);

        Assert.Empty(review.ValidationNotes);
        Assert.True(review.CanSave);
        review.SaveCommand.Execute(null);

        var roll = (RollViewModel)main.Current!;
        roll.RollCommand.Execute(null);
        Assert.Equal(["Smell of burning flesh", "Hissing", "Grated floors reveal dozens of people below"],
            roll.Results.Select(r => r.Text).ToArray());
    }

    [Fact]
    public void Blank_first_column_heading_and_uncertain_headings_are_visible_in_review()
    {
        var (temp, db, main, _) = Start();
        using var _1 = temp; using var _2 = db;

        var review = Import(main, "d6 Weather\nDay\n1-3 Sun\n4-6 Cloud\nNight\n1-6 Moon");

        Assert.Equal(["Day", "Night"], review.ResultSets.Select(s => s.DisplayName).ToArray());
        Assert.Single(review.TableNotes);
        Assert.Contains("“Night”", review.TableNotes[0]);
        Assert.True(review.CanSave);                       // flagged for review, not blocked
    }
}
