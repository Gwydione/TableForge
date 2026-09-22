using Microsoft.Data.Sqlite;
using TableForge.Data;
using TableForge.Dice;
using TableForge.Domain;
using TableForge.Import;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>A random source that hands out chosen die faces in order, so rolls are deterministic.</summary>
internal sealed class SequenceRandom(params int[] faces) : Random
{
    private int _next;
    public int Calls => _next;
    public override int Next(int minValue, int maxValue)
    {
        var face = faces[_next++];
        Assert.InRange(face, minValue, maxValue - 1); // a face outside the die would be a bad test
        return face;
    }
}

/// <summary>The one deliberately narrow dice extension: NdM with a single fixed +N or -N.</summary>
public class DiceModifierTests
{
    // ---- syntax ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("d6", 1, 6, 0)]
    [InlineData("D20", 1, 20, 0)]
    [InlineData("2d6", 2, 6, 0)]
    [InlineData("2D6+1", 2, 6, 1)]
    [InlineData("d20-2", 1, 20, -2)]
    [InlineData("3d8+4", 3, 8, 4)]
    [InlineData("d100-10", 1, 100, -10)]
    [InlineData("d20+2", 1, 20, 2)]
    [InlineData("D100+5", 1, 100, 5)]
    [InlineData("  2d6+1  ", 2, 6, 1)]
    [InlineData("2d6+0", 2, 6, 0)]
    [InlineData("d6+1000", 1, 6, 1000)]
    public void Accepts_NdM_with_at_most_one_fixed_modifier(string text, int count, int sides, int modifier)
    {
        Assert.True(DiceExpression.TryParse(text, out var dice));
        Assert.Equal(new DiceExpression(count, sides, modifier), dice);
    }

    [Theory]
    [InlineData("d20−2", -2)]   // U+2212 minus sign, as a PDF may supply
    [InlineData("d20–2", -2)]   // en dash
    public void A_real_minus_sign_or_en_dash_counts_as_minus(string text, int modifier)
    {
        Assert.True(DiceExpression.TryParse(text, out var dice));
        Assert.Equal(modifier, dice.Modifier);
        Assert.Equal("d20-2", dice.ToString());                    // normalized to plain notation
    }

    [Theory]
    [InlineData("2d6+1d4")]
    [InlineData("2d6+1+2")]
    [InlineData("2d6-1-2")]
    [InlineData("4d6kh3")]
    [InlineData("2d20dl1")]
    [InlineData("d20 advantage")]
    [InlineData("2d6*")]
    [InlineData("2d6*2")]
    [InlineData("2d6/2")]
    [InlineData("2d6+")]
    [InlineData("2d6-")]
    [InlineData("d20+-2")]
    [InlineData("d20--2")]
    [InlineData("+1")]
    [InlineData("2d6 + 1")]
    [InlineData("2d6+1001")]
    [InlineData("2d6+١")]   // a non-ASCII digit
    [InlineData("d1+1")]
    [InlineData("0d6+1")]
    public void Rejects_everything_else_without_partly_parsing_it(string text)
    {
        Assert.False(DiceExpression.TryParse(text, out var dice));
        Assert.Equal(default, dice);
        Assert.Throws<FormatException>(() => DiceExpression.Parse(text));
    }

    [Theory]
    [InlineData("d6", "d6")]
    [InlineData("1d6", "d6")]
    [InlineData("2D6+1", "2d6+1")]
    [InlineData("D20-2", "d20-2")]
    [InlineData("3d8+4", "3d8+4")]
    [InlineData("2d6+0", "2d6")]
    public void Displays_in_normal_notation_with_the_sign_exactly(string text, string shown) =>
        Assert.Equal(shown, DiceExpression.Parse(text).ToString());

    // ---- legal range ------------------------------------------------------------------------------

    [Theory]
    [InlineData("d20", 1, 20)]
    [InlineData("2d6", 2, 12)]
    [InlineData("2d6+1", 3, 13)]
    [InlineData("d20-2", -1, 18)]
    [InlineData("3d8+4", 7, 28)]
    [InlineData("d100", 1, 100)]
    [InlineData("d100+5", 6, 105)]
    [InlineData("d100-10", -9, 90)]
    public void The_legal_range_is_the_final_range_after_the_modifier(string text, int min, int max)
    {
        var dice = DiceExpression.Parse(text);

        Assert.Equal((min, max), (dice.Min, dice.Max));
        Assert.True(dice.IsLegal(min) && dice.IsLegal(max));
        Assert.False(dice.IsLegal(min - 1) || dice.IsLegal(max + 1));
    }

    [Fact]
    public void The_base_dice_and_the_modifier_can_be_taken_apart_for_a_future_provider()
    {
        var dice = DiceExpression.Parse("3d8+4");

        Assert.Equal(new DiceExpression(3, 8), dice.Base);
        Assert.Equal(18, dice.Apply(14));                                   // subtotal 14 + 4
        Assert.Equal(dice, dice.Base with { Modifier = 4 });
    }

    // ---- built-in rolling ---------------------------------------------------------------------------

    [Fact]
    public void Two_d6_plus_1_with_dice_2_and_4_gives_7()
    {
        var random = new SequenceRandom(2, 4);

        Assert.Equal(7, new BuiltInDiceProvider(random).Roll(DiceExpression.Parse("2d6+1")));
        Assert.Equal(2, random.Calls);                                     // two dice rolled; the modifier is not a die
    }

    [Fact]
    public void D20_minus_2_with_a_1_gives_minus_1()
    {
        Assert.Equal(-1, new BuiltInDiceProvider(new SequenceRandom(1)).Roll(DiceExpression.Parse("d20-2")));
    }

    [Theory]
    [InlineData("d20", new[] { 13 }, 13)]
    [InlineData("3d8+4", new[] { 1, 1, 1 }, 7)]
    [InlineData("3d8+4", new[] { 8, 8, 8 }, 28)]
    [InlineData("d100-10", new[] { 100 }, 90)]
    [InlineData("d100+5", new[] { 100 }, 105)]
    public void The_final_number_is_the_dice_sum_plus_the_modifier(string text, int[] faces, int expected) =>
        Assert.Equal(expected, new BuiltInDiceProvider(new SequenceRandom(faces)).Roll(DiceExpression.Parse(text)));

    [Theory]
    [InlineData("d6")]
    [InlineData("2d6+1")]
    [InlineData("d20-2")]
    [InlineData("3d8+4")]
    [InlineData("d100+5")]
    public void Real_random_rolls_stay_inside_the_final_range_and_reach_both_ends(string text)
    {
        var dice = DiceExpression.Parse(text);
        var provider = new BuiltInDiceProvider(new Random(99));
        var seen = new HashSet<int>();
        for (var i = 0; i < 20000; i++)
        {
            var roll = provider.Roll(dice);
            Assert.True(dice.IsLegal(roll), $"{roll} outside {dice.Min}..{dice.Max}");
            seen.Add(roll);
        }
        Assert.Contains(dice.Min, seen);
        Assert.Contains(dice.Max, seen);
    }

    [Fact]
    public void The_resolver_receives_only_the_final_number_and_needs_no_modifier_step()
    {
        var table = new RollableTable
        {
            Dice = DiceExpression.Parse("2d6+1"),
            ResultSets = [new ResultSet { Entries = [new() { Min = 3, Max = 6, Text = "Hostile" }, new() { Min = 7, Max = 13, Text = "Friendly" }] }],
        };
        var final = new BuiltInDiceProvider(new SequenceRandom(2, 4)).Roll(table.Dice);   // 2 + 4 + 1

        var resolution = TableResolver.Resolve(table, final);

        Assert.Equal((7, "Friendly"), (resolution.Roll, resolution.Results[0].Entry!.Text));
    }

    // ---- validation ---------------------------------------------------------------------------------

    private static IReadOnlyList<ValidationFinding> Validate(string dice, params (int Min, int Max, string Text)[] entries) =>
        TableValidator.Validate(Fixtures.Table(DiceExpression.Parse(dice), entries));

    [Fact]
    public void A_table_covering_the_final_range_is_valid_and_gaps_are_measured_against_it()
    {
        Assert.Empty(Validate("2d6+1", (3, 6, "low"), (7, 13, "high")));

        var gap = Assert.Single(Validate("2d6+1", (3, 6, "low"), (7, 12, "high")));
        Assert.Equal((ValidationKind.Gap, 13, 13), (gap.Kind, gap.Start, gap.End));
    }

    [Fact]
    public void A_result_below_or_above_the_final_range_is_out_of_range()
    {
        var below = Assert.Single(Validate("2d6+1", (2, 6, "low"), (7, 13, "high")));   // 2 is a raw 2d6 minimum, not a legal 2d6+1 result
        Assert.Equal((ValidationKind.BelowMinimum, 2, 2), (below.Kind, below.Start, below.End));

        var above = Assert.Single(Validate("2d6+1", (3, 6, "low"), (7, 14, "high")));
        Assert.Equal((ValidationKind.AboveMaximum, 14, 14), (above.Kind, above.Start, above.End));
    }

    [Fact]
    public void Negative_finals_are_part_of_the_legal_range_of_a_negative_modifier()
    {
        var findings = Validate("d20-2", (1, 20, "all"));

        Assert.Contains(findings, f => f.Kind == ValidationKind.Gap && (f.Start, f.End) == (-1, 0));
        Assert.Contains(findings, f => f.Kind == ValidationKind.AboveMaximum && (f.Start, f.End) == (19, 20));
        Assert.DoesNotContain(Validate("d20-2", (-1, 0, "bad"), (1, 18, "ok")), f => f.Kind != ValidationKind.Gap);
    }

    // ---- range text with negative numbers (only where the dice can produce them) --------------------

    [Theory]
    [InlineData("-1", -1, -1)]
    [InlineData("-1-0", -1, 0)]
    [InlineData("-3--1", -3, -1)]
    [InlineData("0-5", 0, 5)]
    [InlineData("1-18", 1, 18)]
    public void Ranges_may_be_negative_for_dice_that_can_go_negative(string text, int min, int max)
    {
        Assert.True(RangeText.TryParse(text, DiceExpression.Parse("d20-2"), out var range, out _));
        Assert.Equal((min, max, (string?)null), (range.Min, range.Max, range.DisplayRange));
    }

    [Theory]
    [InlineData("d10")]
    [InlineData("2d6+1")]
    public void Negative_ranges_stay_rejected_for_dice_that_cannot_go_negative(string dice)
    {
        Assert.False(RangeText.TryParse("-1", DiceExpression.Parse(dice), out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Leading_zero_display_forms_are_kept_exactly_as_before()
    {
        Assert.True(RangeText.TryParse("01-05", DiceExpression.Parse("d100"), out var range, out _));
        Assert.Equal((1, 5, "01–05"), (range.Min, range.Max, range.DisplayRange));
    }

    // ---- d100 --------------------------------------------------------------------------------------

    [Fact]
    public void A_plain_d100_keeps_its_00_behaviour_exactly()
    {
        var d100 = DiceExpression.Parse("d100");

        Assert.True(d100.IsPercentile);
        Assert.Equal("00", d100.FormatValue(100));
        Assert.True(RangeText.TryParse("96-00", d100, out var range, out _));
        Assert.Equal((96, 100, "96–00"), (range.Min, range.Max, range.DisplayRange));
        Assert.True(RangeText.TryParseValue("00", d100, out var value));
        Assert.Equal(100, value);
        Assert.Equal(d100, DiceExpression.Parse("d100+0"));
    }

    [Fact]
    public void A_modified_d100_has_final_results_not_die_faces_so_it_has_no_00()
    {
        var d100Plus = DiceExpression.Parse("d100+5");

        Assert.False(d100Plus.IsPercentile);
        Assert.Equal("100", d100Plus.FormatValue(100));                      // 95 + 5, not a die face
        Assert.True(RangeText.TryParse("00", d100Plus, out var zero, out _));
        Assert.Equal(0, zero.Min);                                            // "00" is just zero here
        Assert.True(DiceExpression.Parse("d100-10").IsLegal(0));
    }

    // ---- headings and import -----------------------------------------------------------------------------

    [Theory]
    [InlineData("2D6+1 REACTION", "2d6+1", "Reaction")]
    [InlineData("D20-2 RANDOM EVENT", "d20-2", "Random Event")]
    [InlineData("d20+5 Wandering Monster", "d20+5", "Wandering Monster")]
    [InlineData("D100-10 LOOT", "d100-10", "Loot")]
    [InlineData("D20–2 RANDOM EVENT", "d20-2", "Random Event")]
    [InlineData("Encounter Reaction (2d6+1)", "2d6+1", "Encounter Reaction")]
    public void Headings_with_a_modifier_give_the_dice_and_the_name_and_no_stray_result_text(string heading, string dice, string name)
    {
        var draft = TableTextParser.Parse(heading + "\n1-20 Something");

        Assert.Equal((dice, name), (draft.DiceText, draft.TableName));
        Assert.DoesNotContain(draft.Issues, i => i.Target is ParseIssueTarget.Dice or ParseIssueTarget.TableName);
        Assert.DoesNotContain("+", draft.TableName);
        Assert.Equal("Something", draft.ResultSets[0].Entries[0].Text);
    }

    [Fact]
    public void A_heading_that_is_only_dice_with_a_modifier_is_understood()
    {
        var draft = TableTextParser.Parse("2D6+1\n3-13 x");

        Assert.Equal("2d6+1", draft.DiceText);
        Assert.Equal("", draft.TableName);
    }

    [Theory]
    [InlineData("2D6+1D4 REACTION")]
    [InlineData("2D6*2 REACTION")]
    [InlineData("4D6KH3 STATS")]
    public void Unsupported_expressions_in_a_heading_are_not_partly_read(string heading)
    {
        var draft = TableTextParser.Parse(heading + "\n1-20 Something");

        Assert.Equal("", draft.DiceText);
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.NoDiceExpression);
    }

    [Fact]
    public void A_zero_or_absurd_dice_expression_in_a_heading_is_reported_with_the_clear_wording()
    {
        var draft = TableTextParser.Parse("d0+1 Loot\n1 x");

        var issue = Assert.Single(draft.Issues, i => i.Code == ParseIssueCode.UnsupportedDice);
        Assert.Contains("Use a dice expression such as d20, 2d6, or 2d6+1.", issue.Message);
    }

    [Fact]
    public void Repeated_headings_with_a_modifier_are_still_recognised_and_different_ones_are_flagged()
    {
        Assert.Equal("Syllable", TableTextParser.Parse("D6+1 SYLLABLE D6+1 SYLLABLE\n3-7 x").TableName);
        Assert.Contains(TableTextParser.Parse("D6+1 SYLLABLE D6 SYLLABLE\n3-7 x").Issues, i => i.Code == ParseIssueCode.AmbiguousHeading);
    }

    [Fact]
    public void A_full_import_of_a_modified_table_builds_validates_and_resolves_on_the_final_number()
    {
        var table = Fixtures.ParseAndBuild("2D6+1 ENCOUNTER REACTION\n3-6 Hostile\n7-9 Wary\n10-13 Friendly");

        Assert.Equal(new DiceExpression(2, 6, 1), table.Dice);
        Assert.Empty(TableValidator.Validate(table));
        Assert.Equal("Friendly", TableResolver.Resolve(table, 10).Results[0].Entry!.Text);
        Assert.Equal("Hostile", TableResolver.Resolve(table, 3).Results[0].Entry!.Text);
        Assert.Equal(ResolutionStatus.NoMatch, TableResolver.Resolve(table, 2).Results[0].Status);
    }

    [Fact]
    public void Existing_simple_headings_are_unchanged()
    {
        foreach (var (heading, dice, name) in new[] { ("D10 RANDOM STARTING GEAR", "d10", "Random Starting Gear"), ("d100 Treasure", "d100", "Treasure"), ("2d6 Reaction", "2d6", "Reaction") })
        {
            var draft = TableTextParser.Parse(heading + "\n1-100 x");
            Assert.Equal((dice, name), (draft.DiceText, draft.TableName));
        }
    }

    // ---- the Review dice field -------------------------------------------------------------------------------

    [Fact]
    public void The_dice_field_accepts_the_new_syntax_and_explains_what_it_does_not_understand()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        db.CreateCollection("C");
        var main = new MainViewModel(db, new FixedDice(1));
        main.PasteTableCommand.Execute(null);
        ((PasteViewModel)main.Current!).SourceText = "d6 Loot\n1-6 Coin";
        ((PasteViewModel)main.Current!).InterpretCommand.Execute(null);
        var review = (ReviewViewModel)main.Current!;

        review.DiceText = "2d6+1";
        Assert.True(review.CanSave);
        Assert.Contains(review.ValidationNotes, n => n.Contains("below the lowest roll (3)"));      // row 1 is under the final minimum

        review.DiceText = "2d6+1d4";
        Assert.False(review.CanSave);
        var blocker = Assert.Single(review.Blockers, b => b.Contains("2d6+1d4"));
        Assert.Contains("not supported", blocker);
        Assert.Contains("Use a dice expression such as d20, 2d6, or 2d6+1.", blocker);
    }

    [Fact]
    public void Editing_a_table_from_2d6_to_2d6_plus_1_and_adding_rows_uses_the_final_range()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var c = db.CreateCollection("C");
        db.SaveTable(Fixtures.Table(DiceExpression.Parse("2d6"), (2, 12, "x")).Also(t => { t.Name = "Reaction"; t.CollectionId = c.Id; }));
        var main = new MainViewModel(db, new FixedDice(1));
        main.SelectedTable = main.Tables[0];
        main.EditTableCommand.Execute(null);
        var review = (ReviewViewModel)main.Current!;

        review.DiceText = "2d6+1";
        review.AddResultSetCommand.Execute(null);

        Assert.Equal("3-13", review.SelectedResultSet.Rows[0].RangeText);                 // a new set spans the whole final range
        review.SelectedResultSet = review.ResultSets[0];
        review.SaveCommand.Execute(null);
        Assert.Equal(new DiceExpression(2, 6, 1), db.LoadTable(main.Tables[0].Id)!.Dice);
    }

    [Fact]
    public void A_negative_dice_table_can_be_authored_in_review_including_its_negative_results()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        db.CreateCollection("C");
        var main = new MainViewModel(db, new FixedDice(1));
        main.PasteTableCommand.Execute(null);
        ((PasteViewModel)main.Current!).SourceText = "d20-2 Random Event\n1-18 Something happens";
        ((PasteViewModel)main.Current!).InterpretCommand.Execute(null);
        var review = (ReviewViewModel)main.Current!;

        Assert.Contains("No row covers -1–0.", review.ValidationNotes);
        review.SelectedResultSet.AddRowCommand.Execute(null);
        review.Rows[1].RangeText = "-1-0";
        review.Rows[1].Text = "A disaster";

        Assert.Empty(review.ValidationNotes);
        Assert.True(review.CanSave);
        review.SaveCommand.Execute(null);
        var saved = db.LoadTable(main.Tables[0].Id)!;
        Assert.Equal((-1, 0, "A disaster"), (saved.ResultSets[0].Entries[1].Min, saved.ResultSets[0].Entries[1].Max, saved.ResultSets[0].Entries[1].Text));
    }
}

/// <summary>Manual entry, display and history for modified dice, through the roll view model.</summary>
public class DiceModifierRollTests
{
    private static RollViewModel Session(string dice, int fixedRoll, params (int Min, int Max, string Text)[] rows) =>
        new(Fixtures.Table(DiceExpression.Parse(dice), rows), new FixedDice(fixedRoll));

    [Fact]
    public void Manual_entry_is_the_final_result_so_3_and_13_are_accepted_and_2_and_14_are_refused()
    {
        var roll = Session("2d6+1", 5, (3, 8, "Low"), (9, 9, "Nine"), (10, 13, "High"));

        foreach (var (typed, ok) in new[] { ("3", true), ("13", true), ("2", false), ("14", false) })
        {
            roll.ManualRollText = typed;
            roll.ResolveManualCommand.Execute(null);
            Assert.Equal(ok ? "" : "Enter a whole number from 3 to 13.", roll.Message);
        }
    }

    [Fact]
    public void A_typed_result_is_never_modified_again()
    {
        var roll = Session("2d6+1", 5, (3, 8, "Low"), (9, 9, "Nine"), (10, 13, "High"));

        roll.ManualRollText = "9";                                   // the person rolled 2d6, added 1 themselves, and got 9
        roll.ResolveManualCommand.Execute(null);

        Assert.Equal("Nine", Assert.Single(roll.Results).Text);      // not "High": 9 was not turned into 10
        Assert.Equal("Rolled 9 (2d6+1)", roll.RollDisplay);
    }

    [Fact]
    public void A_negative_final_result_can_be_typed_when_the_dice_allow_it()
    {
        var roll = Session("d20-2", 5, (-1, 0, "Disaster"), (1, 18, "Fine"));

        foreach (var typed in new[] { "-1", "−1" })             // hyphen-minus and a real minus sign
        {
            roll.ManualRollText = typed;
            roll.ResolveManualCommand.Execute(null);
            Assert.Equal("", roll.Message);
            Assert.Equal("Disaster", Assert.Single(roll.Results).Text);
            Assert.Equal("Rolled -1 (d20-2)", roll.RollDisplay);
        }

        foreach (var bad in new[] { "-2", "19", "--1", "- 1", "+3", "1-2" })
        {
            roll.ManualRollText = bad;
            roll.ResolveManualCommand.Execute(null);
            Assert.Equal("Enter a whole number from -1 to 18.", roll.Message);
        }
    }

    [Fact]
    public void A_negative_number_is_still_refused_for_dice_that_cannot_produce_one()
    {
        var roll = Session("d10", 5, (1, 10, "x"));

        roll.ManualRollText = "-3";
        roll.ResolveManualCommand.Execute(null);

        Assert.Equal("Enter a whole number from 1 to 10.", roll.Message);
        Assert.Empty(roll.Current.Outcomes);
    }

    [Fact]
    public void The_expression_stays_visible_on_the_roll_screen_and_plain_dice_display_as_before()
    {
        var modified = Session("2d6+1", 7, (3, 13, "Friendly"));
        modified.RollCommand.Execute(null);

        Assert.Equal("2d6+1 · legal rolls 3–13", modified.DiceInfo);
        Assert.Equal("Rolled 7 (2d6+1)", modified.RollDisplay);
        Assert.Equal("Friendly", Assert.Single(modified.Results).Text);

        var negative = Session("d20-2", 1, (-1, 18, "x"));
        Assert.Equal("d20-2 · legal rolls -1–18", negative.DiceInfo);

        var plain = Session("2d6", 7, (2, 12, "x"));
        plain.RollCommand.Execute(null);
        Assert.Equal("Rolled 7", plain.RollDisplay);                 // unchanged
        Assert.Equal("2d6 · legal rolls 2–12", plain.DiceInfo);
    }

    [Fact]
    public void A_d100_roll_still_shows_00_and_a_modified_d100_shows_the_number()
    {
        var plain = Session("d100", 100, (1, 100, "x"));
        plain.RollCommand.Execute(null);
        Assert.Equal("Rolled 00 (numeric 100)", plain.RollDisplay);

        var modified = Session("d100+5", 100, (6, 105, "x"));
        modified.RollCommand.Execute(null);
        Assert.Equal("Rolled 100 (d100+5)", modified.RollDisplay);
    }

    [Fact]
    public void History_records_the_expression_and_the_final_number()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var c = db.CreateCollection("C");
        var table = Fixtures.Table(DiceExpression.Parse("2d6+1"), (3, 6, "Hostile"), (7, 13, "Friendly")).Also(t => { t.Name = "Encounter Reaction"; t.CollectionId = c.Id; });
        db.SaveTable(table);
        var main = new MainViewModel(db, new FixedDice(7));
        main.SelectedTable = main.Tables[0];

        ((RollViewModel)main.Current!).RollCommand.Execute(null);

        var item = Assert.Single(main.RecentRolls).Item;
        Assert.Equal(("2d6+1", 7), (item.DiceText, item.RollValue));
        Assert.Equal("Encounter Reaction\n2d6+1 → 7\n\nFriendly", item.FullText);
        Assert.Equal("2d6+1", main.RecentTables[0].Dice.ToString());
    }
}

/// <summary>Schema version 3: one structured modifier column per table, migrated from RC2 databases.</summary>
public class DiceModifierPersistenceTests
{
    private static SqliteConnection Raw(string path) => new($"Data Source={path};Pooling=False;Foreign Keys=True");

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(string path, string sql)
    {
        using var raw = Raw(path);
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Theory]
    [InlineData("d20", 0)]
    [InlineData("2d6+1", 1)]
    [InlineData("d20-2", -2)]
    [InlineData("3d8+4", 4)]
    [InlineData("d100-10", -10)]
    public void A_modified_expression_round_trips_through_save_load_lists_and_recents(string text, int modifier)
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = db.CreateCollection("C");
        var table = db.SaveTable(Fixtures.Table(DiceExpression.Parse(text), (DiceExpression.Parse(text).Min, DiceExpression.Parse(text).Max, "All")).Also(t => t.CollectionId = c.Id));
        db.MarkTableUsed(table.Id);

        Assert.Equal(DiceExpression.Parse(text), db.LoadTable(table.Id)!.Dice);
        Assert.Equal(modifier, db.LoadTable(table.Id)!.Dice.Modifier);
        Assert.Equal(text, db.GetTableSummaries(c.Id).Single().Dice.ToString());
        Assert.Equal(text, db.GetRecentTables(c.Id).Single().Dice.ToString());
    }

    [Fact]
    public void Changing_the_modifier_of_a_saved_table_replaces_it()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var table = db.SaveTable(Fixtures.Table(DiceExpression.Parse("2d6"), (2, 12, "x")).Also(t => t.CollectionId = db.CreateCollection("C").Id));

        table.Dice = DiceExpression.Parse("2d6+1");
        db.SaveTable(table);

        Assert.Equal(new DiceExpression(2, 6, 1), db.LoadTable(table.Id)!.Dice);
    }

    [Fact]
    public void The_database_holds_the_dice_as_structured_columns_and_bounds_the_modifier()
    {
        using var temp = new TempDatabase();
        using (var db = temp.Open())
            db.SaveTable(Fixtures.Table(DiceExpression.Parse("3d8+4"), (7, 28, "x")).Also(t => t.CollectionId = db.CreateCollection("C").Id));

        Assert.Equal((3L, 8L, 4L), (Scalar(temp.Path, "SELECT DiceCount FROM Tables"), Scalar(temp.Path, "SELECT DiceSides FROM Tables"), Scalar(temp.Path, "SELECT DiceModifier FROM Tables")));
        using var raw = Raw(temp.Path);
        raw.Open();
        Assert.Throws<SqliteException>(() => Exec(raw, "UPDATE Tables SET DiceModifier = 1001"));
        Assert.Throws<SqliteException>(() => Exec(raw, "UPDATE Tables SET DiceModifier = -1001"));
    }

    [Fact]
    public void An_rc2_database_migrates_to_the_current_version_with_every_table_at_modifier_zero_and_all_data_intact()
    {
        using var temp = new TempDatabase();
        using (var raw = Raw(temp.Path))
        {
            raw.Open();
            DatabaseMigrations.Apply(raw, upToVersion: 2);           // exactly the RC2 schema
            Assert.Equal(2, DatabaseMigrations.GetVersion(raw));
            Exec(raw, """
                INSERT INTO Collections (Id, Name, CreatedUtc) VALUES (1, 'Dungeon', '2026-01-01T00:00:00.0000000Z');
                INSERT INTO Tables (Id, CollectionId, Name, DiceCount, DiceSides, CreatedUtc, UpdatedUtc, LastUsedUtc) VALUES
                    (10, 1, 'Scavenged Items', 1, 20, '2026-01-03T00:00:00.0000000Z', '2026-01-03T00:00:00.0000000Z', '2026-02-01T00:00:00.0000000Z'),
                    (11, 1, 'Scavenging', 2, 6, '2026-01-04T00:00:00.0000000Z', '2026-01-05T00:00:00.0000000Z', '2026-02-02T00:00:00.0000000Z'),
                    (12, 1, 'Treasure', 1, 100, '2026-01-06T00:00:00.0000000Z', '2026-01-06T00:00:00.0000000Z', NULL);
                INSERT INTO ResultSets (Id, TableId, Name, SortOrder) VALUES (100, 10, '', 0), (101, 11, '', 0), (102, 12, 'Coins', 0), (103, 12, 'Gems', 1);
                INSERT INTO Entries (Id, ResultSetId, MinValue, MaxValue, DisplayText, DisplayRange, LinkedTableId, UnresolvedLinkName, SortOrder) VALUES
                    (1000, 100, 1, 20, 'Rusty nails', NULL, NULL, NULL, 0),
                    (1001, 101, 2, 5, 'Nothing useful', NULL, NULL, NULL, 0),
                    (1002, 101, 6, 12, '1x Scavenged Item', NULL, 10, NULL, 1),
                    (1003, 102, 1, 100, 'Copper', '01–00', NULL, NULL, 0),
                    (1004, 103, 1, 100, 'Garnet', NULL, NULL, 'Gem Table', 0);
                INSERT INTO RollHistory (Id, TableId, TableName, DiceText, RollValue, ResultText, RolledUtc)
                    VALUES (1, 11, 'Scavenging', '2d6', 7, '1x Scavenged Item', '2026-02-02T00:00:00.0000000Z'),
                           (2, NULL, 'Old Deleted Table', 'd20', 12, 'Gone', '2026-02-03T00:00:00.0000000Z');
                """);
        }

        using var db = temp.Open();                                   // migrates on open

        Assert.Equal(4, DatabaseMigrations.CurrentVersion);
        Assert.Equal(4L, Scalar(temp.Path, "PRAGMA user_version"));
        Assert.Equal(0L, Scalar(temp.Path, "SELECT COUNT(*) FROM Tables WHERE DiceModifier <> 0"));   // every existing table: modifier 0

        Assert.Equal([new DiceExpression(1, 20, 0), new DiceExpression(2, 6, 0), new DiceExpression(1, 100, 0)],
            db.GetTableSummaries(1).OrderBy(t => t.Id).Select(t => t.Dice).ToArray());
        var scavenging = db.LoadTable(11)!;
        Assert.Equal(("Scavenging", 0), (scavenging.Name, scavenging.Dice.Modifier));
        Assert.Equal(10L, scavenging.ResultSets[0].Entries[1].LinkedTableId);                       // links preserved
        var treasure = db.LoadTable(12)!;
        Assert.Equal(["Coins", "Gems"], treasure.ResultSets.Select(s => s.Name).ToArray());        // result sets preserved
        Assert.Equal((1, 100, "01–00"), (treasure.ResultSets[0].Entries[0].Min, treasure.ResultSets[0].Entries[0].Max, treasure.ResultSets[0].Entries[0].DisplayRange));
        Assert.Equal((null, "Gem Table"), (treasure.ResultSets[1].Entries[0].LinkedTableId, treasure.ResultSets[1].Entries[0].UnresolvedLinkName));

        Assert.Equal(["Scavenging", "Scavenged Items"], db.GetRecentTables(1).Select(t => t.Name).ToArray());   // recent usage preserved
        var history = db.GetRollHistory();                                                          // history preserved, including a detached row
        Assert.Equal(["Old Deleted Table", "Scavenging"], history.Select(h => h.TableName).ToArray());
        Assert.Equal(("2d6", 7, 11L), (history[1].DiceText, history[1].RollValue, history[1].TableId!.Value));
        Assert.Null(history[0].TableId);

        // And the migrated database works with the new syntax straight away.
        var newTable = db.SaveTable(Fixtures.Table(DiceExpression.Parse("2d6+1"), (3, 13, "x")).Also(t => { t.Name = "New"; t.CollectionId = 1; }));
        Assert.Equal(new DiceExpression(2, 6, 1), db.LoadTable(newTable.Id)!.Dice);
        Assert.Equal(new DiceExpression(2, 6, 0), db.LoadTable(11)!.Dice);
    }

    [Fact]
    public void A_failed_upgrade_to_version_3_rolls_back_and_leaves_the_rc2_database_untouched()
    {
        using var temp = new TempDatabase();
        using (var raw = Raw(temp.Path))
        {
            raw.Open();
            DatabaseMigrations.Apply(raw, upToVersion: 2);
            Exec(raw, "INSERT INTO Collections (Id, Name, CreatedUtc) VALUES (1, 'Keep', '2026-01-01T00:00:00.0000000Z')");
            Exec(raw, "ALTER TABLE Tables ADD COLUMN DiceModifier INTEGER NOT NULL DEFAULT 0");   // makes migration 3 collide
        }

        Assert.Throws<SqliteException>(() => temp.Open());

        Assert.Equal(2L, Scalar(temp.Path, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(temp.Path, "SELECT COUNT(*) FROM Collections"));
    }
}
