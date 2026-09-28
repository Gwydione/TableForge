using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Data.Sqlite;
using TableForge.Data;
using TableForge.Domain;
using TableForge.Import;
using TableForge.ViewModels;

namespace TableForge.Tests;

internal static class ClampFixtures
{
    public static TableEntry E(int min, int max, string text) => new() { Min = min, Max = max, Text = text };

    public static RollableTable Table(string dice, bool clamp, params (int Min, int Max, string Text)[] rows) =>
        Fixtures.Table(DiceExpression.Parse(dice), rows).Also(t => t.ClampResultsToRange = clamp);

    public static RollableTable TwoSets(string dice, bool clamp, TableEntry[] first, TableEntry[] second) => new()
    {
        Name = "Test",
        Dice = DiceExpression.Parse(dice),
        ClampResultsToRange = clamp,
        ResultSets = [new ResultSet { Name = "Difficulty", Entries = [.. first] }, new ResultSet { Name = "Modifier", Entries = [.. second] }],
    };

    /// <summary>d6 with one row per face; row 1 and row 6 have their own text.</summary>
    public static readonly (int, int, string)[] D6Rows =
        [(1, 1, "Calm"), (2, 2, "Two"), (3, 3, "Three"), (4, 4, "Four"), (5, 5, "Five"), (6, 6, "Dangerous Encounter")];
}

/// <summary>What the table's clamp range is, and when there is none (see <see cref="TableClamp"/>).</summary>
public class TableClampRangeTests
{
    [Fact]
    public void The_range_comes_from_the_rows_actually_covered_not_from_the_dice()
    {
        var table = ClampFixtures.Table("d20", true, (5, 10, "Low"), (11, 15, "High"));

        Assert.True(TableClamp.TryGetRange(table, out var min, out var max));
        Assert.Equal((5, 15), (min, max));
        Assert.Equal(5, TableClamp.LookupValue(table, 3));
        Assert.Equal(15, TableClamp.LookupValue(table, 18));
    }

    [Theory]
    [InlineData(8, 6)]
    [InlineData(-2, 1)]
    [InlineData(1, 1)]
    [InlineData(6, 6)]
    [InlineData(3, 3)]
    public void Only_values_outside_the_outer_boundaries_move(int value, int lookup)
    {
        Assert.Equal(lookup, TableClamp.LookupValue(ClampFixtures.Table("d6", true, ClampFixtures.D6Rows), value));
    }

    [Fact]
    public void A_value_in_an_internal_gap_is_not_moved()
    {
        var table = ClampFixtures.Table("d6", true, (1, 3, "Result A"), (5, 6, "Result B"));
        Assert.Equal(4, TableClamp.LookupValue(table, 4));
    }

    [Fact]
    public void With_the_option_off_nothing_moves()
    {
        Assert.Equal(8, TableClamp.LookupValue(ClampFixtures.Table("d6", false, ClampFixtures.D6Rows), 8));
        Assert.False(new RollableTable().ClampResultsToRange); // the default
    }

    [Fact]
    public void A_d66_table_has_no_clamp_range_even_if_the_flag_is_set()
    {
        var table = ClampFixtures.Table("d66", true, (11, 36, "Low"), (41, 66, "High"));

        Assert.False(TableClamp.TryGetRange(table, out _, out _, out var reason));
        Assert.Equal(TableClamp.D66Message, reason);
        Assert.Equal(70, TableClamp.LookupValue(table, 70));
        Assert.Equal(5, TableClamp.LookupValue(table, 5));
    }

    [Fact]
    public void Result_sets_sharing_one_outer_range_clamp_together()
    {
        var table = ClampFixtures.TwoSets("d8", true,
            [ClampFixtures.E(1, 4, "Easy"), ClampFixtures.E(5, 8, "Hard")],
            [ClampFixtures.E(1, 2, "-2"), ClampFixtures.E(3, 8, "+0")]); // different inner rows, same outer range

        Assert.True(TableClamp.TryGetRange(table, out var min, out var max));
        Assert.Equal((1, 8), (min, max));
    }

    [Fact]
    public void Result_sets_with_different_outer_ranges_have_no_single_clamp_range()
    {
        var table = ClampFixtures.TwoSets("d8", true, [ClampFixtures.E(1, 6, "A")], [ClampFixtures.E(1, 8, "B")]);

        Assert.False(TableClamp.TryGetRange(table, out _, out _, out var reason));
        Assert.Equal(TableClamp.IncompatibleMessage, reason);
        Assert.Equal(9, TableClamp.LookupValue(table, 9));    // neither set is clamped on its own
    }

    [Fact]
    public void An_empty_result_set_does_not_block_clamping()
    {
        var table = ClampFixtures.TwoSets("d6", true, [ClampFixtures.E(1, 6, "A")], []);
        Assert.Equal(6, TableClamp.LookupValue(table, 9));
    }
}

/// <summary>Clamp to Range through <see cref="RollViewModel"/>: where in the roll pipeline it applies, and where it never does.</summary>
public class ClampToRangeRollTests
{
    private static (RollViewModel Session, FixedDice Dice, List<RollSnapshot> Rolled) Open(RollableTable table, int providerValue,
        Func<long, RollableTable?>? loadTable = null)
    {
        var provider = new FixedDice(providerValue);
        var rolled = new List<RollSnapshot>();
        var session = new RollViewModel(table, provider, loadTable, rolled: rolled.Add);
        return (session, provider, rolled);
    }

    private static string OnlyText(RollViewModel s) => Assert.Single(s.Results).Text;

    [Fact]
    public void Above_the_maximum_the_calculated_roll_is_shown_and_the_maximum_row_is_used()
    {
        var (session, _, rolled) = Open(ClampFixtures.Table("d8", true, ClampFixtures.D6Rows), 8);

        session.RollCommand.Execute(null);

        Assert.Equal("Rolled 8", session.RollDisplay);                       // never "Rolled 6"
        Assert.Equal("Resolved as 6 (clamped)", session.RollClampNote);
        Assert.Equal("Dangerous Encounter", OnlyText(session));
        Assert.False(Assert.Single(session.Results).IsProblem);
        Assert.True(session.ResultSets[0].Entries[5].IsMatched);             // the row used is the one highlighted
        var snapshot = Assert.Single(rolled);
        Assert.Equal((8, 6, "Dangerous Encounter"), (snapshot.RollValue, snapshot.ClampedValue, snapshot.ResultText));
    }

    [Fact]
    public void The_same_roll_without_clamp_is_no_match()
    {
        var (session, _, rolled) = Open(ClampFixtures.Table("d8", false, ClampFixtures.D6Rows), 8);

        session.RollCommand.Execute(null);

        Assert.Equal("Rolled 8", session.RollDisplay);
        Assert.Equal("", session.RollClampNote);
        Assert.Equal("No entry covers 8.", OnlyText(session));
        Assert.Null(Assert.Single(rolled).ClampedValue);
    }

    [Fact]
    public void A_situational_modifier_above_the_maximum_keeps_its_breakdown_clamps_and_is_used_up()
    {
        var (session, _, rolled) = Open(ClampFixtures.Table("d6", true, ClampFixtures.D6Rows), 6);
        session.ModifierText = "+2";

        session.RollCommand.Execute(null);

        Assert.Equal("Rolled 8", session.RollDisplay);
        Assert.Equal("6 +2 situational", session.RollBreakdown);
        Assert.Equal("Resolved as 6 (clamped)", session.RollClampNote);
        Assert.Equal("Dangerous Encounter", OnlyText(session));
        Assert.Equal("0", session.ModifierText);                             // the provider roll succeeded, so the modifier is consumed
        Assert.Equal((8, 2, (int?)6), (rolled[0].RollValue, rolled[0].SituationalModifier, rolled[0].ClampedValue));
    }

    [Fact]
    public void Below_the_minimum_the_minimum_row_is_used()
    {
        var (session, _, rolled) = Open(ClampFixtures.Table("d6", true, ClampFixtures.D6Rows), 1);
        session.ModifierText = "-3";

        session.RollCommand.Execute(null);

        Assert.Equal("Rolled -2", session.RollDisplay);
        Assert.Equal("1 -3 situational", session.RollBreakdown);
        Assert.Equal("Resolved as 1 (clamped)", session.RollClampNote);
        Assert.Equal("Calm", OnlyText(session));
        Assert.Equal((-2, (int?)1), (rolled[0].RollValue, rolled[0].ClampedValue));
    }

    [Theory]
    [InlineData(1, "Calm")]
    [InlineData(6, "Dangerous Encounter")]
    public void A_roll_exactly_on_a_boundary_is_not_marked_clamped(int value, string text)
    {
        var (session, _, rolled) = Open(ClampFixtures.Table("d6", true, ClampFixtures.D6Rows), value);

        session.RollCommand.Execute(null);

        Assert.Equal(text, OnlyText(session));
        Assert.Equal("", session.RollClampNote);
        Assert.Null(Assert.Single(rolled).ClampedValue);
    }

    [Fact]
    public void A_roll_in_an_internal_gap_stays_no_match()
    {
        var (session, _, rolled) = Open(ClampFixtures.Table("d6", true, (1, 3, "Result A"), (5, 6, "Result B")), 4);

        session.RollCommand.Execute(null);

        Assert.Equal("No entry covers 4.", OnlyText(session));
        Assert.Equal("", session.RollClampNote);
        Assert.Null(Assert.Single(rolled).ClampedValue);
    }

    [Fact]
    public void Clamp_uses_the_table_rows_not_the_dice_range()
    {
        var (session, dice, _) = Open(ClampFixtures.Table("d20", true, (5, 10, "Low"), (11, 15, "High")), 3);

        session.RollCommand.Execute(null);
        Assert.Equal(("Rolled 3", "Resolved as 5 (clamped)", "Low"), (session.RollDisplay, session.RollClampNote, OnlyText(session)));

        dice.Value = 18;
        session.RollCommand.Execute(null);
        Assert.Equal(("Rolled 18", "Resolved as 15 (clamped)", "High"), (session.RollDisplay, session.RollClampNote, OnlyText(session)));
    }

    [Fact]
    public void A_stored_dice_modifier_is_clamped_after_its_ordinary_result_and_the_expression_is_unchanged()
    {
        var table = ClampFixtures.Table("d6+1", true, ClampFixtures.D6Rows);
        var (session, dice, rolled) = Open(table, 7);                        // the provider's final d6+1 result

        session.RollCommand.Execute(null);

        Assert.Equal(["d6+1"], dice.Requested);
        Assert.Equal("Rolled 7 (d6+1)", session.RollDisplay);
        Assert.Equal("Resolved as 6 (clamped)", session.RollClampNote);
        Assert.Equal("Dangerous Encounter", OnlyText(session));
        Assert.Equal("d6+1", table.Dice.ToString());
        Assert.Equal(("d6+1", 7, (int?)6), (rolled[0].DiceText, rolled[0].RollValue, rolled[0].ClampedValue));
    }

    [Fact]
    public void Manual_entry_is_never_clamped()
    {
        var (session, _, rolled) = Open(ClampFixtures.Table("d20", true, (5, 10, "Low"), (11, 15, "High")), 10);

        session.ManualRollText = "18";
        session.ResolveManualCommand.Execute(null);
        Assert.Equal("Rolled 18", session.RollDisplay);
        Assert.Equal("No entry covers 18.", OnlyText(session));
        Assert.Equal("", session.RollClampNote);

        session.ManualRollText = "3";
        session.ResolveManualCommand.Execute(null);
        Assert.Equal("No entry covers 3.", OnlyText(session));

        Assert.All(rolled, r => Assert.Null(r.ClampedValue));
    }

    [Fact]
    public void Inline_rolls_are_not_clamped()
    {
        var (session, dice, rolled) = Open(ClampFixtures.Table("d6", true, (1, 6, "Gain 1d6 Armor")), 9);

        session.RollCommand.Execute(null);                                    // 9 → clamped to 6
        Assert.Equal("Resolved as 6 (clamped)", session.RollClampNote);

        var action = Assert.Single(Assert.Single(session.Results).InlineActions);
        action.RollCommand!.Execute(null);                                    // the inline d6 also "rolls" 9

        Assert.Equal(9, action.LatestValue);
        Assert.Equal("Gain 9 Armor", session.Results[0].ResolvedText);
        Assert.Single(rolled);                                                // inline rolls are never recorded
        Assert.Equal(2, dice.Calls);
    }

    [Fact]
    public void A_d66_table_never_clamps_even_with_the_flag_set()
    {
        var (session, _, rolled) = Open(ClampFixtures.Table("d66", true, (11, 36, "Low")), 55);

        session.RollCommand.Execute(null);

        Assert.Equal("No entry covers 55.", OnlyText(session));
        Assert.Equal("", session.RollClampNote);
        Assert.Null(Assert.Single(rolled).ClampedValue);
    }

    [Fact]
    public void Compatible_result_sets_are_all_looked_up_with_the_one_clamped_value()
    {
        var table = ClampFixtures.TwoSets("d8", true,
            [ClampFixtures.E(1, 4, "Easy"), ClampFixtures.E(5, 8, "Hard")],
            [ClampFixtures.E(1, 7, "+0"), ClampFixtures.E(8, 8, "+2")]);
        var (session, _, rolled) = Open(table, 10);

        session.RollCommand.Execute(null);

        Assert.Equal("Resolved as 8 (clamped)", session.RollClampNote);
        Assert.Equal(["Hard", "+2"], session.Results.Select(r => r.Text).ToArray());
        Assert.Equal("Difficulty: Hard\nModifier: +2", rolled[0].ResultText);
        Assert.Equal(8, rolled[0].ClampedValue);
    }

    [Fact]
    public void Incompatible_result_sets_are_never_clamped_one_by_one()
    {
        var table = ClampFixtures.TwoSets("d8", true, [ClampFixtures.E(1, 6, "A")], [ClampFixtures.E(1, 8, "B")]);
        var (session, _, rolled) = Open(table, 9);

        session.RollCommand.Execute(null);

        Assert.Equal(["No entry covers 9.", "No entry covers 9."], session.Results.Select(r => r.Text).ToArray());
        Assert.Equal("", session.RollClampNote);
        Assert.Null(rolled[0].ClampedValue);
    }

    [Fact]
    public void An_overlap_at_the_boundary_stays_ambiguous()
    {
        var (session, _, rolled) = Open(ClampFixtures.Table("d8", true, (1, 6, "Wide"), (5, 6, "Narrow")), 8);

        session.RollCommand.Execute(null);

        var line = Assert.Single(session.Results);
        Assert.True(line.IsProblem);
        Assert.Equal("Ambiguous: \"Wide\" (1–6) and \"Narrow\" (5–6) both cover 6.", line.Text);
        Assert.Equal("Resolved as 6 (clamped)", session.RollClampNote);
        Assert.Equal(6, rolled[0].ClampedValue);
    }

    [Fact]
    public void A_followed_link_uses_the_destinations_own_setting()
    {
        var clamped = ClampFixtures.Table("d6", true, ClampFixtures.D6Rows).Also(t => { t.Id = 2; t.Name = "Clamped"; });
        var plain = ClampFixtures.Table("d6", false, ClampFixtures.D6Rows).Also(t => { t.Id = 3; t.Name = "Plain"; });
        RollableTable Source(bool clamp, long target) => new()
        {
            Id = 1, Name = "Source", Dice = DiceExpression.Parse("d6"), ClampResultsToRange = clamp,
            ResultSets = [new ResultSet { Entries = [new TableEntry { Min = 1, Max = 6, Text = "Go on", LinkedTableId = target }] }],
        };
        RollableTable? Load(long id) => id == 2 ? clamped : id == 3 ? plain : null;

        // Unclamped source → clamped destination: the destination clamps.
        var (session, dice, _) = Open(Source(false, 2), 8, Load);
        session.RollCommand.Execute(null);
        Assert.Equal("No entry covers 8.", OnlyText(session));                // the source itself does not clamp
        dice.Value = 6;
        session.RollCommand.Execute(null);
        session.Results[0].FollowCommand!.Execute(null);
        session.ModifierText = "+2";
        session.RollCommand.Execute(null);
        Assert.Equal(("Clamped", "Resolved as 6 (clamped)"), (session.Title, session.RollClampNote));

        // Clamped source → unclamped destination: nothing is inherited.
        (session, _, _) = Open(Source(true, 3), 6, Load);
        session.RollCommand.Execute(null);
        session.Results[0].FollowCommand!.Execute(null);
        session.ModifierText = "+2";
        session.RollCommand.Execute(null);
        Assert.Equal(("Plain", "", "No entry covers 8."), (session.Title, session.RollClampNote, OnlyText(session)));
    }
}

/// <summary>Clamp in Recent Rolls and in the database: the calculated roll, the clamped lookup and the resolved text all kept.</summary>
public class ClampToRangeHistoryTests
{
    private sealed class World : IDisposable
    {
        public TempDatabase Temp { get; } = new();
        public AppDatabase Db { get; }
        public FixedDice Dice { get; } = new(6);
        public MainViewModel Main { get; }

        public World()
        {
            Db = Temp.Open();
            var c = Db.CreateCollection("C");
            Db.SaveTable(ClampFixtures.Table("d6", true, ClampFixtures.D6Rows).Also(t => { t.Name = "Encounters"; t.CollectionId = c.Id; }));
            Main = new MainViewModel(Db, Dice);
        }

        public RollViewModel Open(string name)
        {
            Main.SelectedTable = Main.Tables.Single(t => t.Name == name);
            return Assert.IsType<RollViewModel>(Main.Current);
        }

        public void Dispose() => Temp.Dispose();
    }

    [Fact]
    public void Recent_rolls_keep_the_calculated_value_the_clamped_value_and_the_clamped_rows_text()
    {
        using var w = new World();
        var session = w.Open("Encounters");
        session.RollCommand.Execute(null);                                     // 6: an ordinary roll
        session.ModifierText = "+2";
        session.RollCommand.Execute(null);                                     // 8 → 6

        var history = w.Db.GetRollHistory();
        Assert.Equal((8, 2, (int?)6, "Dangerous Encounter"),
            (history[0].RollValue, history[0].SituationalModifier, history[0].ClampedValue, history[0].ResultText));
        Assert.Equal((6, (int?)null), (history[1].RollValue, history[1].ClampedValue));

        Assert.Equal("8 (+2) → 6 (clamped)", w.Main.RecentRolls[0].RollDisplay);
        Assert.Equal("6", w.Main.RecentRolls[1].RollDisplay);                 // unclamped rows look exactly as before
        Assert.Contains("d6 → 8 (6 +2 situational)\nResolved as 6 (clamped)\n\nDangerous Encounter", w.Main.RecentRolls[0].FullText);
        Assert.DoesNotContain("clamped", w.Main.RecentRolls[1].FullText);
    }

    [Fact]
    public void A_clamped_roll_without_a_modifier_reads_compactly()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        db.AddRollHistory(new RollSnapshot(null, "Encounters", "d6+1", 7, "Dangerous Encounter", 0, 6));

        var item = Assert.Single(db.GetRollHistory());
        Assert.Equal("7 → 6 (clamped)", new RecentRollViewModel(item).RollDisplay);
        Assert.Equal("Resolved as 6 (clamped)", item.ClampNote);
    }

    [Fact]
    public void The_setting_round_trips_and_new_tables_default_to_off()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = db.CreateCollection("C");

        var off = db.SaveTable(Fixtures.Table(DiceExpression.Parse("d6"), (1, 6, "x")).Also(t => t.CollectionId = c.Id));
        var on = db.SaveTable(ClampFixtures.Table("d6", true, (1, 6, "x")).Also(t => t.CollectionId = c.Id));
        Assert.False(db.LoadTable(off.Id)!.ClampResultsToRange);
        Assert.True(db.LoadTable(on.Id)!.ClampResultsToRange);

        var loaded = db.LoadTable(on.Id)!;
        loaded.ClampResultsToRange = false;
        db.SaveTable(loaded);
        Assert.False(db.LoadTable(on.Id)!.ClampResultsToRange);
    }

    [Fact]
    public void Deleting_the_tables_folder_keeps_the_setting()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = db.CreateCollection("C");
        var folder = db.CreateFolder(c.Id, "Wilds");
        var table = db.SaveTable(ClampFixtures.Table("d6", true, (1, 6, "x")).Also(t => { t.CollectionId = c.Id; t.FolderId = folder.Id; }));

        db.DeleteFolder(folder.Id);

        var loaded = db.LoadTable(table.Id)!;
        Assert.Null(loaded.FolderId);
        Assert.True(loaded.ClampResultsToRange);
    }
}

/// <summary>Schema v7: Tables.ClampResultsToRange and RollHistory.ClampedValue, added without disturbing existing data.</summary>
public class ClampToRangeMigrationTests
{
    [Fact]
    public void A_version_6_database_migrates_with_clamp_off_and_no_clamped_history()
    {
        using var temp = new TempDatabase();
        using (var raw = new SqliteConnection($"Data Source={temp.Path};Pooling=False;Foreign Keys=True"))
        {
            raw.Open();
            DatabaseMigrations.Apply(raw, upToVersion: 6);
            using var cmd = raw.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Collections (Name, CreatedUtc) VALUES ('Solo', '2026-09-01T12:00:00.0000000Z');
                INSERT INTO Folders (CollectionId, Name) VALUES (1, 'Wilds');
                INSERT INTO Tables (CollectionId, Name, DiceCount, DiceSides, DiceModifier, DiceConvention, FolderId, CreatedUtc, UpdatedUtc)
                VALUES (1, 'Encounters', 1, 6, 1, 0, 1, '2026-09-01T12:00:00.0000000Z', '2026-09-01T12:00:00.0000000Z');
                INSERT INTO ResultSets (TableId, Name, SortOrder) VALUES (1, '', 0);
                INSERT INTO Entries (ResultSetId, MinValue, MaxValue, DisplayText, SortOrder) VALUES (1, 2, 7, 'Something', 0);
                INSERT INTO RollHistory (TableId, TableName, DiceText, RollValue, ResultText, RolledUtc, SituationalModifier)
                VALUES (1, 'Encounters', 'd6+1', 9, 'No entry covers 9.', '2026-09-01T12:00:00.0000000Z', 2);
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = temp.Open(); // migrates on open

        var table = db.LoadTable(1)!;
        Assert.False(table.ClampResultsToRange);
        Assert.Equal(("Encounters", "d6+1", (long?)1), (table.Name, table.Dice.ToString(), table.FolderId));
        var entry = Assert.Single(Assert.Single(table.ResultSets).Entries);
        Assert.Equal((2, 7, "Something"), (entry.Min, entry.Max, entry.Text));

        var item = Assert.Single(db.GetRollHistory());
        Assert.Null(item.ClampedValue);
        Assert.Equal((1L, 9, 2, "No entry covers 9."), (item.TableId!.Value, item.RollValue, item.SituationalModifier, item.ResultText));
        Assert.Equal("9 (+2)", new RecentRollViewModel(item).RollDisplay);
        Assert.Equal(8, DatabaseMigrations.CurrentVersion);
    }
}

/// <summary>The Clamp checkbox on Review/Edit: when it can be turned on, and what Save stores.</summary>
public class ClampToRangeReviewTests
{
    private sealed class World : IDisposable
    {
        public TempDatabase Temp { get; } = new();
        public AppDatabase Db { get; }
        public Collection Collection { get; }
        public MainViewModel Main { get; }

        public World(params RollableTable[] tables)
        {
            Db = Temp.Open();
            Collection = Db.CreateCollection("C");
            Db.CreateFolder(Collection.Id, "Wilds");
            foreach (var t in tables) { t.CollectionId = Collection.Id; Db.SaveTable(t); }
            Main = new MainViewModel(Db, new FixedDice(6));
        }

        public ReviewViewModel Edit(string name)
        {
            Main.SelectedTable = Main.Tables.Single(t => t.Name == name);
            Main.EditTableCommand.Execute(null);
            return Assert.IsType<ReviewViewModel>(Main.Current);
        }

        public RollableTable Stored(string name) => Db.LoadTable(Db.GetTableSummaries(Collection.Id).Single(t => t.Name == name).Id)!;

        public void Dispose() => Temp.Dispose();
    }

    private static RollableTable Named(RollableTable t, string name) => t.Also(x => x.Name = name);

    [Fact]
    public void Turning_clamp_on_saves_and_survives_reopening_renaming_and_moving_folders()
    {
        using var w = new World(Named(ClampFixtures.Table("d6", false, ClampFixtures.D6Rows), "Encounters"));
        var review = w.Edit("Encounters");
        Assert.True(review.IsClampAvailable);
        Assert.False(review.ClampResultsToRange);                              // off unless the user turns it on

        review.ClampResultsToRange = true;
        review.SaveCommand.Execute(null);
        Assert.True(w.Stored("Encounters").ClampResultsToRange);

        review = w.Edit("Encounters");
        Assert.True(review.ClampResultsToRange);
        review.TableName = "Wild Encounters";
        review.SelectedFolder = review.FolderOptions.Single(f => f.Name == "Wilds");
        review.SaveCommand.Execute(null);

        var stored = w.Stored("Wild Encounters");
        Assert.True(stored.ClampResultsToRange);
        Assert.NotNull(stored.FolderId);

        review = w.Edit("Wild Encounters");
        review.ClampResultsToRange = false;
        review.SaveCommand.Execute(null);
        Assert.False(w.Stored("Wild Encounters").ClampResultsToRange);
    }

    [Fact]
    public void Internal_gaps_do_not_stop_clamp()
    {
        using var w = new World(Named(ClampFixtures.Table("d6", false, (1, 3, "A"), (5, 6, "B")), "Gappy"));
        var review = w.Edit("Gappy");

        Assert.True(review.IsClampAvailable);
        Assert.Equal("", review.ClampUnavailableNote);
    }

    [Fact]
    public void A_d66_table_cannot_turn_clamp_on()
    {
        using var w = new World(Named(ClampFixtures.Table("d66", false, (11, 66, "Anything")), "Names"));
        var review = w.Edit("Names");

        Assert.False(review.IsClampAvailable);
        Assert.Equal(TableClamp.D66Message, review.ClampUnavailableNote);
        review.ClampResultsToRange = true;                                     // refused while unavailable
        Assert.False(review.ClampResultsToRange);
        review.SaveCommand.Execute(null);
        Assert.False(w.Stored("Names").ClampResultsToRange);
    }

    [Fact]
    public void Editing_a_clamped_table_into_a_d66_turns_clamp_off_on_save()
    {
        using var w = new World(Named(ClampFixtures.Table("d6", true, (1, 6, "Anything")), "Omens"));
        var review = w.Edit("Omens");
        Assert.True(review.ClampResultsToRange);

        review.DiceText = "d66";
        review.SelectedResultSet.Rows[0].RangeText = "11-16";

        Assert.False(review.IsClampAvailable);
        Assert.False(review.ClampResultsToRange);                              // the box shows what Save will store
        Assert.Equal($"{TableClamp.D66Message} Clamp will be off when this table is saved.", review.ClampUnavailableNote);

        review.SaveCommand.Execute(null);
        var stored = w.Stored("Omens");
        Assert.True(stored.Dice.IsD66);
        Assert.False(stored.ClampResultsToRange);
    }

    [Fact]
    public void Result_sets_that_stop_sharing_a_range_turn_clamp_off_unless_the_range_is_restored_before_saving()
    {
        var table = Named(ClampFixtures.TwoSets("d8", true, [ClampFixtures.E(1, 8, "A")], [ClampFixtures.E(1, 8, "B")]), "Pairs");
        using var w = new World(table);
        var review = w.Edit("Pairs");
        Assert.True(review.ClampResultsToRange);
        var modifier = review.ResultSets.Single(s => s.Name == "Modifier");

        modifier.Rows[0].RangeText = "1-6";
        Assert.False(review.IsClampAvailable);
        Assert.False(review.ClampResultsToRange);
        Assert.Equal($"{TableClamp.IncompatibleMessage} Clamp will be off when this table is saved.", review.ClampUnavailableNote);

        modifier.Rows[0].RangeText = "1-8";                                    // back to one shared range: the choice was kept
        Assert.True(review.IsClampAvailable);
        Assert.True(review.ClampResultsToRange);

        modifier.Rows[0].RangeText = "1-6";
        review.SaveCommand.Execute(null);
        Assert.False(w.Stored("Pairs").ClampResultsToRange);
    }

    [Fact]
    public void Result_sets_with_different_ranges_cannot_turn_clamp_on()
    {
        var table = Named(ClampFixtures.TwoSets("d8", false, [ClampFixtures.E(1, 6, "A")], [ClampFixtures.E(1, 8, "B")]), "Mixed");
        using var w = new World(table);
        var review = w.Edit("Mixed");

        Assert.False(review.IsClampAvailable);
        Assert.Equal(TableClamp.IncompatibleMessage, review.ClampUnavailableNote);
        review.ClampResultsToRange = true;
        Assert.False(review.ClampResultsToRange);
    }

    [Fact]
    public void A_pasted_table_starts_with_clamp_off()
    {
        var draft = TableTextParser.Parse("D6 WEATHER\n1-3 Fair\n4-6 Foul");
        Assert.False(draft.ClampResultsToRange);
        Assert.True(draft.TryBuildTable(1, out var table, out _));
        Assert.False(table!.ClampResultsToRange);
    }
}

/// <summary>Clamp to Range through the real MainWindow, ReviewView and RollView, with the fixed test dice.</summary>
[Collection("UI")]
public class ClampToRangeViewTests
{
    private static UiHarness Open(int roll) => new(roll, (db, c) =>
    {
        db.SaveTable(Fixtures.Table(DiceExpression.Parse("d8"), ClampFixtures.D6Rows).Also(t => { t.Name = "Encounters"; t.CollectionId = c.Id; }));
        db.SaveTable(Fixtures.Table(DiceExpression.Parse("d66"), (11, 66, "Anything")).Also(t => { t.Name = "Names"; t.CollectionId = c.Id; }));
    });

    private static CheckBox ClampBox(UiHarness ui) => ui.One<CheckBox>(c => c.Name == "ClampBox");

    private static void SetClamp(UiHarness ui, string table, bool on)
    {
        ui.SelectTable(table);
        ui.Click("Edit table");
        var box = ClampBox(ui);
        Assert.True(box.IsVisible && box.IsEnabled);
        box.IsChecked = on;
        ui.Layout();
        ui.Click("Save Table");
    }

    [Fact]
    public void Clamp_is_turned_on_in_edit_saved_and_used_by_a_roll_above_the_maximum()
    {
        Sta.Run(() =>
        {
            using var ui = Open(8);
            SetClamp(ui, "Encounters", on: true);

            ui.SelectTable("Encounters");
            ui.Click("Edit table");
            Assert.True(ClampBox(ui).IsChecked);                               // saved and reopened
            ui.Click("Cancel");

            ui.SelectTable("Encounters");
            ui.Click("Roll");
            Assert.Contains(ui.Texts(), t => t.Text == "Rolled 8");            // the calculated number stays visible
            Assert.Contains(ui.Texts(), t => t.Text == "Resolved as 6 (clamped)");
            Assert.Contains(ui.Texts(), t => t.Text == "Dangerous Encounter" && t.FontSize == 26);

            SetClamp(ui, "Encounters", on: false);
            ui.SelectTable("Encounters");
            ui.Click("Roll");
            Assert.Contains(ui.Texts(), t => t.Text == "No entry covers 8.");
            Assert.DoesNotContain(ui.Texts(), t => t.Name == "ClampNoteText");   // (Recent Rolls still shows the earlier clamped roll)
        });
    }

    [Fact]
    public void Manual_entry_outside_the_range_is_not_clamped()
    {
        Sta.Run(() =>
        {
            using var ui = Open(3);
            SetClamp(ui, "Encounters", on: true);
            ui.SelectTable("Encounters");

            var manual = ui.One<TextBox>(t => t.Name == "ManualBox");
            manual.Text = "8";
            CommandManager.InvalidateRequerySuggested();
            ui.Click("Resolve");

            Assert.Contains(ui.Texts(), t => t.Text == "No entry covers 8.");
            Assert.DoesNotContain(ui.Texts(), t => t.Name == "ClampNoteText");
            Assert.DoesNotContain(ui.Texts(), t => t.Text.Contains("clamped"));
        });
    }

    [Fact]
    public void The_clamp_box_is_unavailable_for_a_d66_table()
    {
        Sta.Run(() =>
        {
            using var ui = Open(3);
            ui.SelectTable("Names");
            ui.Click("Edit table");

            var box = ClampBox(ui);
            Assert.True(box.IsVisible);
            Assert.False(box.IsEnabled);
            Assert.False(box.IsChecked);
            Assert.Contains(ui.Texts(), t => t.Text == TableClamp.D66Message);
        });
    }
}
