using System.Windows.Controls;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>The real window: the dice field takes the new syntax, and the roll screen keeps the expression visible.</summary>
[Collection("UI")]
public class DiceModifierViewTests
{
    private const string Reaction = "2D6+1 ENCOUNTER REACTION\n3-6 Hostile\n7-8 Wary\n9 Curious\n10-13 Friendly";

    [Fact]
    public void A_table_headed_2D6_plus_1_travels_paste_review_save_roll_and_manual_entry()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(7, (_, _) => { });

            ui.Click("Paste Table…");
            ui.One<TextBox>(t => t.Name == "PasteSourceBox").Text = Reaction;
            ui.Click("Interpret");

            Assert.Equal("Encounter Reaction", ui.One<TextBox>(t => t.Name == "NameBox").Text);
            Assert.Equal("2d6+1", ui.One<TextBox>(t => t.Name == "DiceBox").Text);       // the modifier is not left in the name or the rows
            Assert.DoesNotContain(ui.Texts(), t => t.Text.StartsWith("⚠") || t.Text.StartsWith("✖"));
            ui.Click("Save Table");

            Assert.Contains(ui.Texts(), t => t.Text == "2d6+1 · legal rolls 3–13");        // the expression stays visible
            ui.Click("Roll");
            Assert.Contains(ui.Texts(), t => t.Text == "Rolled 7 (2d6+1)");
            Assert.Contains(ui.Texts(), t => t.Text == "Wary" && t.FontSize == 26);

            var manual = ui.One<TextBox>(t => t.Name == "ManualBox");
            manual.Text = "9";                                                            // the final number, worked out by hand
            ui.Click("Resolve");
            Assert.Contains(ui.Texts(), t => t.Text == "Rolled 9 (2d6+1)");
            Assert.Contains(ui.Texts(), t => t.Text == "Curious" && t.FontSize == 26);      // resolved as 9, not shifted again

            manual.Text = "2";
            ui.Click("Resolve");
            Assert.Contains(ui.Texts(), t => t.Text == "Enter a whole number from 3 to 13.");

            // The table lists show the expression, and so do recent tables and the history snapshot.
            Assert.Contains(ui.Texts(), t => t.Text == "2d6+1");
            Assert.Equal("Encounter Reaction\n2d6+1 → 9\n\nCurious", ui.Main.RecentRolls[0].Item.FullText);
        });
    }

    [Fact]
    public void An_unsupported_expression_in_the_dice_field_is_explained_in_plain_words()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (_, _) => { });
            ui.Click("Paste Table…");
            ui.One<TextBox>(t => t.Name == "PasteSourceBox").Text = "d6 Loot\n1-6 Coin";
            ui.Click("Interpret");

            ui.One<TextBox>(t => t.Name == "DiceBox").Text = "2d6+1d4";
            ui.Layout();

            Assert.Contains(ui.Texts(), t => t.Text.StartsWith("✖") && t.Text.Contains("'2d6+1d4' is not supported. Use a dice expression such as d20, 2d6, or 2d6+1."));
            Assert.False(ui.One<Button>(b => b.Content as string == "Save Table").IsEnabled);

            ui.One<TextBox>(t => t.Name == "DiceBox").Text = "d20-2";
            ui.Layout();
            Assert.DoesNotContain(ui.Texts(), t => t.Text.StartsWith("✖"));
            Assert.True(ui.One<Button>(b => b.Content as string == "Save Table").IsEnabled);
        });
    }
}
