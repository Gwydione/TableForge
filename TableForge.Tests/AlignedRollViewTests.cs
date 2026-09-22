using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>
/// The Roll screen's aligned multi-column rendering: when a table's result sets are parallel outputs of one roll
/// (same row count, same range at every row — see <see cref="RollStepViewModel.IsAligned"/>), the browsable table at
/// the bottom of the Roll screen must be shown as columns of one grid instead of separate stacked sections, matching
/// the fix already made to the Review screen (see AlignedReviewViewTests). The roll result shown at the top (the
/// "Rolled N" line and each result set's matched output) is unaffected by any of this and is not touched here.
/// Driven through the real MainWindow/RollView, not just the view model in isolation.
/// </summary>
[Collection("UI")]
public class AlignedRollViewTests
{
    private static ItemsControl AlignedRows(UiHarness ui) => ui.One<ItemsControl>(i => i.Name == "AlignedRollRowsList");
    private static ItemsControl Headers(UiHarness ui) => ui.One<ItemsControl>(i => i.Name == "AlignedRollHeaders");
    private static ItemsControl StackedSets(UiHarness ui) => ui.One<ItemsControl>(i => i.Name == "ResultSetsList");

    private static Border AlignedRowBorder(UiHarness ui, int index)
    {
        // The row's own Border comes first in a pre-order walk; deeper Borders belong to the nested Cells
        // ItemsControl's default template, not to anything this test wrote.
        var container = (ContentPresenter)AlignedRows(ui).ItemContainerGenerator.ContainerFromIndex(index);
        return ViewTests.FindAll<Border>(container).First();
    }

    [Fact]
    public void D8_difficulty_modifier_renders_as_one_visible_three_column_grid_in_roll_view()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(6, (db, c) => db.SaveTable(Fixtures.ParseAndBuild(ParallelOutputTests.Difficulty, c.Id)));

            ui.SelectTable("Difficulty Modifier");
            var roll = (RollViewModel)ui.Main.Current!;
            Assert.True(roll.IsAligned);
            Assert.True(AlignedRows(ui).IsVisible);
            Assert.True(Headers(ui).IsVisible);
            Assert.False(StackedSets(ui).IsVisible);

            Assert.Equal(7, AlignedRows(ui).Items.Count);
            Assert.Equal(7, roll.AlignedRows.Count);
            Assert.All(roll.AlignedRows, r => Assert.Equal(2, r.Cells.Count)); // Difficulty + Modifier = one roll, two outputs

            // The header names both columns, and every row's two outputs are both on screen, correctly paired.
            Assert.Contains(ui.Texts(), t => t.Text == "Difficulty" && t.FontWeight == FontWeights.SemiBold);
            Assert.Contains(ui.Texts(), t => t.Text == "Modifier" && t.FontWeight == FontWeights.SemiBold);
            Assert.Equal(["Child's play", "Effortless", "Easy", "Normal", "Demanding", "Hard", "Impossible"],
                roll.AlignedRows.Select(r => r.Cells[0].Text).ToArray());
            Assert.Equal(["+30", "+20", "+10", "+0", "-10", "-20", "-30"], roll.AlignedRows.Select(r => r.Cells[1].Text).ToArray());

            // The roll result at the top is untouched by this rendering change: still one number, both outputs beside it.
            ui.Click("Roll");
            Assert.Equal(["Rolled 6"], ui.Texts().Where(t => t.Text.StartsWith("Rolled")).Select(t => t.Text).ToArray());
            Assert.Equal([("Difficulty", "Demanding"), ("Modifier", "-10")], roll.Results.Select(r => (r.Heading, r.Text)).ToArray());
        });
    }

    [Fact]
    public void One_roll_plus_three_outputs_renders_as_one_visible_four_column_grid()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) => db.SaveTable(Fixtures.ParseAndBuild(ParallelOutputTests.DifficultyModifierSave, c.Id)));

            ui.SelectTable("Difficulty Modifier Save");
            var roll = (RollViewModel)ui.Main.Current!;
            Assert.True(roll.IsAligned);
            Assert.True(AlignedRows(ui).IsVisible);
            Assert.False(StackedSets(ui).IsVisible);

            Assert.Equal(7, AlignedRows(ui).Items.Count);
            Assert.All(roll.AlignedRows, r => Assert.Equal(3, r.Cells.Count)); // Difficulty + Modifier + Save = three outputs

            Assert.Contains(ui.Texts(), t => t.Text == "Difficulty" && t.FontWeight == FontWeights.SemiBold);
            Assert.Contains(ui.Texts(), t => t.Text == "Modifier" && t.FontWeight == FontWeights.SemiBold);
            Assert.Contains(ui.Texts(), t => t.Text == "Save" && t.FontWeight == FontWeights.SemiBold);
            Assert.Equal(["1d4", "1d4", "1d6", "2d4", "2d6", "2d6", "3d6"], roll.AlignedRows.Select(r => r.Cells[2].Text).ToArray());
        });
    }

    [Fact]
    public void Single_result_set_table_still_uses_the_ordinary_stacked_view()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(9, (db, c) => db.SaveTable(Fixtures.ScavengedItems(c.Id)));

            ui.SelectTable("Scavenged Items");
            var roll = (RollViewModel)ui.Main.Current!;
            Assert.False(roll.IsAligned);
            Assert.Empty(roll.AlignedRows);
            Assert.True(StackedSets(ui).IsVisible);
            Assert.False(AlignedRows(ui).IsVisible);
            Assert.Equal(6, roll.ResultSets[0].Entries.Count);

            // Rolling and highlighting still work exactly as before.
            ui.Click("Roll");
            Assert.True(roll.ResultSets[0].Entries[2].IsMatched); // roll 9 covers the D4 rations row (9-12)
            Assert.False(roll.ResultSets[0].Entries[0].IsMatched);
        });
    }

    [Fact]
    public void Unrelated_result_sets_with_different_ranges_still_render_separately()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(68, (db, c) => db.SaveTable(Fixtures.RoomFeatures(c.Id)));

            ui.SelectTable("Room Features");
            var roll = (RollViewModel)ui.Main.Current!;
            Assert.False(roll.IsAligned); // Ambient/Noise/General Feature are independent, not parallel outputs
            Assert.Empty(roll.AlignedRows);
            Assert.True(StackedSets(ui).IsVisible);
            Assert.False(AlignedRows(ui).IsVisible);
            Assert.Equal(3, StackedSets(ui).Items.Count);

            ui.Click("Roll");
            var outputs = ui.Texts().Where(t => t.FontSize == 26).Select(t => t.Text).ToArray();
            Assert.Equal(["Smell of burning flesh", "Hissing", "Grated floors reveal dozens of people below"], outputs);
        });
    }

    [Fact]
    public void Rolled_row_highlight_is_correct_across_the_whole_aligned_grid()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(6, (db, c) => db.SaveTable(Fixtures.ParseAndBuild(ParallelOutputTests.Difficulty, c.Id)));

            ui.SelectTable("Difficulty Modifier");
            var roll = (RollViewModel)ui.Main.Current!;

            // Before rolling, no row is highlighted anywhere in the grid.
            Assert.All(roll.AlignedRows, r => Assert.False(r.IsMatched));

            ui.Click("Roll");

            // Exactly the "6" row (index 4: 1,2,3,4-5,6,7,8) is matched, spanning both columns of the grid.
            var matched = roll.AlignedRows.Where(r => r.IsMatched).ToList();
            var matchedRow = Assert.Single(matched);
            Assert.Equal("6", matchedRow.RangeLabel);
            Assert.Equal(4, roll.AlignedRows.ToList().IndexOf(matchedRow));

            var matchBrush = Color.FromRgb(0xDF, 0xF1, 0xE1);
            Assert.Equal(matchBrush, ((SolidColorBrush)AlignedRowBorder(ui, 4).Background).Color);
            for (var i = 0; i < roll.AlignedRows.Count; i++)
            {
                if (i == 4) continue;
                Assert.Null(AlignedRowBorder(ui, i).Background);
            }
        });
    }
}
