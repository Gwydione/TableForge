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

            Assert.Equal("Ctrl+J — join the current line with the line above it", box.ToolTip);
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
}
