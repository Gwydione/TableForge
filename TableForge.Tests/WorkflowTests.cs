using TableForge.Data;
using TableForge.Dice;
using TableForge.Domain;
using TableForge.Import;
using TableForge.ViewModels;

namespace TableForge.Tests;

internal sealed class FixedDice(int value) : IDiceProvider
{
    public int Value { get; set; } = value;

    /// <summary>How many times anything asked for a roll.</summary>
    public int Calls { get; private set; }

    public int Roll(DiceExpression dice)
    {
        Calls++;
        return Value;
    }
}

/// <summary>The Milestone 1 path — paste, review, correct, save, reload, roll — through the view models and a real database.</summary>
public class WorkflowTests
{
    private static (MainViewModel Main, ReviewViewModel Review) InterpretFixture(AppDatabase db, FixedDice dice)
    {
        var main = new MainViewModel(db, dice);
        main.NewCollectionName = "Solo Play";
        main.CreateCollectionCommand.Execute(null);
        main.PasteTableCommand.Execute(null);

        var paste = Assert.IsType<PasteViewModel>(main.Current);
        paste.SourceText = Fixtures.RandomStartingGear;
        paste.InterpretCommand.Execute(null);

        return (main, Assert.IsType<ReviewViewModel>(main.Current));
    }

    [Fact]
    public void Review_shows_interpretation_with_the_continuation_flagged_on_its_row()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var (_, review) = InterpretFixture(db, new FixedDice(1));

        Assert.Equal("Random Starting Gear", review.TableName);
        Assert.Equal("d10", review.DiceText);
        Assert.Equal(Fixtures.RandomStartingGear, review.SourceText);
        Assert.Equal(8, review.Rows.Count);
        Assert.Equal("9-10", review.Rows[7].RangeText);
        Assert.Equal("D20 Construction Supplies", review.Rows[7].Text);

        Assert.True(review.Rows[7].HasWarning);
        Assert.Contains("Supplies", review.Rows[7].Notes);
        Assert.All(review.Rows.Take(7), r => Assert.False(r.HasNotes));

        Assert.True(review.CanSave);
        Assert.Empty(review.Blockers);
        Assert.Empty(review.ValidationNotes);
    }

    [Fact]
    public void Editing_updates_validation_live_and_bad_input_blocks_saving()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var (_, review) = InterpretFixture(db, new FixedDice(1));

        // A gap is a fact, not a blocker.
        review.Rows[7].RangeText = "9";
        Assert.Contains("No row covers 10.", review.ValidationNotes);
        Assert.True(review.CanSave);

        // An overlap names both rows and marks them.
        review.Rows[7].RangeText = "8-10";
        Assert.Contains("Rows 7 and 8 both cover 8.", review.ValidationNotes);
        Assert.True(review.Rows[6].HasWarning);
        Assert.True(review.Rows[7].HasWarning);

        // A range that cannot be understood is an error on its row and blocks saving.
        review.Rows[7].RangeText = "nine";
        Assert.True(review.Rows[7].HasError);
        Assert.False(review.CanSave);
        Assert.NotEmpty(review.Blockers);

        // Bad dice and empty name also block.
        review.Rows[7].RangeText = "9-10";
        Assert.True(review.CanSave);
        review.DiceText = "d6+1";
        Assert.False(review.CanSave);
        review.DiceText = "2d6";
        Assert.True(review.CanSave); // valid dice; the mismatch with the rows is only a validation note
        review.DiceText = "d10";
        review.TableName = "  ";
        Assert.False(review.CanSave);
    }

    [Fact]
    public void Two_d6_review_validates_against_2_to_12()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var (_, review) = InterpretFixture(db, new FixedDice(1));

        review.DiceText = "2d6";

        // Rows cover 1-10: 1 is below the legal minimum of 2; 11-12 are uncovered.
        Assert.Contains(review.ValidationNotes, n => n.Contains("below the lowest roll (2)"));
        Assert.Contains("No row covers 11–12.", review.ValidationNotes);
    }

    [Fact]
    public void Paste_correct_save_roll_end_to_end()
    {
        using var temp = new TempDatabase();
        var dice = new FixedDice(9);
        long savedId;

        using (var db = temp.Open())
        {
            var (main, review) = InterpretFixture(db, dice);

            // Correct something, then save.
            review.TableName = "Starter Gear";
            review.Rows[0].Text = "Backpack (canvas)";
            review.SaveCommand.Execute(null);

            var roll = Assert.IsType<RollViewModel>(main.Current);
            Assert.Equal("Starter Gear", roll.Title);
            Assert.Equal("d10 · legal rolls 1–10", roll.DiceInfo);
            var saved = Assert.Single(main.Tables);
            Assert.Equal("Starter Gear", saved.Name);
            savedId = saved.Id;

            // Roll (fixed at 9).
            roll.RollCommand.Execute(null);
            Assert.Equal("Rolled 9", roll.RollDisplay);
            Assert.Equal("D20 Construction Supplies", Assert.Single(roll.Results).Text);
            Assert.True(roll.ResultSets[0].Entries[7].IsMatched);
            Assert.False(roll.ResultSets[0].Entries[0].IsMatched);

            // Manual legal values resolve, including boundaries.
            roll.ManualRollText = "1";
            roll.ResolveManualCommand.Execute(null);
            Assert.Equal("Rolled 1", roll.RollDisplay);
            Assert.Equal("Backpack (canvas)", Assert.Single(roll.Results).Text);

            roll.ManualRollText = "10";
            roll.ResolveManualCommand.Execute(null);
            Assert.Equal("D20 Construction Supplies", Assert.Single(roll.Results).Text);

            // Illegal values are refused without disturbing the last result.
            foreach (var bad in new[] { "0", "11", "-1", "abc", "" })
            {
                roll.ManualRollText = bad;
                roll.ResolveManualCommand.Execute(null);
                Assert.Equal("Enter a whole number from 1 to 10.", roll.Message);
                Assert.Equal("Rolled 10", roll.RollDisplay);
            }
            roll.ManualRollText = "4";
            roll.ResolveManualCommand.Execute(null);
            Assert.Equal("", roll.Message);
            Assert.Equal("1x Torch (UD6)", Assert.Single(roll.Results).Text);
        }

        // A fresh session opens the saved table from disk and rolls it.
        using var db2 = temp.Open();
        var main2 = new MainViewModel(db2, new FixedDice(3));
        Assert.Equal("Solo Play", main2.SelectedCollection!.Name);
        main2.SelectedTable = Assert.Single(main2.Tables);
        Assert.Equal(savedId, main2.SelectedTable.Id);
        main2.OpenTableCommand.Execute(null);

        var reopened = Assert.IsType<RollViewModel>(main2.Current);
        reopened.RollCommand.Execute(null);
        Assert.Equal("Knife", Assert.Single(reopened.Results).Text);
    }

    [Fact]
    public void Saved_table_with_a_gap_reports_no_match_for_an_uncovered_roll()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var (main, review) = InterpretFixture(db, new FixedDice(1));

        review.Rows[7].RangeText = "9"; // leaves 10 uncovered
        review.SaveCommand.Execute(null);

        var roll = Assert.IsType<RollViewModel>(main.Current);
        roll.ManualRollText = "10";
        roll.ResolveManualCommand.Execute(null);

        var line = Assert.Single(roll.Results);
        Assert.True(line.IsProblem);
        Assert.Equal("No entry covers 10.", line.Text);
    }

    [Fact]
    public void D100_manual_roll_accepts_00_and_shows_it_as_00()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var main = new MainViewModel(db, new FixedDice(1));
        main.NewCollectionName = "C";
        main.CreateCollectionCommand.Execute(null);
        main.PasteTableCommand.Execute(null);
        ((PasteViewModel)main.Current!).SourceText = "d100 Treasure\n01-50 Copper\n51-95 Silver\n96-00 Gold";
        ((PasteViewModel)main.Current!).InterpretCommand.Execute(null);
        ((ReviewViewModel)main.Current!).SaveCommand.Execute(null);

        var roll = Assert.IsType<RollViewModel>(main.Current);
        roll.ManualRollText = "00";
        roll.ResolveManualCommand.Execute(null);

        Assert.Equal("Rolled 00 (numeric 100)", roll.RollDisplay);
        var line = Assert.Single(roll.Results);
        Assert.Equal(("96–00", "Gold"), (line.Range, line.Text));
        Assert.Equal("d100 · legal rolls 1–00", roll.DiceInfo);
    }

    [Fact]
    public void Cancel_returns_to_the_empty_state_and_saves_nothing()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var (main, review) = InterpretFixture(db, new FixedDice(1));

        review.CancelCommand.Execute(null);

        Assert.Null(main.Current);
        Assert.Empty(main.Tables);
        Assert.Empty(db.GetTableSummaries(main.SelectedCollection!.Id));
    }

    [Fact]
    public void Interpret_is_disabled_for_blank_text_and_paste_needs_a_collection()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var main = new MainViewModel(db, new FixedDice(1));

        Assert.False(main.PasteTableCommand.CanExecute(null));
        Assert.False(main.CreateCollectionCommand.CanExecute(null));

        main.NewCollectionName = "X";
        main.CreateCollectionCommand.Execute(null);
        Assert.True(main.PasteTableCommand.CanExecute(null));
        main.PasteTableCommand.Execute(null);
        var paste = (PasteViewModel)main.Current!;
        Assert.False(paste.InterpretCommand.CanExecute(null));
        paste.SourceText = "  \n ";
        Assert.False(paste.InterpretCommand.CanExecute(null));
        paste.SourceText = "d6 T\n1-6 x";
        Assert.True(paste.InterpretCommand.CanExecute(null));
    }
}
