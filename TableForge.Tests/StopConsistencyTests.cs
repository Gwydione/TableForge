using TableForge.Domain;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>
/// Stop means no further result on every table, not only on tables with Unique rows: dice that settle just as Stop is pressed
/// (the provider answers although the roll was cancelled) are never shown or recorded, a batch or auto-follow chain never
/// continues from them, and the results already shown stay. <see cref="ScriptedDice"/> answers at once even after Stop.
/// </summary>
public class StopConsistencyTests
{
    private static RollableTable Plain(long id, string name, RollableTable? linkTo = null) => new()
    {
        Id = id,
        Name = name,
        Dice = DiceExpression.Parse("d6"),
        ResultSets = [new ResultSet { Entries = [new TableEntry { Min = 1, Max = 6, Text = $"{name} row", LinkedTableId = linkTo?.Id }] }],
    };

    private static (RollViewModel Session, List<RollSnapshot> History, ScriptedDice Dice) Open(RollableTable table, int stopOnThrow,
        Func<long, RollableTable?>? load = null, bool autoFollow = false)
    {
        var history = new List<RollSnapshot>();
        var dice = new ScriptedDice(3);
        var session = new RollViewModel(table, dice, load, rolled: history.Add, autoFollow: new AutoFollowSetting(autoFollow));
        dice.OnThrow = n => { if (n == stopOnThrow) session.CancelRoll(); };
        return (session, history, dice);
    }

    [Fact]
    public void A_single_roll_that_settles_as_Stop_is_pressed_shows_nothing_and_keeps_the_modifier()
    {
        var (session, history, _) = Open(Plain(1, "Plain"), stopOnThrow: 1);
        session.ModifierText = "2";
        session.RollCommand.Execute(null);

        Assert.Empty(session.Results);
        Assert.Empty(history);
        Assert.Equal("The roll was cancelled.", session.Message);
        Assert.Equal("2", session.ModifierText);
        Assert.False(session.IsRolling);
    }

    [Fact]
    public void A_batch_keeps_the_results_shown_before_Stop_and_throws_nothing_more()
    {
        var (session, history, dice) = Open(Plain(1, "Plain"), stopOnThrow: 2);
        session.RollCount = 4;
        session.RollCommand.Execute(null);

        Assert.Equal(2, dice.Requested.Count);
        Assert.Single(history);
        Assert.Single(session.Current.Outcomes);
        Assert.Equal("The roll was cancelled. Stopped at roll 2 of 4: rolls 1–1 are shown; the rest were not rolled.", session.Message);
    }

    [Fact]
    public void An_auto_followed_roll_that_settles_as_Stop_is_pressed_is_not_shown_and_the_chain_ends()
    {
        var c = Plain(3, "C");
        var b = Plain(2, "B", linkTo: c);
        var a = Plain(1, "A", linkTo: b);
        var tables = new[] { a, b, c }.ToDictionary(t => t.Id);
        var (session, history, dice) = Open(a, stopOnThrow: 2, tables.GetValueOrDefault, autoFollow: true);
        session.RollCommand.Execute(null);

        Assert.Equal(2, dice.Requested.Count);
        Assert.Equal(["A", "B"], session.Steps.Select(s => s.Title).ToArray());
        Assert.Empty(session.Current.Outcomes);
        Assert.Single(history);
        Assert.Equal("Auto-follow stopped. The roll was cancelled.", session.Message);
    }

    [Fact]
    public void An_inline_roll_that_settles_as_Stop_is_pressed_keeps_its_previous_value()
    {
        var dice = new ScriptedDice(1, 7, 14);
        var session = new RollViewModel(InlineFixtures.TableWithText("D20 Construction Supplies"), dice);
        dice.OnThrow = n => { if (n == 3) session.CancelRoll(); };
        session.RollCommand.Execute(null);
        var inline = Assert.Single(Assert.Single(session.Results).InlineActions);
        inline.RollCommand!.Execute(null);                                           // 7, as ever
        inline.RollCommand!.Execute(null);                                           // 14 settles as Stop is pressed

        Assert.Equal(7, inline.LatestValue);
        Assert.Equal("The roll was cancelled.", session.Message);
    }

    [Fact]
    public void A_roll_that_is_not_stopped_is_unchanged()
    {
        var (session, history, dice) = Open(Plain(1, "Plain"), stopOnThrow: 0);
        session.RollCount = 3;
        session.RollCommand.Execute(null);

        Assert.Equal(3, dice.Requested.Count);
        Assert.Equal(3, history.Count);
        Assert.Equal("", session.Message);
    }
}
