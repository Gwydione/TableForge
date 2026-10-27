using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TableForge.Domain;
using TableForge.ViewModels;
using static TableForge.Tests.PortableFixtures;

namespace TableForge.Tests;

/// <summary>
/// UI Polish B: the sidebar's one table list, and Recent Rolls as a panel at the foot of the main area — closed at start, kept open
/// or closed for the session (across tables, and while hidden for pasting or editing), and the same history it always showed.
/// </summary>
[Collection("UI")]
public class RecentRollsPanelTests
{
    private const string LongResult =
        "The Weeping Knight, who asks each traveller the same riddle and remembers every answer given to her, however long ago it was";

    private static UiHarness Open(int roll = 1) => new(roll, (db, c) =>
    {
        var monsters = db.SaveTable(new RollableTable
        {
            CollectionId = c.Id, Name = "Monsters", Dice = DiceExpression.Parse("d6"),
            ResultSets = [Set("", E(1, 3, "Goblin"), E(4, 6, LongResult))],
        });
        db.SaveTable(new RollableTable
        {
            CollectionId = c.Id, Name = "Weather", Dice = DiceExpression.Parse("d6"),
            ResultSets = [Set("", E(1, 3, "Rain"), E(4, 6, "Sun"))],
        });
        db.AddRollHistory(new RollSnapshot(monsters.Id, "Monsters", "d6", 5, LongResult, RerolledCount: 2));
    });

    private static Expander Panel(UiHarness ui) => ui.One<Expander>(e => e.Name == "RecentRollsPanel");

    /// <summary>Opens or closes the panel as its header does (through UI Automation, like a click or Space on the header).</summary>
    private static void Toggle(UiHarness ui)
    {
        var peer = (IExpandCollapseProvider)new ExpanderAutomationPeer(Panel(ui)).GetPattern(PatternInterface.ExpandCollapse);
        if (Panel(ui).IsExpanded) peer.Collapse(); else peer.Expand();
        ui.Layout();
    }

    [Fact]
    public void The_panel_is_closed_at_start_and_opening_or_closing_it_changes_nothing_else()
    {
        Sta.Run(() =>
        {
            using var ui = Open();
            Assert.True(Panel(ui).IsVisible);                                          // there is a roll to show, even before a table is open
            Assert.False(Panel(ui).IsExpanded);
            Assert.Equal("Recent rolls (1)", ui.One<TextBlock>(t => t.Name == "RecentRollsHeader").Text);

            ui.SelectTable("Weather");
            var screen = ui.Main.Current;
            var table = ui.Main.SelectedTable;
            Toggle(ui);
            Assert.True(Panel(ui).IsExpanded);
            Assert.True(ui.Main.IsRecentRollsExpanded);
            Assert.True(ui.One<ItemsControl>(i => i.Name == "RecentRollsList").IsVisible);
            Toggle(ui);
            Assert.False(ui.Main.IsRecentRollsExpanded);
            Assert.Same(screen, ui.Main.Current);                                      // the same screen, table and highlight
            Assert.Same(table, ui.Main.SelectedTable);

            ui.Click("Roll");
            Assert.Equal("Recent rolls (2)", ui.One<TextBlock>(t => t.Name == "RecentRollsHeader").Text);
        });
    }

    [Fact]
    public void Open_or_closed_is_kept_across_tables_and_through_editing_and_pasting_which_hide_the_panel()
    {
        Sta.Run(() =>
        {
            using var ui = Open();
            ui.SelectTable("Monsters");
            Toggle(ui);
            ui.SelectTable("Weather");
            Assert.True(Panel(ui).IsExpanded);                                         // kept when switching tables

            ui.Click("Edit table");
            Assert.False(Panel(ui).IsVisible);                                         // the editor needs the height
            Assert.False(ui.Main.ShowRecentRolls);
            ui.Click("Cancel");
            Assert.IsType<RollViewModel>(ui.Main.Current);
            Assert.True(Panel(ui).IsVisible);
            Assert.True(Panel(ui).IsExpanded);                                         // back as it was: open

            Toggle(ui);
            ui.Click("Paste Table…");
            Assert.False(Panel(ui).IsVisible);
            Assert.IsType<PasteViewModel>(ui.Main.Current).CancelCommand.Execute(null);
            ui.Layout();
            Assert.True(Panel(ui).IsVisible);
            Assert.False(Panel(ui).IsExpanded);                                        // back as it was: closed
        });
    }

    [Fact]
    public void Entries_show_the_table_roll_reroll_note_and_whole_result_wrapped_and_a_click_opens_the_table()
    {
        Sta.Run(() =>
        {
            using var ui = Open(roll: 2);
            ui.SelectTable("Weather");
            ui.Click("Roll");                                                          // newest first: Weather, then the seeded Monsters
            Toggle(ui);

            var list = ui.One<ItemsControl>(i => i.Name == "RecentRollsList");
            Assert.Equal(["Weather", "Monsters"], list.Items.Cast<RecentRollViewModel>().Select(r => r.TableName).ToArray());
            var detail = ui.One<TextBlock>(t => t.Name == "RecentRollDetail" && t.Text.Contains("Rerolled"));
            Assert.Matches(@"^\d\d:\d\d · Rerolled 2× · The Weeping Knight, .* however long ago it was$", detail.Text);
            Assert.Equal(TextWrapping.Wrap, detail.TextWrapping);
            Assert.True(detail.ActualHeight > detail.FontSize * 1.5 || detail.ActualWidth > 600, "the whole result is shown");
            var entry = ui.All<Button>().Single(b => b.DataContext is RecentRollViewModel { TableName: "Monsters" } && b.IsVisible);
            Assert.Contains("Rerolled 2× (used Unique results came up)", (string)entry.ToolTip);
            Assert.Contains(ui.Texts(), t => t.Text == "5" && t.FontWeight == FontWeights.SemiBold);

            ((IInvokeProvider)new ButtonAutomationPeer(entry).GetPattern(PatternInterface.Invoke)).Invoke();
            ui.Layout();
            Assert.Equal("Monsters", Assert.IsType<RollViewModel>(ui.Main.Current).Title);
            Assert.True(Panel(ui).IsExpanded);
        });
    }

    [Fact]
    public void The_keyboard_reaches_the_header_then_each_entry_and_carries_on_past_the_panel()
    {
        Sta.Run(() =>
        {
            using var ui = Open();
            ui.SelectTable("Weather");
            ui.Click("Roll");
            Toggle(ui);

            var header = ViewTests.FindAll<ToggleButton>(Panel(ui)).First();
            Assert.True(header.Focusable && header.IsTabStop);
            header.Focus();
            ui.Layout();
            var entries = ui.All<Button>().Where(b => b.DataContext is RecentRollViewModel && b.IsVisible).ToList();
            Assert.Equal(2, entries.Count);

            header.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Assert.Same(entries[0], ui.Focused);
            ((UIElement)ui.Focused!).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Assert.Same(entries[1], ui.Focused);
            ((UIElement)ui.Focused!).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Assert.False(Panel(ui).IsAncestorOf((DependencyObject)ui.Focused!), "Tab must leave the panel after its last entry");
        });
    }
}
