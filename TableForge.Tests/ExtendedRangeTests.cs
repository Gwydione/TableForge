using Microsoft.Data.Sqlite;
using TableForge.Data;
using TableForge.Domain;
using TableForge.Import;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>
/// RC23: extended and open-ended numeric ranges. A table may be authored for modified rolls ("-10-0" or "26+" on a d20);
/// such rows parse, save, resolve and validate as authored, open bounds are internal sentinels that are never shown or
/// exported as numbers, and Clamp never stands in for them.
/// </summary>
public class ExtendedRangeTests
{
    private const int Below = RangeBounds.OpenBelow;
    private const int Above = RangeBounds.OpenAbove;

    internal const string Injury =
        "D20 Injury\n" +
        "-10-0 The character is dead.\n" +
        "1-5 The character succumbs to their wounds...\n" +
        "6-10 The character is severely wounded...\n" +
        "11-15 The character's wounds force them to spend a week resting...\n" +
        "16-20 The character is in shock, but alive...\n" +
        "21-25 The character was simply knocked out...\n" +
        "26+ The character recovers immediately, ready to fight.";

    private static readonly string[] InjuryTexts =
    [
        "The character is dead.",
        "The character succumbs to their wounds...",
        "The character is severely wounded...",
        "The character's wounds force them to spend a week resting...",
        "The character is in shock, but alive...",
        "The character was simply knocked out...",
        "The character recovers immediately, ready to fight.",
    ];

    private static readonly (int, int, string?)[] InjuryBounds =
        [(-10, 0, null), (1, 5, null), (6, 10, null), (11, 15, null), (16, 20, null), (21, 25, null), (26, Above, "26+")];

    private static readonly DiceExpression D20 = DiceExpression.Parse("d20");

    private static void NoSentinel(string text)
    {
        Assert.DoesNotContain("2147483647", text);
        Assert.DoesNotContain("2147483648", text);
    }

    // ---- range syntax -----------------------------------------------------------------------------

    [Theory]
    [InlineData("-10-0", -10, 0, null)]
    [InlineData("-10–0", -10, 0, null)]
    [InlineData("−10–0", -10, 0, null)]        // a real minus sign, then an en dash
    [InlineData("-10 - 0", -10, 0, null)]
    [InlineData("-3--1", -3, -1, null)]
    [InlineData("-5", -5, -5, null)]
    [InlineData("1-5", 1, 5, null)]
    [InlineData("21-25", 21, 25, null)]
    [InlineData("26+", 26, Above, "26+")]
    [InlineData("20+", 20, Above, "20+")]
    [InlineData("20 or more", 20, Above, "20 or more")]
    [InlineData("20 OR MORE", 20, Above, "20 or more")]
    [InlineData("-10 or more", -10, Above, "-10 or more")]
    [InlineData("1 or less", Below, 1, "1 or less")]
    [InlineData("-5 or less", Below, -5, "-5 or less")]
    [InlineData("−5 or less", Below, -5, "-5 or less")]
    [InlineData(" 1  or  less ", Below, 1, "1 or less")]
    public void Signed_and_open_ended_ranges_parse_on_an_ordinary_d20(string text, int min, int max, string? display)
    {
        Assert.True(RangeText.TryParse(text, D20, out var range, out var error), error);
        Assert.Equal((min, max, display), (range.Min, range.Max, range.DisplayRange));
    }

    [Theory]
    [InlineData("01-05", "d100", 1, 5, "01–05")]
    [InlineData("96-00", "d100", 96, 100, "96–00")]
    [InlineData("00", "d100", 100, 100, "00")]
    [InlineData("1-2", "d10", 1, 2, null)]
    [InlineData("-1-0", "d20-2", -1, 0, null)]
    public void Existing_range_forms_are_unchanged(string text, string dice, int min, int max, string? display)
    {
        Assert.True(RangeText.TryParse(text, DiceExpression.Parse(dice), out var range, out _));
        Assert.Equal((min, max, display), (range.Min, range.Max, range.DisplayRange));
    }

    [Theory]
    [InlineData("10-0")]      // backwards
    [InlineData("1--3")]      // backwards: 1 to -3
    [InlineData("–10-0")]     // a leading en dash is never a minus sign
    [InlineData("—10-0")]     // nor an em dash
    [InlineData("–5 or less")]
    [InlineData("5-")]
    [InlineData("1-2-3")]
    [InlineData("+5")]
    [InlineData("26++")]
    [InlineData("26 +")]
    [InlineData("1 or lower")]
    [InlineData("20 or higher")]
    [InlineData("or less")]
    [InlineData("1 or lessons")]
    [InlineData("-5+-3")]
    [InlineData("9999999+")]
    public void Malformed_or_unsupported_ranges_are_still_refused(string text)
    {
        Assert.False(RangeText.TryParse(text, D20, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    [InlineData("61+")]
    [InlineData("11 or less")]
    [InlineData("50 or more")]
    public void Open_ended_ranges_are_refused_for_d66(string text)
    {
        Assert.False(RangeText.TryParse(text, DiceExpression.D66, out _, out var error));
        Assert.Equal("Open-ended ranges are not available for d66 tables.", error);
        Assert.True(RangeText.TryParse("11-16", DiceExpression.D66, out _, out _)); // ordinary d66 ranges are unchanged
    }

    [Fact]
    public void Labels_never_show_a_sentinel()
    {
        Assert.Equal("26+", RangeBounds.Label(26, Above));
        Assert.Equal("1 or less", RangeBounds.Label(Below, 1));
        Assert.Equal("-10–0", RangeBounds.Label(-10, 0));
        Assert.Equal("5", RangeBounds.Label(5, 5));
        Assert.Equal("96–00", RangeBounds.Label(96, 100, DiceExpression.Parse("d100")));
        Assert.Equal("26+", new TableEntry { Min = 26, Max = Above }.RangeLabel);          // even with no DisplayRange stored
        Assert.Equal("0 or less", new TableEntry { Min = Below, Max = 0 }.RangeLabel);
    }

    // ---- the Injury paste -------------------------------------------------------------------------

    [Fact]
    public void The_injury_paste_keeps_all_seven_rows_exactly()
    {
        var draft = TableTextParser.Parse(Injury);

        Assert.Equal(("Injury", "d20"), (draft.TableName, draft.DiceText));
        var set = Assert.Single(draft.ResultSets);
        Assert.Equal(["-10-0", "1-5", "6-10", "11-15", "16-20", "21-25", "26+"], set.Entries.Select(e => e.RangeText).ToArray());
        Assert.Equal(InjuryTexts, set.Entries.Select(e => e.Text).ToArray());   // -10-0 is not dropped; 26+ is not joined to 21-25
        Assert.Empty(draft.Issues);
    }

    [Fact]
    public void The_injury_table_builds_with_its_authored_bounds_and_written_forms()
    {
        var table = Fixtures.ParseAndBuild(Injury);

        Assert.Equal(InjuryBounds, table.ResultSets[0].Entries.Select(e => (e.Min, e.Max, e.DisplayRange)).ToArray());
        Assert.Equal(["-10–0", "1–5", "6–10", "11–15", "16–20", "21–25", "26+"], table.ResultSets[0].Entries.Select(e => e.RangeLabel).ToArray());
    }

    [Fact]
    public void One_or_less_and_twenty_plus_parse_as_open_rows()
    {
        var table = Fixtures.ParseAndBuild("D20 Morale\n1 or less The unit flees.\n2-19 The unit holds.\n20+ The unit charges.");

        Assert.Equal(
            new (int, int, string?, string)[] { (Below, 1, "1 or less", "The unit flees."), (2, 19, null, "The unit holds."), (20, Above, "20+", "The unit charges.") },
            table.ResultSets[0].Entries.Select(e => (e.Min, e.Max, e.DisplayRange, e.Text)).ToArray());
    }

    [Fact]
    public void A_real_minus_sign_row_parses()
    {
        var table = Fixtures.ParseAndBuild("D20 Fall\n−10–0 Dead\n1-20 Alive");
        Assert.Equal([(-10, 0), (1, 20)], table.ResultSets[0].Entries.Select(e => (e.Min, e.Max)).ToArray());
    }

    [Fact]
    public void A_wrapped_line_starting_with_a_negative_number_is_still_joined_to_its_row()
    {
        var draft = TableTextParser.Parse("D6 Traps\n1-2 Pit\n3 Blade: take damage and\n-5 to hit next round\n4-6 Nothing");

        var set = Assert.Single(draft.ResultSets);
        Assert.Equal(["1-2", "3", "4-6"], set.Entries.Select(e => e.RangeText).ToArray());
        Assert.Equal("Blade: take damage and -5 to hit next round", set.Entries[1].Text);
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.ContinuationJoined);
    }

    [Fact]
    public void Existing_negative_rows_for_dice_that_go_negative_still_parse()
    {
        var table = Fixtures.ParseAndBuild("d20-2 Random Event\n-1-0 Disaster\n1-18 Fine");
        Assert.Equal([(-1, 0), (1, 18)], table.ResultSets[0].Entries.Select(e => (e.Min, e.Max)).ToArray());
    }

    [Fact]
    public void An_open_row_followed_by_a_lower_open_row_does_not_overflow()
    {
        var draft = TableTextParser.Parse("D20 Odd\n1-19 A\n20+ B\n1 or less C");
        Assert.Equal(["1-19", "20+", "1 or less"], draft.ResultSets.SelectMany(s => s.Entries).Select(e => e.RangeText).ToArray());
    }

    [Fact]
    public void Nothing_follows_an_open_upper_bound()
    {
        Assert.Null(RangeBounds.Next(Above));
        Assert.Equal(27, RangeBounds.Next(26));
        Assert.Null(D20.NextLegal(Above));
        Assert.Null(DiceExpression.D66.NextLegal(Above));
    }

    [Fact]
    public void Open_ended_rows_on_a_d66_table_cannot_be_saved()
    {
        var draft = TableTextParser.Parse("D66 Odd\n11-56 A\n61+ B");
        Assert.False(draft.TryBuildTable(1, out _, out var errors));
        Assert.Contains(errors, e => e.Contains("Open-ended ranges are not available for d66 tables."));
    }

    // ---- resolution -------------------------------------------------------------------------------

    [Theory]
    [InlineData(-3, "The character is dead.")]
    [InlineData(-10, "The character is dead.")]
    [InlineData(0, "The character is dead.")]
    [InlineData(5, "The character succumbs to their wounds...")]
    [InlineData(25, "The character was simply knocked out...")]
    [InlineData(26, "The character recovers immediately, ready to fight.")]
    [InlineData(27, "The character recovers immediately, ready to fight.")]
    [InlineData(101000, "The character recovers immediately, ready to fight.")]
    public void Injury_values_resolve_to_their_authored_rows(int roll, string expected)
    {
        var result = Assert.Single(TableResolver.Resolve(Fixtures.ParseAndBuild(Injury), roll).Results);
        Assert.Equal(expected, result.Entry!.Text);
    }

    [Fact]
    public void An_uncovered_value_below_a_bounded_negative_row_is_still_no_match()
    {
        var result = Assert.Single(TableResolver.Resolve(Fixtures.ParseAndBuild(Injury), -11).Results);
        Assert.Equal(ResolutionStatus.NoMatch, result.Status);
    }

    [Theory]
    [InlineData(-1000, "Flee")]
    [InlineData(1, "Flee")]
    [InlineData(2, "Hold")]
    public void Open_below_rows_resolve_everything_under_their_bound(int roll, string expected)
    {
        var table = Fixtures.ParseAndBuild("D20 Morale\n1 or less Flee\n2-20 Hold");
        Assert.Equal(expected, Assert.Single(TableResolver.Resolve(table, roll).Results).Entry!.Text);
    }

    // ---- validation -------------------------------------------------------------------------------

    private static IReadOnlyList<ValidationFinding> Validate(string dice, params (int, int, string)[] rows) =>
        TableValidator.Validate(Fixtures.Table(DiceExpression.Parse(dice), rows));

    [Fact]
    public void Injury_rows_beyond_the_d20_are_reported_as_authored_extensions_only()
    {
        var findings = TableValidator.Validate(Fixtures.ParseAndBuild(Injury));

        Assert.All(findings, f => Assert.True(f.IsAuthoredExtension));
        Assert.Equal(
            [(ValidationKind.BelowMinimum, -10, 0), (ValidationKind.AboveMaximum, 21, 25), (ValidationKind.AboveMaximum, 26, Above)],
            findings.Select(f => (f.Kind, f.Start, f.End)).ToArray());
    }

    [Fact]
    public void The_injury_table_reviews_as_healthy_with_information_only()
    {
        using var temp = new TempDatabase();
        var review = Review(temp, Injury);

        Assert.True(review.CanSave);
        Assert.Empty(review.ValidationNotes);
        Assert.False(review.ResultSets[0].HasNotes);                          // no ⚠ on the tab
        Assert.Equal(
            [
                "Row 1 includes -10–0, outside the natural d20 range (1–20); only a modified roll reaches it.",
                "Row 6 includes 21–25, outside the natural d20 range (1–20); only a modified roll reaches it.",
                "Row 7 includes 26+, outside the natural d20 range (1–20); only a modified roll reaches it.",
            ],
            review.InfoNotes.ToArray());
        Assert.All(review.InfoNotes, NoSentinel);
        Assert.All(review.Rows, r => NoSentinel(r.Notes));
    }

    [Fact]
    public void A_set_that_leaves_part_of_its_dice_uncovered_still_warns_about_rows_beyond_them()
    {
        var findings = Validate("d20", (-10, 0, "Dead"), (1, 5, "a"), (7, 20, "b"));

        Assert.Contains(findings, f => f is { Kind: ValidationKind.Gap, Start: 6, End: 6 });
        var below = Assert.Single(findings, f => f.Kind == ValidationKind.BelowMinimum);
        Assert.False(below.IsAuthoredExtension);
    }

    [Fact]
    public void A_gap_between_extended_rows_above_the_dice_is_found()
    {
        var gap = Assert.Single(Validate("d20", (1, 20, "a"), (21, 25, "b"), (27, Above, "c")), f => f.Kind == ValidationKind.Gap);
        Assert.Equal((26, 26), (gap.Start, gap.End));
    }

    [Fact]
    public void A_gap_between_negative_rows_is_found()
    {
        var gap = Assert.Single(Validate("d20", (-10, -5, "a"), (-3, 0, "b"), (1, 20, "c")), f => f.Kind == ValidationKind.Gap);
        Assert.Equal((-4, -4), (gap.Start, gap.End));
    }

    [Fact]
    public void A_gap_after_an_open_below_row_is_found()
    {
        var gap = Assert.Single(Validate("d20", (Below, -5, "a"), (-3, 0, "b"), (1, 20, "c")), f => f.Kind == ValidationKind.Gap);
        Assert.Equal((-4, -4), (gap.Start, gap.End));
    }

    [Fact]
    public void A_table_open_at_both_ends_with_no_gaps_has_only_extension_findings()
    {
        var findings = Validate("d20", (Below, 0, "a"), (1, 20, "b"), (21, Above, "c"));
        Assert.All(findings, f => Assert.True(f.IsAuthoredExtension));
        Assert.DoesNotContain(findings, f => f.Kind == ValidationKind.Gap);
    }

    [Fact]
    public void Overlaps_with_open_rows_are_found_and_shown_without_sentinels()
    {
        var bounded = Validate("d20", (1, 19, "a"), (20, Above, "b"), (21, 25, "c"));
        Assert.Contains(bounded, f => f is { Kind: ValidationKind.Overlap, Start: 21, End: 25 });

        var open = Assert.Single(Validate("d20", (1, 19, "a"), (20, Above, "b"), (25, Above, "c")), f => f.Kind == ValidationKind.Overlap);
        Assert.Equal((25, Above), (open.Start, open.End));
        Assert.Equal("25+", RangeBounds.Label(open.Start, open.End));

        using var temp = new TempDatabase();
        var review = Review(temp, "D20 Odd\n1-19 A\n20+ B\n25+ C");
        Assert.Contains("Rows 2 and 3 both cover 25+.", review.ValidationNotes);
        Assert.All(review.ValidationNotes, NoSentinel);
        Assert.All(review.InfoNotes, NoSentinel);
    }

    [Fact]
    public void A_backwards_range_still_blocks_saving()
    {
        using var temp = new TempDatabase();
        var review = Review(temp, Injury);

        review.Rows[0].RangeText = "0--10";

        Assert.False(review.CanSave);
        Assert.Contains(review.Blockers, b => b.Contains("runs backwards"));
    }

    [Fact]
    public void The_d66_overlap_check_stays_finite_for_open_bounds()
    {
        var table = Fixtures.Table(DiceExpression.D66, (11, Above, "a"), (61, Above, "b"));
        var overlap = Assert.Single(TableValidator.Validate(table), f => f.Kind == ValidationKind.Overlap);
        Assert.Equal((61, 66), (overlap.Start, overlap.End));
    }

    [Fact]
    public void Adding_a_row_after_an_open_row_suggests_the_next_finite_number()
    {
        using var temp = new TempDatabase();
        var review = Review(temp, Injury);

        review.SelectedResultSet.AddRowCommand.Execute(null);

        Assert.Equal("27", review.Rows.Last().RangeText);
    }

    // ---- situational modifier -----------------------------------------------------------------------

    private static (RollViewModel Session, List<RollSnapshot> Rolled) Session(RollableTable table, int providerValue)
    {
        var rolled = new List<RollSnapshot>();
        return (new RollViewModel(table, new FixedDice(providerValue), rolled: rolled.Add), rolled);
    }

    [Fact]
    public void A_d20_roll_with_a_negative_situational_modifier_resolves_into_the_negative_row()
    {
        var (session, rolled) = Session(Fixtures.ParseAndBuild(Injury), 7);
        session.ModifierText = "-10";

        session.RollCommand.Execute(null);

        Assert.Equal("Rolled -3", session.RollDisplay);                    // the calculated result stays visible
        Assert.Equal("7 -10 situational", session.RollBreakdown);
        var line = Assert.Single(session.Results);
        Assert.Equal(("-10–0", "The character is dead."), (line.Range, line.Text));
        Assert.Equal((-3, -10, (int?)null), (rolled[0].RollValue, rolled[0].SituationalModifier, rolled[0].ClampedValue));
    }

    [Fact]
    public void A_d20_roll_with_a_positive_situational_modifier_resolves_into_the_open_row()
    {
        var (session, rolled) = Session(Fixtures.ParseAndBuild(Injury), 19);
        session.ModifierText = "+8";

        session.RollCommand.Execute(null);

        Assert.Equal("Rolled 27", session.RollDisplay);
        var line = Assert.Single(session.Results);
        Assert.Equal(("26+", "The character recovers immediately, ready to fight."), (line.Range, line.Text));
        Assert.Equal(27, rolled[0].RollValue);
    }

    // ---- manual entry -------------------------------------------------------------------------------

    [Theory]
    [InlineData("-3", "The character is dead.")]
    [InlineData("−3", "The character is dead.")]
    [InlineData("27", "The character recovers immediately, ready to fight.")]
    [InlineData("1000", "The character recovers immediately, ready to fight.")]
    [InlineData("12", "The character's wounds force them to spend a week resting...")]
    public void Values_covered_by_extended_rows_can_be_typed(string typed, string expected)
    {
        var (session, _) = Session(Fixtures.ParseAndBuild(Injury), 1);

        session.ManualRollText = typed;
        session.ResolveManualCommand.Execute(null);

        Assert.Equal("", session.Message);
        Assert.Equal(expected, Assert.Single(session.Results).Text);
    }

    [Theory]
    [InlineData("-11")]
    [InlineData("26")]
    [InlineData("abc")]
    public void Values_an_extended_table_does_not_cover_are_refused(string typed)
    {
        var (session, rolled) = Session(Fixtures.Table(D20, (-10, 0, "Dead"), (1, 20, "Alive"), (21, 25, "Up")), 1);

        session.ManualRollText = typed;
        session.ResolveManualCommand.Execute(null);

        Assert.Equal("Enter a value covered by this table.", session.Message);
        Assert.Empty(session.Current.Outcomes);
        Assert.Empty(rolled);
    }

    [Theory]
    [InlineData("-3")]
    [InlineData("21")]
    [InlineData("0")]
    public void An_ordinary_d20_table_still_refuses_values_outside_its_dice(string typed)
    {
        var (session, _) = Session(Fixtures.Table(D20, (1, 20, "Any")), 1);

        session.ManualRollText = typed;
        session.ResolveManualCommand.Execute(null);

        Assert.Equal("Enter a whole number from 1 to 20.", session.Message);
        Assert.Empty(session.Current.Outcomes);
    }

    // ---- clamp --------------------------------------------------------------------------------------

    private static RollSnapshot RollWithModifier(RollableTable table, int providerValue, string modifier, out RollViewModel session)
    {
        (session, var rolled) = Session(table, providerValue);
        session.ModifierText = modifier;
        session.RollCommand.Execute(null);
        return Assert.Single(rolled);
    }

    [Fact]
    public void Clamp_on_a_bounded_extended_table_clamps_to_its_authored_ends()
    {
        var table = ClampFixtures.Table("d20", true, (-10, 0, "Dead"), (1, 20, "Alive"), (21, 25, "Up"));

        var low = RollWithModifier(table, 1, "-15", out var session);
        Assert.Equal((-14, (int?)-10, "Dead"), (low.RollValue, low.ClampedValue, low.ResultText));
        Assert.Equal("Resolved as -10 (clamped)", session.RollClampNote);

        var high = RollWithModifier(table, 20, "+10", out _);
        Assert.Equal((30, (int?)25, "Up"), (high.RollValue, high.ClampedValue, high.ResultText));

        var inside = RollWithModifier(table, 1, "-4", out _);                    // -3 lies inside -10-0: resolved as authored, not clamped
        Assert.Equal((-3, (int?)null, "Dead"), (inside.RollValue, inside.ClampedValue, inside.ResultText));
    }

    [Fact]
    public void Clamp_on_an_open_above_table_never_clamps_upward()
    {
        var table = ClampFixtures.Table("d20", true, (-10, 0, "Dead"), (1, 20, "Alive"), (21, Above, "Up"));

        var high = RollWithModifier(table, 20, "+1000", out var session);
        Assert.Equal((1020, (int?)null, "Up"), (high.RollValue, high.ClampedValue, high.ResultText));
        Assert.Equal("", session.RollClampNote);

        var low = RollWithModifier(table, 1, "-15", out _);
        Assert.Equal((-14, (int?)-10, "Dead"), (low.RollValue, low.ClampedValue, low.ResultText));
    }

    [Fact]
    public void Clamp_on_an_open_below_table_never_clamps_downward()
    {
        var table = ClampFixtures.Table("d20", true, (Below, 0, "Dead"), (1, 20, "Alive"));

        var low = RollWithModifier(table, 1, "-1000", out _);
        Assert.Equal((-999, (int?)null, "Dead"), (low.RollValue, low.ClampedValue, low.ResultText));

        var high = RollWithModifier(table, 20, "+5", out _);
        Assert.Equal((25, (int?)20, "Alive"), (high.RollValue, high.ClampedValue, high.ResultText));
    }

    [Fact]
    public void Clamp_on_a_table_open_at_both_ends_never_changes_a_value()
    {
        var table = ClampFixtures.Table("d20", true, (Below, 0, "Dead"), (1, 20, "Alive"), (21, Above, "Up"));

        foreach (var value in new[] { -1_000_000, -999, 0, 1, 20, 21, 1020, 1_000_000 })
            Assert.Equal(value, TableClamp.LookupValue(table, value));
        Assert.Null(RollWithModifier(table, 1, "-1000", out _).ClampedValue);
        Assert.Null(RollWithModifier(table, 20, "+1000", out _).ClampedValue);
    }

    // ---- persistence --------------------------------------------------------------------------------

    [Fact]
    public void Open_rows_save_and_reload_and_edit_as_written()
    {
        using var temp = new TempDatabase();
        var db = temp.Open();
        var collection = db.CreateCollection("C");
        var saved = db.SaveTable(Fixtures.ParseAndBuild(Injury, collection.Id));
        db.SaveTable(Fixtures.ParseAndBuild("D20 Morale\n1 or less Flee\n2-20 Hold", collection.Id));

        var loaded = db.LoadTable(saved.Id)!;
        Assert.Equal(InjuryBounds, loaded.ResultSets[0].Entries.Select(e => (e.Min, e.Max, e.DisplayRange)).ToArray());

        var draft = TableImportDraft.FromTable(loaded);
        Assert.Equal(["-10-0", "1-5", "6-10", "11-15", "16-20", "21-25", "26+"], draft.ResultSets[0].Entries.Select(e => e.RangeText).ToArray());
        Assert.True(draft.TryBuildTable(collection.Id, out var rebuilt, out _));
        Assert.Equal(loaded.ResultSets[0].Entries.Select(e => (e.Min, e.Max)), rebuilt!.ResultSets[0].Entries.Select(e => (e.Min, e.Max)));
    }

    [Fact]
    public void Open_bounds_are_stored_as_the_documented_integers_in_the_current_schema()
    {
        Assert.Equal(9, DatabaseMigrations.CurrentVersion); // pinned: 8 introduced open bounds; 9 (RC24) added Entries.TextFormatting

        using var temp = new TempDatabase();
        using (var db = temp.Open())
            db.SaveTable(Fixtures.ParseAndBuild("D20 Morale\n1 or less Flee\n2-19 Hold\n20+ Charge", db.CreateCollection("C").Id));

        using var raw = new SqliteConnection($"Data Source={temp.Path};Pooling=False");
        raw.Open();
        Assert.Equal(9, DatabaseMigrations.GetVersion(raw));
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT MinValue, MaxValue, DisplayRange FROM Entries ORDER BY SortOrder";
        using var reader = cmd.ExecuteReader();
        var rows = new List<(long, long, string?)>();
        while (reader.Read()) rows.Add((reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        Assert.Equal(new (long, long, string?)[] { (int.MinValue, 1, "1 or less"), (2, 19, null), (20, int.MaxValue, "20+") }, rows);
    }

    [Fact]
    public void A_version_7_database_upgrades_to_8_with_a_backup_and_its_tables_unchanged()
    {
        using var temp = new TempDatabase();
        long id;
        using (var db = temp.Open())
            id = db.SaveTable(Fixtures.ParseAndBuild("D20 Fall\n-10-0 Dead\n1-20 Alive", db.CreateCollection("C").Id)).Id;
        using (var raw = new SqliteConnection($"Data Source={temp.Path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            // A real version-7 file: later migrations' columns are not there yet (9 added Entries.TextFormatting).
            cmd.CommandText = "ALTER TABLE Entries DROP COLUMN TextFormatting; PRAGMA user_version = 7";
            cmd.ExecuteNonQuery();
        }

        using (var upgraded = temp.Open())
            Assert.Equal([(-10, 0), (1, 20)], upgraded.LoadTable(id)!.ResultSets[0].Entries.Select(e => (e.Min, e.Max)).ToArray());

        Assert.NotEmpty(DatabaseBackup.Existing(temp.Path));
        using var check = new SqliteConnection($"Data Source={temp.Path};Pooling=False");
        check.Open();
        Assert.Equal(DatabaseMigrations.CurrentVersion, DatabaseMigrations.GetVersion(check)); // through 8, and on to the current version
    }

    // ---- exports ------------------------------------------------------------------------------------

    [Fact]
    public void Foundry_refuses_a_result_set_with_an_open_ended_range()
    {
        var table = Fixtures.ParseAndBuild(Injury);

        Assert.False(FoundryTableExporter.TryExport(table, table.ResultSets[0], out var export, out var error));
        Assert.Null(export);
        Assert.Equal("This table contains an open-ended range (\"26+\") that Foundry cannot represent faithfully.", error);
    }

    [Fact]
    public void Tables_plus_refuses_a_result_set_with_an_open_ended_range()
    {
        var table = Fixtures.ParseAndBuild("D20 Morale\n1 or less Flee\n2-20 Hold");

        Assert.False(TablesPlusTableExporter.TryExport(table, table.ResultSets[0], out var export, out var error));
        Assert.Null(export);
        Assert.Equal("This table contains an open-ended range (\"1 or less\") that Tables+ cannot represent faithfully.", error);
    }

    [Fact]
    public void Bounded_rows_beyond_the_dice_still_export_to_foundry_and_tables_plus_as_they_are()
    {
        var table = Fixtures.ParseAndBuild("D20 Fall\n-10-0 Dead\n1-20 Alive\n21-25 Up");
        table.Name = "Fall";

        Assert.True(FoundryTableExporter.TryExport(table, table.ResultSets[0], out var foundry, out _));
        var results = System.Text.Json.JsonDocument.Parse(foundry!.Json).RootElement.GetProperty("results");
        Assert.Equal([(-10, 0), (1, 20), (21, 25)],
            results.EnumerateArray().Select(r => (r.GetProperty("range")[0].GetInt32(), r.GetProperty("range")[1].GetInt32())).ToArray());

        Assert.True(TablesPlusTableExporter.TryExport(table, table.ResultSets[0], out var tablesPlus, out _));
        var entries = System.Text.Json.JsonDocument.Parse(tablesPlus!.Json).RootElement.GetProperty("entries");
        Assert.Equal([(-10, 0), (1, 20), (21, 25)],
            entries.EnumerateArray().Select(e => (e.GetProperty("low").GetInt32(), e.GetProperty("high").GetInt32())).ToArray());
        NoSentinel(foundry.Json);
        NoSentinel(tablesPlus.Json);
    }

    [Fact]
    public void Text_exports_keep_the_written_open_ranges()
    {
        var injury = Fixtures.ParseAndBuild(Injury);
        var text = TableTextExporter.Export(injury, injury.ResultSets[0]);
        Assert.Equal(
            string.Join("\r\n",
                "Injury", "d20", "",
                "-10-0\tThe character is dead.",
                "1-5\tThe character succumbs to their wounds...",
                "6-10\tThe character is severely wounded...",
                "11-15\tThe character's wounds force them to spend a week resting...",
                "16-20\tThe character is in shock, but alive...",
                "21-25\tThe character was simply knocked out...",
                "26+\tThe character recovers immediately, ready to fight."),
            text);

        var spaces = TableTextExporter.Export(injury, injury.ResultSets[0], TableTextSeparator.Space);
        Assert.EndsWith("\r\n26+ The character recovers immediately, ready to fight.", spaces);
        Assert.StartsWith("Injury\r\nd20\r\n\r\n-10-0 The character is dead.", spaces);

        var morale = Fixtures.ParseAndBuild("D20 Morale\n1 or less Flee\n2-19 Hold\n20 or more Charge");
        Assert.Equal("1 or less\tFlee\r\n2-19\tHold\r\n20 or more\tCharge", TableTextExporter.ExportRows(morale, morale.ResultSets[0]));
    }

    [Fact]
    public void Text_exports_never_show_a_sentinel_even_without_a_written_form()
    {
        var table = Fixtures.Table(D20, (Below, 0, "Low"), (1, 20, "Mid"), (21, Above, "High"));

        var text = TableTextExporter.ExportRows(table, table.ResultSets[0]);

        Assert.Equal("0 or less\tLow\r\n1-20\tMid\r\n21+\tHigh", text);
        NoSentinel(TableTextExporter.Export(table, table.ResultSets[0]));
        NoSentinel(TableTextExporter.Export(table, table.ResultSets[0], TableTextSeparator.Space));
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private static ReviewViewModel Review(TempDatabase temp, string source)
    {
        var db = temp.Open();
        db.CreateCollection("C");
        var main = new MainViewModel(db, new FixedDice(1));
        main.PasteTableCommand.Execute(null);
        ((PasteViewModel)main.Current!).SourceText = source;
        ((PasteViewModel)main.Current!).InterpretCommand.Execute(null);
        return (ReviewViewModel)main.Current!;
    }
}
