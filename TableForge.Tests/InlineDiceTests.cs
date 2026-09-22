using TableForge.Data;
using TableForge.Dice;
using TableForge.Domain;
using TableForge.ViewModels;

namespace TableForge.Tests;

internal static class InlineFixtures
{
    /// <summary>One entry spanning the whole d8 range, with the given text and (optionally) a link.</summary>
    public static RollableTable TableWithText(string text, long collectionId = 1, long? linkedTableId = null) => new()
    {
        CollectionId = collectionId,
        Name = "Test",
        Dice = DiceExpression.Parse("d8"),
        ResultSets = [new ResultSet { Entries = [new TableEntry { Min = 1, Max = 8, Text = text, LinkedTableId = linkedTableId }] }],
    };

    /// <summary>Two aligned result sets (same single row, same range), each with its own dice-looking text.</summary>
    public static RollableTable AlignedTableWithDiceText() => new()
    {
        Name = "Aligned",
        Dice = DiceExpression.Parse("d8"),
        ResultSets =
        [
            new ResultSet { Name = "Coins", Entries = [new TableEntry { Min = 1, Max = 8, Text = "Gain 1d6 coins" }] },
            new ResultSet { Name = "Gems", Entries = [new TableEntry { Min = 1, Max = 8, Text = "and 1d4 gems" }] },
        ],
    };
}

/// <summary>
/// Pure detection: which dice-looking substrings TableForge already knows how to roll, found with strict token boundaries,
/// using the table's own <see cref="DiceExpression.TryParse"/> — never a second, more permissive grammar.
/// </summary>
public class InlineDiceDetectorTests
{
    private static string[] Names(IEnumerable<DiceExpression> found) => found.Select(d => d.ToString()).ToArray();

    [Fact]
    public void A_bare_die_is_detected_anywhere_in_the_text()
    {
        Assert.Equal(["d20"], Names(InlineDiceDetector.Detect("D20 Construction Supplies")));
        Assert.Equal(["d20"], Names(InlineDiceDetector.Detect("Use d20 if necessary")));
        Assert.Equal(["d20"], Names(InlineDiceDetector.Detect("You find d20 Construction Supplies")));
    }

    [Fact]
    public void A_counted_die_is_detected()
    {
        Assert.Equal(["d6"], Names(InlineDiceDetector.Detect("Obtain 1d6 trinkets")));
    }

    [Fact]
    public void A_die_embedded_in_a_larger_word_is_not_detected()
    {
        Assert.Empty(InlineDiceDetector.Detect("Torch (UD6)"));
        Assert.Empty(InlineDiceDetector.Detect("Torch (ud6)"));
    }

    [Fact]
    public void A_fixed_modifier_is_kept_as_part_of_the_one_action()
    {
        Assert.Equal(["2d6+1"], Names(InlineDiceDetector.Detect("Take 2d6+1 damage")));
        Assert.Equal(["d20-2"], Names(InlineDiceDetector.Detect("Take d20-2 damage")));
    }

    [Fact]
    public void The_d66_convention_is_detected_as_d66_not_as_a_66_sided_die()
    {
        var found = Assert.Single(InlineDiceDetector.Detect("Roll d66 for an encounter"));
        Assert.True(found.IsD66);
        Assert.Equal("d66", found.ToString());
    }

    [Fact]
    public void A_d66_with_a_modifier_is_not_supported_and_produces_nothing()
    {
        Assert.Empty(InlineDiceDetector.Detect("Roll d66+1 for an encounter"));
    }

    [Fact]
    public void Multiple_distinct_expressions_are_all_found_in_order_of_first_appearance()
    {
        Assert.Equal(["d6", "d4"], Names(InlineDiceDetector.Detect("Gain 1d6 coins and 1d4 gems")));
    }

    [Fact]
    public void The_same_expression_repeated_is_reported_only_once()
    {
        Assert.Equal(["d6"], Names(InlineDiceDetector.Detect("d6 food and d6 water")));
    }

    [Fact]
    public void Keep_drop_notation_produces_no_action_the_trailing_letters_break_the_token_boundary()
    {
        Assert.Empty(InlineDiceDetector.Detect("4d6kh3 damage"));
    }

    [Fact]
    public void Unsupported_dice_looking_text_alone_produces_no_action()
    {
        Assert.Empty(InlineDiceDetector.Detect("UD6"));
        Assert.Empty(InlineDiceDetector.Detect("no dice here at all"));
        Assert.Empty(InlineDiceDetector.Detect(""));
    }

    [Fact]
    public void Case_does_not_matter_and_the_canonical_lowercase_form_is_used_for_display()
    {
        var found = Assert.Single(InlineDiceDetector.Detect("D20 Construction Supplies"));
        Assert.Equal("d20", found.ToString());
    }
}

/// <summary>The roll screen: buttons appear beside a matched result, roll through the table's own provider, and never touch the source text.</summary>
public class InlineDiceSessionTests
{
    private static ResultLineViewModel OnlyLine(RollViewModel s) => Assert.Single(s.Results);

    [Fact]
    public void A_matched_result_offers_one_button_per_detected_expression()
    {
        var session = new RollViewModel(InlineFixtures.TableWithText("D20 Construction Supplies"), new FixedDice(1));

        session.RollCommand.Execute(null);

        var line = OnlyLine(session);
        var action = Assert.Single(line.InlineActions);
        Assert.Equal("d20", action.DisplayExpression);
        Assert.Equal("Roll d20", action.RollLabel);
        Assert.True(line.ShowInlineActions);
        Assert.False(action.HasResults);
    }

    [Fact]
    public void Pressing_the_button_rolls_through_the_tables_own_provider_and_never_changes_the_text()
    {
        var dice = new FixedDice(1);
        var session = new RollViewModel(InlineFixtures.TableWithText("D20 Construction Supplies"), dice);
        session.RollCommand.Execute(null);
        var line = OnlyLine(session);
        var action = Assert.Single(line.InlineActions);

        dice.Value = 14;
        action.RollCommand!.Execute(null);

        Assert.Equal(["14"], action.Results);
        Assert.Equal("D20 Construction Supplies", line.Text); // the source text is never rewritten
        Assert.Equal(2, dice.Calls); // the table's own roll, plus this one
    }

    [Fact]
    public void Repeated_presses_append_results_and_the_label_becomes_Roll_Again()
    {
        var dice = new FixedDice(1);
        var session = new RollViewModel(InlineFixtures.TableWithText("D20 Construction Supplies"), dice);
        session.RollCommand.Execute(null);
        var action = Assert.Single(OnlyLine(session).InlineActions);

        dice.Value = 14; action.RollCommand!.Execute(null);
        Assert.Equal("Roll Again", action.RollLabel);
        dice.Value = 7; action.RollCommand.Execute(null);
        dice.Value = 19; action.RollCommand.Execute(null);

        Assert.Equal(["14", "7", "19"], action.Results);
        Assert.Equal("14, 7, 19", action.ResultsText);
    }

    [Fact]
    public void Multiple_distinct_expressions_in_one_result_each_get_their_own_independent_action()
    {
        var dice = new FixedDice(1);
        var session = new RollViewModel(InlineFixtures.TableWithText("Gain 1d6 coins and 1d4 gems"), dice);
        session.RollCommand.Execute(null);
        var actions = OnlyLine(session).InlineActions;
        Assert.Equal(["d6", "d4"], actions.Select(a => a.DisplayExpression).ToArray());

        dice.Value = 3;
        actions[0].RollCommand!.Execute(null);

        Assert.Equal(["3"], actions[0].Results);
        Assert.Empty(actions[1].Results); // rolling one never rolls the other
    }

    [Fact]
    public void Unsupported_notation_offers_no_button_at_all()
    {
        var session = new RollViewModel(InlineFixtures.TableWithText("Torch (UD6)"), new FixedDice(3));

        session.RollCommand.Execute(null);

        Assert.Empty(OnlyLine(session).InlineActions);
        Assert.False(OnlyLine(session).ShowInlineActions);
    }

    [Fact]
    public void Each_result_set_in_an_aligned_multi_column_roll_gets_its_own_inline_actions_and_rendering_is_unaffected()
    {
        var session = new RollViewModel(InlineFixtures.AlignedTableWithDiceText(), new FixedDice(3));

        session.RollCommand.Execute(null);

        Assert.True(session.IsAligned);
        Assert.Equal(["d6"], session.Results[0].InlineActions.Select(a => a.DisplayExpression).ToArray());
        Assert.Equal(["d4"], session.Results[1].InlineActions.Select(a => a.DisplayExpression).ToArray());
        var row = Assert.Single(session.AlignedRows); // the browsable aligned grid still shows every row untouched
        Assert.True(row.IsMatched);
    }

    // ---- lifecycle ---------------------------------------------------------------------------

    [Fact]
    public void Rolling_the_parent_table_again_clears_the_previous_rolls_inline_state()
    {
        var dice = new FixedDice(1);
        var session = new RollViewModel(InlineFixtures.TableWithText("D20 Construction Supplies"), dice);
        session.RollCommand.Execute(null);
        var first = OnlyLine(session);
        var firstAction = Assert.Single(first.InlineActions);
        dice.Value = 9;
        firstAction.RollCommand!.Execute(null);
        Assert.True(first.ShowInlineActions);

        dice.Value = 2;
        session.RollCommand.Execute(null);

        Assert.False(first.ShowInlineActions); // the superseded roll's action is no longer offered
        var second = OnlyLine(session);
        Assert.NotSame(first, second);
        var secondAction = Assert.Single(second.InlineActions);
        Assert.False(secondAction.HasResults); // brand-new action: nothing carried over
        Assert.True(second.ShowInlineActions);
    }

    [Fact]
    public void Following_a_link_deactivates_the_parents_inline_actions()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var target = db.SaveTable(Fixtures.ScavengedItems(collection.Id));
        var table = db.SaveTable(InlineFixtures.TableWithText("D20 Construction Supplies", collection.Id, target.Id));
        var session = new RollViewModel(db.LoadTable(table.Id)!, new FixedDice(3), db.LoadTable);
        session.RollCommand.Execute(null);
        var line = OnlyLine(session);
        Assert.True(line.ShowInlineActions);

        line.FollowCommand!.Execute(null);

        Assert.False(line.ShowInlineActions);
        Assert.Empty(session.Current.Outcomes); // the child was not rolled
    }

    [Fact]
    public void An_entry_can_offer_both_an_inline_dice_action_and_a_linked_table_follow_action_independently()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var target = db.SaveTable(Fixtures.ScavengedItems(collection.Id));
        var table = db.SaveTable(InlineFixtures.TableWithText("D20 Construction Supplies", collection.Id, target.Id));
        var session = new RollViewModel(db.LoadTable(table.Id)!, new FixedDice(3), db.LoadTable);

        session.RollCommand.Execute(null);
        var line = OnlyLine(session);

        Assert.True(line.ShowInlineActions);
        Assert.True(line.ShowFollow);
        var action = Assert.Single(line.InlineActions);

        action.RollCommand!.Execute(null); // rolling the inline action does not follow the link
        Assert.Single(session.Steps);
        Assert.True(line.ShowFollow);

        line.FollowCommand!.Execute(null); // and following the link does not disturb the inline action's own results
        Assert.Equal(2, session.Steps.Count);
        Assert.NotEmpty(action.Results);
    }

    [Fact]
    public void Inline_rolls_are_never_written_to_the_stored_roll_history_only_the_parent_roll_is()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var saved = db.SaveTable(InlineFixtures.TableWithText("D20 Construction Supplies", collection.Id));
        var dice = new FixedDice(3);
        var main = new MainViewModel(db, dice);
        main.OpenRecentTableCommand.Execute(new TableSummary(saved.Id, saved.Name, saved.Dice));
        var session = (RollViewModel)main.Current!;

        session.RollCommand.Execute(null);
        Assert.Single(db.GetRollHistory());

        var action = Assert.Single(session.Results.Single().InlineActions);
        dice.Value = 14;
        action.RollCommand!.Execute(null);
        action.RollCommand.Execute(null);

        Assert.Equal(["14", "14"], action.Results);
        Assert.Single(db.GetRollHistory()); // still only the one parent-table roll
        Assert.Single(main.RecentRolls);
    }
}

/// <summary>The dddice path: an inline roll shows the visible dice and only reveals its result once they settle, exactly like a normal roll.</summary>
public class InlineDiceDddiceTests
{
    [Fact]
    public async Task An_inline_roll_hides_its_result_until_the_dice_settle_and_is_never_recorded_to_history()
    {
        var roller = new FakeDddiceRoller();
        var provider = new DddiceDiceProvider(roller);
        var history = new List<RollSnapshot>();
        var session = new RollViewModel(InlineFixtures.TableWithText("D20 Construction Supplies"), provider, rolled: history.Add);

        session.RollCommand.Execute(null);
        roller.Settle(("d8", 5)); // the parent table itself is a d8
        await session.RollTask;
        Assert.Single(history);

        var action = Assert.Single(session.Results.Single().InlineActions);
        action.RollCommand!.Execute(null);

        Assert.True(action.IsRolling);
        Assert.False(action.HasResults);
        Assert.Single(history); // the inline roll has not settled: nothing new recorded

        roller.Settle(("d20", 14));
        await session.RollTask;

        Assert.False(action.IsRolling);
        Assert.Equal(["14"], action.Results);
        Assert.Single(history); // and never gets recorded once it does settle, either
    }

    [Fact]
    public async Task Pressing_an_inline_button_while_any_roll_is_in_flight_is_ignored_not_raced()
    {
        var roller = new FakeDddiceRoller();
        var provider = new DddiceDiceProvider(roller);
        var session = new RollViewModel(InlineFixtures.AlignedTableWithDiceText(), provider);
        session.RollCommand.Execute(null);
        roller.Settle(("d8", 5));
        await session.RollTask;

        var a = session.Results[0].InlineActions[0];
        var b = session.Results[1].InlineActions[0];

        a.RollCommand!.Execute(null);
        Assert.Equal(["d8", "d6"], roller.Requests.Select(r => string.Join(",", r)).ToArray()); // the parent's roll, then a's

        b.RollCommand!.Execute(null); // ignored: a roll is already under way
        Assert.False(b.RollCommand.CanExecute(null));
        session.RollCommand.Execute(null); // the parent Roll button is likewise ignored while inline is in flight
        Assert.False(session.RollCommand.CanExecute(null));
        Assert.Equal(2, roller.Requests.Count); // no third request from either ignored press

        roller.Settle(("d6", 4));
        await session.RollTask;

        Assert.Equal(["4"], a.Results);
        Assert.Empty(b.Results);
        Assert.True(session.RollCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancelling_while_an_inline_roll_is_in_flight_abandons_it_cleanly()
    {
        var roller = new FakeDddiceRoller();
        var provider = new DddiceDiceProvider(roller);
        var session = new RollViewModel(InlineFixtures.TableWithText("D20 Construction Supplies"), provider);
        session.RollCommand.Execute(null);
        roller.Settle(("d8", 5));
        await session.RollTask;
        var action = Assert.Single(session.Results.Single().InlineActions);

        action.RollCommand!.Execute(null);
        session.CancelRoll();
        await session.RollTask;

        Assert.True(roller.LastRollToken.IsCancellationRequested);
        Assert.False(session.IsRolling);
        Assert.False(action.IsRolling);
        Assert.Empty(action.Results);
        Assert.Equal("The roll was cancelled.", session.Message);
        Assert.True(action.RollCommand.CanExecute(null));
    }

    [Fact]
    public async Task A_late_dddice_completion_after_cancellation_is_discarded()
    {
        var roller = new FakeDddiceRoller();
        var provider = new DddiceDiceProvider(roller);
        var session = new RollViewModel(InlineFixtures.TableWithText("D20 Construction Supplies"), provider);
        session.RollCommand.Execute(null);
        roller.Settle(("d8", 5));
        await session.RollTask;
        var action = Assert.Single(session.Results.Single().InlineActions);

        action.RollCommand!.Execute(null);
        session.CancelRoll();
        await session.RollTask;

        roller.Settle(("d20", 20)); // the animation finishes after TableForge stopped waiting for it

        Assert.Empty(action.Results);
        Assert.False(action.IsRolling);
    }
}

/// <summary>The real view: the button renders next to the matched text and works end to end.</summary>
[Collection("UI")]
public class InlineDiceViewTests
{
    [Fact]
    public void Roll_button_appears_beside_a_matched_result_and_rolling_it_changes_nothing_but_itself()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(3, (db, c) => db.SaveTable(InlineFixtures.TableWithText("D20 Construction Supplies", c.Id)));

            ui.SelectTable("Test");
            ui.Click("Roll");

            Assert.Contains(ui.Texts(), t => t.Text == "D20 Construction Supplies" && t.FontSize == 26);
            Assert.True(ui.HasVisibleButton("Roll d20"));

            ui.Click("Roll d20");

            Assert.Contains(ui.Texts(), t => t.Text == "D20 Construction Supplies"); // source text unchanged
            Assert.True(ui.HasVisibleButton("Roll Again"));
            Assert.False(ui.HasVisibleButton("Roll d20"));
        });
    }

    [Fact]
    public void Unsupported_notation_shows_no_button_in_the_real_view()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(3, (db, c) => db.SaveTable(InlineFixtures.TableWithText("Torch (UD6)", c.Id)));

            ui.SelectTable("Test");
            ui.Click("Roll");

            Assert.Contains(ui.Texts(), t => t.Text == "Torch (UD6)");
            Assert.DoesNotContain(ui.All<System.Windows.Controls.Button>(), b => b.IsVisible && (b.Content as string)?.StartsWith("Roll d") == true);
        });
    }
}
