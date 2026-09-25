using Microsoft.Data.Sqlite;
using TableForge.Data;
using TableForge.Dice;
using TableForge.Domain;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>Reading the Roll screen's Modifier field: one signed whole number, -1000 to +1000, blank meaning 0.</summary>
public class SituationalModifierParsingTests
{
    [Theory]
    [InlineData("3", 3)]
    [InlineData("+3", 3)]
    [InlineData("-2", -2)]
    [InlineData("0", 0)]
    [InlineData("+0", 0)]
    [InlineData("-0", 0)]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData(" +3 ", 3)]
    [InlineData("1000", 1000)]
    [InlineData("+1000", 1000)]
    [InlineData("-1000", -1000)]
    [InlineData("007", 7)]
    public void Accepts_one_signed_whole_number_within_the_limit(string text, int expected)
    {
        Assert.True(SituationalModifier.TryParse(text, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("abc")]
    [InlineData("2.5")]
    [InlineData("+1001")]
    [InlineData("-1001")]
    [InlineData("+2-1")]
    [InlineData("1d4")]
    [InlineData("STR")]
    [InlineData("advantage")]
    [InlineData("+ 3")]
    [InlineData("--3")]
    [InlineData("99999999999999999999")]
    [InlineData("٣")] // a digit from another script is not a TableForge number
    public void Refuses_anything_else_instead_of_reading_it_as_zero(string text)
    {
        Assert.False(SituationalModifier.TryParse(text, out _));
    }
}

/// <summary>
/// The situational modifier through <see cref="RollViewModel"/>: captured when Roll starts, added after the provider's roll,
/// used exactly as calculated (including outside the table's range), and used up only by a roll that actually happened.
/// </summary>
public class SituationalModifierRollTests
{
    private static readonly (int, int, string)[] D20Rows = [(1, 9, "Low"), (10, 13, "Mid"), (14, 20, "High")];
    private static readonly (int, int, string)[] D6Rows = [(1, 2, "One-two"), (3, 4, "Three-four"), (5, 6, "Five-six")];

    private static (RollViewModel Session, FixedDice Dice, List<RollSnapshot> Rolled) Open(string dice, int providerValue, params (int, int, string)[] rows)
    {
        var provider = new FixedDice(providerValue);
        var rolled = new List<RollSnapshot>();
        var session = new RollViewModel(Fixtures.Table(DiceExpression.Parse(dice), rows), provider, rolled: rolled.Add);
        return (session, provider, rolled);
    }

    private static string OnlyText(RollViewModel s) => Assert.Single(s.Results).Text;

    [Fact]
    public void A_positive_modifier_is_added_to_the_provider_roll_and_the_sum_is_looked_up()
    {
        var (session, dice, rolled) = Open("d20", 11, D20Rows);
        session.ModifierText = "+3";

        session.RollCommand.Execute(null);

        Assert.Equal(["d20"], dice.Requested);                    // the provider rolls the ordinary dice; it never sees the modifier
        Assert.Equal("Rolled 14", session.RollDisplay);
        Assert.Equal("11 +3 situational", session.RollBreakdown);
        Assert.Equal("High", OnlyText(session));                  // 14, not 11
        Assert.Equal("0", session.ModifierText);                  // used up
        var snap = Assert.Single(rolled);
        Assert.Equal((14, 3), (snap.RollValue, snap.SituationalModifier));
    }

    [Fact]
    public void A_negative_modifier_is_subtracted()
    {
        var (session, _, rolled) = Open("d20", 11, D20Rows);
        session.ModifierText = "-2";

        session.RollCommand.Execute(null);

        Assert.Equal("Rolled 9", session.RollDisplay);
        Assert.Equal("11 -2 situational", session.RollBreakdown);
        Assert.Equal("Low", OnlyText(session));
        Assert.Equal((9, -2), (rolled[0].RollValue, rolled[0].SituationalModifier));
    }

    [Fact]
    public void The_next_roll_after_a_modified_one_starts_from_zero()
    {
        var (session, _, rolled) = Open("d20", 11, D20Rows);
        session.ModifierText = "+3";
        session.RollCommand.Execute(null);

        session.RollCommand.Execute(null);

        Assert.Equal("Rolled 11", session.RollDisplay);
        Assert.Equal("", session.RollBreakdown);
        Assert.Equal("Mid", OnlyText(session));
        Assert.Equal(0, rolled[1].SituationalModifier);
    }

    [Fact]
    public void A_stored_fixed_modifier_and_a_situational_one_stay_separate()
    {
        var table = Fixtures.Table(DiceExpression.Parse("2d6+1"), (3, 7, "Poor"), (8, 13, "Good"));
        var session = new RollViewModel(table, new FixedDice(9)); // 9 is the table's own final roll: dice plus its +1
        session.ModifierText = "-2";

        session.RollCommand.Execute(null);

        Assert.Equal("Rolled 7 (2d6+1)", session.RollDisplay);
        Assert.Equal("9 -2 situational", session.RollBreakdown);
        Assert.Equal("Poor", OnlyText(session));
        Assert.Equal("2d6+1", table.Dice.ToString());                // the saved expression is not rewritten
        Assert.Equal("2d6+1 · legal rolls 3–13", session.DiceInfo);
    }

    [Fact]
    public void A_result_below_the_table_is_used_as_calculated_and_shows_no_match()
    {
        var (session, _, rolled) = Open("d6", 1, D6Rows);
        session.ModifierText = "-1";

        session.RollCommand.Execute(null);

        Assert.Equal("Rolled 0", session.RollDisplay);               // not clamped to 1, not rerolled
        Assert.Equal("1 -1 situational", session.RollBreakdown);
        var line = Assert.Single(session.Results);
        Assert.True(line.IsProblem);
        Assert.Equal("No entry covers 0.", line.Text);
        Assert.Equal("0", session.ModifierText);                     // a roll that happened uses the modifier up, match or not
        Assert.Equal((0, -1, "No entry covers 0."), (rolled[0].RollValue, rolled[0].SituationalModifier, rolled[0].ResultText));
        Assert.All(session.ResultSets[0].Entries, e => Assert.False(e.IsMatched)); // no row is highlighted
    }

    [Fact]
    public void A_result_above_the_table_is_used_as_calculated_and_shows_no_match()
    {
        var (session, _, _) = Open("d6", 6, D6Rows);
        session.ModifierText = "+1";

        session.RollCommand.Execute(null);

        Assert.Equal("Rolled 7", session.RollDisplay);               // not clamped to 6, not wrapped to 1
        Assert.Equal("6 +1 situational", session.RollBreakdown);
        Assert.Equal("No entry covers 7.", OnlyText(session));
        Assert.Equal("0", session.ModifierText);
    }

    [Theory]
    [InlineData("+1000", 1003)]
    [InlineData("-1000", -997)]
    public void The_largest_modifiers_still_do_plain_arithmetic_far_outside_the_table(string modifier, int expected)
    {
        var (session, _, rolled) = Open("d6", 3, D6Rows);
        session.ModifierText = modifier;

        session.RollCommand.Execute(null);

        Assert.Equal($"Rolled {expected}", session.RollDisplay);
        Assert.Equal($"No entry covers {expected}.", OnlyText(session));
        Assert.Equal(expected, rolled[0].RollValue);
    }

    [Fact]
    public void The_field_is_not_checked_against_the_table_range()
    {
        var (session, _, _) = Open("d6", 3, D6Rows);
        session.ModifierText = "-10";

        Assert.True(session.IsModifierValid);
        Assert.True(session.RollCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("+0")]
    [InlineData("-0")]
    public void A_zero_or_blank_modifier_leaves_the_roll_exactly_as_before(string modifier)
    {
        var (session, _, rolled) = Open("d20", 11, D20Rows);
        session.ModifierText = modifier;

        session.RollCommand.Execute(null);

        Assert.Equal("Rolled 11", session.RollDisplay);
        Assert.Equal("", session.RollBreakdown);
        Assert.False(session.Current.Outcomes[^1].HasBreakdown);
        Assert.Equal("Mid", OnlyText(session));
        Assert.Equal(0, rolled[0].SituationalModifier);
    }

    [Theory]
    [InlineData("+")]
    [InlineData("-")]
    [InlineData("abc")]
    [InlineData("2.5")]
    [InlineData("+1001")]
    [InlineData("-1001")]
    public void An_unreadable_modifier_stops_the_roll_before_it_starts(string modifier)
    {
        var (session, dice, rolled) = Open("d20", 11, D20Rows);
        session.ModifierText = modifier;

        Assert.False(session.IsModifierValid);
        Assert.True(session.HasModifierError);
        Assert.Equal(SituationalModifier.InvalidMessage, session.ModifierError);
        Assert.False(session.RollCommand.CanExecute(null));          // the Roll button is unavailable

        session.RollAsync();                                          // and even a direct call does nothing

        Assert.Equal(0, dice.Calls);
        Assert.Empty(rolled);
        Assert.False(session.Current.HasOutcomes);
        Assert.Equal(modifier, session.ModifierText);                 // never quietly turned into 0
        Assert.Equal(SituationalModifier.InvalidMessage, session.Message);
    }

    [Fact]
    public void Fixing_an_unreadable_modifier_makes_roll_available_again()
    {
        var (session, _, _) = Open("d20", 11, D20Rows);
        session.ModifierText = "abc";
        session.ModifierText = "+2";

        Assert.True(session.IsModifierValid);
        Assert.Equal("", session.ModifierError);
        Assert.True(session.RollCommand.CanExecute(null));
    }

    [Fact]
    public void The_modifier_applies_equally_to_every_aligned_result_set()
    {
        var table = new RollableTable
        {
            Name = "Weather",
            Dice = DiceExpression.Parse("d6"),
            ResultSets =
            [
                new ResultSet { Name = "Sky", Entries = [new() { Min = 1, Max = 4, Text = "Clear" }, new() { Min = 5, Max = 6, Text = "Storm" }] },
                new ResultSet { Name = "Wind", Entries = [new() { Min = 1, Max = 4, Text = "Calm" }, new() { Min = 5, Max = 6, Text = "Gale" }] },
            ],
        };
        var session = new RollViewModel(table, new FixedDice(3));
        session.ModifierText = "+2";

        session.RollCommand.Execute(null);

        Assert.True(session.IsAligned);
        Assert.Equal(["Storm", "Gale"], session.Results.Select(r => r.Text).ToArray()); // both looked up at 5
        Assert.True(session.AlignedRows[1].IsMatched);
        Assert.False(session.AlignedRows[0].IsMatched);
    }

    [Fact]
    public void A_modified_d100_roll_is_a_calculated_number_never_shown_as_00()
    {
        var (session, _, _) = Open("d100", 97, (1, 100, "Anything"));
        session.ModifierText = "+3";
        session.RollCommand.Execute(null);
        Assert.Equal("Rolled 100", session.RollDisplay);
        Assert.Equal("97 +3 situational", session.RollBreakdown);

        var (down, _, _) = Open("d100", 100, (1, 100, "Anything"));
        down.ModifierText = "-3";
        down.RollCommand.Execute(null);
        Assert.Equal("Rolled 97", down.RollDisplay);
        Assert.Equal("00 -3 situational", down.RollBreakdown);       // the die itself still reads 00
    }

    // ---- d66 ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_d66_table_offers_no_modifier_and_never_adds_one()
    {
        var (session, _, rolled) = Open("d66", 35, (11, 66, "Anything"));

        Assert.False(session.IsModifierAvailable);
        session.ModifierText = "+2";                                  // cannot happen through the hidden field; proves it is ignored anyway
        Assert.True(session.IsModifierValid);
        session.ModifierText = "abc";
        Assert.True(session.IsModifierValid);                         // nothing to validate: the field does not apply
        Assert.True(session.RollCommand.CanExecute(null));

        session.ModifierText = "+2";
        session.RollCommand.Execute(null);

        Assert.Equal("Rolled 35 (d66)", session.RollDisplay);         // never 37
        Assert.Equal("", session.RollBreakdown);
        Assert.Equal((35, 0), (rolled[0].RollValue, rolled[0].SituationalModifier));
    }

    // ---- manual entry and inline rolls ignore it ------------------------------------------------------------------

    [Fact]
    public void A_typed_roll_is_already_final_so_the_modifier_is_ignored_and_kept()
    {
        var (session, dice, rolled) = Open("d20", 11, D20Rows);
        session.ModifierText = "+3";
        session.ManualRollText = "14";

        session.ResolveManualCommand.Execute(null);

        Assert.Equal(0, dice.Calls);
        Assert.Equal("Rolled 14", session.RollDisplay);               // not 17
        Assert.Equal("", session.RollBreakdown);
        Assert.Equal("High", OnlyText(session));
        Assert.Equal("+3", session.ModifierText);                     // still waiting for the next real Roll
        Assert.Equal((14, 0), (rolled[0].RollValue, rolled[0].SituationalModifier));

        session.RollCommand.Execute(null);                            // ...which does use it
        Assert.Equal("Rolled 14", session.RollDisplay);
        Assert.Equal("11 +3 situational", session.RollBreakdown);
        Assert.Equal("0", session.ModifierText);
    }

    [Fact]
    public void An_inline_roll_ignores_the_modifier_and_leaves_it_pending()
    {
        var dice = new FixedDice(1);
        var session = new RollViewModel(Fixtures.Table(DiceExpression.Parse("d6"), (1, 6, "D20 Construction Supplies")), dice);
        session.RollCommand.Execute(null);
        var action = Assert.Single(Assert.Single(session.Results).InlineActions);

        session.ModifierText = "+3";
        dice.Value = 12;
        action.RollCommand!.Execute(null);

        Assert.Equal(12, action.LatestValue);                         // not 15
        Assert.Equal("12 Construction Supplies", session.Results.Single().ResolvedText);
        Assert.Equal("+3", session.ModifierText);
        Assert.Equal("Rolled 1", session.RollDisplay);                // the table's own roll is untouched
    }

    // ---- a visual provider: captured at the start, used only on success ------------------------------------------

    private static (RollViewModel Session, FakeDddiceRoller Roller, List<RollSnapshot> Rolled) OpenDddice()
    {
        var roller = new FakeDddiceRoller();
        var rolled = new List<RollSnapshot>();
        var session = new RollViewModel(Fixtures.Table(DiceExpression.Parse("d20"), D20Rows), new DddiceDiceProvider(roller), rolled: rolled.Add);
        return (session, roller, rolled);
    }

    [Fact]
    public async Task With_dddice_the_dice_show_their_real_value_and_the_modifier_is_added_only_once_they_settle()
    {
        var (session, roller, rolled) = OpenDddice();
        session.ModifierText = "+3";

        var roll = session.RollAsync();
        await Wait.Until(() => roller.IsRollPending, "the dice to be thrown");

        Assert.Equal(["d20"], roller.Requests.Single());              // dddice is asked for a plain d20
        Assert.False(session.IsModifierEditable);                     // locked while the dice are in the air
        Assert.Equal("", session.RollDisplay);                        // nothing shown before roll:finished
        session.ModifierText = "+9";                                  // even a change now would not reach this roll

        roller.Settle(("d20", 11));                                   // the die visibly lands on 11
        await roll;

        Assert.Equal("Rolled 14", session.RollDisplay);
        Assert.Equal("11 +3 situational", session.RollBreakdown);
        Assert.Equal("High", OnlyText(session));
        Assert.Equal("0", session.ModifierText);
        Assert.True(session.IsModifierEditable);
        Assert.Equal((14, 3), (rolled[0].RollValue, rolled[0].SituationalModifier));
    }

    [Fact]
    public async Task A_cancelled_dddice_roll_keeps_the_modifier_for_a_retry()
    {
        var (session, roller, rolled) = OpenDddice();
        session.ModifierText = "+3";

        var roll = session.RollAsync();
        await Wait.Until(() => roller.IsRollPending, "the dice to be thrown");
        session.CancelRoll();
        await roll;

        Assert.Equal("The roll was cancelled.", session.Message);
        Assert.Equal("+3", session.ModifierText);
        Assert.False(session.Current.HasOutcomes);
        Assert.Empty(rolled);
        Assert.True(session.IsModifierEditable);
    }

    [Fact]
    public async Task A_failed_dddice_roll_keeps_the_modifier_and_a_retry_uses_it()
    {
        var (session, roller, rolled) = OpenDddice();
        session.ModifierText = "-2";

        var roll = session.RollAsync();
        await Wait.Until(() => roller.IsRollPending, "the dice to be thrown");
        roller.FailRoll("The connection to dddice was lost during the roll.");
        await roll;

        Assert.Equal("The connection to dddice was lost during the roll.", session.Message);
        Assert.Equal("-2", session.ModifierText);
        Assert.Empty(rolled);

        var retry = session.RollAsync();
        await Wait.Until(() => roller.IsRollPending, "the retry");
        roller.Settle(("d20", 11));
        await retry;

        Assert.Equal("Rolled 9", session.RollDisplay);
        Assert.Equal("0", session.ModifierText);
    }

    // ---- navigation ----------------------------------------------------------------------------------------------

    [Fact]
    public void Following_a_link_starts_the_destination_at_zero()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var (_, temperature) = Fixtures.SeedTemperature(db, db.CreateCollection("C").Id);
        var session = new RollViewModel(db.LoadTable(temperature.Id)!, new FixedDice(19), db.LoadTable);
        session.RollCommand.Execute(null);

        session.ModifierText = "+3";                                  // typed for Temperature, not yet used
        Assert.Single(session.Results).FollowCommand!.Execute(null);

        Assert.Equal("Unusual Temperature", session.Current.Table.Name);
        Assert.Equal("0", session.ModifierText);
    }
}

/// <summary>The modifier in the shell: it never follows you to another table, and only a used one is ever stored.</summary>
public class SituationalModifierShellTests
{
    private sealed class World : IDisposable
    {
        public TempDatabase Temp { get; } = new();
        public AppDatabase Db { get; }
        public FixedDice Dice { get; } = new(11);
        public MainViewModel Main { get; private set; } = null!;

        public World()
        {
            Db = Temp.Open();
            var c = Db.CreateCollection("C");
            Db.SaveTable(Fixtures.Table(DiceExpression.Parse("d20"), (1, 9, "Low"), (10, 13, "Mid"), (14, 20, "High")).Also(t => { t.Name = "Omens"; t.CollectionId = c.Id; }));
            Db.SaveTable(Fixtures.Table(DiceExpression.Parse("d6"), (1, 6, "Anything")).Also(t => { t.Name = "Weather"; t.CollectionId = c.Id; }));
            Start();
        }

        public void Start() => Main = new MainViewModel(Db, Dice);

        public RollViewModel Open(string name)
        {
            Main.SelectedTable = Main.Tables.Single(t => t.Name == name);
            return Assert.IsType<RollViewModel>(Main.Current);
        }

        public void Dispose() => Temp.Dispose();
    }

    [Fact]
    public void Switching_to_another_table_starts_it_at_zero_and_coming_back_does_too()
    {
        using var w = new World();
        w.Open("Omens").ModifierText = "+3";

        Assert.Equal("0", w.Open("Weather").ModifierText);
        Assert.Equal("0", w.Open("Omens").ModifierText);
    }

    [Fact]
    public void Recent_rolls_record_the_final_value_and_the_modifier_used()
    {
        using var w = new World();
        var omens = w.Open("Omens");
        omens.RollCommand.Execute(null);                              // unmodified
        omens.ModifierText = "+3";
        omens.RollCommand.Execute(null);

        var history = w.Db.GetRollHistory();
        Assert.Equal((14, 3), (history[0].RollValue, history[0].SituationalModifier));
        Assert.Equal((11, 0), (history[1].RollValue, history[1].SituationalModifier));

        Assert.Equal("14 (+3)", w.Main.RecentRolls[0].RollDisplay);
        Assert.Equal("11", w.Main.RecentRolls[1].RollDisplay);        // unmodified rolls look exactly as before
        Assert.Contains("d20 → 14 (11 +3 situational)", w.Main.RecentRolls[0].FullText);
        Assert.DoesNotContain("situational", w.Main.RecentRolls[1].FullText);
    }

    [Fact]
    public void An_out_of_range_modified_roll_is_recorded_with_its_no_match_result()
    {
        using var w = new World();
        w.Dice.Value = 1;
        var weather = w.Open("Weather");
        weather.ModifierText = "-1";
        weather.RollCommand.Execute(null);

        var item = Assert.Single(w.Db.GetRollHistory());
        Assert.Equal((0, -1, "No entry covers 0."), (item.RollValue, item.SituationalModifier, item.ResultText));
        Assert.Equal("0 (-1)", w.Main.RecentRolls[0].RollDisplay);
    }

    [Fact]
    public void A_pending_modifier_is_never_saved_so_it_is_gone_after_a_restart()
    {
        using var w = new World();
        w.Open("Omens").ModifierText = "+3";                           // typed but never rolled

        w.Start();                                                     // the app opens again on the same database

        Assert.Equal("0", w.Open("Omens").ModifierText);
        Assert.Empty(w.Db.GetRollHistory());
    }
}

/// <summary>Schema v6: the modifier a recorded roll used, added to Recent Rolls history without disturbing existing rows.</summary>
public class SituationalModifierHistoryMigrationTests
{
    [Fact]
    public void Existing_history_rows_migrate_with_a_zero_modifier_and_keep_everything_else()
    {
        using var temp = new TempDatabase();
        using (var raw = new SqliteConnection($"Data Source={temp.Path};Pooling=False;Foreign Keys=True"))
        {
            raw.Open();
            DatabaseMigrations.Apply(raw, upToVersion: 5);
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                INSERT INTO RollHistory (TableId, TableName, DiceText, RollValue, ResultText, RolledUtc)
                VALUES (NULL, 'Omens', 'd100', 100, 'Doom', '2026-09-01T12:00:00.0000000Z');
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = temp.Open(); // migrates on open

        var item = Assert.Single(db.GetRollHistory());
        Assert.Equal(("Omens", "d100", 100, "Doom", 0), (item.TableName, item.DiceText, item.RollValue, item.ResultText, item.SituationalModifier));
        Assert.Equal("00", item.RollDisplay);                          // an unmodified d100 still reads 00
        Assert.Equal(6, DatabaseMigrations.CurrentVersion);
    }

    [Fact]
    public void A_stored_modifier_round_trips_and_the_database_refuses_one_outside_the_limit()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        db.AddRollHistory(new RollSnapshot(null, "Omens", "d20", -997, "No entry covers -997.", -1000));
        Assert.Equal(-1000, Assert.Single(db.GetRollHistory()).SituationalModifier);

        Assert.ThrowsAny<SqliteException>(() => db.AddRollHistory(new RollSnapshot(null, "Omens", "d20", 1012, "x", 1001)));
    }
}
