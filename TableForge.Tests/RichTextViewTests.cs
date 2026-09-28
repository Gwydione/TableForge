using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using TableForge.Domain;
using TableForge.ViewModels;
using TableForge.Views;

namespace TableForge.Tests;

/// <summary>
/// RC24 through the real WPF views: the Review screen's B / I / Clear Formatting (which never take focus, so the selection
/// survives), the formatted preview, exact edits reported by the real text box, the aligned view keeping the editor being
/// typed in, and formatted results on the Roll screen.
/// <para>
/// Ctrl+B / Ctrl+I / Ctrl+Space themselves: as with Ctrl+J (see <see cref="PasteRawCleanupViewTests"/>), a real Ctrl+key
/// chord cannot be driven reliably in the suite (Keyboard.Modifiers reads live OS state). ReviewView.OnPreviewKeyDown is a
/// short gate — exactly Control, a key <see cref="ReviewView.FormattingKey"/> maps, focus in a row's result text — that then
/// calls the same <see cref="ReviewView.ApplyFormatting"/> the buttons do; the mapping, the target check and everything past
/// the gate are tested here through the real view.
/// </para>
/// </summary>
[Collection("UI")]
public class RichTextViewTests
{
    private static (UiHarness Ui, ReviewViewModel Review, ReviewView View) EditTable(UiHarness ui, string name)
    {
        ui.SelectTable(name);
        ui.Click("Edit table");
        return (ui, Assert.IsType<ReviewViewModel>(ui.Main.Current), ui.One<ReviewView>(_ => true));
    }

    private static void SeedPlain(Data.AppDatabase db, Collection c, string name, params string[] texts) => db.SaveTable(new RollableTable
    {
        CollectionId = c.Id, Name = name, Dice = DiceExpression.Parse($"d{Math.Max(2, texts.Length)}"),
        ResultSets = [new ResultSet { Entries = texts.Select((t, i) => new TableEntry { Min = i + 1, Max = i + 1, Text = t }).ToList() }],
    });

    /// <summary>The visible editors of the per-set (not aligned) rows.</summary>
    private static List<TextBox> ResultBoxes(UiHarness ui) =>
        ViewTests.FindAll<TextBox>(ui.One<ItemsControl>(i => i.Name == "RowsList")).Where(t => t.Name == "ResultTextBox").ToList();

    /// <summary>The row's formatted preview; null when it has none (only a row with formatting gets one).</summary>
    private static Border? Preview(FrameworkElement row) => ViewTests.FindAll<Border>(row).SingleOrDefault(b => b.Name == "FormattedPreview");

    private static FrameworkElement RowOf(TextBox box)
    {
        DependencyObject current = box;
        while (current is not ContentPresenter { Parent: null } && current is not null) current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        return (FrameworkElement)(current ?? box);
    }

    private static (string Text, bool Bold, bool Italic)[] Runs(TextBlock block) => block.Inlines.OfType<Run>()
        .Select(r => (r.Text, r.FontWeight == FontWeights.Bold, r.FontStyle == FontStyles.Italic)).ToArray();

    private static void Focus(UiHarness ui, TextBox box)
    {
        ui.Window.Activate();
        box.Focus();
        Keyboard.Focus(box);
        ui.Layout();
        Assert.True(box.IsKeyboardFocused, "the result text box should have keyboard focus");
    }

    // ---- the formatting bar -------------------------------------------------------------------------------------------

    [Fact]
    public void The_formatting_buttons_cannot_take_focus_and_name_their_shortcuts()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) => SeedPlain(db, c, "Boons", "gains +2 Armor now", "x"));
            EditTable(ui, "Boons");

            foreach (var (name, shortcut) in new[] { ("BoldButton", "Ctrl+B"), ("ItalicButton", "Ctrl+I"), ("ClearFormattingButton", "Ctrl+Space") })
            {
                var button = ui.One<Button>(b => b.Name == name);
                Assert.False(button.Focusable);
                Assert.False(button.IsTabStop);
                Assert.Contains(shortcut, (string)button.ToolTip);
            }
            Assert.Equal(FormattingAction.Bold, ReviewView.FormattingKey(Key.B));
            Assert.Equal(FormattingAction.Italic, ReviewView.FormattingKey(Key.I));
            Assert.Equal(FormattingAction.Clear, ReviewView.FormattingKey(Key.Space));
            Assert.Null(ReviewView.FormattingKey(Key.J));   // Ctrl+J stays Join With Previous Row
            Assert.Null(ReviewView.FormattingKey(Key.U));   // no underline
        });
    }

    [Fact]
    public void Bold_and_italic_buttons_format_the_selection_and_leave_focus_and_selection_in_the_text_box()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) => SeedPlain(db, c, "Boons", "The creature gains +2 Armor until the next dawn.", "Nothing."));
            var (_, review, _) = EditTable(ui, "Boons");
            var box = ResultBoxes(ui)[0];
            Assert.Null(Preview(RowOf(box)));                                    // no formatting, no preview

            Focus(ui, box);
            box.Select(box.Text.IndexOf("+2 Armor", StringComparison.Ordinal), "+2 Armor".Length);
            ui.Click("B");
            Assert.True(box.IsKeyboardFocused);
            Assert.Equal("+2 Armor", box.SelectedText);                          // the selection is still there to act on
            box.Select(box.Text.IndexOf("next dawn", StringComparison.Ordinal), "next dawn".Length);
            ui.Click("I");

            Assert.Equal("The creature gains **+2 Armor** until the *next dawn*.", TextStylesTests.ToMd(review.Rows[0].Text, review.Rows[0].Styles));
            Assert.Equal("The creature gains +2 Armor until the next dawn.", box.Text);
            Assert.Equal("Made the selection italic.", ui.One<TextBlock>(t => t.Name == "FormatMessageText").Text);

            var preview = Preview(RowOf(box));
            Assert.NotNull(preview);
            Assert.True(preview.IsVisible);
            var text = (TextBlock)preview.Child;
            Assert.Equal("The creature gains +2 Armor until the next dawn.", text.Text);
            Assert.Equal([("The creature gains ", false, false), ("+2 Armor", true, false), (" until the ", false, false), ("next dawn", false, true), (".", false, false)], Runs(text));
            Assert.Null(Preview(RowOf(ResultBoxes(ui)[1])));

            box.Select(0, box.Text.Length);
            ui.Click("Clear Formatting");
            Assert.False(review.Rows[0].HasFormatting);
            ui.Layout();
            Assert.Null(Preview(RowOf(box)));
        });
    }

    [Fact]
    public void Formatting_needs_the_focus_in_a_rows_result_text()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) => SeedPlain(db, c, "Boons", "gains +2 Armor now", "x"));
            var (_, review, view) = EditTable(ui, "Boons");
            var name = ui.One<TextBox>(t => t.Name == "NameBox");
            Focus(ui, name);
            name.SelectAll();

            view.ApplyFormatting(FormattingAction.Bold);   // what Ctrl+B would do here, if the gate let it through

            Assert.False(review.Rows[0].HasFormatting);
            Assert.Equal("Select some result text first, then choose Bold, Italic or Clear Formatting.", review.FormatMessage);
            Assert.Equal("Boons", name.Text);              // table names are never formatted
        });
    }

    [Fact]
    public void The_real_text_box_reports_exact_edits_so_a_repeated_character_lands_on_the_right_side()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) => SeedPlain(db, c, "T", "abb", "x"));
            var (_, review, view) = EditTable(ui, "T");
            var box = ResultBoxes(ui)[0];
            Focus(ui, box);
            box.Select(1, 2);
            view.ApplyFormatting(FormattingAction.Bold);   // a**bb**

            box.Select(1, 0);
            box.SelectedText = "b";                        // typed just before the bold run
            ui.Layout();
            Assert.Equal("ab**bb**", TextStylesTests.ToMd(review.Rows[0].Text, review.Rows[0].Styles));

            box.Select(box.Text.Length, 0);
            box.SelectedText = "b";                        // typed just after it: not extended
            ui.Layout();
            Assert.Equal("ab**bb**b", TextStylesTests.ToMd(review.Rows[0].Text, review.Rows[0].Styles));
        });
    }

    [Fact]
    public void A_text_block_goes_back_to_plain_text_when_its_formatting_is_removed_even_if_the_text_is_the_same()
    {
        Sta.Run(() =>
        {
            var block = new TextBlock();
            var (text, styles) = TextStylesTests.Md("gains **+2 Armor** now");
            FormattedText.SetSegments(block, styles.Segments(text));
            Assert.Equal(text, block.Text);
            Assert.Equal([("gains ", false, false), ("+2 Armor", true, false), (" now", false, false)], Runs(block));

            FormattedText.SetSegments(block, TextStyles.Empty.Segments(text));   // same text, formatting cleared
            Assert.Equal(text, block.Text);
            Assert.DoesNotContain(block.Inlines.OfType<Run>(), r => r.FontWeight == FontWeights.Bold);
            Assert.Equal(text, string.Concat(block.Inlines.OfType<Run>().Select(r => r.Text)));

            FormattedText.SetSegments(block, TextStylesTests.Md("*gains* +2 Armor now").Styles.Segments(text));
            Assert.Equal([("gains", false, true), (" +2 Armor now", false, false)], Runs(block));
            FormattedText.SetSegments(block, null);
            Assert.Equal("", block.Text);
        });
    }

    // ---- the aligned view ---------------------------------------------------------------------------------------------

    private static TextBox AlignedBox(UiHarness ui, string text) =>
        ViewTests.FindAll<TextBox>(ui.One<ItemsControl>(i => i.Name == "AlignedRowsList")).Single(t => t.Name == "ResultTextBox" && t.Text == text);

    private static (UiHarness Ui, ReviewViewModel Review, ReviewView View) PasteAligned()
    {
        var ui = new UiHarness(1, (_, _) => { });
        ui.Click("Paste Table…");
        ui.One<TextBox>(t => t.AcceptsReturn).Text = ParallelOutputTests.Difficulty;
        ui.Click("Interpret");
        var review = Assert.IsType<ReviewViewModel>(ui.Main.Current);
        Assert.True(review.IsAligned);
        return (ui, review, ui.One<ReviewView>(_ => true));
    }

    [Fact]
    public void Typing_in_an_aligned_cell_keeps_the_same_editor_and_the_keyboard_focus()
    {
        Sta.Run(() =>
        {
            var (ui, _, _) = PasteAligned();
            using var _ui = ui;
            var box = AlignedBox(ui, "Easy");
            Focus(ui, box);

            box.Select(4, 0);
            box.SelectedText = "!";                 // one keystroke
            ui.Layout();
            box.Select(5, 0);                       // (assigning SelectedText selects what it inserted; a keystroke leaves the caret after it)
            box.SelectedText = "?";                 // and the next one goes to the same editor
            ui.Layout();

            Assert.Same(box, AlignedBox(ui, "Easy!?"));  // RC23 replaced it (and lost focus) on every keystroke
            Assert.True(box.IsKeyboardFocused);
            Assert.Equal("Easy!?", box.Text);
        });
    }

    [Fact]
    public void Typing_in_an_aligned_rows_range_keeps_the_same_range_box_and_the_keyboard_focus()
    {
        Sta.Run(() =>
        {
            var (ui, review, _) = PasteAligned();
            using var _ui = ui;
            var list = ui.One<ItemsControl>(i => i.Name == "AlignedRowsList");
            var box = ViewTests.FindAll<TextBox>(list).First(t => t.Name != "ResultTextBox" && t.Text == "3");
            Focus(ui, box);

            box.Select(1, 0);
            box.SelectedText = " ";   // "3 " — every column changes together, still aligned
            ui.Layout();

            Assert.True(review.IsAligned);
            Assert.Contains(box, ViewTests.FindAll<TextBox>(list));
            Assert.True(box.IsKeyboardFocused);
            Assert.All(review.ResultSets, s => Assert.Equal("3 ", s.Rows[2].RangeText));
        });
    }

    [Fact]
    public void Formatting_an_aligned_cell_keeps_focus_and_typing_there_keeps_the_formatting()
    {
        Sta.Run(() =>
        {
            var (ui, review, view) = PasteAligned();
            using var _ui = ui;
            var box = AlignedBox(ui, "Easy");
            Focus(ui, box);
            box.SelectAll();
            view.ApplyFormatting(FormattingAction.Bold);
            Assert.True(box.IsKeyboardFocused);
            Assert.Equal("Easy", box.SelectedText);

            box.Select(4, 0);
            box.SelectedText = "!";   // the same row is also shown, hidden, in the per-set layout: it must not undo this
            ui.Layout();

            var row = review.AlignedRows[2].Cells[0];
            Assert.Equal("**Easy**!", TextStylesTests.ToMd(row.Text, row.Styles));
            var preview = ViewTests.FindAll<Border>(ui.One<ItemsControl>(i => i.Name == "AlignedRowsList")).Where(b => b.Name == "FormattedPreview" && b.IsVisible).ToList();
            Assert.Equal([("Easy", true, false), ("!", false, false)], Runs((TextBlock)Assert.Single(preview).Child));
        });
    }

    // ---- the Roll screen ----------------------------------------------------------------------------------------------

    [Fact]
    public void The_Roll_screen_shows_the_result_and_the_resolved_inline_roll_formatted()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(3, (db, c) => db.SaveTable(RichTextPersistenceTests.Formatted(c.Id)));
            ui.SelectTable("Boons");
            ui.Click("Roll");

            var result = ui.Texts().Single(t => t.Text == "You gain +1d4 Armor until next dawn." && t.FontSize == 26);
            Assert.Equal([("You gain ", false, false), ("+1d4 Armor", true, true), (" until ", false, false), ("next dawn", false, true), (".", false, false)], Runs(result));

            ui.Click("Roll d4");
            var resolved = ui.One<TextBlock>(t => t.Name == "ResolvedInlineText");
            Assert.Equal("Resolved: You gain +3 Armor until next dawn.", resolved.Text);
            Assert.Equal([("Resolved: ", false, false), ("You gain ", false, false), ("+3 Armor", true, true), (" until ", false, false), ("next dawn", false, true), (".", false, false)], Runs(resolved));

            // The entry list shows each row's own formatting; the plain row is one plain run, exactly as before.
            var listed = ui.Texts().Where(t => t.FontSize != 26).ToList();
            Assert.Equal([("The creature gains ", false, false), ("+2 Armor", true, false), (" until the ", false, false), ("next dawn", false, true), (".", false, false)],
                Runs(listed.Single(t => t.Text == "The creature gains +2 Armor until the next dawn.")));
            Assert.Equal([("Nothing happens.", false, false)], Runs(listed.Single(t => t.Text == "Nothing happens.")));
        });
    }

    [Fact]
    public void Recent_Rolls_show_the_plain_text()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) => db.SaveTable(RichTextPersistenceTests.Formatted(c.Id)));
            ui.SelectTable("Boons");
            ui.Click("Roll");

            var recent = ui.One<ItemsControl>(i => i.Name == "RecentRollsList");
            Assert.Contains(ViewTests.FindAll<TextBlock>(recent), t => t.Text.Contains("The creature gains +2 Armor until the next dawn.", StringComparison.Ordinal));
            Assert.All(ViewTests.FindAll<TextBlock>(recent), t => Assert.All(t.Inlines.OfType<Run>(), r => Assert.NotEqual(FontWeights.Bold, r.FontWeight)));
        });
    }
}
