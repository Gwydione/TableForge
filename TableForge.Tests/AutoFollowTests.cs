using System.IO;
using TableForge.Dice;
using TableForge.Domain;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>
/// Auto-follow linked tables through <see cref="RollViewModel"/>: after one of the person's own actions, follow a single resolved
/// link and roll the destination with the ordinary pipeline, and stop (leaving every manual control) whenever a decision or a
/// limit says so. Off by default, where everything is exactly as before.
/// </summary>
public class AutoFollowTests
{
    /// <summary>A little world of linked tables: every table rolls a d6 and has one row per face, unless a test says otherwise.</summary>
    private sealed class World
    {
        public Dictionary<long, RollableTable> Tables { get; } = [];

        public RollableTable Add(string name, bool manual = false, string dice = "d6", int sets = 1)
        {
            var parsed = DiceExpression.Parse(dice);
            var table = new RollableTable
            {
                Id = Tables.Count + 1,
                Name = name,
                Dice = parsed,
                IsManualSelection = manual,
                ResultSets = Enumerable.Range(0, sets).Select(s => new ResultSet
                {
                    Name = sets > 1 ? $"Set {s + 1}" : "",
                    SortOrder = s,
                    Entries = Enumerable.Range(parsed.Min, parsed.Max - parsed.Min + 1)
                        .Select(v => new TableEntry { Min = v, Max = v, Text = $"{name} {v}", SortOrder = v }).ToList(),
                }).ToList(),
            };
            Tables[table.Id] = table;
            return table;
        }

        /// <summary>Row <paramref name="value"/> of <paramref name="from"/> (in every result set given) links to <paramref name="to"/>.</summary>
        public void Link(RollableTable from, int value, RollableTable to, params int[] sets)
        {
            foreach (var s in sets.Length == 0 ? [0] : sets)
                from.ResultSets[s].Entries.Single(e => e.Min == value).LinkedTableId = to.Id;
        }

        public RollableTable? Load(long id) => Tables.GetValueOrDefault(id);
    }

    private sealed record Rig(RollViewModel Session, List<RollSnapshot> History, List<long> Used);

    private static Rig Open(World world, RollableTable start, IDiceProvider dice, bool autoFollow = true, Func<bool>? diceReady = null)
    {
        var history = new List<RollSnapshot>();
        var used = new List<long>();
        var session = new RollViewModel(start, dice, world.Load, used.Add, history.Add, diceReady,
            autoFollow: new AutoFollowSetting(autoFollow));
        return new Rig(session, history, used);
    }

    private static string[] Titles(RollViewModel s) => s.Steps.Select(st => st.Title).ToArray();

    private static string[] Shown(RollViewModel s) =>
        s.Steps.SelectMany(st => st.Outcomes).Select(o => o.Lines[0].Text).ToArray();

    /// <summary>A → B → C → D: each roll of 1 links on.</summary>
    private static (World World, RollableTable A) Chain()
    {
        var w = new World();
        var a = w.Add("A");
        var b = w.Add("B");
        var c = w.Add("C");
        var d = w.Add("D");
        w.Link(a, 1, b);
        w.Link(b, 1, c);
        w.Link(c, 1, d);
        return (w, a);
    }

    // ---- off by default --------------------------------------------------------------------------------------------------

    [Fact]
    public void Off_by_default_a_linked_result_only_offers_its_link_exactly_as_before()
    {
        var (w, a) = Chain();
        var dice = new SequenceDice(1);
        var session = new RollViewModel(a, dice, w.Load);
        Assert.False(session.AutoFollow.IsOn);

        session.RollCommand.Execute(null);

        Assert.Equal(["A"], Titles(session));
        Assert.True(Assert.Single(session.Results).ShowFollow);
        Assert.Single(dice.Requested);
        Assert.Equal("", session.Message);

        session.ManualRollText = "1";                                    // a typed result doesn't follow either
        session.ResolveManualCommand.Execute(null);
        Assert.Equal(["A"], Titles(session));
    }

    [Fact]
    public void The_setting_is_shared_and_remembered_and_a_failure_to_save_it_changes_nothing()
    {
        var saved = new List<bool>();
        var setting = new AutoFollowSetting(save: saved.Add);
        var (w, a) = Chain();
        var one = new RollViewModel(a, new SequenceDice(1), w.Load, autoFollow: setting);
        var two = new RollViewModel(a, new SequenceDice(1), w.Load, autoFollow: setting);

        one.AutoFollow.IsOn = true;
        Assert.True(two.AutoFollow.IsOn);
        one.AutoFollow.IsOn = true;                                       // unchanged: not saved again
        two.AutoFollow.IsOn = false;
        Assert.Equal([true, false], saved);

        var failing = new AutoFollowSetting(save: _ => throw new IOException("disk full")) { IsOn = true };
        Assert.True(failing.IsOn);
    }

    // ---- following a chain -----------------------------------------------------------------------------------------------

    [Fact]
    public void A_chain_is_followed_and_each_table_rolled_in_order_like_manual_Open_and_Roll()
    {
        var (w, a) = Chain();
        var dice = new SequenceDice(1, 1, 1, 4);
        var rig = Open(w, a, dice);

        rig.Session.RollCommand.Execute(null);

        Assert.Equal(["A", "B", "C", "D"], Titles(rig.Session));
        Assert.Equal(["A 1", "B 1", "C 1", "D 4"], Shown(rig.Session));
        Assert.Equal(["d6", "d6", "d6", "d6"], dice.Requested);
        Assert.Equal(["A", "B", "C", "D"], rig.History.Select(h => h.TableName).ToArray());     // one ordinary record per roll
        Assert.Equal([2L, 3L, 4L], rig.Used);                                                    // each followed table is used, as Open does
        Assert.Equal("", rig.Session.Message);
        Assert.False(rig.Session.IsRolling);
        Assert.Equal("", rig.Session.RollStatus);

        // Compact parent references on the steps auto-follow opened; never the parent's whole result.
        Assert.Equal(["", "Auto-followed from A (1)", "Auto-followed from B (1)", "Auto-followed from C (1)"],
            rig.Session.Steps.Select(s => s.AutoFollowedFrom).ToArray());
        Assert.False(rig.Session.Steps[0].IsAutoFollowed);
        Assert.True(rig.Session.Steps[1].IsAutoFollowed);

        // The trail keeps every parent result; only the last is still active, exactly as after manual Open.
        Assert.All(rig.Session.Steps.Take(3), s => Assert.True(Assert.Single(s.Outcomes).Lines[0].ShowLinkedNote));
        Assert.Equal("D", rig.Session.Title);
    }

    [Fact]
    public void A_result_without_a_link_ends_the_chain_quietly()
    {
        var (w, a) = Chain();
        var rig = Open(w, a, new SequenceDice(3));

        rig.Session.RollCommand.Execute(null);

        Assert.Equal(["A"], Titles(rig.Session));
        Assert.Equal("", rig.Session.Message);
    }

    [Fact]
    public void Every_automatic_step_starts_like_a_followed_table_modifier_and_roll_count_reset_and_its_own_clamp_used()
    {
        var w = new World();
        var a = w.Add("A");
        var b = w.Add("B", dice: "d4");
        b.ResultSets[0].Entries.RemoveAt(0);                    // B covers only 2-4 ...
        b.ClampResultsToRange = true;                           // ... and clamps
        w.Link(a, 1, b);
        var rig = Open(w, a, new SequenceDice(-1, 1));          // A: -1 +2 situational = 1; B: 1, clamped to 2
        rig.Session.ModifierText = "+2";

        rig.Session.RollCommand.Execute(null);

        Assert.Equal(["A 1", "B 2"], Shown(rig.Session));
        Assert.Equal((2, 0), (rig.History[0].SituationalModifier, rig.History[1].SituationalModifier)); // only the person's roll used it
        Assert.Equal(2, rig.History[1].ClampedValue);
        Assert.Equal(("0", 1), (rig.Session.ModifierText, rig.Session.RollCount));
    }

    // ---- Manual-selection tables -----------------------------------------------------------------------------------------

    /// <summary>Area Type 5 → Current Depth selector (Manual-selection) → each depth's encounters.</summary>
    private static (World World, RollableTable Area, RollableTable Selector) Depths()
    {
        var w = new World();
        var area = w.Add("Area Type");
        var selector = w.Add("Current Depth — Encounter Selector", manual: true, dice: "d4");
        var gutters = w.Add("Gutters Encounters");
        var bogtown = w.Add("Bogtown Encounters");
        w.Link(area, 5, selector);
        w.Link(selector, 1, gutters);
        w.Link(selector, 4, bogtown);
        return (w, area, selector);
    }

    [Fact]
    public void A_Manual_selection_destination_is_opened_but_never_rolled_and_waits_for_the_choice()
    {
        var (w, area, _) = Depths();
        var dice = new SequenceDice(5);
        var rig = Open(w, area, dice);

        rig.Session.RollCommand.Execute(null);

        Assert.Equal(["Area Type", "Current Depth — Encounter Selector"], Titles(rig.Session));
        Assert.Empty(rig.Session.Current.Outcomes);                                  // not rolled: no depth was chosen at random
        Assert.Single(dice.Requested);
        Assert.Single(rig.History);
        Assert.True(rig.Session.IsManualSelection);
        Assert.True(rig.Session.Current.IsManualSelection);
        Assert.Equal("Auto-followed from Area Type (5)", rig.Session.Current.AutoFollowedFrom);
        Assert.Equal("", rig.Session.Message);
        Assert.False(rig.Session.IsRolling);
        Assert.True(rig.Session.RollCommand.CanExecute(null));                       // Roll stays available
    }

    [Fact]
    public void Choosing_a_result_on_the_Manual_selection_table_continues_the_chain()
    {
        var (w, area, _) = Depths();
        var dice = new SequenceDice(5, 2);
        var rig = Open(w, area, dice);
        rig.Session.RollCommand.Execute(null);

        rig.Session.ManualRollText = "4";                                           // the player's depth: Bogtown
        rig.Session.ResolveManualCommand.Execute(null);

        Assert.Equal(["Area Type", "Current Depth — Encounter Selector", "Bogtown Encounters"], Titles(rig.Session));
        Assert.Equal(["Area Type 5", "Current Depth — Encounter Selector 4", "Bogtown Encounters 2"], Shown(rig.Session));
        Assert.Equal(["d6", "d6"], dice.Requested);                                  // the selector itself was never rolled
        Assert.Equal(["Area Type", "Current Depth — Encounter Selector", "Bogtown Encounters"], rig.History.Select(h => h.TableName).ToArray());
        Assert.Equal("Auto-followed from Current Depth — Encounter Selector (4)", rig.Session.Current.AutoFollowedFrom);
        Assert.False(rig.Session.IsManualSelection);
    }

    [Fact]
    public void Starting_from_a_Manual_selection_table_a_chosen_result_is_followed_and_rolled()
    {
        var (w, _, selector) = Depths();
        var rig = Open(w, selector, new SequenceDice(6));
        Assert.True(rig.Session.IsManualSelection);

        rig.Session.ManualRollText = "1";
        rig.Session.ResolveManualCommand.Execute(null);

        Assert.Equal(["Current Depth — Encounter Selector 1", "Gutters Encounters 6"], Shown(rig.Session));
    }

    [Fact]
    public void A_Roll_on_a_Manual_selection_table_shows_its_result_but_never_follows_from_it()
    {
        var (w, _, selector) = Depths();
        var dice = new SequenceDice(4);
        var rig = Open(w, selector, dice);

        rig.Session.RollCommand.Execute(null);

        Assert.Equal(["Current Depth — Encounter Selector"], Titles(rig.Session));
        Assert.True(Assert.Single(rig.Session.Results).ShowFollow);                   // the link stays the person's to follow
        Assert.Single(dice.Requested);
        Assert.Equal("", rig.Session.Message);
    }

    // ---- several, missing or unresolved links ----------------------------------------------------------------------------

    [Fact]
    public void More_than_one_linked_result_stops_and_every_link_stays_to_choose_from()
    {
        var w = new World();
        var a = w.Add("A", sets: 2);
        var b = w.Add("B");
        var c = w.Add("C");
        w.Link(a, 1, b, 0);
        w.Link(a, 1, c, 1);
        var dice = new SequenceDice(1);
        var rig = Open(w, a, dice);

        rig.Session.RollCommand.Execute(null);

        Assert.Equal(["A"], Titles(rig.Session));
        Assert.Equal("Auto-follow stopped: this result has more than one link. Choose one below.", rig.Session.Message);
        Assert.All(rig.Session.Results, l => Assert.True(l.ShowFollow));

        // Open (by hand) still only opens, with the setting on: it never rolls.
        rig.Session.Results[1].FollowCommand!.Execute(null);
        Assert.Equal(["A", "C"], Titles(rig.Session));
        Assert.Empty(rig.Session.Current.Outcomes);
        Assert.Single(dice.Requested);
        Assert.Equal("", rig.Session.Current.AutoFollowedFrom);
    }

    [Fact]
    public void One_linked_result_set_among_several_is_followed()
    {
        var w = new World();
        var a = w.Add("A", sets: 2);
        var b = w.Add("B");
        w.Link(a, 1, b, 1);
        var rig = Open(w, a, new SequenceDice(1, 3));

        rig.Session.RollCommand.Execute(null);

        Assert.Equal(["A", "B"], Titles(rig.Session));
        Assert.Equal("Auto-followed from A (1)", rig.Session.Current.AutoFollowedFrom);
    }

    [Fact]
    public void A_missing_destination_or_an_unresolved_link_stops_with_the_usual_warning_in_place()
    {
        var w = new World();
        var a = w.Add("A");
        a.ResultSets[0].Entries[0].LinkedTableId = 99;                       // a table that no longer loads
        a.ResultSets[0].Entries[1].UnresolvedLinkName = "Treasure";          // a link with no table yet
        var rig = Open(w, a, new SequenceDice(1, 2));

        rig.Session.RollCommand.Execute(null);
        Assert.Equal(["A"], Titles(rig.Session));
        Assert.True(Assert.Single(rig.Session.Results).ShowMissing);
        Assert.Equal("Auto-follow stopped: the linked table isn't available.", rig.Session.Message);

        rig.Session.RollCommand.Execute(null);
        Assert.Equal(["A"], Titles(rig.Session));
        Assert.True(Assert.Single(rig.Session.Results).ShowUnresolved);
        Assert.Equal("Auto-follow stopped: the linked table isn't available.", rig.Session.Message);
    }

    [Fact]
    public void Several_rolls_at_once_never_auto_follow_and_say_so_only_when_there_is_a_link()
    {
        var (w, a) = Chain();
        var dice = new SequenceDice(1, 3, 2, 3);
        var rig = Open(w, a, dice);
        rig.Session.RollCount = 2;

        rig.Session.RollCommand.Execute(null);
        Assert.Equal(["A"], Titles(rig.Session));
        Assert.Equal(2, dice.Requested.Count);
        Assert.Equal("Auto-follow runs after a single roll only. Choose a link below.", rig.Session.Message);
        Assert.True(rig.Session.LatestRolls[0].Lines[0].ShowFollow);

        rig.Session.RollCommand.Execute(null);                               // 2 and 3: no link anywhere
        Assert.Equal("", rig.Session.Message);
    }

    // ---- limits: self-links, loops, long chains --------------------------------------------------------------------------

    [Fact]
    public void A_self_linked_reroll_is_rolled_again_at_most_twice_then_stops_with_the_link_still_offered()
    {
        var w = new World();
        var poi = w.Add("Pale Arroyo POI");
        w.Link(poi, 5, poi);                                                 // "Reroll on Pale Arroyo POI"
        var dice = new SequenceDice(5);                                     // it keeps landing on 5
        var rig = Open(w, poi, dice);

        rig.Session.RollCommand.Execute(null);

        Assert.Equal(3, dice.Requested.Count);                              // the person's roll + 2 automatic rerolls
        Assert.Equal(3, rig.Session.Steps.Count);
        Assert.Equal(3, rig.History.Count);
        Assert.Equal($"Auto-follow stopped: Pale Arroyo POI was already rolled automatically {RollViewModel.MaxAutoRollsPerTable} times. Continue with the link below.",
            rig.Session.Message);
        Assert.True(Assert.Single(rig.Session.Results).ShowFollow);

        // Deliberate intervention starts a new chain with fresh counts.
        rig.Session.RollCommand.Execute(null);
        Assert.Equal(6, dice.Requested.Count);
        Assert.Equal(5, rig.Session.Steps.Count);
    }

    [Fact]
    public void A_self_linked_reroll_that_lands_elsewhere_simply_ends()
    {
        var w = new World();
        var poi = w.Add("Pale Arroyo POI");
        w.Link(poi, 5, poi);
        var rig = Open(w, poi, new SequenceDice(5, 2));

        rig.Session.RollCommand.Execute(null);

        Assert.Equal(["Pale Arroyo POI 5", "Pale Arroyo POI 2"], Shown(rig.Session));
        Assert.Equal("", rig.Session.Message);
    }

    [Fact]
    public void A_loop_between_two_tables_ends_within_the_per_table_limit()
    {
        var w = new World();
        var a = w.Add("A");
        var b = w.Add("B");
        w.Link(a, 1, b);
        w.Link(b, 1, a);
        var dice = new SequenceDice(1);
        var rig = Open(w, a, dice);

        rig.Session.RollCommand.Execute(null);

        // A (person) → B → A → B → A, then B would be rolled automatically a third time.
        Assert.Equal(["A", "B", "A", "B", "A"], Titles(rig.Session));
        Assert.Contains("B was already rolled automatically 2 times", rig.Session.Message);
    }

    [Fact]
    public void A_long_chain_stops_after_the_follow_limit_and_can_be_continued_by_hand()
    {
        var w = new World();
        var tables = Enumerable.Range(0, 12).Select(i => w.Add($"T{i}")).ToList();
        for (var i = 0; i < tables.Count - 1; i++) w.Link(tables[i], 1, tables[i + 1]);
        var dice = new SequenceDice(1);
        var rig = Open(w, tables[0], dice);

        rig.Session.RollCommand.Execute(null);

        Assert.Equal(1 + RollViewModel.MaxAutoFollows, dice.Requested.Count);
        Assert.Equal("T8", rig.Session.Title);
        Assert.Equal($"Auto-follow stopped after {RollViewModel.MaxAutoFollows} linked tables. Continue with the link below.", rig.Session.Message);
        var line = Assert.Single(rig.Session.Results);
        Assert.True(line.ShowFollow);

        line.FollowCommand!.Execute(null);                                   // by hand: opens T9, does not roll it
        Assert.Equal("T9", rig.Session.Title);
        Assert.Empty(rig.Session.Current.Outcomes);
        rig.Session.RollCommand.Execute(null);                               // and Roll there starts a fresh chain
        Assert.Equal("T11", rig.Session.Title);
    }

    // ---- failures, readiness, the setting turned off ---------------------------------------------------------------------

    [Fact]
    public void A_failed_automatic_roll_stops_with_the_destination_open_and_unrolled()
    {
        var (w, a) = Chain();
        var dice = new SequenceDice(1) { FailOnCall = 2 };
        var rig = Open(w, a, dice);

        rig.Session.RollCommand.Execute(null);

        Assert.Equal(["A", "B"], Titles(rig.Session));
        Assert.Empty(rig.Session.Current.Outcomes);
        Assert.Single(rig.History);
        Assert.Equal("Auto-follow stopped. dddice is temporarily unavailable. Try again shortly.", rig.Session.Message);
        Assert.False(rig.Session.IsRolling);
        Assert.True(rig.Session.RollCommand.CanExecute(null));
    }

    [Fact]
    public void Dice_that_are_not_ready_stop_the_chain_before_anything_is_opened()
    {
        var (w, a) = Chain();
        var dice = new SequenceDice(1);
        var rig = Open(w, a, dice, diceReady: () => dice.Requested.Count == 0); // ready for the first roll only

        rig.Session.RollCommand.Execute(null);

        Assert.Equal(["A"], Titles(rig.Session));
        Assert.True(Assert.Single(rig.Session.Results).ShowFollow);
        Assert.Equal("Auto-follow stopped: the dice aren't ready. Continue with the link below.", rig.Session.Message);
    }

    [Fact]
    public void Inline_dice_on_an_automatic_step_behave_exactly_as_on_any_current_result()
    {
        var (w, a) = Chain();
        w.Tables[2].ResultSets[0].Entries[2].Text = "Gain +1d4 Armor";      // B 3
        var dice = new SequenceDice(1, 3, 2);
        var rig = Open(w, a, dice);

        rig.Session.RollCommand.Execute(null);

        var line = Assert.Single(rig.Session.Results);
        Assert.False(line.HasResolved);                                      // never rolled by itself
        var action = Assert.Single(line.InlineActions);
        action.RollCommand!.Execute(null);
        Assert.Equal("Gain +2 Armor", line.ResolvedText);
        Assert.Equal("Gain +1d4 Armor", line.Text);
        Assert.Equal(2, rig.History.Count);                                  // inline rolls are still never recorded
    }

    // ---- cancellation with the real dddice provider (a scripted roller underneath) ---------------------------------------

    private static (Rig Rig, FakeDddiceRoller Roller) OpenDddice(World world, RollableTable start)
    {
        var roller = new FakeDddiceRoller();
        return (Open(world, start, new DddiceDiceProvider(roller)), roller);
    }

    [Fact]
    public async Task With_dddice_each_step_waits_for_its_dice_and_Stop_cancels_the_chain_mid_roll()
    {
        var (w, a) = Chain();
        var (rig, roller) = OpenDddice(w, a);
        var session = rig.Session;

        session.RollCommand.Execute(null);
        Assert.True(roller.IsRollPending);
        roller.Settle(("d6", 1));                                            // A settles on 1 → B is opened and thrown
        await Wait.Until(() => roller.Requests.Count == 2 && roller.IsRollPending, "B's dice in the air");

        Assert.Equal(["A", "B"], Titles(session));
        Assert.True(session.IsRolling);
        Assert.Equal("Auto-following… step 1", session.RollStatus);
        Assert.True(session.StopCommand.CanExecute(null));
        Assert.False(session.RollCommand.CanExecute(null));
        Assert.False(session.ResolveManualCommand.CanExecute(null));

        session.StopCommand.Execute(null);
        await session.RollTask;

        Assert.True(roller.LastRollToken.IsCancellationRequested);
        Assert.Equal(["A", "B"], Titles(session));
        Assert.Empty(session.Current.Outcomes);                              // B stays open, unrolled
        Assert.Single(rig.History);
        Assert.Equal("Auto-follow stopped. The roll was cancelled.", session.Message);
        Assert.False(session.IsRolling);
        Assert.Equal("", session.RollStatus);
        Assert.False(session.StopCommand.CanExecute(null));
        Assert.True(session.RollCommand.CanExecute(null));
    }

    [Fact]
    public async Task With_dddice_leaving_the_table_cancels_the_chain_like_Stop()
    {
        var (w, a) = Chain();
        var (rig, roller) = OpenDddice(w, a);

        rig.Session.RollCommand.Execute(null);
        roller.Settle(("d6", 1));
        await Wait.Until(() => roller.Requests.Count == 2 && roller.IsRollPending, "B's dice in the air");
        rig.Session.CancelRoll();                                            // what MainViewModel does when the screen is left
        await rig.Session.RollTask;

        Assert.Equal(2, roller.Requests.Count);
        Assert.Single(rig.History);
        Assert.False(rig.Session.IsRolling);
    }

    [Fact]
    public async Task With_dddice_a_complete_chain_settles_step_by_step()
    {
        var (w, a) = Chain();
        var (rig, roller) = OpenDddice(w, a);

        rig.Session.RollCommand.Execute(null);
        roller.Settle(("d6", 1));
        await Wait.Until(() => roller.Requests.Count == 2 && roller.IsRollPending, "B");
        roller.Settle(("d6", 1));
        await Wait.Until(() => roller.Requests.Count == 3 && roller.IsRollPending, "C");
        Assert.Equal("Auto-following… step 2", rig.Session.RollStatus);
        roller.Settle(("d6", 6));
        await rig.Session.RollTask;

        Assert.Equal(["A 1", "B 1", "C 6"], Shown(rig.Session));
        Assert.Equal(3, rig.History.Count);
        Assert.Equal("", rig.Session.Message);
        Assert.False(rig.Session.IsRolling);
    }

    [Fact]
    public async Task With_dddice_turning_the_setting_off_mid_chain_stops_after_the_roll_in_the_air()
    {
        var (w, a) = Chain();
        var (rig, roller) = OpenDddice(w, a);

        rig.Session.RollCommand.Execute(null);
        roller.Settle(("d6", 1));
        await Wait.Until(() => roller.Requests.Count == 2 && roller.IsRollPending, "B");
        rig.Session.AutoFollow.IsOn = false;
        roller.Settle(("d6", 1));                                            // B lands on its link to C ...
        await rig.Session.RollTask;

        Assert.Equal(["A 1", "B 1"], Shown(rig.Session));                    // ... which is not followed
        Assert.Equal("Auto-follow stopped.", rig.Session.Message);
        Assert.True(Assert.Single(rig.Session.Results).ShowFollow);
    }

    [Fact]
    public async Task Stop_also_cancels_an_ordinary_dddice_roll_with_the_setting_off()
    {
        var (w, a) = Chain();
        var roller = new FakeDddiceRoller();
        var history = new List<RollSnapshot>();
        var session = new RollViewModel(a, new DddiceDiceProvider(roller), w.Load, rolled: history.Add);
        Assert.False(session.StopCommand.CanExecute(null));

        session.RollCommand.Execute(null);
        Assert.True(session.StopCommand.CanExecute(null));
        session.StopCommand.Execute(null);
        await session.RollTask;

        Assert.True(roller.LastRollToken.IsCancellationRequested);
        Assert.Empty(session.Results);
        Assert.Empty(history);
        Assert.Equal("The roll was cancelled.", session.Message);
        Assert.True(session.RollCommand.CanExecute(null));
    }

    // ---- the built-in provider -------------------------------------------------------------------------------------------

    [Fact]
    public void With_built_in_dice_a_chain_completes_at_once_within_the_limits()
    {
        var w = new World();
        var tables = Enumerable.Range(0, 4).Select(i => w.Add($"T{i}")).ToList();
        foreach (var t in tables)                                            // every row links onwards (or back to itself at the end)
            for (var v = 1; v <= 6; v++) w.Link(t, v, tables[(int)Math.Min(t.Id, tables.Count - 1)]);
        var rig = Open(w, tables[0], new BuiltInDiceProvider(new Random(7)));

        rig.Session.RollCommand.Execute(null);

        Assert.True(rig.Session.RollTask.IsCompleted);
        Assert.False(rig.Session.IsRolling);
        Assert.Equal(["T0", "T1", "T2", "T3", "T3"], Titles(rig.Session)); // T3 links to itself: rolled automatically twice in all
        Assert.Equal(5, rig.History.Count);
        Assert.Contains("T3 was already rolled automatically 2 times", rig.Session.Message);
    }
}
