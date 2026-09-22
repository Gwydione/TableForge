using Microsoft.Data.Sqlite;
using TableForge.Data;
using TableForge.Dice;
using TableForge.Domain;
using TableForge.Import;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>
/// "2d6" adds two d6; "d66" reads two ordered d6 as tens and ones. They are different conventions, decided by the expression alone
/// and never inferred from a table's rows.
/// </summary>
public class D66ExpressionTests
{
    [Theory]
    [InlineData("d66")]
    [InlineData("D66")]
    [InlineData("  d66 ")]
    public void D66_in_any_case_is_the_d66_convention_made_of_two_ordinary_d6(string text)
    {
        Assert.True(DiceExpression.TryParse(text, out var dice));

        Assert.Equal(RollConvention.D66, dice.Convention);
        Assert.True(dice.IsD66);
        Assert.Equal((2, 6, 0), (dice.Count, dice.Sides, dice.Modifier));   // physically two d6, never one 66-sided die
        Assert.Equal(DiceExpression.D66, dice);
        Assert.Equal("d66", dice.ToString());
    }

    [Theory]
    [InlineData("2d6", 2, 6, 0)]
    [InlineData("2D6", 2, 6, 0)]
    [InlineData("d6", 1, 6, 0)]
    [InlineData("d20", 1, 20, 0)]
    [InlineData("2d6+1", 2, 6, 1)]
    [InlineData("d20-2", 1, 20, -2)]
    public void Existing_expressions_are_unchanged_and_2d6_is_never_read_as_d66(string text, int count, int sides, int modifier)
    {
        Assert.True(DiceExpression.TryParse(text, out var dice));

        Assert.Equal(RollConvention.StandardSum, dice.Convention);
        Assert.False(dice.IsD66);
        Assert.Equal((count, sides, modifier), (dice.Count, dice.Sides, dice.Modifier));
    }

    [Fact]
    public void D66_and_2d6_are_different_expressions_even_though_they_use_the_same_two_dice()
    {
        var twoD6 = DiceExpression.Parse("2d6");
        Assert.NotEqual(twoD6, DiceExpression.D66);
        Assert.Equal((twoD6.Count, twoD6.Sides), (DiceExpression.D66.Count, DiceExpression.D66.Sides));
        Assert.Equal((2, 12), (twoD6.Min, twoD6.Max));
        Assert.Equal((11, 66), (DiceExpression.D66.Min, DiceExpression.D66.Max));
    }

    [Theory]
    [InlineData("d66+1")]
    [InlineData("D66-2")]
    [InlineData("d66+0")]
    public void D66_takes_no_modifier_and_says_so(string text)
    {
        Assert.False(DiceExpression.TryParse(text, out _));
        Assert.Contains("A d66 takes no modifier.", DiceExpression.UnsupportedHint(text));
        Assert.Equal("", DiceExpression.UnsupportedHint("2d6+1d4"));   // the hint is only for d66
    }

    [Fact]
    public void An_explicit_count_keeps_its_old_meaning_so_old_tables_and_the_word_d66_never_collide()
    {
        // Only a BARE d66 is the convention. "1d66" / "2d66" are the old single/multiple 66-sided dice, written with a count so they can be told apart.
        Assert.True(DiceExpression.TryParse("2d66", out var two));
        Assert.Equal(new DiceExpression(2, 66), two);
        Assert.Equal("2d66", two.ToString());

        Assert.True(DiceExpression.TryParse("1d66", out var one));
        Assert.Equal(new DiceExpression(1, 66), one);
        Assert.False(one.IsD66);
        Assert.Equal("1d66", one.ToString());                                 // NOT "d66": that would silently change its meaning on the next edit
        Assert.Equal(one, DiceExpression.Parse(one.ToString()));
        Assert.Equal(DiceExpression.D66, DiceExpression.Parse(DiceExpression.D66.ToString()));
    }

    [Theory]
    [InlineData("11")] [InlineData("16")] [InlineData("21")] [InlineData("35")] [InlineData("61")] [InlineData("66")]
    public void Every_tens_and_ones_value_with_digits_1_to_6_is_a_legal_d66_result(string value) =>
        Assert.True(DiceExpression.D66.IsLegal(int.Parse(value)));

    [Theory]
    [InlineData(0)] [InlineData(10)] [InlineData(17)] [InlineData(20)] [InlineData(27)] [InlineData(30)]
    [InlineData(40)] [InlineData(50)] [InlineData(60)] [InlineData(67)] [InlineData(70)] [InlineData(100)] [InlineData(-11)]
    public void Impossible_values_are_not_legal_even_when_they_lie_between_11_and_66(int value) =>
        Assert.False(DiceExpression.D66.IsLegal(value));

    [Fact]
    public void The_legal_domain_is_exactly_the_36_tens_and_ones_values_and_2d6_stays_continuous()
    {
        var legal = Enumerable.Range(-5, 200).Where(DiceExpression.D66.IsLegal).ToArray();
        var expected = (from tens in Enumerable.Range(1, 6) from ones in Enumerable.Range(1, 6) select tens * 10 + ones).ToArray();
        Assert.Equal(expected, legal);
        Assert.Equal(36, legal.Length);

        var twoD6 = DiceExpression.Parse("2d6");
        Assert.Equal(Enumerable.Range(2, 11), Enumerable.Range(-5, 200).Where(twoD6.IsLegal));   // 17 is not legal for 2d6 either, but 7 is, and 12
    }

    [Fact]
    public void The_next_legal_value_skips_the_impossible_ones()
    {
        Assert.Equal(21, DiceExpression.D66.NextLegal(16));
        Assert.Equal(12, DiceExpression.D66.NextLegal(11));
        Assert.Equal(11, DiceExpression.D66.NextLegal(0));
        Assert.Null(DiceExpression.D66.NextLegal(66));
        Assert.Equal(8, DiceExpression.Parse("2d6").NextLegal(7));   // ordinary dice: simply the next number
    }

    [Fact]
    public void Faces_are_read_in_order_as_tens_and_ones_for_d66_and_added_for_2d6()
    {
        Assert.Equal(35, DiceExpression.D66.ResultFromFaces([3, 5]));
        Assert.Equal(53, DiceExpression.D66.ResultFromFaces([5, 3]));   // order matters
        Assert.Equal(61, DiceExpression.D66.ResultFromFaces([6, 1]));
        Assert.Equal(8, DiceExpression.Parse("2d6").ResultFromFaces([3, 5]));
        Assert.Equal(8, DiceExpression.Parse("2d6").ResultFromFaces([5, 3]));
        Assert.Equal(9, DiceExpression.Parse("2d6+1").ResultFromFaces([3, 5]));
        Assert.Throws<ArgumentException>(() => DiceExpression.D66.ResultFromFaces([3]));
        Assert.Throws<ArgumentException>(() => DiceExpression.D66.ResultFromFaces([3, 7]));
    }

    [Fact]
    public void A_d66_value_prints_as_plain_digits_and_never_as_00()
    {
        Assert.Equal("66", DiceExpression.D66.FormatValue(66));
        Assert.False(DiceExpression.D66.IsPercentile);
    }
}

public class D66BuiltInProviderTests
{
    [Theory]
    [InlineData(3, 5, 35)]
    [InlineData(6, 1, 61)]
    [InlineData(1, 1, 11)]
    [InlineData(6, 6, 66)]
    public void Built_in_reads_the_first_die_as_tens_and_the_second_as_ones(int first, int second, int expected)
    {
        var random = new SequenceRandom(first, second);

        Assert.Equal(expected, new BuiltInDiceProvider(random).Roll(DiceExpression.D66));
        Assert.Equal(2, random.Calls);   // two independent d6, not one summed path
    }

    [Fact]
    public async Task Built_in_d66_through_the_async_contract_is_immediate_and_ordered()
    {
        var task = new BuiltInDiceProvider(new SequenceRandom(3, 5)).RollAsync(DiceExpression.D66, CancellationToken.None);

        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(35, await task);
    }

    [Fact]
    public void The_same_faces_on_an_ordinary_2d6_still_add_up()
    {
        Assert.Equal(8, new BuiltInDiceProvider(new SequenceRandom(3, 5)).Roll(DiceExpression.Parse("2d6")));
        Assert.Equal(9, new BuiltInDiceProvider(new SequenceRandom(3, 5)).Roll(DiceExpression.Parse("2d6+1")));   // and the modifier still applies
        Assert.Equal(7, new BuiltInDiceProvider(new SequenceRandom(6, 1)).Roll(DiceExpression.Parse("2d6")));
    }

    [Fact]
    public void Real_random_d66_rolls_are_only_ever_legal_and_reach_all_36_values()
    {
        var provider = new BuiltInDiceProvider(new Random(2026));
        var seen = new HashSet<int>();
        for (var i = 0; i < 4000; i++)
        {
            var value = provider.Roll(DiceExpression.D66);
            Assert.True(DiceExpression.D66.IsLegal(value), $"{value} is not a possible d66 result");
            seen.Add(value);
        }
        Assert.Equal(36, seen.Count);
    }

    [Fact]
    public void The_two_dice_are_independent_all_36_ordered_pairs_occur_including_both_orders()
    {
        var provider = new BuiltInDiceProvider(new Random(7));
        var seen = new HashSet<int>();
        for (var i = 0; i < 4000; i++) seen.Add(provider.Roll(DiceExpression.D66));

        Assert.Contains(35, seen);
        Assert.Contains(53, seen);   // 3-then-5 and 5-then-3 are different results
    }
}

public class D66DddiceTests
{
    [Fact]
    public void A_d66_visually_rolls_two_ordinary_d6_and_nothing_else()
    {
        Assert.Equal("d6,d6", string.Join(",", DddiceDiceMapping.ToDddice(DiceExpression.D66)));
        Assert.True(DddiceDiceMapping.IsSupported(DiceExpression.D66));
    }

    [Theory]
    [InlineData(3, 5, 35)]
    [InlineData(5, 3, 53)]
    [InlineData(6, 1, 61)]
    public void The_ordered_faces_become_the_tens_and_ones_result(int first, int second, int expected)
    {
        var result = DddiceDiceMapping.Interpret(DiceExpression.D66, [new("d6", first), new("d6", second)]);

        Assert.Equal(expected, result.Final);
        Assert.Equal([first, second], result.Faces.Select(f => f.Value));   // the faces are kept in order
    }

    [Fact]
    public async Task The_d66_result_is_not_available_until_the_dice_finish_and_the_same_faces_on_2d6_add()
    {
        var roller = new FakeDddiceRoller();
        var provider = new DddiceDiceProvider(roller);

        var roll = provider.RollAsync(DiceExpression.D66, CancellationToken.None);

        Assert.False(roll.IsCompleted);                                            // dice in the air: nothing to show
        Assert.Equal(["d6,d6"], roller.Requests.Select(r => string.Join(",", r)));
        roller.Settle(FakeDddiceRoller.Twod6(3, 5));                               // dddice's roll:finished
        Assert.Equal(35, await roll);

        var sum = provider.RollAsync(DiceExpression.Parse("2d6"), CancellationToken.None);
        roller.Settle(FakeDddiceRoller.Twod6(3, 5));
        Assert.Equal(8, await sum);
    }

    [Fact]
    public void Faces_outside_a_d6_are_rejected_for_d66()
    {
        Assert.Throws<DddiceException>(() => DddiceDiceMapping.Interpret(DiceExpression.D66, [new("d6", 7), new("d6", 1)]));
        Assert.Throws<DddiceException>(() => DddiceDiceMapping.Interpret(DiceExpression.D66, [new("d6", 3), new("d8", 5)]));
        Assert.Throws<DddiceException>(() => DddiceDiceMapping.Interpret(DiceExpression.D66, [new("d6", 3)]));
    }

    [Fact]
    public async Task The_tracker_still_completes_a_d66_only_on_roll_finished()
    {
        var tracker = new DddiceRollTracker();
        var settled = tracker.Begin("mine");
        tracker.OnPageMessage("""{"kind":"roll:started","roll":{"uuid":"u","externalId":"mine","values":[{"type":"d6","value":3},{"type":"d6","value":5}]}}""");
        Assert.False(settled.IsCompleted);

        tracker.OnPageMessage("""{"kind":"roll:finished","roll":{"uuid":"u","externalId":"mine","values":[{"type":"d6","value":3},{"type":"d6","value":5}]}}""");

        Assert.Equal(35, DddiceDiceMapping.Interpret(DiceExpression.D66, await settled).Final);
    }
}

/// <summary>The roll screen: manual entry, display, resolution and history for a d66 table.</summary>
public class D66RollSessionTests
{
    private static RollableTable Table() => Fixtures.Table(DiceExpression.D66,
        (11, 16, "A"), (21, 26, "B"), (31, 36, "C"), (41, 46, "D"), (51, 56, "E"), (61, 66, "F"));

    [Theory]
    [InlineData("11", "A")] [InlineData("16", "A")] [InlineData("35", "C")] [InlineData("61", "F")] [InlineData("66", "F")]
    public void A_legal_manual_d66_value_resolves_exactly_as_typed(string typed, string expected)
    {
        var dice = new FixedDice(99);
        var history = new List<RollSnapshot>();
        var session = new RollViewModel(Table(), dice, rolled: history.Add) { ManualRollText = typed };

        session.ResolveManualCommand.Execute(null);

        Assert.Equal(expected, Assert.Single(session.Results).Text);
        Assert.Equal($"Rolled {typed} (d66)", session.RollDisplay);
        Assert.Equal(0, dice.Calls);                                       // manual entry never rolls anything
        Assert.Equal("", session.Message);
        var snapshot = Assert.Single(history);
        Assert.Equal(("d66", int.Parse(typed)), (snapshot.DiceText, snapshot.RollValue));
    }

    [Theory]
    [InlineData("10")] [InlineData("17")] [InlineData("20")] [InlineData("27")] [InlineData("30")] [InlineData("60")] [InlineData("67")] [InlineData("0")] [InlineData("100")]
    public void An_impossible_manual_d66_value_is_refused_with_an_explanation_and_changes_nothing(string typed)
    {
        var history = new List<RollSnapshot>();
        var session = new RollViewModel(Table(), new FixedDice(99), rolled: history.Add) { ManualRollText = typed };

        session.ResolveManualCommand.Execute(null);

        Assert.Empty(session.Results);
        Assert.Empty(history);
        Assert.Equal($"{typed} is not a possible d66 result. Enter two digits from 1 to 6, such as 35.", session.Message);
    }

    [Theory]
    [InlineData("")] [InlineData("abc")] [InlineData("3 5")]
    public void Text_that_is_not_a_number_is_refused_too(string typed)
    {
        var session = new RollViewModel(Table(), new FixedDice(99)) { ManualRollText = typed };

        session.ResolveManualCommand.Execute(null);

        Assert.Empty(session.Results);
        Assert.Equal("Enter a d66 result: two digits from 1 to 6, such as 35.", session.Message);
    }

    [Fact]
    public void The_screen_names_the_dice_d66_not_2d6_and_does_not_present_a_sum()
    {
        var session = new RollViewModel(Table(), new FixedDice(35));

        Assert.StartsWith("d66", session.DiceInfo);
        Assert.DoesNotContain("2d6", session.DiceInfo);
        session.RollCommand.Execute(null);
        Assert.Equal("Rolled 35 (d66)", session.RollDisplay);
    }

    [Fact]
    public void A_built_in_d66_roll_with_known_faces_resolves_the_concatenated_value()
    {
        var history = new List<RollSnapshot>();
        var session = new RollViewModel(Table(), new BuiltInDiceProvider(new SequenceRandom(3, 5)), rolled: history.Add);

        session.RollCommand.Execute(null);

        Assert.Equal("Rolled 35 (d66)", session.RollDisplay);
        Assert.Equal("C", Assert.Single(session.Results).Text);            // 35 is in 31-36; the sum 8 would have matched nothing
        Assert.Equal(35, Assert.Single(history).RollValue);
    }

    [Fact]
    public async Task A_dddice_d66_shows_nothing_until_the_dice_settle_then_records_d66_35()
    {
        var roller = new FakeDddiceRoller();
        var providers = new DiceProviderViewModel(new FixedDice(1), new DddiceDiceProvider(roller));
        providers.Select(DiceProviderKind.Dddice);
        await Wait.Until(() => providers.State == DiceProviderState.Ready, "ready");
        var history = new List<RollSnapshot>();
        var session = new RollViewModel(Table(), providers, rolled: history.Add, diceReady: () => providers.CanRoll);

        session.RollCommand.Execute(null);

        Assert.True(session.IsRolling);
        Assert.Empty(session.Results);                                      // the result stays hidden while the dice roll
        Assert.Empty(history);
        Assert.Equal(["d6,d6"], roller.Requests.Select(r => string.Join(",", r)));

        roller.Settle(FakeDddiceRoller.Twod6(3, 5));
        await session.RollTask;

        Assert.Equal("Rolled 35 (d66)", session.RollDisplay);
        Assert.Equal("C", Assert.Single(session.Results).Text);
        var snapshot = Assert.Single(history);
        Assert.Equal(("d66", 35), (snapshot.DiceText, snapshot.RollValue));
    }

    [Fact]
    public void A_recent_roll_reads_d66_35_from_the_stored_snapshot()
    {
        var item = new RollHistoryItem(1, null, "Encounters", "d66", 35, "C", DateTime.UtcNow);

        Assert.Equal("35", item.RollDisplay);
        Assert.Contains("d66 → 35", item.FullText);
    }
}

/// <summary>Validation against the discrete d66 domain.</summary>
public class D66ValidatorTests
{
    private static IReadOnlyList<ValidationFinding> Validate(params (int Min, int Max)[] rows) =>
        TableValidator.Validate(Fixtures.Table(DiceExpression.D66, rows.Select(r => (r.Min, r.Max, "x")).ToArray()));

    private static readonly (int, int)[] Complete = [(11, 16), (21, 26), (31, 36), (41, 46), (51, 56), (61, 66)];

    [Fact]
    public void Complete_d66_coverage_has_no_findings_and_never_reports_the_impossible_numbers_as_gaps()
    {
        Assert.Empty(Validate(Complete));   // no gap at 17-20, 27-30, 37-40, 47-50, 57-60
    }

    [Fact]
    public void Single_value_rows_covering_all_36_results_are_complete_too()
    {
        var rows = (from tens in Enumerable.Range(1, 6) from ones in Enumerable.Range(1, 6) select (tens * 10 + ones, tens * 10 + ones)).ToArray();

        Assert.Empty(Validate(rows));
    }

    [Fact]
    public void Smaller_ranges_inside_a_tens_row_are_valid()
    {
        Assert.Empty(Validate((11, 13), (14, 16), (21, 26), (31, 36), (41, 46), (51, 56), (61, 66)));
    }

    [Fact]
    public void A_whole_missing_tens_row_is_one_gap_of_possible_results_only()
    {
        var finding = Assert.Single(Validate((11, 16), (21, 26), (41, 46), (51, 56), (61, 66)));

        Assert.Equal((ValidationKind.Gap, 31, 36), (finding.Kind, finding.Start, finding.End));
    }

    [Fact]
    public void A_missing_16_and_a_missing_21_are_two_gaps_not_one_range_through_the_impossible_numbers()
    {
        var findings = Validate((11, 15), (22, 26), (31, 36), (41, 46), (51, 56), (61, 66));

        Assert.Equal([(ValidationKind.Gap, 16, 16), (ValidationKind.Gap, 21, 21)], findings.Select(f => (f.Kind, f.Start, f.End)));
    }

    [Fact]
    public void An_empty_table_reports_each_tens_row_as_missing_and_nothing_else()
    {
        var findings = Validate();

        Assert.Equal(6, findings.Count);
        Assert.All(findings, f => Assert.Equal(ValidationKind.Gap, f.Kind));
        Assert.Equal([11, 21, 31, 41, 51, 61], findings.Select(f => f.Start));
        Assert.Equal([16, 26, 36, 46, 56, 66], findings.Select(f => f.End));
    }

    [Fact]
    public void A_row_for_an_impossible_result_is_flagged_as_such()
    {
        var findings = Validate((11, 16), (17, 17), (21, 26), (31, 36), (41, 46), (51, 56), (61, 66));

        var finding = Assert.Single(findings);
        Assert.Equal((ValidationKind.ImpossibleValue, 17, 17), (finding.Kind, finding.Start, finding.End));
        Assert.Equal([1], finding.EntryIndexes);
    }

    [Fact]
    public void A_range_crossing_impossible_values_is_flagged_for_exactly_the_impossible_stretch()
    {
        // 15-22 holds 15, 16, 21, 22 (possible) and 17, 18, 19, 20 (not). It is questionable, not a clean continuous range.
        var findings = Validate((11, 14), (15, 22), (23, 26), (31, 36), (41, 46), (51, 56), (61, 66));

        var finding = Assert.Single(findings);
        Assert.Equal((ValidationKind.ImpossibleValue, 17, 20), (finding.Kind, finding.Start, finding.End));
        Assert.Equal([1], finding.EntryIndexes);
    }

    [Fact]
    public void One_row_across_several_tens_flags_each_impossible_stretch_separately()
    {
        var findings = Validate((11, 36));

        Assert.Equal([(ValidationKind.ImpossibleValue, 17, 20), (ValidationKind.ImpossibleValue, 27, 30)],
            findings.Where(f => f.Kind == ValidationKind.ImpossibleValue).Select(f => (f.Kind, f.Start, f.End)));
        Assert.Contains(findings, f => f.Kind == ValidationKind.Gap && f.Start == 41);   // and the missing results are still found
    }

    [Fact]
    public void Rows_below_11_or_above_66_are_reported_like_any_out_of_range_row()
    {
        var findings = Validate((1, 16), (21, 26), (31, 36), (41, 46), (51, 56), (61, 70));

        Assert.Contains(findings, f => f is { Kind: ValidationKind.BelowMinimum, Start: 1, End: 10 });
        Assert.Contains(findings, f => f is { Kind: ValidationKind.AboveMaximum, Start: 67, End: 70 });
        Assert.DoesNotContain(findings, f => f.Kind == ValidationKind.Gap);
    }

    [Fact]
    public void Overlapping_rows_are_reported_only_over_possible_results()
    {
        var overlap = Validate((11, 16), (16, 26), (31, 36), (41, 46), (51, 56), (61, 66));
        Assert.Contains(overlap, f => f is { Kind: ValidationKind.Overlap, Start: 16, End: 16 });

        // two rows sharing only impossible numbers (18-19) overlap nowhere that can occur: each is flagged as impossible, not as an overlap
        var shared = Validate((11, 16), (17, 20), (18, 19), (21, 26), (31, 36), (41, 46), (51, 56), (61, 66));
        Assert.DoesNotContain(shared, f => f.Kind == ValidationKind.Overlap);
        Assert.Equal(2, shared.Count(f => f.Kind == ValidationKind.ImpossibleValue));
    }

    [Fact]
    public void Ordinary_dice_are_validated_exactly_as_before()
    {
        // 2d6 is a continuous 2-12: a row for 17 is simply above the maximum, and 13-20 gaps do not exist.
        var findings = TableValidator.Validate(Fixtures.Table(DiceExpression.Parse("2d6"), (2, 6, "a"), (7, 12, "b"), (17, 17, "c")));

        var finding = Assert.Single(findings);
        Assert.Equal((ValidationKind.AboveMaximum, 17, 17), (finding.Kind, finding.Start, finding.End));
        Assert.Empty(TableValidator.Validate(Fixtures.Table(DiceExpression.Parse("2d6"), (2, 6, "a"), (7, 12, "b"))));
    }

    [Fact]
    public void A_table_resolves_a_d66_value_and_a_value_that_cannot_occur_matches_nothing()
    {
        var table = Fixtures.Table(DiceExpression.D66, Complete.Select(r => (r.Item1, r.Item2, "x")).ToArray());

        Assert.Equal(ResolutionStatus.Matched, TableResolver.Resolve(table, 35).Results[0].Status);
        Assert.Equal(ResolutionStatus.NoMatch, TableResolver.Resolve(table, 17).Results[0].Status);
    }
}

/// <summary>Import headings, row labels and the Review screen for d66 tables.</summary>
public class D66ImportAndReviewTests
{
    private const string Rows = "\n11-16 Nothing\n21-26 A patrol\n31-36 A trap\n41-46 A merchant\n51-56 A ghost\n61-66 A dragon";

    [Theory]
    [InlineData("D66 RANDOM ENCOUNTER", "Random Encounter")]
    [InlineData("D66 TREASURE", "Treasure")]
    [InlineData("NAME GENERATOR (D66)", "Name Generator")]
    [InlineData("d66 WANDERING THINGS", "Wandering Things")]
    public void Headings_with_d66_are_d66_tables(string heading, string name)
    {
        var draft = TableTextParser.Parse(heading + Rows);

        Assert.Equal("d66", draft.DiceText);
        Assert.Equal(name, draft.TableName);
        Assert.Equal(6, draft.ResultSets.Single().Entries.Count);
        Assert.True(draft.TryBuildTable(1, out var table, out var errors), string.Join("; ", errors));
        Assert.Equal(DiceExpression.D66, table!.Dice);
    }

    [Fact]
    public void A_2d6_heading_stays_2d6_even_with_rows_labelled_11_to_66()
    {
        var draft = TableTextParser.Parse("2D6 RANDOM ENCOUNTER" + Rows);

        Assert.Equal("2d6", draft.DiceText);
        Assert.Equal(DiceExpression.Parse("2d6"), DiceExpression.Parse(draft.DiceText));   // the rows never change the convention
    }

    [Fact]
    public void A_d20_heading_with_d66_looking_rows_is_still_a_d20()
    {
        Assert.Equal("d20", TableTextParser.Parse("D20 THINGS" + Rows).DiceText);
    }

    [Fact]
    public void Ordinary_d66_row_labels_are_read_as_plain_numbers_and_ranges()
    {
        var draft = TableTextParser.Parse("D66 LABELS\n11 One\n12 Two\n13-16 Three to six\n21-24 Some\n25-26 More\n31-66 Rest");
        var entries = draft.ResultSets.Single().Entries;

        Assert.Equal(["11", "12", "13-16", "21-24", "25-26", "31-66"], entries.Select(e => e.RangeText));
    }

    [Fact]
    public void After_a_row_number_on_its_own_line_the_next_d66_row_16_then_21_is_a_new_row_not_wrapped_text()
    {
        // The parser keeps a numbered line as text only if it does not CONTINUE the numbering; on a d66 the number after 16 is 21.
        var draft = TableTextParser.Parse("D66 STANDALONE\n11-16\nFirst text\n21-26 Second text\n31-36 Third text");

        Assert.Equal(["11-16", "21-26", "31-36"], draft.ResultSets.Single().Entries.Select(e => e.RangeText));
        Assert.DoesNotContain(draft.Issues, i => i.Code == ParseIssueCode.NumberedLineKeptAsText);
    }

    [Fact]
    public void A_complete_d66_paste_has_no_parser_issues_and_no_false_coverage_warnings()
    {
        var draft = TableTextParser.Parse("D66 RANDOM ENCOUNTER" + Rows);

        Assert.DoesNotContain(draft.Issues, i => i.Severity != ParseIssueSeverity.Info);
    }

    [Fact]
    public void D66_with_a_modifier_in_a_heading_is_refused_with_the_reason()
    {
        var draft = TableTextParser.Parse("D66+1 ODDITIES" + Rows);

        var issue = Assert.Single(draft.Issues, i => i.Code == ParseIssueCode.UnsupportedDice);
        Assert.Contains("A d66 takes no modifier.", issue.Message);
    }

    private static (MainViewModel Main, ReviewViewModel Review) Review(string text)
    {
        var temp = new TempDatabase();
        var db = temp.Open();
        db.CreateCollection("C");
        var main = new MainViewModel(db, new FixedDice(1));
        main.PasteTableCommand.Execute(null);
        ((PasteViewModel)main.Current!).SourceText = text;
        ((PasteViewModel)main.Current!).InterpretCommand.Execute(null);
        return (main, (ReviewViewModel)main.Current!);
    }

    [Fact]
    public void The_review_screen_accepts_a_complete_d66_table_with_no_notes_and_saves_it()
    {
        var (_, review) = Review("D66 RANDOM ENCOUNTER" + Rows);

        Assert.Equal("d66", review.DiceText);
        Assert.Empty(review.ValidationNotes);
        Assert.True(review.CanSave);
    }

    [Fact]
    public void The_dice_field_accepts_d66_and_switching_between_d66_and_2d6_changes_the_meaning_of_the_rows()
    {
        var (_, review) = Review("D66 RANDOM ENCOUNTER" + Rows);

        review.DiceText = "2d6";
        Assert.Contains(review.ValidationNotes, n => n.Contains("above the highest roll (12)"));   // 2d6 cannot make 21-66

        review.DiceText = "D66";
        Assert.Empty(review.ValidationNotes);
    }

    [Fact]
    public void An_impossible_row_gets_an_understandable_note_and_a_missing_result_is_a_gap()
    {
        var (_, review) = Review("D66 ODDITIES\n11-16 One\n17 Seventeen\n21-26 Two\n31-36 Three\n41-46 Four\n51-56 Five");

        Assert.Contains(review.ValidationNotes, n => n == "Row 2: 17 is not a possible d66 result (each digit must be 1 to 6).");
        Assert.Contains(review.ValidationNotes, n => n == "No row covers 61–66.");
        Assert.DoesNotContain(review.ValidationNotes, n => n.Contains("17–20"));
    }

    [Fact]
    public void A_range_crossing_impossible_numbers_is_named_as_impossible_results()
    {
        var (_, review) = Review("D66 ODDITIES\n11-14 One\n15-22 Two\n23-26 Three\n31-36 Four\n41-46 Five\n51-56 Six\n61-66 Seven");

        Assert.Contains(review.ValidationNotes, n => n == "Row 2: 17–20 are not possible d66 results (each digit must be 1 to 6).");
    }

    [Fact]
    public void The_dice_field_explains_that_d66_takes_no_modifier()
    {
        var (_, review) = Review("D66 RANDOM ENCOUNTER" + Rows);

        review.DiceText = "d66+1";

        Assert.False(review.CanSave);
        Assert.Contains(review.Blockers, b => b.Contains("'d66+1'") && b.Contains("A d66 takes no modifier."));
    }
}

/// <summary>Schema version 4 (the roll convention), real temporary SQLite files.</summary>
public class D66PersistenceTests
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

    [Fact]
    public void A_d66_table_round_trips_through_save_load_lists_and_recents_as_two_d6_with_the_d66_convention()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = db.CreateCollection("C");
        var table = db.SaveTable(Fixtures.Table(DiceExpression.D66, (11, 16, "A"), (21, 26, "B")).Also(t => t.CollectionId = c.Id));
        db.MarkTableUsed(table.Id);

        Assert.Equal(DiceExpression.D66, db.LoadTable(table.Id)!.Dice);
        Assert.True(db.LoadTable(table.Id)!.Dice.IsD66);
        Assert.Equal(DiceExpression.D66, db.GetTableSummaries(c.Id).Single().Dice);
        Assert.Equal(DiceExpression.D66, db.GetRecentTables(c.Id).Single().Dice);
        Assert.Equal((2L, 6L, 0L, 1L), (Scalar(temp.Path, "SELECT DiceCount FROM Tables"), Scalar(temp.Path, "SELECT DiceSides FROM Tables"),
            Scalar(temp.Path, "SELECT DiceModifier FROM Tables"), Scalar(temp.Path, "SELECT DiceConvention FROM Tables")));
    }

    [Fact]
    public void A_2d6_table_round_trips_unchanged_beside_a_d66_table()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = db.CreateCollection("C");
        var twoD6 = db.SaveTable(Fixtures.Table(DiceExpression.Parse("2d6+1"), (3, 13, "x")).Also(t => { t.CollectionId = c.Id; t.Name = "Sum"; }));
        db.SaveTable(Fixtures.Table(DiceExpression.D66, (11, 16, "A")).Also(t => { t.CollectionId = c.Id; t.Name = "Tens"; }));

        var loaded = db.LoadTable(twoD6.Id)!.Dice;
        Assert.Equal(new DiceExpression(2, 6, 1), loaded);
        Assert.Equal(RollConvention.StandardSum, loaded.Convention);
        Assert.Equal("2d6+1", loaded.ToString());
        Assert.Equal(["2d6+1", "d66"], db.GetTableSummaries(c.Id).OrderBy(s => s.Name == "Sum" ? 0 : 1).Select(s => s.Dice.ToString()));
    }

    [Fact]
    public void Editing_a_table_between_2d6_and_d66_replaces_the_convention()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var table = db.SaveTable(Fixtures.Table(DiceExpression.Parse("2d6"), (2, 12, "x")).Also(t => t.CollectionId = db.CreateCollection("C").Id));

        table.Dice = DiceExpression.D66;
        db.SaveTable(table);
        Assert.Equal(DiceExpression.D66, db.LoadTable(table.Id)!.Dice);

        table.Dice = DiceExpression.Parse("2d6");
        db.SaveTable(table);
        Assert.Equal(DiceExpression.Parse("2d6"), db.LoadTable(table.Id)!.Dice);
    }

    [Fact]
    public void The_convention_column_only_accepts_the_two_known_conventions()
    {
        using var temp = new TempDatabase();
        using (var db = temp.Open()) db.SaveTable(Fixtures.Table(DiceExpression.Parse("d20"), (1, 20, "x")).Also(t => t.CollectionId = db.CreateCollection("C").Id));

        using var raw = Raw(temp.Path);
        raw.Open();
        Assert.Throws<SqliteException>(() => Exec(raw, "UPDATE Tables SET DiceConvention = 2"));
    }

    [Fact]
    public void A_version_3_database_migrates_to_4_with_every_existing_dice_still_standard_and_all_data_intact()
    {
        using var temp = new TempDatabase();
        using (var raw = Raw(temp.Path))
        {
            raw.Open();
            DatabaseMigrations.Apply(raw, upToVersion: 3);
            Assert.Equal(3, DatabaseMigrations.GetVersion(raw));
            Exec(raw, """
                INSERT INTO Collections (Id, Name, CreatedUtc) VALUES (1, 'Campaign', '2026-01-01T00:00:00.0000000Z');
                INSERT INTO Tables (Id, CollectionId, Name, DiceCount, DiceSides, DiceModifier, CreatedUtc, UpdatedUtc, LastUsedUtc) VALUES
                    (10, 1, 'Loot',      1, 20, 0,  '2026-01-02T00:00:00.0000000Z', '2026-01-02T00:00:00.0000000Z', '2026-02-01T00:00:00.0000000Z'),
                    (11, 1, 'Reaction',  2,  6, 1,  '2026-01-03T00:00:00.0000000Z', '2026-01-03T00:00:00.0000000Z', '2026-02-02T00:00:00.0000000Z'),
                    (12, 1, 'Sum',       2,  6, 0,  '2026-01-04T00:00:00.0000000Z', '2026-01-04T00:00:00.0000000Z', NULL),
                    (13, 1, 'Old d66',   1, 66, 0,  '2026-01-05T00:00:00.0000000Z', '2026-01-05T00:00:00.0000000Z', NULL),
                    (14, 1, 'Pct',       1, 100, -5,'2026-01-06T00:00:00.0000000Z', '2026-01-06T00:00:00.0000000Z', NULL);
                INSERT INTO ResultSets (Id, TableId, Name, SortOrder) VALUES (100, 10, '', 0), (101, 11, 'Mood', 0), (102, 12, '', 0);
                INSERT INTO Entries (Id, ResultSetId, MinValue, MaxValue, DisplayText, DisplayRange, LinkedTableId, UnresolvedLinkName, SortOrder) VALUES
                    (1000, 100, 1, 20, 'Rusty nails', NULL, NULL, NULL, 0),
                    (1001, 101, 3, 8, 'Hostile', NULL, 10, NULL, 0),
                    (1002, 101, 9, 13, 'Friendly', NULL, NULL, 'Missing Table', 1),
                    (1003, 102, 2, 12, 'Anything', NULL, NULL, NULL, 0);
                INSERT INTO RollHistory (Id, TableId, TableName, DiceText, RollValue, ResultText, RolledUtc) VALUES
                    (1, 12, 'Sum', '2d6', 8, 'Anything', '2026-02-03T00:00:00.0000000Z'),
                    (2, 11, 'Reaction', '2d6+1', 9, 'Mood: Friendly', '2026-02-04T00:00:00.0000000Z');
                """);
        }

        using var db = temp.Open();                                           // migrates on open

        Assert.Equal(4, DatabaseMigrations.CurrentVersion);
        Assert.Equal(4L, Scalar(temp.Path, "PRAGMA user_version"));
        Assert.Equal(0L, Scalar(temp.Path, "SELECT COUNT(*) FROM Tables WHERE DiceConvention <> 0"));   // nothing became a d66 by itself
        Assert.Equal(5L, Scalar(temp.Path, "SELECT COUNT(*) FROM Tables"));

        var dice = db.GetTableSummaries(1).OrderBy(t => t.Id).Select(t => t.Dice).ToArray();
        Assert.Equal([new DiceExpression(1, 20, 0), new DiceExpression(2, 6, 1), new DiceExpression(2, 6, 0), new DiceExpression(1, 66, 0), new DiceExpression(1, 100, -5)], dice);
        Assert.All(dice, d => Assert.Equal(RollConvention.StandardSum, d.Convention));
        Assert.False(db.LoadTable(12)!.Dice.IsD66);                           // the plain 2d6 table still sums
        Assert.Equal(DiceExpression.Parse("2d6"), db.LoadTable(12)!.Dice);
        Assert.Equal("1d66", db.LoadTable(13)!.Dice.ToString());              // an old single 66-sided die is shown with its count, never as d66

        var reaction = db.LoadTable(11)!;                                     // modifier, result set, entries and links survive
        Assert.Equal("Mood", reaction.ResultSets.Single().Name);
        Assert.Equal(["Hostile", "Friendly"], reaction.ResultSets[0].Entries.Select(e => e.Text));
        Assert.Equal(10L, reaction.ResultSets[0].Entries[0].LinkedTableId);
        Assert.Equal("Missing Table", reaction.ResultSets[0].Entries[1].UnresolvedLinkName);
        Assert.Equal(["Reaction", "Sum"], db.GetRollHistory().Select(h => h.TableName));   // history (newest first)
        Assert.Equal(["2d6+1", "2d6"], db.GetRollHistory().Select(h => h.DiceText));
        Assert.Equal(new long[] { 11, 10 }, db.GetRecentTables(1).Select(t => t.Id).ToArray());  // recent usage

        // and a table can now be saved as d66 into the migrated database
        var made = db.SaveTable(Fixtures.Table(DiceExpression.D66, (11, 16, "A")).Also(t => t.CollectionId = 1));
        Assert.Equal(DiceExpression.D66, db.LoadTable(made.Id)!.Dice);
    }

    [Fact]
    public void The_saved_dice_provider_choice_file_is_unaffected_by_the_migration()
    {
        // The dddice/Built-in choice lives in dice-provider.txt, outside the database: the migration cannot touch it.
        using var temp = new TempDatabase();
        var pref = System.IO.Path.ChangeExtension(temp.Path, ".txt");
        File.WriteAllText(pref, "dddice");
        try
        {
            using (var raw = Raw(temp.Path)) { raw.Open(); DatabaseMigrations.Apply(raw, upToVersion: 3); }
            using var db = temp.Open();
            Assert.Equal("dddice", File.ReadAllText(pref));
        }
        finally { File.Delete(pref); }
    }
}

/// <summary>The developer data-folder override that lets a published build be smoke-tested without touching real data.</summary>
public class DataFolderOverrideTests
{
    [Fact]
    public void Without_an_override_the_normal_per_user_folder_is_used()
    {
        var normal = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TableForge");

        Assert.Equal(normal, AppDatabase.ResolveDataFolder(null));
        Assert.Equal(normal, AppDatabase.ResolveDataFolder(""));
        Assert.Equal(normal, AppDatabase.ResolveDataFolder("   "));
    }

    [Fact]
    public void An_override_replaces_the_folder_entirely()
    {
        var elsewhere = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tf-override-check");

        Assert.Equal(elsewhere, AppDatabase.ResolveDataFolder(elsewhere));
        Assert.Equal(elsewhere, AppDatabase.ResolveDataFolder("  " + elsewhere + " "));
        Assert.Equal("TABLEFORGE_DATA_DIR", AppDatabase.DataFolderVariable);
    }
}
