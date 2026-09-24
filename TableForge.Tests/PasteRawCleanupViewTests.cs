using System.Windows.Controls;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>The Paste screen's raw-text cleanup affordances through the real view: Normalize Text, Dehyphenate,
/// the replacement-character warning and Find Next Damaged Character. Ctrl+J itself is covered at the pure-logic
/// level in <c>RawLineJoinTests</c>; WPF does not offer a reliable way to simulate a real Ctrl+key chord in tests
/// (Keyboard.Modifiers reads live OS keyboard state, and driving that for real via SendInput/keybd_event was tried
/// and found to leave the desktop's focus/activation state disturbed enough to make unrelated tests in the same run
/// flaky — not worth it), so this only checks that the affordance (the tooltip) is really on the view, the same way
/// the existing Ctrl+Enter test checks its KeyBinding declaratively instead of firing it.</summary>
[Collection("UI")]
public class PasteRawCleanupViewTests
{
    [Fact]
    public void Normalize_text_button_cleans_the_raw_paste_box_and_reports_what_it_did()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) => { });
            ui.Click("Paste Table…");
            var box = ui.One<TextBox>(t => t.Name == "PasteSourceBox");
            box.Text = "D4 Bandages\n1\tExtra   spaces";
            ui.Layout();

            ui.Click("Normalize Text");

            Assert.Equal("D4 Bandages\n1 Extra spaces", box.Text);
            Assert.Equal("Normalized the pasted text.", ((PasteViewModel)ui.Main.Current!).CleanupMessage);
        });
    }

    [Fact]
    public void Normalize_text_reports_when_nothing_needed_fixing()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) => { });
            ui.Click("Paste Table…");
            var box = ui.One<TextBox>(t => t.Name == "PasteSourceBox");
            box.Text = "1 Coin\n2 Gem";
            ui.Layout();

            ui.Click("Normalize Text");

            Assert.Equal("1 Coin\n2 Gem", box.Text);
            Assert.Equal("Nothing needed normalizing.", ((PasteViewModel)ui.Main.Current!).CleanupMessage);
        });
    }

    [Fact]
    public void Dehyphenate_button_joins_a_pdf_line_wrap_hyphen_in_the_raw_text()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) => { });
            ui.Click("Paste Table…");
            var box = ui.One<TextBox>(t => t.Name == "PasteSourceBox");
            box.Text = "1 A magnifi-\ncent gem";
            ui.Layout();

            ui.Click("Dehyphenate");

            Assert.Equal("1 A magnificent gem", box.Text);
            Assert.Equal("Removed line-wrap hyphenation from the pasted text.", ((PasteViewModel)ui.Main.Current!).CleanupMessage);
        });
    }

    [Fact]
    public void Dehyphenate_reports_when_there_is_nothing_to_join()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) => { });
            ui.Click("Paste Table…");
            var box = ui.One<TextBox>(t => t.Name == "PasteSourceBox");
            box.Text = "A well-made half-orc";
            ui.Layout();

            ui.Click("Dehyphenate");

            Assert.Equal("A well-made half-orc", box.Text);
            Assert.Equal("No line-wrap hyphenation was found in the pasted text.", ((PasteViewModel)ui.Main.Current!).CleanupMessage);
        });
    }

    [Fact]
    public void The_replacement_character_warning_appears_with_a_find_button_and_never_alters_the_text()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) => { });
            ui.Click("Paste Table…");
            var box = ui.One<TextBox>(t => t.Name == "PasteSourceBox");
            Assert.False(ui.HasVisibleButton("Find Next Damaged Character"));

            box.Text = "1 magn�icent gem\n2 wea�on";
            ui.Layout();

            Assert.True(ui.HasVisibleButton("Find Next Damaged Character"));
            Assert.Contains(ui.Texts(), t => t.Text.Contains("2 damaged characters detected."));

            box.CaretIndex = 0;
            ui.Click("Find Next Damaged Character");
            Assert.Equal(box.Text.IndexOf('�'), box.SelectionStart);
            Assert.Equal(1, box.SelectionLength);

            Assert.Equal("1 magn�icent gem\n2 wea�on", box.Text); // never altered
        });
    }

    [Fact]
    public void Find_next_damaged_character_wraps_around_to_the_start()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) => { });
            ui.Click("Paste Table…");
            var box = ui.One<TextBox>(t => t.Name == "PasteSourceBox");
            box.Text = "1 magn�icent gem\n2 wea�on";
            ui.Layout();

            var firstIndex = box.Text.IndexOf('�');
            var secondIndex = box.Text.IndexOf('�', firstIndex + 1);

            box.Select(firstIndex, 1);
            ui.Click("Find Next Damaged Character");
            Assert.Equal(secondIndex, box.SelectionStart);

            ui.Click("Find Next Damaged Character"); // no more ahead: wraps back to the first
            Assert.Equal(firstIndex, box.SelectionStart);
        });
    }

    [Fact]
    public void The_paste_box_carries_a_ctrl_j_tooltip()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) => { });
            ui.Click("Paste Table…");
            var box = ui.One<TextBox>(t => t.Name == "PasteSourceBox");

            Assert.Equal("Ctrl+J — join the selected lines into one line, or the current line into the line above it", box.ToolTip);
        });
    }

    /// <summary>
    /// Exercises the real Ctrl+J code path (the real TextBox's caret, its real undo-preserving replace, the real bound
    /// ViewModel message) without a synthetic key event: <see cref="TableForge.Views.PasteView.OnPasteSourceKeyDown"/>
    /// is a three-line pass-through (bail unless Key.J and Keyboard.Modifiers is exactly Control, else call this same
    /// private method), so calling it directly here proves everything past that gate, while the tooltip test above and
    /// the plain code read together cover the gate itself. See this class's doc comment for why the gate is not fired
    /// through a real OS key chord in the suite.
    /// </summary>
    [Fact]
    public void Ctrl_J_joins_the_current_line_with_the_previous_line_in_the_real_view()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) => { });
            ui.Click("Paste Table…");
            var box = ui.One<TextBox>(t => t.Name == "PasteSourceBox");
            var view = ui.One<TableForge.Views.PasteView>(_ => true);
            box.Text = "7 You find a damaged chest containing\nseveral old coins and a silver key.";
            ui.Layout();
            box.CaretIndex = box.Text.IndexOf("several", StringComparison.Ordinal) + 3; // caret on the continuation line

            var join = typeof(TableForge.Views.PasteView).GetMethod("JoinCurrentLineWithPrevious",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            join.Invoke(view, null);
            ui.Layout();

            Assert.Equal("7 You find a damaged chest containing several old coins and a silver key.", box.Text);
            Assert.Equal("Joined the current line with the previous line.", ((PasteViewModel)ui.Main.Current!).CleanupMessage);
        });
    }

    // ---- Bulk Join: Ctrl+J (and Join Lines) with several lines selected ------------------------------------------

    private const string Wrapped = "D100 FOLLOWERS\n01-02 Kael Dravorn\nthe scarred scout\nfrom the north\n03-04 Morthan Vex";
    private const string WrappedRow = "01-02 Kael Dravorn\nthe scarred scout\nfrom the north";

    /// <summary>The same dispatch Ctrl+J runs once its key gate passes (see the note on the single-line test above).</summary>
    private static void PressCtrlJ(TableForge.Views.PasteView view) =>
        typeof(TableForge.Views.PasteView).GetMethod("JoinLines", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(view, null);

    private static (UiHarness Ui, TextBox Box, TableForge.Views.PasteView View) OpenPaste(string text)
    {
        var ui = new UiHarness(1, (db, c) => { });
        ui.Click("Paste Table…");
        var box = ui.One<TextBox>(t => t.Name == "PasteSourceBox");
        box.Text = text;
        ui.Layout();
        return (ui, box, ui.One<TableForge.Views.PasteView>(_ => true));
    }

    [Fact]
    public void Ctrl_J_with_several_lines_selected_joins_them_and_native_undo_restores_the_exact_original()
    {
        Sta.Run(() =>
        {
            var (ui, box, view) = OpenPaste(Wrapped);
            using var _ = ui;
            box.Focus();
            box.Select(Wrapped.IndexOf(WrappedRow, StringComparison.Ordinal), WrappedRow.Length);

            PressCtrlJ(view);
            ui.Layout();

            const string joined = "D100 FOLLOWERS\n01-02 Kael Dravorn the scarred scout from the north\n03-04 Morthan Vex";
            Assert.Equal(joined, box.Text);
            Assert.Equal(joined, ((PasteViewModel)ui.Main.Current!).SourceText);   // the bound source text follows
            Assert.Equal("Joined 3 selected lines into one.", ((PasteViewModel)ui.Main.Current!).CleanupMessage);
            Assert.Equal(0, box.SelectionLength);
            Assert.Equal(joined.IndexOf(" from the north", StringComparison.Ordinal) + " from the north".Length, box.CaretIndex); // end of the joined line

            Assert.True(box.CanUndo);
            box.Undo();                                                             // what Ctrl+Z runs
            ui.Layout();
            Assert.Equal(Wrapped, box.Text);                                        // one step back, exactly
            Assert.Equal(Wrapped, ((PasteViewModel)ui.Main.Current!).SourceText);
        });
    }

    [Fact]
    public void The_join_lines_button_does_the_same_bulk_join_on_the_selection()
    {
        Sta.Run(() =>
        {
            var (ui, box, _) = OpenPaste("KEEP\nAlpha\n\n   Beta   \nKEEP2");
            using var __ = ui;
            box.Select("KEEP\n".Length, "Alpha\n\n   Beta   ".Length);

            ui.Click("Join Lines");
            ui.Layout();

            Assert.Equal("KEEP\nAlpha Beta\nKEEP2", box.Text);
            box.Undo();
            Assert.Equal("KEEP\nAlpha\n\n   Beta   \nKEEP2", box.Text);
        });
    }

    [Fact]
    public void Ctrl_J_without_a_multiline_selection_still_joins_the_caret_line_into_the_line_above()
    {
        Sta.Run(() =>
        {
            var (ui, box, view) = OpenPaste("Alpha\nBeta words\nGamma");
            using var _ = ui;

            box.CaretIndex = "Alpha\nBe".Length;                                    // no selection
            PressCtrlJ(view);
            Assert.Equal("Alpha Beta words\nGamma", box.Text);
            Assert.Equal("Joined the current line with the previous line.", ((PasteViewModel)ui.Main.Current!).CleanupMessage);

            box.Select("Alpha Beta words\nGa".Length, 2);                           // a selection inside one line
            PressCtrlJ(view);
            Assert.Equal("Alpha Beta words Gamma", box.Text);                       // the single-line join, not a bulk join
            Assert.Equal("Joined the current line with the previous line.", ((PasteViewModel)ui.Main.Current!).CleanupMessage);
        });
    }
}
