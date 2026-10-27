using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TableForge.Domain;
using TableForge.ViewModels;
using TableForge.Views;
using static TableForge.Tests.PortableFixtures;

namespace TableForge.Tests;

/// <summary>UI Polish C: the column-width rule itself.</summary>
public class AlignedColumnWidthTests
{
    [Theory]
    [InlineData(1000, 2, 98, 0, 451)]     // Roll: (1000 - 98) / 2
    [InlineData(1000, 3, 98, 0, 300)]     // 300.67, rounded down so the columns never overflow
    [InlineData(1000, 5, 98, 0, 230)]     // 180 would be too narrow: the minimum, and the area scrolls
    [InlineData(1000, 2, 150, 4, 421)]    // Edit: (1000 - 150) / 2 - 4
    [InlineData(600, 1, 150, 4, 446)]
    [InlineData(0, 2, 98, 0, 230)]        // not laid out yet
    [InlineData(1000, 0, 98, 0, 230)]     // no result sets
    public void Columns_share_the_viewport_after_its_fixed_parts_but_never_go_below_230(double viewport, int sets, double fixedWidth, double gap, double expected) =>
        Assert.Equal(expected, AlignedColumnWidth.For(viewport, sets, fixedWidth, gap));

    [Fact]
    public void Columns_never_add_up_to_more_than_the_viewport()
    {
        for (var viewport = 400; viewport <= 3000; viewport += 7)
            for (var sets = 2; sets <= 5; sets++)
            {
                var w = AlignedColumnWidth.For(viewport, sets, 150, 4);
                if (w > AlignedColumnWidth.MinWidth) Assert.True(150 + sets * (w + 4) <= viewport, $"{viewport} px, {sets} sets: {w}");
            }
    }
}

/// <summary>UI Polish C: aligned result columns on the Roll and Edit screens, through the real window.</summary>
[Collection("UI")]
public class AlignedColumnsViewTests
{
    private const string Long = "A long result that keeps going so that it must wrap inside its own column rather than push the table wider";

    /// <summary>An aligned table with <paramref name="sets"/> result sets of four d8 rows; the last set's first row is long.</summary>
    private static RollableTable Aligned(long collectionId, int sets) => new()
    {
        CollectionId = collectionId, Name = $"Aligned {sets}", Dice = DiceExpression.Parse("d8"),
        ResultSets = Enumerable.Range(1, sets).Select(n => Set($"Set {n}",
            E(1, 2, n == sets ? Long : $"First {n}"), E(3, 4, $"Second {n}"), E(5, 6, $"Third {n}"), E(7, 8, $"Fourth {n}"))).ToList(),
    };

    private static UiHarness Open(int width, int height, int roll = 1) => new UiHarness(roll, (db, c) =>
    {
        foreach (var n in new[] { 2, 3, 4, 5 }) db.SaveTable(Aligned(c.Id, n));
        var unique = Aligned(c.Id, 2).Also(t => t.Name = "Unique pair");
        unique.ResultSets[1].Entries[0].IsUnique = true;
        unique.ResultSets[1].Entries[1].IsUnique = true;
        db.SaveTable(unique);
        db.SaveTable(new RollableTable
        {
            CollectionId = c.Id, Name = "Single", Dice = DiceExpression.Parse("d6"),
            ResultSets = [Set("", E(1, 3, "Rain"), E(4, 6, Long))],
        });
    }).Also(ui => Resize(ui, width, height));

    private static void Resize(UiHarness ui, int width, int height)
    {
        ui.Window.Width = width;
        ui.Window.Height = height;
        ui.Layout();
    }

    private static double X(FrameworkElement e, UiHarness ui) => e.TransformToAncestor(ui.Window).Transform(new Point(0, 0)).X;

    private static List<T> Visible<T>(UiHarness ui, string name) where T : FrameworkElement =>
        ui.All<T>().Where(e => e.Name == name && e.IsVisible).ToList();

    /// <summary>Every heading is as wide as its column's cells and starts where they start, in every row.</summary>
    private static void AssertAligned<THeading, TCell>(UiHarness ui, string heading, string cell, int sets, double cellInset)
        where THeading : FrameworkElement where TCell : FrameworkElement
    {
        var headings = Visible<THeading>(ui, heading);
        var cells = Visible<TCell>(ui, cell);
        Assert.Equal(sets, headings.Count);
        Assert.Equal(sets * 4, cells.Count);
        for (var i = 0; i < cells.Count; i++)
        {
            var h = headings[i % sets];
            Assert.Equal(h.ActualWidth, cells[i].ActualWidth, 0.5);
            Assert.Equal(X(h, ui), X(cells[i], ui) + cellInset, 0.5);
        }
    }

    // ---- Roll screen ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1000, 640)]
    [InlineData(1200, 820)]
    [InlineData(1920, 1080)]
    public void On_the_Roll_screen_columns_fill_the_width_stay_aligned_and_scroll_sideways_only_below_230(int width, int height)
    {
        Sta.Run(() =>
        {
            using var ui = Open(width, height);
            foreach (var sets in new[] { 2, 3, 4, 5 })
            {
                ui.SelectTable($"Aligned {sets}");
                var scroller = ui.One<ScrollViewer>(s => s.Name == "AlignedRollScroller");
                var expected = AlignedColumnWidth.For(scroller.ViewportWidth, sets, 98, 0);
                var cells = Visible<Border>(ui, "AlignedCell");
                Assert.All(cells, c => Assert.Equal(expected, c.ActualWidth));
                AssertAligned<TextBlock, Border>(ui, "AlignedRollHeader", "AlignedCell", sets, cellInset: 2);   // text starts 2 px into its cell

                if (expected > AlignedColumnWidth.MinWidth)
                {
                    Assert.True(scroller.ExtentWidth <= scroller.ViewportWidth + 0.5, $"{sets} sets at {width}: no sideways scrolling needed");
                    Assert.True(scroller.ViewportWidth - (98 + sets * expected) < sets, "the columns use the width there is");
                }
                else
                {
                    Assert.Equal(Visibility.Visible, scroller.ComputedHorizontalScrollBarVisibility);
                    scroller.ScrollToRightEnd();
                    ui.Layout();
                    AssertAligned<TextBlock, Border>(ui, "AlignedRollHeader", "AlignedCell", sets, cellInset: 2);   // headings scroll with the rows
                    var last = cells[^1];
                    var right = last.TransformToAncestor(scroller).Transform(new Point(last.ActualWidth, 0)).X;
                    Assert.True(right <= scroller.ViewportWidth + 1, "the last column can be scrolled into view");
                }

                // Long text wraps inside its column.
                var longText = ui.Texts().Single(t => t.Text == Long && ((FrameworkElement)t.Parent).Parent is Border { Name: "AlignedCell" });
                Assert.True(longText.ActualWidth <= expected, "long text stays inside its column");
                if (expected < 700) Assert.True(longText.ActualHeight > longText.FontSize * 2, "and wraps onto more lines");
            }
        });
    }

    [Fact]
    public void Resizing_keeps_the_rolled_row_green_used_cells_gray_and_changes_nothing_else()
    {
        Sta.Run(() =>
        {
            using var ui = Open(1200, 820, roll: 1);
            ui.SelectTable("Unique pair");
            ui.Click("Roll");                                                          // row 1: its Unique cell is used, and rolled
            ui.Dice.Value = 3;
            ui.Click("Roll");                                                          // row 2
            var screen = ui.Main.Current;
            var table = ui.Main.SelectedTable;
            var outcomes = ((RollViewModel)screen!).Current.Outcomes.Count;

            foreach (var (w, h) in new[] { (1000, 640), (1920, 1080), (1200, 820), (1000, 640) })
            {
                Resize(ui, w, h);
                var cells = Visible<Border>(ui, "AlignedCell");
                var used = (System.Windows.Media.SolidColorBrush)cells[1].Background;  // row 1, set 2: used, not the latest roll
                Assert.Equal(System.Windows.Media.Color.FromRgb(0xEE, 0xEE, 0xEE), used.Color);
                Assert.Null(cells[3].Background);                                      // row 2, set 2: used but rolled, so the row's green shows
                Assert.Null(cells[0].Background);                                      // repeatable
                var tags = ui.Texts().Where(t => t.Name == "UniqueTagText").ToList();
                Assert.All(tags, t => Assert.True(cells.Any(c => c.IsAncestorOf(t))));  // each tag inside its own cell
                AssertAligned<TextBlock, Border>(ui, "AlignedRollHeader", "AlignedCell", 2, cellInset: 2);
            }

            Assert.Same(screen, ui.Main.Current);                                      // resizing changed no screen, table or result
            Assert.Same(table, ui.Main.SelectedTable);
            Assert.Equal(outcomes, ((RollViewModel)screen).Current.Outcomes.Count);
            Assert.Equal(2, ((RollViewModel)screen).Current.ResultSets[1].Entries.Count(e => e.IsUsed));
        });
    }

    [Fact]
    public void The_layout_settles_after_a_resize_with_no_further_size_changes()
    {
        Sta.Run(() =>
        {
            using var ui = Open(1200, 820);
            ui.SelectTable("Aligned 3");
            foreach (var (w, h) in new[] { (1000, 640), (1920, 1080), (1003, 700), (1200, 820) })
            {
                Resize(ui, w, h);
                var changes = 0;
                SizeChangedEventHandler count = (_, _) => changes++;
                var cells = Visible<Border>(ui, "AlignedCell");
                foreach (var c in cells) c.SizeChanged += count;
                ui.Layout();
                ui.Layout();
                foreach (var c in cells) c.SizeChanged -= count;
                Assert.Equal(0, changes);                                              // settled: no feedback loop, no oscillation
            }
        });
    }

    [Fact]
    public void The_mouse_wheel_over_an_aligned_table_still_scrolls_the_Roll_screen()
    {
        Sta.Run(() =>
        {
            using var ui = Open(1000, 640);
            ui.SelectTable("Aligned 5");
            for (var i = 0; i < 4; i++) ui.Click("Roll");                              // a tall page
            var page = ui.One<ScrollViewer>(s => s.Name == "TrailScroller");
            page.ScrollToTop();
            ui.Layout();
            Assert.True(page.ScrollableHeight > 0);

            var cell = Visible<Border>(ui, "AlignedCell")[0];
            cell.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent });
            ui.Layout();
            Assert.True(page.VerticalOffset > 0, "the page scrolled");
            Assert.Equal(0, ui.One<ScrollViewer>(s => s.Name == "AlignedRollScroller").VerticalOffset);
        });
    }

    [Fact]
    public void Single_and_stacked_tables_keep_their_layout()
    {
        Sta.Run(() =>
        {
            using var ui = Open(1200, 820);
            ui.SelectTable("Single");
            Assert.False(ui.One<ScrollViewer>(s => s.Name == "AlignedRollScroller").IsVisible);
            Assert.True(ui.One<ItemsControl>(i => i.Name == "ResultSetsList").IsVisible);
            var text = ui.Texts().Single(t => t.Text == Long);
            Assert.True(text.ActualWidth > 600, "a single set's result column still takes the remaining width");

            ui.Click("Edit table");
            Assert.False(ui.One<ScrollViewer>(s => s.Name == "AlignedHeaderScroller").IsVisible);
            Assert.True(ui.All<TextBox>().First(b => b.Name == "ResultTextBox" && b.IsVisible).ActualWidth > 300);
        });
    }

    // ---- Edit screen ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1000, 640)]
    [InlineData(1200, 820)]
    [InlineData(1920, 1080)]
    public void In_the_editor_name_boxes_sit_over_their_columns_at_any_width_and_follow_sideways_scrolling(int width, int height)
    {
        Sta.Run(() =>
        {
            using var ui = Open(width, height);
            foreach (var sets in new[] { 2, 5 })
            {
                ui.SelectTable($"Aligned {sets}");
                ui.Click("Edit table");
                var rows = ui.One<ScrollViewer>(s => s.Name == "AlignedRowsScroller");
                var header = ui.One<ScrollViewer>(s => s.Name == "AlignedHeaderScroller");
                var expected = AlignedColumnWidth.For(rows.ViewportWidth, sets, 150, 4);
                Assert.All(Visible<StackPanel>(ui, "AlignedCell"), c => Assert.Equal(expected, c.ActualWidth));
                Assert.Equal(rows.ViewportWidth, header.ActualWidth, 0.5);
                AssertAligned<TextBox, StackPanel>(ui, "AlignedSetName", "AlignedCell", sets, cellInset: 0);

                // Every cell keeps its link chooser and Unique checkbox in full.
                foreach (var cell in Visible<StackPanel>(ui, "AlignedCell"))
                {
                    var box = ViewTests.FindAll<CheckBox>(cell).Single(b => b.Name == "UniqueBox");
                    var combo = ViewTests.FindAll<ComboBox>(cell).Single();
                    Assert.True(combo.ActualWidth >= 100, "the link chooser keeps usable room");
                    Assert.True(box.TransformToAncestor(cell).Transform(new Point(box.ActualWidth, 0)).X <= cell.ActualWidth + 0.5);
                }

                if (expected == AlignedColumnWidth.MinWidth)
                {
                    rows.ScrollToRightEnd();
                    ui.Layout();
                    Assert.True(rows.HorizontalOffset > 0);
                    Assert.Equal(rows.HorizontalOffset, header.HorizontalOffset, 0.5);
                    AssertAligned<TextBox, StackPanel>(ui, "AlignedSetName", "AlignedCell", sets, cellInset: 0);
                }
                else Assert.True(rows.ExtentWidth <= rows.ViewportWidth + 0.5, "no sideways scrolling when the columns fit");
                ui.Click("Cancel");
            }
        });
    }

    [Fact]
    public void In_the_editor_Tab_order_is_unchanged_and_typing_or_tabbing_keeps_headings_and_rows_together()
    {
        Sta.Run(() =>
        {
            using var ui = Open(1000, 640);
            ui.SelectTable("Aligned 5");
            ui.Click("Edit table");
            var rows = ui.One<ScrollViewer>(s => s.Name == "AlignedRowsScroller");
            var header = ui.One<ScrollViewer>(s => s.Name == "AlignedHeaderScroller");

            // Each cell: text → link → Unique, then the next cell's text.
            var first = ViewTests.FindAll<TextBox>(Visible<StackPanel>(ui, "AlignedCell")[0]).First(b => b.Name == "ResultTextBox");
            first.Focus();
            ui.Layout();
            var order = new List<string>();
            for (var i = 0; i < 4; i++)
            {
                ((UIElement)ui.Focused!).MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                ui.Layout();
                order.Add(ui.Focused switch { ComboBox => "link", CheckBox => "unique", TextBox { Name: "ResultTextBox" } => "text", var f => f?.GetType().Name ?? "none" });
            }
            Assert.Equal(["link", "unique", "text", "link"], order);

            // Typing changes no scroll position.
            first.Focus();
            ui.Layout();
            var offset = rows.HorizontalOffset;
            first.Text += " more";
            ui.Layout();
            Assert.Equal(offset, rows.HorizontalOffset);

            // Tab into the last name box, scrolled out of view: the rows come along with the headings.
            var names = Visible<TextBox>(ui, "AlignedSetName");
            names[^1].Focus();
            ui.Layout();
            Assert.True(header.HorizontalOffset > 0);
            Assert.Equal(header.HorizontalOffset, rows.HorizontalOffset, 0.5);
            AssertAligned<TextBox, StackPanel>(ui, "AlignedSetName", "AlignedCell", 5, cellInset: 0);
        });
    }
}
