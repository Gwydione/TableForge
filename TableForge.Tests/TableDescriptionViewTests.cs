using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TableForge.Domain;
using static TableForge.Tests.PortableFixtures;

namespace TableForge.Tests;

/// <summary>RC26 Table Description through the real MainWindow: edited on Review/Edit, shown under the dice while rolling.</summary>
[Collection("UI")]
public class TableDescriptionViewTests
{
    private const string Note = "Roll when entering a new region or when the weather changes.";

    private static UiHarness Open(int roll, string weatherNote = Note, string stormNote = "") => new(roll, (db, c) =>
    {
        var storm = db.SaveTable(new RollableTable
        {
            CollectionId = c.Id, Name = "Storm", Dice = DiceExpression.Parse("d6"), Description = stormNote,
            ResultSets = [Set("", E(1, 6, "Lightning"))],
        });
        db.SaveTable(new RollableTable
        {
            CollectionId = c.Id, Name = "Weather", Dice = DiceExpression.Parse("d20"), Description = weatherNote,
            ResultSets = [Set("", E(1, 10, "Clear"), E(11, 20, "Storm brewing", link: storm.Id))],
        });
    });

    private static List<TextBlock> Descriptions(UiHarness ui) => ui.All<TextBlock>().Where(t => t.Name == "DescriptionText").ToList();

    private static double Top(UiHarness ui, FrameworkElement element) => element.TranslatePoint(new Point(0, 0), ui.Window).Y;

    // ---- Roll ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_description_is_shown_under_the_dice_and_nowhere_else()
    {
        Sta.Run(() =>
        {
            using var ui = Open(3);
            ui.SelectTable("Weather");

            var shown = Assert.Single(Descriptions(ui), t => t.IsVisible);
            Assert.Equal(Note, shown.Text);
            Assert.Equal(TextWrapping.Wrap, shown.TextWrapping);
            var dice = ui.Texts().Single(t => t.Text.StartsWith("d20 · legal rolls", StringComparison.Ordinal));
            Assert.True(Top(ui, shown) > Top(ui, dice));                                             // beneath the dice line
            Assert.Single(ui.Texts(), t => t.Text.Contains(Note, StringComparison.Ordinal));          // not in the table list or Recent Tables

            ui.Click("Roll");
            Assert.Contains(ui.Texts(), t => t.Text == "Clear" && t.FontSize == 26);
            Assert.Single(ui.Texts(), t => t.Text.Contains(Note, StringComparison.Ordinal));          // nor in Recent Rolls
        });
    }

    [Fact]
    public void An_empty_description_takes_no_space_at_all()
    {
        Sta.Run(() =>
        {
            using var ui = Open(3, weatherNote: "");
            ui.SelectTable("Weather");

            var element = Assert.Single(Descriptions(ui));
            Assert.Equal(Visibility.Collapsed, element.Visibility);
            Assert.Equal(0, element.ActualHeight);
        });
    }

    [Fact]
    public void A_followed_table_shows_its_own_description_in_its_own_step()
    {
        Sta.Run(() =>
        {
            using var ui = Open(15, stormNote: "Only outdoors.");
            ui.SelectTable("Weather");
            ui.Click("Roll");
            ui.Click("Open Storm");

            Assert.Equal([Note, "Only outdoors."], Descriptions(ui).Where(t => t.IsVisible).OrderBy(t => Top(ui, t)).Select(t => t.Text).ToArray());
        });
    }

    [Fact]
    public void A_followed_table_without_a_description_shows_none()
    {
        Sta.Run(() =>
        {
            using var ui = Open(15);
            ui.SelectTable("Weather");
            ui.Click("Roll");
            ui.Click("Open Storm");

            Assert.Equal([Note], Descriptions(ui).Where(t => t.IsVisible).Select(t => t.Text).ToArray());
            Assert.Equal(2, Descriptions(ui).Count);
        });
    }

    [Fact]
    public void The_longest_description_leaves_the_roll_controls_usable()
    {
        Sta.Run(() =>
        {
            var longest = string.Concat(Enumerable.Repeat("Roll this when the weather turns and the party is outdoors. ", 40))[..TableDescription.MaxLength];
            using var ui = Open(3, weatherNote: longest);
            ui.SelectTable("Weather");

            var scroller = ui.One<ScrollViewer>(s => s.Name == "TrailScroller");
            var roll = ui.One<Button>(b => b.Name == "RollButton");
            bool RollInView()
            {
                var y = roll.TranslatePoint(new Point(0, 0), scroller).Y;
                return y >= 0 && y + roll.ActualHeight <= scroller.ViewportHeight;
            }

            Assert.Equal(longest, Assert.Single(Descriptions(ui), t => t.IsVisible).Text);
            Assert.True(RollInView());
            ui.Click("Roll");
            ui.Click("Roll");
            Assert.True(RollInView());                                                                  // the usual scroll-to-latest still keeps Roll in sight
        });
    }

    // ---- Review / Edit ------------------------------------------------------------------------------------------------

    [Fact]
    public void Edit_shows_the_description_in_a_multiline_box_between_the_name_and_clamp()
    {
        Sta.Run(() =>
        {
            using var ui = Open(3, weatherNote: "Line one.\nLine two.");
            ui.SelectTable("Weather");
            ui.Main.EditTableCommand.Execute(null);
            ui.Layout();

            var box = ui.One<TextBox>(t => t.Name == "DescriptionBox");
            Assert.Equal("Line one.\nLine two.", box.Text);
            Assert.True(box.AcceptsReturn);
            Assert.Equal(TextWrapping.Wrap, box.TextWrapping);
            Assert.Equal(TableDescription.MaxLength, box.MaxLength);
            Assert.Equal("Table description", AutomationProperties.GetName(box));
            Assert.InRange(box.ActualHeight, 50, 80);                                                    // about three lines

            var name = ui.One<TextBox>(t => t.Name == "NameBox");
            var clamp = ui.One<CheckBox>(c => c.Name == "ClampBox");
            Assert.True(Top(ui, name) < Top(ui, box) && Top(ui, box) < Top(ui, clamp));
        });
    }

    [Fact]
    public void A_description_typed_on_edit_is_saved_and_shown_when_rolling()
    {
        Sta.Run(() =>
        {
            using var ui = Open(3, weatherNote: "");
            ui.SelectTable("Weather");
            ui.Main.EditTableCommand.Execute(null);
            ui.Layout();

            ui.One<TextBox>(t => t.Name == "DescriptionBox").Text = "  Twice at night.\r\n  ";
            ui.Click("Save Table");

            Assert.Equal("Twice at night.", Assert.Single(Descriptions(ui), t => t.IsVisible).Text);
            Assert.Equal("Twice at night.", ui.Db.LoadTable(ui.Main.Tables.Single(t => t.Name == "Weather").Id)!.Description);
        });
    }
}
