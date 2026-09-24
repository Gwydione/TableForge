using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>
/// The Review screen's aligned multi-column rendering: when a table's result sets are parallel outputs of one roll
/// (same row count, same range at every row — see <see cref="ReviewViewModel.IsAligned"/>), they must be shown as
/// columns of one table instead of the ordinary one-set-at-a-time tabs. Driven through the real MainWindow/ReviewView,
/// not just the parser or the view model in isolation, since the reported bug was purely in this rendering: the
/// parser and the view model's ResultSets were already correct (see ParallelOutputTests), but ReviewView still showed
/// one result set at a time with no way to see them side by side.
/// </summary>
[Collection("UI")]
public class AlignedReviewViewTests
{
    private static ListBox Tabs(UiHarness ui) => ui.One<ListBox>(l => l.Name == "ResultSetTabs");
    private static ItemsControl AlignedRows(UiHarness ui) => ui.One<ItemsControl>(i => i.Name == "AlignedRowsList");
    private static ItemsControl PerSetRows(UiHarness ui) => ui.One<ItemsControl>(i => i.Name == "RowsList");

    [Fact]
    public void D8_difficulty_modifier_renders_as_one_visible_three_column_table()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (_, _) => { });
            ui.Click("Paste Table…");
            ui.One<TextBox>(t => t.AcceptsReturn).Text = ParallelOutputTests.Difficulty;
            ui.Click("Interpret");

            var review = (ReviewViewModel)ui.Main.Current!;
            Assert.True(review.IsAligned);
            Assert.False(Tabs(ui).IsVisible);                 // no more tab switching: both columns are visible together
            Assert.True(AlignedRows(ui).IsVisible);
            Assert.False(PerSetRows(ui).IsVisible);

            Assert.Equal(7, AlignedRows(ui).Items.Count);
            Assert.Equal(7, review.AlignedRows.Count);
            Assert.All(review.AlignedRows, r => Assert.Equal(2, r.Cells.Count));  // Range + Difficulty + Modifier = one roll column, two outputs

            // The header names both columns, and every row's two outputs are both on screen, correctly paired.
            Assert.Contains(ui.All<TextBox>(), t => t.Text == "Difficulty");
            Assert.Contains(ui.All<TextBox>(), t => t.Text == "Modifier");
            Assert.Equal(["Child's play", "Effortless", "Easy", "Normal", "Demanding", "Hard", "Impossible"],
                review.AlignedRows.Select(r => r.Cells[0].Text).ToArray());
            Assert.Equal(["+30", "+20", "+10", "+0", "-10", "-20", "-30"], review.AlignedRows.Select(r => r.Cells[1].Text).ToArray());

            // Roll resolution is untouched by the rendering change: both result sets still resolve independently and together.
            ui.Click("Save Table");
            ui.Click("Roll");
            Assert.Equal(["Difficulty: Child's play", "Modifier: +30"], ((RollViewModel)ui.Main.Current!).Results.Select(r => $"{r.Heading}: {r.Text}").ToArray());
        });
    }

    [Fact]
    public void One_roll_plus_three_outputs_renders_as_one_visible_four_column_table()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (_, _) => { });
            ui.Click("Paste Table…");
            ui.One<TextBox>(t => t.AcceptsReturn).Text = ParallelOutputTests.DifficultyModifierSave;
            ui.Click("Interpret");

            var review = (ReviewViewModel)ui.Main.Current!;
            Assert.True(review.IsAligned);
            Assert.False(Tabs(ui).IsVisible);
            Assert.True(AlignedRows(ui).IsVisible);

            Assert.Equal(7, AlignedRows(ui).Items.Count);
            Assert.All(review.AlignedRows, r => Assert.Equal(3, r.Cells.Count));  // Range + Difficulty + Modifier + Save = three outputs

            Assert.Contains(ui.All<TextBox>(), t => t.Text == "Difficulty");
            Assert.Contains(ui.All<TextBox>(), t => t.Text == "Modifier");
            Assert.Contains(ui.All<TextBox>(), t => t.Text == "Save");
            Assert.Contains(ui.All<TextBox>(), t => t.Text == "1d4");   // the third column's own values are on screen too
            Assert.Contains(ui.All<TextBox>(), t => t.Text == "3d6");
        });
    }

    [Fact]
    public void Single_result_set_table_still_uses_the_ordinary_one_set_view()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (_, _) => { });
            ui.Click("Paste Table…");
            ui.One<TextBox>(t => t.AcceptsReturn).Text = Fixtures.RandomStartingGear;
            ui.Click("Interpret");

            var review = (ReviewViewModel)ui.Main.Current!;
            Assert.False(review.IsAligned);
            Assert.Empty(review.AlignedRows);
            Assert.True(Tabs(ui).IsVisible);
            Assert.True(PerSetRows(ui).IsVisible);
            Assert.False(AlignedRows(ui).IsVisible);
            Assert.Equal(8, PerSetRows(ui).Items.Count);
        });
    }

    [Fact]
    public void Unrelated_result_sets_with_different_row_counts_remain_separate_tabs()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (_, _) => { });
            ui.Click("Paste Table…");
            ui.One<TextBox>(t => t.AcceptsReturn).Text = MultiSetParserTests.RoomFeatures;
            ui.Click("Interpret");

            var review = (ReviewViewModel)ui.Main.Current!;
            Assert.False(review.IsAligned);                    // Ambient/Noise/General Feature are independent, not parallel outputs
            Assert.Equal(["Ambient", "Noise", "General Feature"], review.ResultSets.Select(s => s.DisplayName).ToArray());
            Assert.True(Tabs(ui).IsVisible);
            Assert.False(AlignedRows(ui).IsVisible);
            Assert.Equal(4, PerSetRows(ui).Items.Count);        // Ambient's own row count, not forced to line up with the others
        });
    }

    /// <summary>The aligned header's name editors, left to right.</summary>
    private static List<TextBox> SetNameBoxes(UiHarness ui) =>
        ui.All<TextBox>().Where(t => t.ToolTip as string == "This column's result set name, e.g. Difficulty, Modifier")
            .OrderBy(t => t.TranslatePoint(new System.Windows.Point(0, 0), ui.Window).X).ToList();

    /// <summary>
    /// Every result set's name editor is visible, full width and not clipped, and sits directly above its own column
    /// (the first row's cell for that result set).
    /// </summary>
    private static void AssertNameEditorsAboveColumns(UiHarness ui, int columns)
    {
        var boxes = SetNameBoxes(ui);
        Assert.Equal(columns, boxes.Count);
        var firstRow = (FrameworkElement)AlignedRows(ui).ItemContainerGenerator.ContainerFromIndex(0);
        var cells = ViewTests.FindAll<TextBox>(firstRow).Where(t => t.Width is double.NaN && t.FontFamily.Source != "Consolas" && t.IsVisible)
            .OrderBy(t => t.TranslatePoint(new System.Windows.Point(0, 0), ui.Window).X).ToList();
        Assert.Equal(columns, cells.Count);

        for (var c = 0; c < columns; c++)
        {
            var box = boxes[c];
            Assert.True(box.IsVisible && box.IsEnabled && !box.IsReadOnly, $"name editor {c + 1} should be visible and editable");
            Assert.Null(System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(box)); // drawn whole, not cut off
            var header = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(box));
            var right = box.TranslatePoint(new System.Windows.Point(box.ActualWidth, 0), header).X;
            Assert.True(right <= header.ActualWidth + 0.5, $"name editor {c + 1} is cut off by its header ({right} > {header.ActualWidth})");
            Assert.Equal(cells[c].ActualWidth, box.ActualWidth, 0.5);
            Assert.Equal(cells[c].TranslatePoint(new System.Windows.Point(0, 0), ui.Window).X, box.TranslatePoint(new System.Windows.Point(0, 0), ui.Window).X, 0.5);
        }
    }

    [Fact]
    public void Unnamed_aligned_result_sets_can_be_named_above_their_columns_in_review_and_in_edit()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(55, (_, _) => { });
            ui.Click("Paste Table…");
            ui.One<TextBox>(t => t.AcceptsReturn).Text = ParallelOutputTests.Wind;
            ui.Click("Interpret");

            // Review: three unnamed sets, each with its own editor over its column.
            Assert.True(((ReviewViewModel)ui.Main.Current!).IsAligned);
            AssertNameEditorsAboveColumns(ui, 3);
            Assert.All(SetNameBoxes(ui), b => Assert.Equal("", b.Text));
            var names = new[] { "Wind Type", "Strength", "Hull Damage Caused" };
            foreach (var (box, name) in SetNameBoxes(ui).Zip(names)) box.Text = name;   // typed, as the person would
            ui.Layout();
            ui.Click("Save Table");

            // Edit: the saved names come back in the same editors, still above their columns, and can be changed.
            ui.Click("Edit table");
            Assert.True(((ReviewViewModel)ui.Main.Current!).IsAligned);
            AssertNameEditorsAboveColumns(ui, 3);
            Assert.Equal(names, SetNameBoxes(ui).Select(b => b.Text).ToArray());
            SetNameBoxes(ui)[2].Text = "Hull Damage";
            ui.Layout();
            ui.Click("Save Table");

            var table = ui.Db.LoadTable(ui.Db.GetTableSummaries(ui.Collection!.Id).Single(t => t.Name == "Wind Type Strength Hull Damage Caused").Id)!;
            Assert.Equal(["Wind Type", "Strength", "Hull Damage"], table.ResultSets.Select(s => s.Name).ToArray());
        });
    }

    [Fact]
    public void Row_alignment_stays_correct_across_result_sets_including_after_an_edit()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (_, _) => { });
            ui.Click("Paste Table…");
            ui.One<TextBox>(t => t.AcceptsReturn).Text = ParallelOutputTests.Difficulty;
            ui.Click("Interpret");
            var review = (ReviewViewModel)ui.Main.Current!;

            // Every aligned row pairs the same range with the matching text from each independent result set.
            var expected = new[] { ("1", "Child's play", "+30"), ("2", "Effortless", "+20"), ("3", "Easy", "+10"),
                ("4-5", "Normal", "+0"), ("6", "Demanding", "-10"), ("7", "Hard", "-20"), ("8", "Impossible", "-30") };
            for (var i = 0; i < expected.Length; i++)
            {
                var (range, difficulty, modifier) = expected[i];
                Assert.Equal(range, review.AlignedRows[i].RangeText);
                Assert.Equal(range, review.ResultSets[0].Rows[i].RangeText);   // the two underlying result sets still carry it independently
                Assert.Equal(range, review.ResultSets[1].Rows[i].RangeText);
                Assert.Equal(difficulty, review.AlignedRows[i].Cells[0].Text);
                Assert.Equal(modifier, review.AlignedRows[i].Cells[1].Text);
            }

            // Editing the shared range on one aligned row keeps both underlying result sets in step.
            review.AlignedRows[3].RangeText = "4-4";
            Assert.Equal("4-4", review.ResultSets[0].Rows[3].RangeText);
            Assert.Equal("4-4", review.ResultSets[1].Rows[3].RangeText);

            // Deleting an aligned row removes that roll from every result set, keeping the rest in step.
            var countBefore = review.AlignedRows.Count;
            review.AlignedRows[3].DeleteCommand.Execute(null);
            Assert.Equal(countBefore - 1, review.ResultSets[0].Rows.Count);
            Assert.Equal(countBefore - 1, review.ResultSets[1].Rows.Count);
            Assert.Equal(review.ResultSets[0].Rows.Select(r => r.RangeText), review.ResultSets[1].Rows.Select(r => r.RangeText));
            Assert.True(review.IsAligned);   // still aligned after the edit, not silently broken
        });
    }
}
