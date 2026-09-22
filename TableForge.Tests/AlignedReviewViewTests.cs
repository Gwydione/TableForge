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
