using TableForge.Data;
using TableForge.Domain;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>The in-memory linked-roll trail: links are followed only by the user and never roll anything themselves.</summary>
public class RollSessionTests
{
    private static (RollViewModel Session, FixedDice Dice) OpenScavenging(TempDatabase temp, int roll, Action<AppDatabase>? tweak = null)
    {
        var db = temp.Open();
        Fixtures.SeedScavenging(db, db.CreateCollection("C").Id);
        tweak?.Invoke(db);
        var id = db.GetTableSummaries(1).Single(t => t.Name == "Scavenging").Id;
        var dice = new FixedDice(roll);
        return (new RollViewModel(db.LoadTable(id)!, dice, db.LoadTable), dice);
    }

    private static ResultLineViewModel OnlyLine(RollViewModel s) => Assert.Single(s.Results);

    [Fact]
    public void Following_a_link_adds_a_step_and_keeps_the_parent_result_visible_without_rolling()
    {
        using var temp = new TempDatabase();
        var (session, dice) = OpenScavenging(temp, 6);

        session.RollCommand.Execute(null);
        var line = OnlyLine(session);
        Assert.Equal("1x Scavenged Item", line.Text);
        Assert.Equal(LinkState.Resolved, line.Link);
        Assert.Equal("Scavenged Items", line.LinkName);
        Assert.Equal("Open Scavenged Items", line.FollowLabel);
        Assert.True(line.ShowFollow);
        Assert.Single(session.Steps);

        line.FollowCommand!.Execute(null);

        Assert.Equal(2, session.Steps.Count);
        Assert.Equal(1, dice.Calls);                                     // following did not roll
        Assert.Equal("Scavenged Items", session.Current.Title);
        Assert.Equal("Scavenged Items", session.Title);
        Assert.Equal("d20 · legal rolls 1–20", session.DiceInfo);
        Assert.Empty(session.Current.Outcomes);                          // the child is not rolled yet
        Assert.Equal("", session.RollDisplay);
        Assert.Empty(session.Results);

        // Prior context is still there, unchanged, but its link is no longer an active action.
        var parent = session.Steps[0];
        Assert.Equal("Scavenging", parent.Title);
        var kept = Assert.Single(Assert.Single(parent.Outcomes).Lines);
        Assert.Equal("1x Scavenged Item", kept.Text);
        Assert.Equal("Rolled 6", parent.Outcomes[0].Display);
        Assert.False(kept.ShowFollow);
        Assert.True(kept.ShowLinkedNote);
        Assert.False(kept.FollowCommand!.CanExecute(null));
    }

    [Fact]
    public void Child_is_rolled_only_when_the_user_rolls_it()
    {
        using var temp = new TempDatabase();
        var (session, dice) = OpenScavenging(temp, 6);
        session.RollCommand.Execute(null);
        OnlyLine(session).FollowCommand!.Execute(null);

        dice.Value = 12;
        session.RollCommand.Execute(null);

        Assert.Equal(2, dice.Calls);
        Assert.Equal("Rolled 12", session.RollDisplay);
        Assert.Equal("D4 rations", OnlyLine(session).Text);
        Assert.Equal(["Scavenging", "Scavenged Items"], session.Steps.Select(s => s.Title).ToArray());
        Assert.Equal("Rolled 6", session.Steps[0].Outcomes[0].Display); // parent context intact
    }

    [Fact]
    public void Repeated_rolls_stay_inside_the_current_step()
    {
        using var temp = new TempDatabase();
        var (session, dice) = OpenScavenging(temp, 9);
        session.RollCommand.Execute(null);
        OnlyLine(session).FollowCommand!.Execute(null);

        foreach (var value in new[] { 3, 12, 20 })
        {
            dice.Value = value;
            session.RollCommand.Execute(null);
        }

        Assert.Equal(2, session.Steps.Count);                                        // no new level per repeat
        Assert.Equal(["Rolled 3", "Rolled 12", "Rolled 20"], session.Current.Outcomes.Select(o => o.Display).ToArray());
        Assert.Equal(["Rusty nails", "D4 rations", "Silver coin"], session.Current.Outcomes.Select(o => o.Lines[0].Text).ToArray());
        Assert.Single(session.Steps[0].Outcomes);                                    // parent unchanged
        Assert.Equal("Rolled 20", session.RollDisplay);                              // "latest" reflects the newest roll
    }

    [Fact]
    public void Two_x_in_the_text_does_not_roll_twice()
    {
        using var temp = new TempDatabase();
        var (session, dice) = OpenScavenging(temp, 9); // "2x Scavenged Items"

        session.RollCommand.Execute(null);
        var line = OnlyLine(session);
        Assert.Equal("2x Scavenged Items", line.Text);
        Assert.Equal(1, dice.Calls);

        line.FollowCommand!.Execute(null);
        Assert.Equal(1, dice.Calls);
        Assert.Empty(session.Current.Outcomes);

        // The user can roll the child as many times as they choose: here, once.
        session.RollCommand.Execute(null);
        Assert.Equal(2, dice.Calls);
        Assert.Single(session.Current.Outcomes);
    }

    [Fact]
    public void A_superseded_roll_no_longer_offers_its_link()
    {
        using var temp = new TempDatabase();
        var (session, dice) = OpenScavenging(temp, 6);
        session.RollCommand.Execute(null);
        var first = OnlyLine(session);

        dice.Value = 7;
        session.RollCommand.Execute(null);

        Assert.False(first.ShowFollow);
        Assert.True(OnlyLine(session).ShowFollow);
        Assert.Equal(2, session.Current.Outcomes.Count); // both rolls kept in the step
    }

    [Fact]
    public void Following_a_link_twice_from_the_same_result_is_ignored()
    {
        using var temp = new TempDatabase();
        var (session, _) = OpenScavenging(temp, 6);
        session.RollCommand.Execute(null);
        var line = OnlyLine(session);

        line.FollowCommand!.Execute(null);
        line.FollowCommand.Execute(null);

        Assert.Equal(2, session.Steps.Count);
    }

    [Fact]
    public void Links_can_chain_when_the_user_keeps_following_them()
    {
        using var temp = new TempDatabase();
        var (session, dice) = OpenScavenging(temp, 6, db =>
        {
            // Make the 20 on Scavenged Items link onward to Room Features.
            var rooms = db.SaveTable(Fixtures.RoomFeatures(1));
            var items = db.LoadTable(db.GetTableSummaries(1).Single(t => t.Name == "Scavenged Items").Id)!;
            items.ResultSets[0].Entries[5].LinkedTableId = rooms.Id;
            db.SaveTable(items);
        });

        session.RollCommand.Execute(null);
        OnlyLine(session).FollowCommand!.Execute(null);
        dice.Value = 20;
        session.RollCommand.Execute(null);
        OnlyLine(session).FollowCommand!.Execute(null);

        Assert.Equal(["Scavenging", "Scavenged Items", "Room Features"], session.Steps.Select(s => s.Title).ToArray());
        Assert.Equal(2, dice.Calls);
        Assert.Empty(session.Current.Outcomes);
    }

    [Fact]
    public void Unresolved_link_is_shown_explicitly_and_cannot_be_followed()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var table = db.SaveTable(Fixtures.Scavenging(collection.Id, null, unresolvedName: "Scavenged Items"));
        var dice = new FixedDice(6);
        var session = new RollViewModel(db.LoadTable(table.Id)!, dice, db.LoadTable);

        session.RollCommand.Execute(null);

        var line = OnlyLine(session);
        Assert.Equal("1x Scavenged Item", line.Text);
        Assert.Equal(LinkState.Unresolved, line.Link);
        Assert.Equal("Scavenged Items", line.LinkName);
        Assert.True(line.ShowUnresolved);
        Assert.False(line.ShowFollow);
        Assert.Null(line.FollowCommand);
        Assert.Contains("Unresolved link to “Scavenged Items”", line.UnresolvedNote);
        Assert.Single(session.Steps);                          // nothing guessed, nothing followed
        Assert.DoesNotContain(db.GetTableSummaries(collection.Id), t => t.Name == "Scavenged Items"); // nothing auto-created
    }

    [Fact]
    public void Link_to_a_table_that_no_longer_loads_is_reported_not_thrown()
    {
        var table = Fixtures.Scavenging(1, itemsTableId: 42);
        var session = new RollViewModel(table, new FixedDice(6), loadTable: _ => null);

        session.RollCommand.Execute(null);

        var line = OnlyLine(session);
        Assert.Equal(LinkState.Missing, line.Link);
        Assert.True(line.ShowMissing);
        Assert.Null(line.FollowCommand);
    }

    [Fact]
    public void Result_without_a_link_shows_no_link_ui()
    {
        using var temp = new TempDatabase();
        var (session, _) = OpenScavenging(temp, 3); // "Nothing useful"

        session.RollCommand.Execute(null);

        var line = OnlyLine(session);
        Assert.Equal(LinkState.None, line.Link);
        Assert.False(line.ShowFollow || line.ShowUnresolved || line.ShowMissing || line.ShowLinkedNote);
    }

    [Fact]
    public void After_following_manual_rolls_use_the_child_tables_legal_range()
    {
        using var temp = new TempDatabase();
        var (session, _) = OpenScavenging(temp, 6);
        session.RollCommand.Execute(null);

        session.ManualRollText = "20";
        session.ResolveManualCommand.Execute(null);
        Assert.Equal("Enter a whole number from 2 to 12.", session.Message); // 2d6 rejects 20

        OnlyLine(session).FollowCommand!.Execute(null);
        Assert.Equal("", session.Message);
        Assert.Equal("", session.ManualRollText);

        session.ManualRollText = "20";
        session.ResolveManualCommand.Execute(null);
        Assert.Equal("Silver coin", OnlyLine(session).Text);
        Assert.Equal(2, session.Steps.Count);
    }

    [Fact]
    public void Renaming_the_destination_is_reflected_in_the_link_label_without_breaking_it()
    {
        using var temp = new TempDatabase();
        var (session, dice) = OpenScavenging(temp, 6, db =>
        {
            var items = db.LoadTable(db.GetTableSummaries(1).Single(t => t.Name == "Scavenged Items").Id)!;
            items.Name = "Salvage";
            db.SaveTable(items);
        });

        session.RollCommand.Execute(null);
        var line = OnlyLine(session);
        Assert.Equal("Open Salvage", line.FollowLabel);
        line.FollowCommand!.Execute(null);
        Assert.Equal("Salvage", session.Current.Title);
    }

    // ---- multiple result sets at runtime ----------------------------------------------------

    [Fact]
    public void One_roll_shows_the_number_once_and_all_result_set_outputs()
    {
        var session = new RollViewModel(Fixtures.RoomFeatures(), new FixedDice(68));

        session.RollCommand.Execute(null);

        Assert.Equal("Rolled 68", session.RollDisplay);
        Assert.Single(session.Current.Outcomes);
        Assert.Equal(
            [("Ambient", "Smell of burning flesh"), ("Noise", "Hissing"), ("General Feature", "Grated floors reveal dozens of people below")],
            session.Results.Select(r => (r.Heading, r.Text)).ToArray());

        // The reference listing marks the matching row in each set.
        Assert.Equal(["Smell of burning flesh", "Hissing", "Grated floors reveal dozens of people below"],
            session.ResultSets.Select(s => Assert.Single(s.Entries, e => e.IsMatched).Text).ToArray());
    }

    [Fact]
    public void D100_roll_of_100_shows_as_00_for_every_set()
    {
        var session = new RollViewModel(Fixtures.RoomFeatures(), new FixedDice(100));

        session.RollCommand.Execute(null);

        Assert.Equal("Rolled 00 (numeric 100)", session.RollDisplay);
        Assert.Equal(["Heavy incense", "Low chanting", "Carved pillars"], session.Results.Select(r => r.Text).ToArray());
    }

    [Fact]
    public void A_hole_in_one_set_shows_no_match_for_that_set_only()
    {
        var table = Fixtures.RoomFeatures();
        table.ResultSets[1].Entries.RemoveAll(e => e.Text == "Hissing");
        var session = new RollViewModel(table, new FixedDice(68));

        session.RollCommand.Execute(null);

        Assert.Equal(["Smell of burning flesh", "No entry covers 68.", "Grated floors reveal dozens of people below"],
            session.Results.Select(r => r.Text).ToArray());
        Assert.Equal([false, true, false], session.Results.Select(r => r.IsProblem).ToArray());
    }
}
