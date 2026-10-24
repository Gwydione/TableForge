using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using TableForge.Domain;
using TableForge.Export.Pum;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>Export to PUM Companion through the real window: the main window's button, the Roll screen's menu item and the review window.</summary>
[Collection("UI")]
public class PumExportViewTests
{
    private static TableEntry E(int min, int max, string text) => new() { Min = min, Max = max, Text = text };

    private static void SeedTwo(Data.AppDatabase db, Collection c)
    {
        Fixtures.SeedScavenging(db, c.Id);
        db.SaveTable(new RollableTable { CollectionId = c.Id, Name = "Blank", Dice = DiceExpression.Parse("d6"), ResultSets = [new ResultSet { Entries = [E(1, 6, "")] }] });
    }

    [Fact]
    public void The_collection_is_exported_with_the_button_under_the_collection_actions_after_a_review()
    {
        Sta.Run(() =>
        {
            var folder = Directory.CreateTempSubdirectory("tableforge-pum-").FullName;
            try
            {
                using var ui = new UiHarness(6, SeedTwo);
                var button = ui.One<Button>(b => b.Name == "ExportPumButton");
                Assert.Equal("Export to PUM Companion…", button.Content);
                Assert.True(button.IsEnabled);
                Assert.True(ui.HasVisibleButton("Export Collection…"));   // the existing controls are unchanged
                Assert.True(ui.HasVisibleButton("Delete Collection…"));

                ui.SavePath = Path.Combine(folder, "Dungeon.pumb");
                ui.Click("Export to PUM Companion…");

                var review = Assert.Single(ui.PumReviews);
                Assert.Equal("2 tables will be exported. 1 table will be skipped.", review.Summary);
                Assert.Equal(("Export to PUM Companion", "Dungeon.pumb"), (ui.SaveTitles[^1], ui.SuggestedFileNames[^1]));
                Assert.True(File.Exists(ui.SavePath));
                Assert.Equal("Exported 2 of 3 tables from \"Dungeon\" to Dungeon.pumb (1 skipped).", ui.Main.Status);
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        });
    }

    [Fact]
    public void A_new_installation_has_nothing_to_export_to_PUM()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1);
            Assert.False(ui.One<Button>(b => b.Name == "ExportPumButton").IsEnabled);
        });
    }

    [Fact]
    public void The_Roll_screen_saves_the_table_it_shows_including_a_followed_link()
    {
        Sta.Run(() =>
        {
            var folder = Directory.CreateTempSubdirectory("tableforge-pum-").FullName;
            try
            {
                using var ui = new UiHarness(6, SeedTwo);
                ui.SelectTable("Scavenging");
                ui.Click("Roll");
                ui.Click("Open Scavenged Items");

                ui.SavePath = Path.Combine(folder, "items.pumb");
                ui.ChooseExport("Save PUM Companion Bundle…");
                Assert.Equal("Export \"Scavenged Items\" to PUM Companion", Assert.Single(ui.PumReviews).Heading);
                Assert.Equal("Scavenged Items.pumb", ui.SuggestedFileNames[^1]);
                Assert.Contains(ui.Texts(), t => t.Name == "CopyMessageText" && t.Text == "PUM bundle saved to items.pumb.");
                Assert.True(File.Exists(ui.SavePath));

                ui.SelectTable("Blank");
                ui.ChooseExport("Save PUM Companion Bundle…");
                Assert.Single(ui.PumReviews);   // no review for a table that can't be exported
                Assert.Contains(ui.Texts(), t => t.Name == "CopyMessageText" && t.Text.StartsWith("This table cannot be exported to PUM Companion: Every result is blank"));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        });
    }

    /// <summary>Shows the real review window as a dialog and presses one of its buttons once it is up.</summary>
    private static bool? ShowAndPress(PumExportReviewViewModel review, string buttonName, Action<PumExportReviewWindow>? look = null)
    {
        var window = new PumExportReviewWindow
        {
            DataContext = review,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false,
        };
        window.ContentRendered += (_, _) =>
        {
            look?.Invoke(window);
            var button = ViewTests.FindAll<Button>(window).Single(b => b.Name == buttonName);
            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        };
        return window.ShowDialog();
    }

    [Fact]
    public void The_review_window_shows_the_summary_and_warnings_first_then_the_full_report_and_answers_Save_or_Cancel()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1);   // listens for binding errors
            var report = PumExporter.Export(new("Dungeon",
            [
                new(1, new RollableTable { Name = "Fine", Dice = DiceExpression.Parse("d6"), ResultSets = [new ResultSet { Entries = [E(1, 6, "x")] }] }),
                new(2, new RollableTable { Name = "Blank", Dice = DiceExpression.Parse("d6"), ResultSets = [new ResultSet { Entries = [E(1, 6, "")] }] }),
            ]), DateTime.UnixEpoch).Report;
            var copied = new List<string>();
            var review = new PumExportReviewViewModel("Dungeon", report, copied.Add);

            var saved = ShowAndPress(review, "SaveBundleButton", window =>
            {
                var texts = ViewTests.FindAll<TextBlock>(window).Where(t => t.IsVisible).ToList();
                Assert.Contains(texts, t => t.Name == "PumSummary" && t.Text == "1 table will be exported. 1 table will be skipped.");
                Assert.Contains(texts, t => t.Text.StartsWith("⚠ Skipped: Blank: Every result is blank"));
                Assert.Contains(texts, t => t.Name == "PumReimportWarning" && t.Text == PumExporter.ReimportWarning);
                var reportBox = ViewTests.FindAll<TextBox>(window).Single(b => b.Name == "PumReportText");
                Assert.Equal(report.ToText(), reportBox.Text);
                Assert.True(reportBox.IsReadOnly);

                // Summary and warnings sit above the report.
                var summaryTop = texts.Single(t => t.Name == "PumSummary").TranslatePoint(new Point(), window).Y;
                var warningTop = texts.Single(t => t.Name == "PumReimportWarning").TranslatePoint(new Point(), window).Y;
                Assert.True(summaryTop < warningTop && warningTop < reportBox.TranslatePoint(new Point(), window).Y);

                var copy = ViewTests.FindAll<Button>(window).Single(b => b.Name == "CopyReportButton");
                ((IInvokeProvider)new ButtonAutomationPeer(copy).GetPattern(PatternInterface.Invoke)).Invoke();
                window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => { });   // an automation click is queued
                Assert.Equal([report.ToText()], copied);
                Assert.Equal("Cancel", ViewTests.FindAll<Button>(window).Single(b => b.Name == "PumCancelButton").Content);
            });
            Assert.True(saved);
            Assert.False(ShowAndPress(review, "PumCancelButton"));
        });
    }

    [Fact]
    public void With_nothing_to_save_the_review_window_offers_only_Close()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1);
            var report = PumExporter.Export(new("Dungeon",
                [new(2, new RollableTable { Name = "Blank", Dice = DiceExpression.Parse("d6"), ResultSets = [new ResultSet { Entries = [E(1, 6, "")] }] })]),
                DateTime.UnixEpoch).Report;
            var closed = ShowAndPress(new PumExportReviewViewModel("Dungeon", report, _ => { }), "PumCancelButton", window =>
            {
                var buttons = ViewTests.FindAll<Button>(window).ToList();
                Assert.False(buttons.Single(b => b.Name == "SaveBundleButton").IsVisible);
                Assert.Equal("Close", buttons.Single(b => b.Name == "PumCancelButton").Content);
                Assert.False(ViewTests.FindAll<TextBlock>(window).Single(t => t.Name == "PumReimportWarning").IsVisible);
            });
            Assert.False(closed);
        });
    }
}
