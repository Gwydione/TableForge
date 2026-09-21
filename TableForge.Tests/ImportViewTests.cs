using System.Windows;
using System.Windows.Controls;
using TableForge.Domain;
using TableForge.ViewModels;

namespace TableForge.Tests;

[Collection("UI")]
public class ImportViewTests
{
    [Fact]
    public void Room_features_copy_travels_Paste_Review_Save_Roll_through_the_real_views_as_three_sets()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(68, (_, _) => { });

            // Paste → Interpret
            ui.Click("Paste Table…");
            ui.One<TextBox>(t => t.AcceptsReturn).Text = MultiSetParserTests.RoomFeatures;
            ui.Click("Interpret");

            // Review: three sets on one screen, none needing attention.
            var tabs = ui.One<ListBox>(l => l.Name == "ResultSetTabs");
            var rows = ui.One<ItemsControl>(i => i.Name == "RowsList");
            Assert.Equal("Room Features", ui.One<TextBox>(t => t.Name == "NameBox").Text);
            Assert.Equal(["Ambient", "Noise", "General Feature"], tabs.Items.Cast<ResultSetEditorViewModel>().Select(s => s.DisplayName).ToArray());
            Assert.Equal(4, rows.Items.Count);
            Assert.Equal("Ambient", ui.One<TextBox>(t => t.Name == "SetNameBox").Text);
            Assert.DoesNotContain(ui.Texts(), t => t.Text.StartsWith("⚠"));

            tabs.SelectedIndex = 1;
            ui.Layout();
            Assert.Equal(5, rows.Items.Count);
            Assert.Equal("Noise", ui.One<TextBox>(t => t.Name == "SetNameBox").Text);
            Assert.Contains(ui.All<TextBox>(), t => rows.IsAncestorOf(t) && t.Text == "Low chanting");

            // Save → Roll: one number, three outputs.
            ui.Click("Save Table");
            ui.Click("Roll");
            Assert.Equal(["Rolled 68"], ui.Texts().Where(t => t.Text.StartsWith("Rolled")).Select(t => t.Text).ToArray());
            Assert.Equal(["Smell of burning flesh", "Hissing", "Grated floors reveal dozens of people below"],
                ui.Texts().Where(t => t.FontSize == 26).Select(t => t.Text).ToArray());
        });
    }

    [Fact]
    public void Uncertain_structure_is_visible_on_the_review_screen_and_side_by_side_text_is_split()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (_, _) => { });
            ui.Click("Paste Table…");
            ui.One<TextBox>(t => t.AcceptsReturn).Text = "d100 Names\n01-25 Ash        51-75 Kel\n26-50 Bar        76-00 Lor";
            ui.Click("Interpret");

            var rows = ui.One<ItemsControl>(i => i.Name == "RowsList");
            Assert.Equal(4, rows.Items.Count);
            Assert.Contains(ui.All<TextBlock>(), t => t.Name == "NotesText" && t.IsVisible && t.Text.Contains("side-by-side")); // the split is reported on its row
            Assert.Equal(["Ash", "Bar", "Kel", "Lor"], ui.All<TextBox>().Where(t => rows.IsAncestorOf(t) && t.Text.Length is > 0 and < 5 && !char.IsDigit(t.Text[0])).Select(t => t.Text).ToArray());
        });
    }

    [Fact]
    public void Rows_can_be_pasted_into_the_selected_result_set_from_the_review_screen()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (_, _) => { });
            ui.Click("Paste Table…");
            ui.One<TextBox>(t => t.AcceptsReturn).Text = MultiSetParserTests.RoomFeatures;
            ui.Click("Interpret");
            var tabs = ui.One<ListBox>(l => l.Name == "ResultSetTabs");
            var rows = ui.One<ItemsControl>(i => i.Name == "RowsList");

            Assert.False(ui.One<TextBox>(t => t.Name == "PasteRowsBox").IsVisible);   // the panel is inline and closed by default
            tabs.SelectedIndex = 1;
            ui.Layout();
            ui.Click("Paste rows into this set…");
            Assert.True(ui.One<TextBox>(t => t.Name == "PasteRowsBox").IsVisible);
            Assert.Contains(ui.Texts(), t => t.Text.Contains("Noise") && t.Text == "Noise");

            ui.One<TextBox>(t => t.Name == "PasteRowsBox").Text = "01-50 Quiet\n51-00 Loud";
            ui.Click("Replace all rows");

            Assert.Equal(2, rows.Items.Count);
            Assert.Contains(ui.Texts(), t => t.Text == "Replaced the rows of Noise with 2 pasted rows.");
            Assert.Equal(["Cold stale air", "Damp stone", "Smell of burning flesh", "Heavy incense"],
                ((ReviewViewModel)ui.Main.Current!).ResultSets[0].Rows.Select(r => r.Text).ToArray());   // other sets untouched
            Assert.Equal("Room Features", ui.One<TextBox>(t => t.Name == "NameBox").Text);
            Assert.Equal("d100", ui.One<TextBox>(t => t.Name == "DiceBox").Text);

            ui.Click("Close");
            Assert.False(ui.One<TextBox>(t => t.Name == "PasteRowsBox").IsVisible);
        });
    }

    // ---- long roll trails -----------------------------------------------------------------

    /// <summary>True when the element lies fully inside the scroll viewer's visible area.</summary>
    private static bool IsInView(ScrollViewer scroller, FrameworkElement element)
    {
        var bounds = element.TransformToAncestor(scroller).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Top >= -1 && bounds.Bottom <= scroller.ViewportHeight + 1;
    }

    [Fact]
    public void Repeated_rolls_beyond_the_window_keep_the_newest_result_and_roll_button_in_view()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(12, (db, c) => Fixtures.SeedScavenging(db, c.Id));
            ui.SelectTable("Scavenged Items");
            var scroller = ui.One<ScrollViewer>(s => s.Name == "TrailScroller");
            Assert.Equal(0, scroller.VerticalOffset);

            for (var i = 0; i < 14; i++) ui.Click("Roll");

            Assert.True(scroller.ExtentHeight > scroller.ViewportHeight, "the trail should be longer than the window for this test to mean anything");
            Assert.True(scroller.VerticalOffset > 0);
            Assert.True(IsInView(scroller, ui.One<Button>(b => b.IsVisible && b.Content as string == "Roll")), "the Roll button must stay reachable");
            Assert.True(IsInView(scroller, ui.Texts().Last(t => t.Text.StartsWith("Rolled"))), "the newest roll must be visible");
            Assert.Equal(14, ((RollViewModel)ui.Main.Current!).Current.Outcomes.Count);   // nothing was collapsed
        });
    }

    [Fact]
    public void Following_a_link_from_a_long_trail_brings_the_new_step_and_its_roll_button_into_view()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(6, (db, c) => Fixtures.SeedScavenging(db, c.Id));
            ui.SelectTable("Scavenging");
            var scroller = ui.One<ScrollViewer>(s => s.Name == "TrailScroller");
            for (var i = 0; i < 10; i++) ui.Click("Roll");                   // ten rolls of "1x Scavenged Item" in the first step
            Assert.True(scroller.ExtentHeight > scroller.ViewportHeight);

            ui.Click("Roll Scavenged Items");                                // only the latest roll offers the link

            var roll = (RollViewModel)ui.Main.Current!;
            Assert.Equal(2, roll.Steps.Count);
            Assert.Equal(10, roll.Steps[0].Outcomes.Count);                  // earlier steps are left as they were
            Assert.True(IsInView(scroller, ui.Texts().Single(t => t.Text == "Scavenged Items" && t.FontSize == 24)), "the new step's heading must be visible");
            Assert.True(IsInView(scroller, ui.One<Button>(b => b.IsVisible && b.Content as string == "Roll")));
            Assert.Equal(10, ui.Dice.Calls);                                 // following did not roll
        });
    }

    [Fact]
    public void Opening_another_table_starts_at_the_top_again()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(12, (db, c) => { Fixtures.SeedScavenging(db, c.Id); });
            ui.SelectTable("Scavenged Items");
            var scroller = ui.One<ScrollViewer>(s => s.Name == "TrailScroller");
            for (var i = 0; i < 14; i++) ui.Click("Roll");
            Assert.True(scroller.VerticalOffset > 0);

            ui.SelectTable("Scavenging");

            Assert.Equal(0, scroller.VerticalOffset);
        });
    }
}
