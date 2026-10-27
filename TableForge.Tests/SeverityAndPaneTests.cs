using System.Windows.Controls;
using System.Windows.Media;
using TableForge.Data;
using TableForge.Domain;
using TableForge.Import;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>Info / Warning / Error presentation in Review, at view-model level.</summary>
public class SeverityTests
{
    private const string Columns = "d100 Names\n01-25 Ash        51-75 Kel\n26-50 Bar        76-00 Lor";

    private static ReviewViewModel Review(TempDatabase temp, out MainViewModel main, string text)
    {
        var db = temp.Open();
        db.CreateCollection("C");
        main = new MainViewModel(db, new FixedDice(1));
        main.PasteTableCommand.Execute(null);
        ((PasteViewModel)main.Current!).SourceText = text;
        ((PasteViewModel)main.Current!).InterpretCommand.Execute(null);
        return (ReviewViewModel)main.Current!;
    }

    [Fact]
    public void Information_is_not_styled_or_symbolised_like_a_warning()
    {
        using var temp = new TempDatabase();
        var review = Review(temp, out _, Columns);

        var row = review.Rows[0];
        Assert.True(row.HasInfo);
        Assert.False(row.HasWarning || row.HasError);
        Assert.StartsWith("ℹ ", row.Notes);
        Assert.Contains("side-by-side", row.Notes);
        Assert.All(review.Rows.Skip(1), r => Assert.False(r.HasNotes));
        Assert.False(review.ResultSets[0].HasNotes);         // information alone does not put the ⚠ on the tab
        Assert.Empty(review.ValidationNotes);
        Assert.True(review.CanSave);
    }

    [Fact]
    public void Warnings_and_errors_are_distinct_and_the_most_serious_note_styles_the_row()
    {
        using var temp = new TempDatabase();
        var review = Review(temp, out _, Columns);

        review.Rows[0].RangeText = "01-30";                   // overlaps row 2: a validation warning on rows 1 and 2
        Assert.True(review.Rows[0].HasWarning);              // the row still carries its information note...
        Assert.False(review.Rows[0].HasInfo);                // ...but is styled by the warning
        Assert.Contains("ℹ", review.Rows[0].Notes);
        Assert.Contains("⚠", review.Rows[0].Notes);
        Assert.True(review.Rows[1].HasWarning);
        Assert.StartsWith("⚠ ", review.Rows[1].Notes);
        Assert.True(review.ResultSets[0].HasNotes);

        review.Rows[1].RangeText = "nine";
        Assert.True(review.Rows[1].HasError);
        Assert.False(review.Rows[1].HasWarning);
        Assert.StartsWith("✖ ", review.Rows[1].Notes);
        Assert.False(review.CanSave);
    }

    [Fact]
    public void Parser_severity_maps_to_the_matching_level_on_a_row()
    {
        using var temp = new TempDatabase();
        // A joined wrapped line is a Warning; a proven column split is Info: one import shows both.
        var review = Review(temp, out _, "d100 Mixed\n\nNAMES\n01-50 Ash        51-00 Kel\n\nOTHER\n01-50 Wrapped\nline\n51-00 Fine");

        Assert.True(review.ResultSets[0].Rows[0].HasInfo);
        var wrapped = review.ResultSets[1].Rows[0];
        Assert.True(wrapped.HasWarning);
        Assert.StartsWith("⚠ ", wrapped.Notes);
    }

    [Fact]
    public void Table_level_information_is_listed_apart_from_warnings()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var collection = db.CreateCollection("C");
        var draft = TableTextParser.Parse("d6 Loot\n1-6 Coin");
        draft.Issues.Add(new ParseIssue(ParseIssueCode.SideBySideSplit, ParseIssueSeverity.Info, ParseIssueTarget.Source, "FYI: something was interpreted."));
        draft.Issues.Add(new ParseIssue(ParseIssueCode.UnrecognizedLine, ParseIssueSeverity.Warning, ParseIssueTarget.Source, "Check this line."));

        var review = new ReviewViewModel(draft, collection, db, _ => { }, () => { });

        Assert.Equal(["FYI: something was interpreted."], review.InfoNotes.ToArray());
        Assert.Equal(["Check this line."], review.TableNotes.ToArray());
    }
}

/// <summary>Severity styling and the left-hand Recent Tables / Recent Rolls lists, through the real views.</summary>
[Collection("UI")]
public class SeverityAndPaneViewTests
{
    private static Color BackgroundOf(Border b) => ((SolidColorBrush)b.Background).Color;

    private static Border RowBorder(UiHarness ui, int index) =>
        ui.All<Border>().Where(b => b.Name == "RowBorder").ElementAt(index);

    [Fact]
    public void Info_and_warning_rows_look_different_and_only_the_warning_flags_the_tab()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (_, _) => { });
            ui.Click("Paste Table…");
            ui.One<TextBox>(t => t.AcceptsReturn).Text = "d100 Names\n01-25 Ash        51-75 Kel\n26-50 Bar        76-00 Lor";
            ui.Click("Interpret");

            // Information only: a note on row 1, in the information colours, with an ℹ, and no ⚠ anywhere.
            var infoNotes = ui.All<TextBlock>().Single(t => t.Name == "NotesText" && t.IsVisible);
            Assert.StartsWith("ℹ", infoNotes.Text);
            var infoBackground = BackgroundOf(RowBorder(ui, 0));
            var infoForeground = ((SolidColorBrush)infoNotes.Foreground).Color;
            Assert.DoesNotContain(ui.Texts(), t => t.Text.Contains('⚠'));

            // Now make a real problem: rows 1 and 2 overlap.
            ((ReviewViewModel)ui.Main.Current!).Rows[0].RangeText = "01-30";
            ui.Layout();

            var warningNotes = ui.All<TextBlock>().Where(t => t.Name == "NotesText" && t.IsVisible).ToList();
            Assert.Equal(2, warningNotes.Count);
            var warningBackground = BackgroundOf(RowBorder(ui, 1));
            var warningForeground = ((SolidColorBrush)warningNotes[1].Foreground).Color;
            Assert.StartsWith("⚠", warningNotes[1].Text);

            Assert.NotEqual(infoBackground, warningBackground);
            Assert.NotEqual(infoForeground, warningForeground);
            Assert.Contains(ui.Texts(), t => t.Text.Contains('⚠'));                                  // bottom list and the tab
            Assert.Equal(warningBackground, BackgroundOf(RowBorder(ui, 0)));                            // row 1 now carries a warning too

            // An error is different again.
            ((ReviewViewModel)ui.Main.Current!).Rows[1].RangeText = "nine";
            ui.Layout();
            var errorBackground = BackgroundOf(RowBorder(ui, 1));
            Assert.NotEqual(errorBackground, warningBackground);
            Assert.NotEqual(errorBackground, infoBackground);
        });
    }

    // ---- left pane ----------------------------------------------------------------------------

    private static Button Row(UiHarness ui, string listName, Func<object, bool> match) =>
        ui.One<ItemsControl>(i => i.Name == listName).Items.Cast<object>().Where(match)
            .Select(item => ui.All<Button>().First(b => ReferenceEquals(b.DataContext, item) && b.IsVisible)).First();

    private static void Invoke(UiHarness ui, Button button)
    {
        ui.Layout();
        Assert.True(button.IsEnabled);
        ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(button)
            .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        ui.Layout();
    }

    [Fact]
    public void Recent_rolls_show_compactly_with_a_full_snapshot_tooltip_and_reopen_their_table()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(68, (db, c) => { db.SaveTable(Fixtures.RoomFeatures(c.Id)); Fixtures.SeedScavenging(db, c.Id); });
            var panel = ui.One<Expander>(e => e.Name == "RecentRollsPanel");
            Assert.False(panel.IsVisible);                                             // nothing yet: no empty panel

            ui.SelectTable("Room Features");
            ui.Click("Roll");
            ui.SelectTable("Scavenging");
            ui.Dice.Value = 6;
            ui.Click("Roll");

            Assert.True(panel.IsVisible);
            Assert.False(panel.IsExpanded);                                            // closed until opened
            Assert.DoesNotContain(ui.All<ItemsControl>(), i => i.Name == "RecentRollsList" && i.IsVisible);
            Assert.Equal("Recent rolls (2)", ui.One<TextBlock>(t => t.Name == "RecentRollsHeader").Text);
            panel.IsExpanded = true;
            ui.Layout();
            Assert.True(ui.Main.IsRecentRollsExpanded);
            var list = ui.One<ItemsControl>(i => i.Name == "RecentRollsList");
            Assert.True(list.IsVisible);
            Assert.Equal(2, list.Items.Count);
            Assert.Contains(ui.Texts(), t => t.Text == "Scavenging");
            Assert.Contains(ui.Texts(), t => t.Text == "6");
            Assert.Contains(ui.Texts(), t => t.Text == "Room Features");
            Assert.Contains(ui.Texts(), t => t.Text == "68");
            var detail = ui.One<TextBlock>(t => t.Name == "RecentRollDetail" && t.Text.EndsWith("Ambient: Smell of burning flesh · Noise: Hissing · General Feature: Grated floors reveal dozens of people below"));
            Assert.Equal(System.Windows.TextWrapping.Wrap, detail.TextWrapping);                    // the whole result, wrapped

            var roomEntry = Row(ui, "RecentRollsList", o => ((RecentRollViewModel)o).TableName == "Room Features");
            Assert.Equal("Room Features\nd100 → 68\n\nAmbient: Smell of burning flesh\nNoise: Hissing\nGeneral Feature: Grated floors reveal dozens of people below",
                roomEntry.ToolTip);

            // Clicking reopens the table for future rolling: fresh, with no result restored and no roll made.
            Invoke(ui, roomEntry);
            var roll = (RollViewModel)ui.Main.Current!;
            Assert.Equal("Room Features", roll.Title);
            Assert.Empty(roll.Current.Outcomes);
            Assert.Contains(ui.Texts(), t => t.Text == "Not rolled yet.");
            Assert.Equal(2, ui.Main.RecentRolls.Count);
            Assert.True(panel.IsExpanded);                                             // still open on the reopened table
        });
    }

    [Fact]
    public void Recent_tables_are_still_tracked_but_the_sidebar_lists_only_the_searchable_table_list()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(6, (db, c) => { db.SaveTable(Fixtures.RoomFeatures(c.Id)); Fixtures.SeedScavenging(db, c.Id); });
            ui.SelectTable("Scavenging");
            ui.SelectTable("Room Features");

            Assert.Equal(["Room Features", "Scavenging"], ui.Main.RecentTables.Select(t => t.Name).ToArray());   // tracking unchanged
            Assert.DoesNotContain(ui.All<ItemsControl>(), i => i.Name == "RecentTablesList");
            Assert.DoesNotContain(ui.Texts(), t => t.Text == "Recent tables");
            var list = ui.One<ListBox>(l => l.Name == "TablesList");
            Assert.Equal(3, list.Items.Count);                                         // every table, each once
            // The list now fills the pane down to Edit/Delete (more than the 220 px it had), with both buttons in view.
            Assert.True(list.MaxHeight > 220, $"the table list may only grow to {list.MaxHeight:0} px");
            var pane = ui.One<ScrollViewer>(v => v.Name == "LeftScroll");
            var delete = ui.All<Button>().Single(b => b.Content as string == "Delete table");
            var bottom = delete.TransformToAncestor(pane).Transform(new System.Windows.Point(0, delete.ActualHeight)).Y;
            Assert.True(bottom <= pane.ViewportHeight + 1, "Edit and Delete stay in view without scrolling");

            ui.One<TextBox>(t => t.Name == "TableFilterBox").Text = "scavenged";
            ui.Layout();
            Assert.Equal("Scavenged Items", Assert.Single(list.Items.Cast<TableSummary>()).Name);
            Assert.Equal("Search results", ui.One<TextBlock>(t => t.Name == "TableListHeading").Text);
        });
    }

    [Fact]
    public void A_recent_roll_of_a_deleted_table_stays_readable_but_cannot_be_opened()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(12, (db, c) => Fixtures.SeedScavenging(db, c.Id));
            ui.SelectTable("Scavenged Items");
            ui.Click("Roll");

            ui.Main.SelectedTable = ui.Main.Tables.Single(t => t.Name == "Scavenged Items");
            ui.Click("Delete table");                                          // the harness confirms
            ui.Main.IsRecentRollsExpanded = true;
            ui.Layout();

            var entry = Row(ui, "RecentRollsList", o => true);
            Assert.False(entry.IsEnabled);
            Assert.Contains(ui.Texts(), t => t.Text == "Scavenged Items" && t.FontStyle == System.Windows.FontStyles.Italic);
            Assert.Contains(ui.Texts(), t => t.Text.EndsWith("D4 rations"));
            Assert.Contains("deleted", (string)entry.ToolTip);
        });
    }
}
