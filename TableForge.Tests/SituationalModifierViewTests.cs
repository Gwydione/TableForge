using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TableForge.Domain;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>The Roll screen's Modifier field through the real MainWindow and RollView, with the fixed test dice.</summary>
[Collection("UI")]
public class SituationalModifierViewTests
{
    private static UiHarness Open(int roll) => new(roll, (db, c) =>
    {
        db.SaveTable(Fixtures.Table(DiceExpression.Parse("d20"), (1, 9, "Low"), (10, 13, "Mid"), (14, 20, "High")).Also(t => { t.Name = "Omens"; t.CollectionId = c.Id; }));
        db.SaveTable(Fixtures.Table(DiceExpression.Parse("d6"), (1, 6, "D20 Construction Supplies")).Also(t => { t.Name = "Salvage"; t.CollectionId = c.Id; }));
        db.SaveTable(Fixtures.Table(DiceExpression.Parse("d66"), (11, 66, "Anything")).Also(t => { t.Name = "Names"; t.CollectionId = c.Id; }));
    });

    private static TextBox ModifierBox(UiHarness ui) => ui.One<TextBox>(t => t.Name == "ModifierBox");

    private static void Type(UiHarness ui, TextBox box, string text)
    {
        box.Text = text;
        CommandManager.InvalidateRequerySuggested(); // what a real keystroke does for the Roll button's enabled state
        ui.Layout();
    }

    [Fact]
    public void The_modifier_is_used_by_one_roll_shown_as_a_breakdown_and_reset_to_zero()
    {
        Sta.Run(() =>
        {
            using var ui = Open(11);
            ui.SelectTable("Omens");

            var box = ModifierBox(ui);
            Assert.True(box.IsVisible && box.IsEnabled);
            Assert.Equal("0", box.Text);

            Type(ui, box, "+3");
            ui.Click("Roll");

            Assert.Contains(ui.Texts(), t => t.Text == "Rolled 14");
            Assert.Contains(ui.Texts(), t => t.Text == "11 +3 situational");
            Assert.Contains(ui.Texts(), t => t.Text == "High" && t.FontSize == 26);
            Assert.Equal("0", box.Text);

            ui.Click("Roll");                                               // an ordinary roll again: no breakdown line
            Assert.Equal(["Rolled 14", "Rolled 11"], ui.Texts().Where(t => t.Text.StartsWith("Rolled")).Select(t => t.Text).ToArray());
            Assert.Single(ui.Texts(), t => t.Text.EndsWith("situational"));
        });
    }

    [Fact]
    public void Enter_in_the_modifier_box_rolls()
    {
        Sta.Run(() =>
        {
            using var ui = Open(11);
            ui.SelectTable("Omens");
            var box = ModifierBox(ui);
            Type(ui, box, "-2");

            ui.Press(box, Key.Enter, until: () => ui.Texts().Any(t => t.Text == "Rolled 9"));

            Assert.Contains(ui.Texts(), t => t.Text == "11 -2 situational");
            Assert.Equal("0", box.Text);
        });
    }

    [Fact]
    public void A_roll_outside_the_table_shows_its_calculated_number_and_no_match()
    {
        Sta.Run(() =>
        {
            using var ui = Open(1);
            ui.SelectTable("Omens");
            Type(ui, ModifierBox(ui), "-1");

            ui.Click("Roll");

            Assert.Contains(ui.Texts(), t => t.Text == "Rolled 0");
            Assert.Contains(ui.Texts(), t => t.Text == "1 -1 situational");
            Assert.Contains(ui.Texts(), t => t.Text == "No entry covers 0.");
            Assert.Equal("0 (-1)", ui.Main.RecentRolls[0].RollDisplay);
        });
    }

    [Fact]
    public void An_unreadable_modifier_disables_roll_and_says_why_without_changing_the_text()
    {
        Sta.Run(() =>
        {
            using var ui = Open(11);
            ui.SelectTable("Omens");
            var box = ModifierBox(ui);

            Type(ui, box, "2.5");

            var roll = ui.One<Button>(b => b.Name == "RollButton");
            Assert.False(roll.IsEnabled);
            var error = ui.One<TextBlock>(t => t.Name == "ModifierErrorText");
            Assert.True(error.IsVisible);
            Assert.Equal(SituationalModifier.InvalidMessage, error.Text);
            Assert.Equal("2.5", box.Text);
            Assert.Equal(0, ui.Dice.Calls);

            Type(ui, box, "+2");
            Assert.True(roll.IsEnabled);
            Assert.False(error.IsVisible);
        });
    }

    [Fact]
    public void Switching_tables_resets_a_pending_modifier()
    {
        Sta.Run(() =>
        {
            using var ui = Open(11);
            ui.SelectTable("Omens");
            Type(ui, ModifierBox(ui), "+3");

            ui.SelectTable("Salvage");
            Assert.Equal("0", ModifierBox(ui).Text);

            ui.SelectTable("Omens");
            Assert.Equal("0", ModifierBox(ui).Text);
        });
    }

    [Fact]
    public void A_typed_roll_ignores_the_modifier_and_leaves_it_waiting()
    {
        Sta.Run(() =>
        {
            using var ui = Open(11);
            ui.SelectTable("Omens");
            var box = ModifierBox(ui);
            Type(ui, box, "+3");

            ui.One<TextBox>(t => t.Name == "ManualBox").Text = "14";
            ui.Click("Resolve");

            Assert.Contains(ui.Texts(), t => t.Text == "Rolled 14");
            Assert.DoesNotContain(ui.Texts(), t => t.Text.EndsWith("situational"));
            Assert.Equal("+3", box.Text);
            Assert.Equal(0, ui.Dice.Calls);
        });
    }

    [Fact]
    public void A_d66_table_offers_no_modifier()
    {
        Sta.Run(() =>
        {
            using var ui = Open(35);
            ui.SelectTable("Names");

            Assert.False(ModifierBox(ui).IsVisible);
            Assert.DoesNotContain(ui.Texts(), t => t.Text == "Modifier:");
            ui.Click("Roll");
            Assert.Contains(ui.Texts(), t => t.Text == "Rolled 35 (d66)");
        });
    }

    [Fact]
    public void An_inline_roll_does_not_use_up_the_modifier()
    {
        Sta.Run(() =>
        {
            using var ui = Open(4);
            ui.SelectTable("Salvage");
            ui.Click("Roll");
            var box = ModifierBox(ui);
            Type(ui, box, "+3");

            ui.Dice.Value = 12;
            ui.Click("Roll d20");

            Assert.Equal(12, Assert.Single(Assert.Single(((RollViewModel)ui.Main.Current!).Results).InlineActions).LatestValue);
            Assert.Contains(ui.Texts(), t => t.Text == "Resolved: 12 Construction Supplies");
            Assert.Equal("+3", box.Text);
        });
    }

    // ---- arriving at the box selects its value (RC26): typing replaces the "0" instead of joining it ("+20") --------------

    /// <summary>Types as the keyboard does, through WPF's text input into whatever has keyboard focus (honouring caret and selection).</summary>
    private static void TypeKeys(UiHarness ui, string text)
    {
        foreach (var c in text)
        {
            TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, Keyboard.FocusedElement, c.ToString()));
            ui.Layout();
        }
        CommandManager.InvalidateRequerySuggested();
        ui.Layout();
    }

    private static void AssertWholeValueSelected(TextBox box, string value)
    {
        Assert.True(box.IsKeyboardFocused);
        Assert.Equal(value, box.Text);
        Assert.Equal((0, value.Length), (box.SelectionStart, box.SelectionLength));
    }

    [Fact]
    public void Tabbing_into_the_modifier_selects_the_0_so_typing_replaces_it_and_so_does_the_reset_after_a_roll()
    {
        Sta.Run(() =>
        {
            using var ui = Open(11);
            ui.SelectTable("Omens");
            ui.Window.Activate();
            var box = ModifierBox(ui);
            var roll = ui.One<Button>(b => b.Name == "RollButton");
            Assert.Equal("0", box.Text);

            roll.Focus();
            roll.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));                    // what Tab does
            ui.Layout();
            AssertWholeValueSelected(box, "0");

            TypeKeys(ui, "+2");
            Assert.Equal("+2", box.Text);                                                             // RC25 gave "+20"

            ui.Press(box, Key.Enter, until: () => ui.Texts().Any(t => t.Text == "Rolled 13"));
            Assert.Contains(ui.Texts(), t => t.Text == "11 +2 situational");
            AssertWholeValueSelected(box, "0");                                                        // reset, focus kept, ready to type over

            TypeKeys(ui, "-2");
            Assert.Equal("-2", box.Text);
            TypeKeys(ui, "1");
            Assert.Equal("-21", box.Text);                                                             // typing itself never reselects
        });
    }

    [Theory]
    [InlineData("2", "2")]
    [InlineData("-2", "-2")]
    [InlineData("+2", "+2")]
    public void Shift_tabbing_back_into_the_modifier_selects_the_0_too(string typed, string expected)
    {
        Sta.Run(() =>
        {
            using var ui = Open(11);
            ui.SelectTable("Omens");
            ui.Window.Activate();
            var box = ModifierBox(ui);
            var manual = ui.One<TextBox>(t => t.Name == "ManualBox");

            manual.Focus();
            manual.MoveFocus(new TraversalRequest(FocusNavigationDirection.Previous));               // what Shift+Tab does
            ui.Layout();
            AssertWholeValueSelected(box, "0");

            TypeKeys(ui, typed);
            Assert.Equal(expected, box.Text);
            Assert.Equal(expected, ((RollViewModel)ui.Main.Current!).ModifierText);
        });
    }

    [Fact]
    public void The_first_click_into_the_modifier_selects_the_0_and_a_later_click_places_the_caret_as_usual()
    {
        Sta.Run(() =>
        {
            using var ui = Open(11);
            ui.SelectTable("Omens");
            ui.Window.Activate();
            var box = ModifierBox(ui);
            ui.One<Button>(b => b.Name == "RollButton").Focus();
            ui.Layout();

            MouseButtonEventArgs Click()
            {
                var e = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent };
                box.RaiseEvent(e);
                ui.Layout();
                return e;
            }

            Assert.True(Click().Handled);                                                              // only focuses: no caret beside the 0
            AssertWholeValueSelected(box, "0");
            TypeKeys(ui, "+2");
            Assert.Equal("+2", box.Text);

            Assert.False(Click().Handled);                                                             // already focused: the TextBox handles it normally
            Assert.Equal("+2", box.Text);
        });
    }
}
