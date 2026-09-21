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
            Assert.False(ui.One<ItemsControl>(i => i.Name == "RecentRollsList").IsVisible);   // nothing yet: no empty box

            ui.SelectTable("Room Features");
            ui.Click("Roll");
            ui.SelectTable("Scavenging");
            ui.Dice.Value = 6;
            ui.Click("Roll");

            var list = ui.One<ItemsControl>(i => i.Name == "RecentRollsList");
            Assert.True(list.IsVisible);
            Assert.Equal(2, list.Items.Count);
            Assert.Contains(ui.Texts(), t => t.Text == "Scavenging");
            Assert.Contains(ui.Texts(), t => t.Text == "6");
            Assert.Contains(ui.Texts(), t => t.Text == "Room Features");
            Assert.Contains(ui.Texts(), t => t.Text == "68");
            Assert.Contains(ui.Texts(), t => t.Text.EndsWith("Ambient: Smell of burning flesh · Noise: Hissing · General Feature: Grated floors reveal dozens of people below"));

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
        });
    }

    [Fact]
    public void Recent_tables_list_the_tables_in_use_and_return_to_them_in_one_click()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(6, (db, c) => { db.SaveTable(Fixtures.RoomFeatures(c.Id)); Fixtures.SeedScavenging(db, c.Id); });
            var recent = ui.One<ItemsControl>(i => i.Name == "RecentTablesList");
            Assert.False(recent.IsVisible);                       // the search list alone makes nothing recent
            Assert.Equal(3, ui.One<ListBox>(l => l.Name == "TablesList").Items.Count);

            ui.SelectTable("Scavenging");
            ui.SelectTable("Room Features");
            Assert.True(recent.IsVisible);
            Assert.Equal(["Room Features", "Scavenging"], recent.Items.Cast<TableSummary>().Select(t => t.Name).ToArray());

            // Filtering the big list does not touch the recent list.
            ui.One<TextBox>(t => t.Name == "TableFilterBox").Text = "scavenged";
            ui.Layout();
            Assert.Single(ui.One<ListBox>(l => l.Name == "TablesList").Items);
            Assert.Equal(2, recent.Items.Count);

            Invoke(ui, Row(ui, "RecentTablesList", o => ((TableSummary)o).Name == "Scavenging"));

            Assert.Equal("Scavenging", ((RollViewModel)ui.Main.Current!).Title);
            Assert.Equal(["Scavenging", "Room Features"], recent.Items.Cast<TableSummary>().Select(t => t.Name).ToArray());
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

            var entry = Row(ui, "RecentRollsList", o => true);
            Assert.False(entry.IsEnabled);
            Assert.Contains(ui.Texts(), t => t.Text == "Scavenged Items" && t.FontStyle == System.Windows.FontStyles.Italic);
            Assert.Contains(ui.Texts(), t => t.Text.EndsWith("D4 rations"));
            Assert.Contains("deleted", (string)entry.ToolTip);
        });
    }
}
