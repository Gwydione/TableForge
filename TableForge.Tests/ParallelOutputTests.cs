using TableForge.Domain;
using TableForge.Import;

namespace TableForge.Tests;

/// <summary>Part C: printed columns that are separate outputs of the same roll become separate result sets.</summary>
public class ParallelOutputTests
{
    /// <summary>The real Broken Shores source: three printed columns (D8 | DIFFICULTY | MODIFIER).</summary>
    public const string Difficulty =
        "D8 DIFFICULTY MODIFIER\n" +
        "1 Child's play +30\n" +
        "2 Effortless +20\n" +
        "3 Easy +10\n" +
        "4-5 Normal +0\n" +
        "6 Demanding -10\n" +
        "7 Hard -20\n" +
        "8 Impossible -30";

    /// <summary>Generalizes <see cref="Difficulty"/> to three outputs (four printed columns), no column-gap evidence.</summary>
    public const string DifficultyModifierSave =
        "D8 DIFFICULTY MODIFIER SAVE\n" +
        "1 Child's play +30 1d4\n" +
        "2 Effortless +20 1d4\n" +
        "3 Easy +10 1d6\n" +
        "4-5 Normal +0 2d4\n" +
        "6 Demanding -10 2d6\n" +
        "7 Hard -20 2d6\n" +
        "8 Impossible -30 3d6";

    /// <summary>
    /// A real source whose heading has more words than columns (WIND TYPE | STRENGTH | HULL DAMAGE CAUSED), with a
    /// multi-word first column, a plain-number column, a dice-text column and a dash standing for "no damage".
    /// </summary>
    public const string Wind =
        "D100 WIND TYPE STRENGTH HULL DAMAGE CAUSED\n" +
        "01-50 Calm 0 –\n" +
        "51-65 Breeze 5 D4\n" +
        "66-80 Moderate Wind 10 D6\n" +
        "81-90 Strong Wind 20 D6+2\n" +
        "91-99 Gale 40 D10+5\n" +
        "100 Hurricane 80 3D6+10";

    private static string[] Rows(ResultSetDraft s) => s.Entries.Select(e => $"{e.RangeText} | {e.Text}").ToArray();

    [Fact]
    public void Difficulty_modifier_becomes_two_result_sets_named_from_the_heading()
    {
        var draft = TableTextParser.Parse(Difficulty);

        Assert.Equal("Difficulty Modifier", draft.TableName);
        Assert.Equal("d8", draft.DiceText);
        Assert.Equal(["Difficulty", "Modifier"], draft.ResultSets.Select(s => s.Name).ToArray());
    }

    [Fact]
    public void Each_result_set_holds_its_own_column_against_the_same_ranges()
    {
        var draft = TableTextParser.Parse(Difficulty);

        Assert.Equal(
            ["1 | Child's play", "2 | Effortless", "3 | Easy", "4-5 | Normal", "6 | Demanding", "7 | Hard", "8 | Impossible"],
            Rows(draft.ResultSets[0]));
        Assert.Equal(
            ["1 | +30", "2 | +20", "3 | +10", "4-5 | +0", "6 | -10", "7 | -20", "8 | -30"],
            Rows(draft.ResultSets[1]));
        Assert.Equal(
            draft.ResultSets[0].Entries.Select(e => e.RangeText),
            draft.ResultSets[1].Entries.Select(e => e.RangeText));                       // the ranges align row for row
    }

    [Fact]
    public void The_split_is_reported_once_as_information()
    {
        var draft = TableTextParser.Parse(Difficulty);

        var issue = Assert.Single(draft.Issues);
        Assert.Equal((ParseIssueCode.ParallelOutputsSplit, ParseIssueSeverity.Info), (issue.Code, issue.Severity));
        Assert.Contains("Difficulty, Modifier", issue.Message);
        Assert.Contains("signed number", issue.Message);
    }

    [Fact]
    public void A_d8_roll_resolves_both_outputs_independently()
    {
        var table = Fixtures.ParseAndBuild(Difficulty);

        string[] Outputs(int roll) => TableResolver.Resolve(table, roll).Results.Select(r => r.Entry!.Text).ToArray();

        Assert.Equal(["Child's play", "+30"], Outputs(1));
        Assert.Equal(["Normal", "+0"], Outputs(4));
        Assert.Equal(["Normal", "+0"], Outputs(5));              // the 4-5 row covers both
        Assert.Equal(["Impossible", "-30"], Outputs(8));
        Assert.Empty(TableValidator.Validate(table));            // both sets fully cover d8
        Assert.Equal(new DiceExpression(1, 8), table.Dice);
    }

    [Fact]
    public void Both_result_sets_survive_a_save_and_reload()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var saved = db.SaveTable(Fixtures.ParseAndBuild(Difficulty, db.CreateCollection("C").Id));

        var loaded = db.LoadTable(saved.Id)!;

        Assert.Equal(["Difficulty", "Modifier"], loaded.ResultSets.Select(s => s.Name).ToArray());
        Assert.Equal(7, loaded.ResultSets[0].Entries.Count);
        Assert.Equal(7, loaded.ResultSets[1].Entries.Count);
        Assert.Equal(["Demanding", "-10"], TableResolver.Resolve(loaded, 6).Results.Select(r => r.Entry!.Text).ToArray());
    }

    [Fact]
    public void The_rolled_number_shows_once_with_both_outputs_in_recent_rolls()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var collection = db.CreateCollection("C");
        db.SaveTable(Fixtures.ParseAndBuild(Difficulty, collection.Id));
        var main = new ViewModels.MainViewModel(db, new FixedDice(5));
        main.SelectedTable = main.Tables[0];

        ((ViewModels.RollViewModel)main.Current!).RollCommand.Execute(null);

        Assert.Equal("Difficulty: Normal\nModifier: +0", Assert.Single(main.RecentRolls).Item.ResultText);
    }

    // ---- other parallel layouts ------------------------------------------------------------------

    [Fact]
    public void Column_gaps_split_parallel_outputs_and_take_their_names_from_the_heading()
    {
        var draft = TableTextParser.Parse("D6 NAME TYPE\n1 Ash    Elf\n2 Bar    Orc\n3-4 Cor    Elf\n5-6 Dun    Dwarf");

        Assert.Equal(["Name", "Type"], draft.ResultSets.Select(s => s.Name).ToArray());
        Assert.Equal(["Ash", "Bar", "Cor", "Dun"], draft.ResultSets[0].Entries.Select(e => e.Text).ToArray());
        Assert.Equal(["Elf", "Orc", "Elf", "Dwarf"], draft.ResultSets[1].Entries.Select(e => e.Text).ToArray());
        var issue = Assert.Single(draft.Issues);
        Assert.Contains("separated by column gaps", issue.Message);
        Assert.Equal(ParseIssueSeverity.Info, issue.Severity);
    }

    [Fact]
    public void Three_gap_separated_outputs_become_three_result_sets()
    {
        var draft = TableTextParser.Parse("D6 NAME RACE AGE\n1 Ash    Elf    300\n2 Bar    Orc    18\n3-6 Cor    Human    40");

        Assert.Equal(["Name", "Race", "Age"], draft.ResultSets.Select(s => s.Name).ToArray());
        Assert.Equal(["300", "18", "40"], draft.ResultSets[2].Entries.Select(e => e.Text).ToArray());
    }

    [Fact]
    public void More_heading_words_than_columns_creates_unnamed_sets_and_asks_you_to_name_them()
    {
        var draft = TableTextParser.Parse("D6 HIT LOCATION DAMAGE TAKEN\n1 Head    Severe\n2 Arm    Light\n3-6 Leg    Moderate");

        Assert.Equal(["", ""], draft.ResultSets.Select(s => s.Name).ToArray());
        var issue = Assert.Single(draft.Issues);
        Assert.Equal(ParseIssueSeverity.Warning, issue.Severity);
        Assert.Contains("name them", issue.Message);
    }

    [Fact]
    public void Two_dice_style_outputs_are_split_but_flagged_more_strongly()
    {
        var draft = TableTextParser.Parse("D6 ENCOUNTER NUMBER\n1 Goblins 2d6\n2 Wolves 1d4\n3-6 Bandits 3d4");

        Assert.Equal(["Encounter", "Number"], draft.ResultSets.Select(s => s.Name).ToArray());
        Assert.Equal(["2d6", "1d4", "3d4"], draft.ResultSets[1].Entries.Select(e => e.Text).ToArray());
        Assert.Equal(ParseIssueSeverity.Warning, Assert.Single(draft.Issues).Severity);
    }

    /// <summary>
    /// Generalizes the Broken Shores case (one roll, two outputs) to three outputs (four printed columns) with no
    /// column-gap evidence at all — the shape real PDF extraction produces once column widths are tight enough that
    /// the gaps collapse to single spaces. The trailing-field split must peel one output per heading word, not just two.
    /// </summary>
    [Fact]
    public void Three_outputs_with_no_column_gaps_are_split_by_trailing_fields_alone()
    {
        var draft = TableTextParser.Parse(DifficultyModifierSave);

        Assert.Equal("d8", draft.DiceText);
        Assert.Equal(["Difficulty", "Modifier", "Save"], draft.ResultSets.Select(s => s.Name).ToArray());
        Assert.Equal(
            ["Child's play", "Effortless", "Easy", "Normal", "Demanding", "Hard", "Impossible"],
            draft.ResultSets[0].Entries.Select(e => e.Text).ToArray());
        Assert.Equal(["+30", "+20", "+10", "+0", "-10", "-20", "-30"], draft.ResultSets[1].Entries.Select(e => e.Text).ToArray());
        Assert.Equal(["1d4", "1d4", "1d6", "2d4", "2d6", "2d6", "3d6"], draft.ResultSets[2].Entries.Select(e => e.Text).ToArray());
        Assert.Equal(
            draft.ResultSets[0].Entries.Select(e => e.RangeText),
            draft.ResultSets[1].Entries.Select(e => e.RangeText));
        Assert.Equal(
            draft.ResultSets[0].Entries.Select(e => e.RangeText),
            draft.ResultSets[2].Entries.Select(e => e.RangeText));

        var issue = Assert.Single(draft.Issues);
        Assert.Equal(ParseIssueCode.ParallelOutputsSplit, issue.Code);
        Assert.Contains("signed number", issue.Message);
        Assert.Contains("dice expression", issue.Message);
    }

    // ---- more heading words than columns: typed trailing columns ---------------------------------

    [Fact]
    public void Wind_splits_into_three_aligned_unnamed_result_sets_by_its_typed_columns()
    {
        var draft = TableTextParser.Parse(Wind);

        Assert.Equal("d100", draft.DiceText);                               // D4, D6+2... are result text, not the roll
        Assert.Equal(3, draft.ResultSets.Count);
        Assert.All(draft.ResultSets, s => Assert.Equal("", s.Name));        // which words name which column is not guessed
        Assert.Equal(["Calm", "Breeze", "Moderate Wind", "Strong Wind", "Gale", "Hurricane"], draft.ResultSets[0].Entries.Select(e => e.Text).ToArray());
        Assert.Equal(["0", "5", "10", "20", "40", "80"], draft.ResultSets[1].Entries.Select(e => e.Text).ToArray());
        Assert.Equal(["–", "D4", "D6", "D6+2", "D10+5", "3D6+10"], draft.ResultSets[2].Entries.Select(e => e.Text).ToArray());
        Assert.All(draft.ResultSets, s => Assert.Equal(["01-50", "51-65", "66-80", "81-90", "91-99", "100"], s.Entries.Select(e => e.RangeText).ToArray()));

        var issue = Assert.Single(draft.Issues);
        Assert.Equal(ParseIssueCode.ParallelOutputsSplit, issue.Code);
        Assert.Equal(ParseIssueSeverity.Warning, issue.Severity);
        Assert.Contains("name them", issue.Message);
    }

    [Fact]
    public void Wind_named_in_review_saves_and_a_roll_of_55_gives_breeze_5_d4_in_aligned_columns()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var draft = TableTextParser.Parse(Wind);
        foreach (var (set, name) in draft.ResultSets.Zip(["Wind Type", "Strength", "Hull Damage Caused"]))
            set.Name = name;                                                // what the person does in Review
        Assert.True(draft.TryBuildTable(db.CreateCollection("C").Id, out var built, out var errors), string.Join("; ", errors));

        var loaded = db.LoadTable(db.SaveTable(built!).Id)!;

        Assert.Equal(new DiceExpression(1, 100), loaded.Dice);
        Assert.Equal(["Wind Type", "Strength", "Hull Damage Caused"], loaded.ResultSets.Select(s => s.Name).ToArray());
        Assert.Equal(["Breeze", "5", "D4"], TableResolver.Resolve(loaded, 55).Results.Select(r => r.Entry!.Text).ToArray());
        Assert.Equal(["Calm", "0", "–"], TableResolver.Resolve(loaded, 1).Results.Select(r => r.Entry!.Text).ToArray());
        Assert.Empty(TableValidator.Validate(loaded));
        Assert.True(new ViewModels.RollStepViewModel(loaded, isFirst: true).IsAligned);
    }

    [Fact]
    public void Rows_ending_only_in_plain_numbers_stay_one_result_set()
    {
        var draft = TableTextParser.Parse("D6 TREASURE FOUND TODAY\n1 Coins 50\n2 Gems 3\n3-6 Arrows 20");

        Assert.Single(draft.ResultSets);
        Assert.DoesNotContain(draft.Issues, i => i.Code == ParseIssueCode.ParallelOutputsSplit);
    }

    [Fact]
    public void Two_plain_number_columns_with_no_signed_or_dice_column_stay_one_result_set()
    {
        var draft = TableTextParser.Parse("D6 WEAPON COST AND WEIGHT\n1 Sword 10 3\n2 Long Bow 25 2\n3-6 Spear 5 4");

        Assert.Single(draft.ResultSets);
        Assert.DoesNotContain(draft.Issues, i => i.Code == ParseIssueCode.ParallelOutputsSplit);
    }

    [Fact]
    public void A_column_made_mostly_of_dashes_is_not_evidence_of_a_typed_column()
    {
        var draft = TableTextParser.Parse("D6 WIND TYPE STRENGTH HULL DAMAGE\n1 Calm 0 –\n2 Breeze 5 –\n3-4 Gale 40 –\n5-6 Storm 80 D6");

        Assert.Single(draft.ResultSets);
        Assert.DoesNotContain(draft.Issues, i => i.Code == ParseIssueCode.ParallelOutputsSplit);
    }

    // ---- what must stay one result set -----------------------------------------------------------

    [Fact]
    public void A_two_word_title_over_ordinary_rows_is_left_alone()
    {
        var draft = TableTextParser.Parse("D6 RANDOM ENCOUNTER\n1 Goblin ambush\n2 Wolves in the woods\n3-6 A quiet road");

        var set = Assert.Single(draft.ResultSets);
        Assert.Equal("", set.Name);
        Assert.Equal(3, set.Entries.Count);
        Assert.Empty(draft.Issues);
    }

    [Fact]
    public void Only_some_rows_ending_in_a_signed_number_is_not_enough()
    {
        var draft = TableTextParser.Parse("D8 DIFFICULTY MODIFIER\n1 Child's play +30\n2 Effortless +20\n3 Easy\n4-8 Hard -10");

        Assert.Single(draft.ResultSets);
        Assert.DoesNotContain(draft.Issues, i => i.Code == ParseIssueCode.ParallelOutputsSplit);
    }

    [Fact]
    public void Fewer_than_three_rows_is_not_enough_evidence()
    {
        var draft = TableTextParser.Parse("D8 DIFFICULTY MODIFIER\n1-4 Easy +10\n5-8 Hard -10");

        Assert.Single(draft.ResultSets);
    }

    [Fact]
    public void Three_heading_words_and_only_a_typed_trailing_field_is_not_guessed_at()
    {
        // Two outputs but three words: which words name which column? Do not guess.
        var draft = TableTextParser.Parse("D8 TASK DIFFICULTY MODIFIER\n1 Easy +10\n2-4 Moderate +0\n5-8 Hard -10");

        Assert.Single(draft.ResultSets);
        Assert.DoesNotContain(draft.Issues, i => i.Code == ParseIssueCode.ParallelOutputsSplit);
    }

    [Fact]
    public void Uneven_gap_counts_are_not_treated_as_columns()
    {
        var draft = TableTextParser.Parse("D6 NAME TYPE\n1 Ash    Elf\n2 Bar Orc\n3-6 Cor    Elf");

        Assert.Single(draft.ResultSets);
    }

    [Fact]
    public void A_table_with_a_price_column_and_no_gaps_and_a_heading_of_one_word_is_left_alone()
    {
        var draft = TableTextParser.Parse("D6 SHOP\n1 Rope +5\n2 Lamp +7\n3-6 Oil +9");

        Assert.Single(draft.ResultSets);
    }

    [Fact]
    public void Continuation_columns_are_never_mistaken_for_parallel_outputs()
    {
        var draft = TableTextParser.Parse(ContinuationColumnTests.Syllables(heading: "D100 SYLLABLE NAME"));

        Assert.Single(draft.ResultSets);
        Assert.Equal(50, draft.ResultSets[0].Entries.Count);
        Assert.DoesNotContain(draft.Issues, i => i.Code == ParseIssueCode.ParallelOutputsSplit);
    }

    [Fact]
    public void A_multi_result_set_table_with_headings_is_untouched()
    {
        var draft = TableTextParser.Parse(MultiSetParserTests.RoomFeatures);

        Assert.Equal(["Ambient", "Noise", "General Feature"], draft.ResultSets.Select(s => s.Name).ToArray());
        Assert.Empty(draft.Issues);
    }

    /// <summary>
    /// Two genuinely independent tables printed in side-by-side page columns (not one roll with several outputs) must
    /// stay split as side-by-side entries of one result set, never be reinterpreted as parallel outputs. This heading
    /// has three words — newly eligible for the trailing-field split now that it is not capped at exactly two — so it
    /// specifically exercises that the SideBySideSplit evidence still blocks the parallel-outputs rewrite for it.
    /// </summary>
    [Fact]
    public void Two_side_by_side_independent_tables_are_never_merged_into_parallel_outputs()
    {
        const string twoColumnsOfOneList =
            "D100 REGION FEATURE TYPE\n" +
            "01-05 Tavern        51-55 Well\n" +
            "06-10 Market        56-60 Bridge\n" +
            "11-15 Temple        61-65 Gate";

        var draft = TableTextParser.Parse(twoColumnsOfOneList);

        var set = Assert.Single(draft.ResultSets);
        Assert.Equal(6, set.Entries.Count);
        Assert.DoesNotContain(draft.Issues, i => i.Code == ParseIssueCode.ParallelOutputsSplit);
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.SideBySideSplit);
    }

    // ---- through the app -----------------------------------------------------------------------------

    [Fact]
    public void Difficulty_modifier_reviews_as_two_named_tabs_and_saves_and_rolls()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        db.CreateCollection("C");
        var main = new ViewModels.MainViewModel(db, new FixedDice(8));
        main.PasteTableCommand.Execute(null);
        ((ViewModels.PasteViewModel)main.Current!).SourceText = Difficulty;
        ((ViewModels.PasteViewModel)main.Current!).InterpretCommand.Execute(null);
        var review = (ViewModels.ReviewViewModel)main.Current!;

        Assert.Equal(["Difficulty", "Modifier"], review.ResultSets.Select(s => s.DisplayName).ToArray());
        Assert.Empty(review.ValidationNotes);
        Assert.Single(review.InfoNotes);                       // information, not a warning
        Assert.Empty(review.TableNotes);
        review.SaveCommand.Execute(null);

        var roll = (ViewModels.RollViewModel)main.Current!;
        roll.RollCommand.Execute(null);
        Assert.Equal([("Difficulty", "Impossible"), ("Modifier", "-30")], roll.Results.Select(r => (r.Heading, r.Text)).ToArray());
    }
}
