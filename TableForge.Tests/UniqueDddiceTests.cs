using TableForge.Dice;
using TableForge.Domain;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>
/// Unique Results with dddice (Checkpoint 4): the real <see cref="DddiceDiceProvider"/> over a scripted room, through the same
/// <see cref="UniqueRoller"/> as the built-in dice — the original dice on every throw, 10 throws per result (the first included),
/// Stop while a throw or reroll is in the air, late dice that settle after Stop, dddice failures, and auto-follow.
/// </summary>
public class UniqueDddiceTests
{
    /// <summary>
    /// The dddice room, scripted: every throw is kept (with its token) so a test can settle an earlier one late, after Stop and
    /// after a newer roll has begun. A throw stays in the air until the test lands it, unless <see cref="Instant"/> has a value.
    /// </summary>
    private sealed class Room : IDddiceRoomRoller
    {
        public sealed record Throw(IReadOnlyList<string> Dice, TaskCompletionSource<IReadOnlyList<DddiceFace>> Faces, CancellationToken Token);

        public List<Throw> Throws { get; } = [];

        /// <summary>Totals for throws that settle at once, as soon as they are asked for.</summary>
        public Queue<int> Instant { get; } = new();

        /// <summary>Called with the 1-based throw number as each throw is asked for (before an instant one settles).</summary>
        public Action<int>? OnThrow { get; set; }

        public Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<DddiceFace>> RollAsync(IReadOnlyList<string> diceTypes, CancellationToken cancellationToken)
        {
            var thrown = new Throw(diceTypes, new(TaskCreationOptions.RunContinuationsAsynchronously), cancellationToken);
            Throws.Add(thrown);
            OnThrow?.Invoke(Throws.Count);
            // An instant throw is the dice settling just as Stop is pressed: dddice answers anyway, whatever the token says.
            if (Instant.TryDequeue(out var total)) return Task.FromResult(Faces(diceTypes, total));
            return thrown.Faces.Task.WaitAsync(cancellationToken);
        }

        /// <summary>The faces of these dice that add up to <paramref name="total"/> (as even as possible).</summary>
        public static IReadOnlyList<DddiceFace> Faces(IReadOnlyList<string> dice, int total) =>
            dice.Select((d, i) => new DddiceFace(d, total / dice.Count + (i < total % dice.Count ? 1 : 0))).ToList();

        public void Land(int index, int total) => Throws[index].Faces.TrySetResult(Faces(Throws[index].Dice, total));
        public void Fail(int index, Exception ex) => Throws[index].Faces.TrySetException(ex);
    }

    private sealed class Rig
    {
        public Room Room { get; } = new();
        public Dictionary<long, RollableTable> Tables { get; } = [];
        public List<RollSnapshot> History { get; } = [];
        public UsedResultsStore Inner { get; } = UsedResultsStore.InMemory();
        public bool FailMark { get; set; }
        public RollViewModel Session { get; private set; } = null!;

        public RollableTable Add(string name, string dice, params TableEntry[] rows)
        {
            var table = new RollableTable { Id = Tables.Count + 1, Name = name, Dice = DiceExpression.Parse(dice), ResultSets = [new ResultSet { Entries = [.. rows] }] };
            Tables[table.Id] = table;
            return table;
        }

        public RollViewModel Open(RollableTable start, bool autoFollow = false)
        {
            var store = new UsedResultsStore(Inner.Get, (id, keys) =>
            {
                if (FailMark) throw new InvalidOperationException("disk full");
                Inner.MarkUsed(id, keys);
            });
            return Session = new RollViewModel(start, new DddiceDiceProvider(Room), Tables.GetValueOrDefault, rolled: History.Add,
                autoFollow: new AutoFollowSetting(autoFollow), usedResults: store);
        }

        private IEnumerable<(RollableTable Table, TableEntry Entry)> Rows =>
            Tables.Values.SelectMany(t => t.ResultSets.SelectMany(s => s.Entries).Select(e => (t, e)));

        public void MarkUsed(params string[] texts)
        {
            foreach (var (table, entry) in Rows.Where(r => texts.Contains(r.Entry.Text))) Inner.MarkUsed(table.Id, [entry.RowKey]);
        }

        public string[] Used() => Rows.Where(r => Inner.Get(r.Table.Id).Contains(r.Entry.RowKey)).Select(r => r.Entry.Text).Order().ToArray();

        public string[][] Shown() => Session.Steps.SelectMany(s => s.Outcomes).Select(o => o.Lines.Select(l => l.Text).ToArray()).ToArray();

        public Task Roll(int count = 1)
        {
            Session.RollCount = count;
            Session.RollCommand.Execute(null);
            return Session.RollTask;
        }

        /// <summary>Lands the throw in the air on <paramref name="total"/>, then waits for the next throw or for the action to end.</summary>
        public async Task Land(params int[] totals)
        {
            foreach (var total in totals)
            {
                await Wait.Until(() => Room.Throws.Count > 0 && !Room.Throws[^1].Faces.Task.IsCompleted, "a throw in the air");
                var before = Room.Throws.Count;
                Room.Land(before - 1, total);
                await Wait.Until(() => Room.Throws.Count > before || Session.RollTask.IsCompleted, "the next throw or the end");
            }
        }

        public async Task InTheAir(int throws) => await Wait.Until(() => Room.Throws.Count == throws && !Session.RollTask.IsCompleted, $"throw {throws} in the air");
    }

    private static TableEntry Row(int min, int max, string text, bool unique = true, RollableTable? link = null) =>
        new() { Min = min, Max = max, Text = text, IsUnique = unique, RowKey = Guid.NewGuid(), LinkedTableId = link?.Id };

    /// <summary>Monsters (2d6): Goblin 2-6, Ogre 7, Dragon 8-12, every row Unique.</summary>
    private static RollableTable Monsters(Rig rig, RollableTable? goblinLinksTo = null) =>
        rig.Add("Monsters", "2d6", Row(2, 6, "Goblin", link: goblinLinksTo), Row(7, 7, "Ogre"), Row(8, 12, "Dragon"));

    // ---- rerolls and the ten-throw limit ---------------------------------------------------------------------------------

    [Fact]
    public void Dddice_allows_10_throws_per_result_and_the_built_in_dice_10000()
    {
        Assert.Equal(10, new DddiceDiceProvider(new Room()).MaxAttemptsPerResult);
        Assert.Equal(10_000, new BuiltInDiceProvider().MaxAttemptsPerResult);
    }

    [Fact]
    public async Task A_result_accepted_on_the_first_throw_has_no_rerolls()
    {
        var rig = new Rig();
        rig.Open(Monsters(rig));
        var roll = rig.Roll();
        await rig.Land(7);
        await roll;

        Assert.Single(rig.Room.Throws);
        Assert.Equal([["Ogre"]], rig.Shown());
        Assert.Equal(0, rig.History.Single().RerolledCount);
        Assert.Equal(0, rig.Session.Current.Outcomes.Single().RerolledCount);
        Assert.Equal(["Ogre"], rig.Used());
    }

    [Fact]
    public async Task Used_results_are_rethrown_with_the_same_dice_and_only_the_accepted_third_throw_is_shown_recorded_and_used()
    {
        var rig = new Rig();
        rig.Open(Monsters(rig));
        rig.MarkUsed("Dragon");
        var roll = rig.Roll();
        await rig.Land(10, 8, 4);
        await roll;

        Assert.All(rig.Room.Throws, t => Assert.Equal(["d6", "d6"], t.Dice));        // 2d6 every time, never narrowed to what is left
        Assert.Equal(3, rig.Room.Throws.Count);
        Assert.Equal([["Goblin"]], rig.Shown());
        var record = rig.History.Single();
        Assert.Equal((4, 2), (record.RollValue, record.RerolledCount));
        Assert.Equal(2, rig.Session.Current.Outcomes.Single().RerolledCount);
        Assert.Equal(["Dragon", "Goblin"], rig.Used());
        Assert.Equal("", rig.Session.Message);
    }

    [Fact]
    public async Task Exactly_10_throws_are_made_then_nothing_is_used_shown_or_recorded_and_the_modifier_stays()
    {
        var rig = new Rig();
        // d6 + modifier: Low 2-3 is still unused, so the table is not exhausted; every throw here lands on High.
        rig.Open(rig.Add("Depth", "d6", Row(2, 3, "Low"), Row(4, 7, "High")));
        rig.MarkUsed("High");
        rig.Session.ModifierText = "1";
        var roll = rig.Roll();
        await rig.Land(Enumerable.Repeat(5, 10).ToArray());
        await roll;

        Assert.Equal(10, rig.Room.Throws.Count);                                     // no eleventh
        Assert.Equal("No unused result came up in 10 rolls, so nothing was used. Roll again, or enter a result manually.", rig.Session.Message);
        Assert.Empty(rig.Session.Current.Outcomes);
        Assert.Empty(rig.History);
        Assert.Equal(["High"], rig.Used());
        Assert.Equal("1", rig.Session.ModifierText);
        Assert.False(rig.Session.IsRolling);
    }

    [Fact]
    public async Task The_modifier_and_clamp_apply_to_every_dddice_throw()
    {
        var rig = new Rig();
        var table = rig.Add("Depth", "d6", Row(1, 3, "Low"), Row(4, 5, "High"));
        table.ClampResultsToRange = true;
        rig.Open(table);
        rig.MarkUsed("High");
        rig.Session.ModifierText = "2";
        var roll = rig.Roll();
        await rig.Land(4, 3, 1);                                                     // 6 → clamped 5 (High), 5 (High), then 3 (Low)
        await roll;

        Assert.Equal([["Low"]], rig.Shown());
        var record = rig.History.Single();
        Assert.Equal((3, 2, (int?)null, 2), (record.RollValue, record.SituationalModifier, record.ClampedValue, record.RerolledCount));
        Assert.Equal("0", rig.Session.ModifierText);
    }

    [Fact]
    public async Task A_repeatable_row_is_still_rolled_once_every_unique_row_is_used()
    {
        var rig = new Rig();
        rig.Open(rig.Add("Monsters", "d6", Row(1, 4, "Goblin"), Row(5, 6, "Rats", unique: false)));
        rig.MarkUsed("Goblin");
        var roll = rig.Roll();
        await rig.Land(2, 6);
        await roll;

        Assert.Equal([["Rats"]], rig.Shown());
        Assert.Equal(1, rig.History.Single().RerolledCount);
        Assert.Equal(["Goblin"], rig.Used());
    }

    [Fact]
    public async Task An_exhausted_table_throws_no_dddice_dice()
    {
        var rig = new Rig();
        rig.Open(Monsters(rig));
        rig.MarkUsed("Goblin", "Ogre", "Dragon");
        await rig.Roll();

        Assert.Empty(rig.Room.Throws);
        Assert.Equal(UniqueResults.ExhaustedMessage, rig.Session.Message);
    }

    [Fact]
    public async Task A_batch_keeps_its_accepted_results_when_a_later_result_reaches_the_limit()
    {
        var rig = new Rig();
        rig.Open(Monsters(rig));
        rig.MarkUsed("Dragon");
        var roll = rig.Roll(3);
        await rig.Land(9, 7);                                                        // result 1: Dragon rerolled, Ogre
        await rig.Land(Enumerable.Repeat(7, 10).ToArray());                          // result 2: Ogre (now used) ten times
        await roll;

        Assert.Equal(12, rig.Room.Throws.Count);
        Assert.Equal([["Ogre"]], rig.Shown());
        Assert.Equal([1], rig.History.Select(h => h.RerolledCount).ToArray());
        Assert.Equal(["Dragon", "Ogre"], rig.Used());
        Assert.Equal($"{RollViewModel.AttemptLimitMessage(10)} Stopped at roll 2 of 3: rolls 1–1 are shown; the rest were not rolled.", rig.Session.Message);
    }

    // ---- Stop, cancellation and late dice --------------------------------------------------------------------------------

    [Fact]
    public async Task Stop_while_the_first_throw_is_in_the_air_cancels_it_and_changes_nothing()
    {
        var rig = new Rig();
        rig.Open(Monsters(rig));
        rig.Session.ModifierText = "1";
        var roll = rig.Roll();
        await rig.InTheAir(1);
        rig.Session.CancelRoll();
        await roll;

        Assert.True(rig.Room.Throws[0].Token.IsCancellationRequested);
        Assert.Equal("The roll was cancelled.", rig.Session.Message);
        Assert.Empty(rig.Shown());
        Assert.Empty(rig.History);
        Assert.Empty(rig.Used());
        Assert.Equal("1", rig.Session.ModifierText);
    }

    [Fact]
    public async Task Stop_while_a_reroll_is_in_the_air_uses_nothing_and_its_late_dice_cannot_touch_the_next_roll()
    {
        var rig = new Rig();
        rig.Open(Monsters(rig));
        rig.MarkUsed("Dragon");
        var roll = rig.Roll();
        await rig.Land(11);                                                          // Dragon: rejected, rethrown
        await rig.InTheAir(2);
        rig.Session.CancelRoll();
        await roll;

        Assert.True(rig.Room.Throws[1].Token.IsCancellationRequested);
        Assert.Equal("The roll was cancelled.", rig.Session.Message);
        Assert.Empty(rig.History);
        Assert.Equal(["Dragon"], rig.Used());

        var next = rig.Roll();                                                       // a new roll is a new throw, counted from zero
        await rig.InTheAir(3);
        rig.Room.Land(1, 7);                                                         // the stopped reroll settles late, on Ogre
        await Task.Delay(50);
        Assert.Empty(rig.Shown());
        Assert.Empty(rig.History);
        Assert.Equal(["Dragon"], rig.Used());
        Assert.False(next.IsCompleted);                                              // the new roll is still waiting for its own dice

        await rig.Land(4);
        await next;

        Assert.Equal([["Goblin"]], rig.Shown());
        Assert.Equal((4, 0), (rig.History.Single().RollValue, rig.History.Single().RerolledCount));
        Assert.Equal(["Dragon", "Goblin"], rig.Used());
        Assert.Equal("", rig.Session.Message);
    }

    [Fact]
    public async Task Dice_that_settle_just_as_Stop_is_pressed_are_never_shown_or_used_and_do_not_continue_a_batch()
    {
        var rig = new Rig();
        rig.Open(Monsters(rig));
        rig.MarkUsed("Dragon");
        rig.Room.Instant.Enqueue(7);                                                 // result 1: Ogre, accepted
        rig.Room.Instant.Enqueue(10);                                                // result 2: Dragon, rejected
        rig.Room.Instant.Enqueue(4);                                                 // ... its reroll lands on Goblin as Stop is pressed
        rig.Room.OnThrow = n => { if (n == 3) rig.Session.CancelRoll(); };
        await rig.Roll(3);

        Assert.Equal(3, rig.Room.Throws.Count);
        Assert.Equal([["Ogre"]], rig.Shown());
        Assert.Equal([0], rig.History.Select(h => h.RerolledCount).ToArray());
        Assert.Equal(["Dragon", "Ogre"], rig.Used());
        Assert.Equal("The roll was cancelled. Stopped at roll 2 of 3: rolls 1–1 are shown; the rest were not rolled.", rig.Session.Message);
    }

    [Fact]
    public async Task A_first_throw_that_settles_after_Stop_is_not_shown_either()
    {
        var rig = new Rig();
        rig.Open(Monsters(rig));
        rig.Room.Instant.Enqueue(7);
        rig.Room.OnThrow = _ => rig.Session.CancelRoll();
        await rig.Roll();

        Assert.Empty(rig.Shown());
        Assert.Empty(rig.History);
        Assert.Empty(rig.Used());
        Assert.Equal("The roll was cancelled.", rig.Session.Message);
    }

    [Fact]
    public void A_late_finished_event_from_an_abandoned_roll_does_not_complete_the_next_one()
    {
        static string Finished(string id, int value) =>
            $$$"""{"kind":"roll:finished","roll":{"externalId":"{{{id}}}","values":[{"type":"d6","value":{{{value}}}}]}}""";

        var tracker = new DddiceRollTracker();
        _ = tracker.Begin("old");
        tracker.Abandon();                                                           // Stop
        var next = tracker.Begin("new");

        tracker.OnPageMessage(Finished("old", 6));
        Assert.False(next.IsCompleted);
        tracker.OnPageMessage(Finished("new", 2));
        Assert.Equal(2, next.Result.Single().Value);
    }

    // ---- dddice failures -------------------------------------------------------------------------------------------------

    public static TheoryData<string, string> Failures => new()
    {
        { "refused", "dddice refused the roll: busy" },
        { "timeout", "The dddice roll did not finish within 30 seconds." },
        { "dropped", "Gidubba could not reach dddice. Check the internet connection." },
        { "invalid", "dddice returned dice that do not match the roll that was asked for." },
        { "unexpected", "The roll could not be made: boom" },
    };

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_reroll_that_fails_uses_nothing_records_nothing_keeps_the_batch_and_says_why(string failure, string message)
    {
        var rig = new Rig();
        rig.Open(Monsters(rig));
        rig.MarkUsed("Dragon");
        var roll = rig.Roll(2);
        await rig.Land(7);                                                           // result 1: Ogre
        await rig.Land(12);                                                          // result 2: Dragon, rejected
        await rig.InTheAir(3);
        switch (failure)
        {
            case "refused": rig.Room.Fail(2, new DddiceException("dddice refused the roll: busy")); break;
            case "timeout": rig.Room.Fail(2, new DddiceException("The dddice roll did not finish within 30 seconds.")); break;
            case "dropped": rig.Room.Fail(2, new DddiceException("Gidubba could not reach dddice. Check the internet connection.") { IsTemporary = true }); break;
            case "invalid": rig.Room.Throws[2].Faces.SetResult([new DddiceFace("d8", 3)]); break;
            case "unexpected": rig.Room.Fail(2, new InvalidOperationException("boom")); break;
        }
        await roll;

        Assert.Equal($"{message} Stopped at roll 2 of 2: rolls 1–1 are shown; the rest were not rolled.", rig.Session.Message);
        Assert.Equal([["Ogre"]], rig.Shown());
        Assert.Single(rig.History);
        Assert.Equal(["Dragon", "Ogre"], rig.Used());
        Assert.False(rig.Session.IsRolling);
    }

    [Fact]
    public async Task Settled_dice_whose_result_cannot_be_recorded_as_used_are_not_shown()
    {
        var rig = new Rig();
        rig.Open(Monsters(rig));
        rig.FailMark = true;
        var roll = rig.Roll();
        await rig.Land(7);
        await roll;

        Assert.Equal("The result could not be recorded as used, so it was not shown: disk full", rig.Session.Message);
        Assert.Empty(rig.Shown());
        Assert.Empty(rig.History);
    }

    // ---- auto-follow -----------------------------------------------------------------------------------------------------

    /// <summary>Area (d6): Lair 3 links on (so a +2 modifier on Area matters), everything else is Empty.</summary>
    private static RollableTable Area(Rig rig, RollableTable to) =>
        rig.Add("Area", "d6", Row(1, 2, "Empty", unique: false), Row(3, 3, "Lair", unique: false, link: to), Row(4, 8, "Empty", unique: false));

    [Fact]
    public async Task An_auto_followed_destination_rerolls_with_dddice_on_its_own_dice_without_the_origins_modifier()
    {
        var rig = new Rig();
        var monsters = rig.Add("Monsters", "d6", Row(1, 2, "Goblin"), Row(3, 4, "Ogre"), Row(5, 6, "Dragon"));
        rig.Open(Area(rig, monsters), autoFollow: true);
        rig.MarkUsed("Ogre");
        rig.Session.ModifierText = "2";
        var roll = rig.Roll();
        await rig.Land(1, 3, 2);                                                     // Area 1+2 = Lair; Monsters 3 (Ogre, used; 5 if +2 were kept), then 2
        await roll;

        Assert.Equal([["Lair"], ["Goblin"]], rig.Shown());
        var monstersRoll = rig.History[1];
        Assert.Equal(("Monsters", 2, 0, 1), (monstersRoll.TableName, monstersRoll.RollValue, monstersRoll.SituationalModifier, monstersRoll.RerolledCount));
        Assert.Equal(["Goblin", "Ogre"], rig.Used());
    }

    [Fact]
    public async Task An_auto_followed_destination_gets_its_own_10_throws_and_then_stops_the_chain()
    {
        var rig = new Rig();
        rig.Open(Area(rig, Monsters(rig)), autoFollow: true);
        rig.MarkUsed("Dragon");
        var roll = rig.Roll();
        await rig.Land(3);
        await rig.Land(Enumerable.Repeat(9, 10).ToArray());
        await roll;

        Assert.Equal(11, rig.Room.Throws.Count);
        Assert.Equal($"Auto-follow stopped. {RollViewModel.AttemptLimitMessage(10)}", rig.Session.Message);
        Assert.Empty(rig.Session.Current.Outcomes);
        Assert.Single(rig.History);
        Assert.Equal(["Dragon"], rig.Used());
    }

    [Fact]
    public async Task Stop_during_a_nested_destinations_reroll_keeps_the_levels_already_accepted_and_late_dice_do_not_resume_the_chain()
    {
        var rig = new Rig();
        var hoard = rig.Add("Hoard", "d6", Row(1, 3, "Gold"), Row(4, 6, "Gems"));
        rig.Open(Area(rig, Monsters(rig, goblinLinksTo: hoard)), autoFollow: true);
        rig.MarkUsed("Gold");
        var roll = rig.Roll();
        await rig.Land(3, 5, 2);                                                     // Area → Lair; Monsters → Goblin; Hoard → Gold (used)
        await rig.InTheAir(4);                                                       // Hoard's reroll
        rig.Session.CancelRoll();
        await roll;

        Assert.Equal("Auto-follow stopped. The roll was cancelled.", rig.Session.Message);
        Assert.Equal(["Area", "Monsters", "Hoard"], rig.Session.Steps.Select(s => s.Title).ToArray());
        Assert.Equal([["Lair"], ["Goblin"]], rig.Shown());
        Assert.Equal(["Goblin", "Gold"], rig.Used());
        Assert.Equal(2, rig.History.Count);

        rig.Room.Land(3, 5);                                                         // the stopped reroll settles late, on Gems
        await Task.Delay(50);
        Assert.Equal(4, rig.Room.Throws.Count);
        Assert.Equal([["Lair"], ["Goblin"]], rig.Shown());
        Assert.Equal(["Goblin", "Gold"], rig.Used());
        Assert.Equal(2, rig.History.Count);
        Assert.False(rig.Session.IsRolling);
    }

    // ---- tables without Unique rows --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_dddice_roll_on_a_table_without_unique_rows_is_one_throw_as_before()
    {
        var rig = new Rig();
        rig.Open(rig.Add("Plain", "2d6", Row(2, 12, "Anything", unique: false)));
        var roll = rig.Roll();
        await rig.Land(8);
        await roll;

        Assert.Single(rig.Room.Throws);
        Assert.Equal([["Anything"]], rig.Shown());
        Assert.Equal(0, rig.History.Single().RerolledCount);
    }

    [Fact]
    public async Task On_a_table_without_unique_rows_dice_settling_after_Stop_cannot_touch_the_next_roll()
    {
        var rig = new Rig();
        rig.Open(rig.Add("Plain", "d6", Row(1, 3, "Low", unique: false), Row(4, 6, "High", unique: false)));
        var stopped = rig.Roll();
        await rig.InTheAir(1);
        rig.Session.CancelRoll();
        await stopped;

        var next = rig.Roll();
        await rig.InTheAir(2);
        rig.Room.Land(0, 6);                                                         // the stopped throw settles late, on High
        await Task.Delay(50);
        Assert.Empty(rig.Shown());
        Assert.Empty(rig.History);
        Assert.False(next.IsCompleted);

        await rig.Land(2);
        await next;
        Assert.Equal([["Low"]], rig.Shown());
        Assert.Equal(2, rig.History.Single().RollValue);
    }
}
