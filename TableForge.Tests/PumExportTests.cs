using System.IO;
using System.Reflection;
using TableForge.Domain;
using TableForge.Export.Pum;
using TableForge.Portable;

namespace TableForge.Tests;

/// <summary>
/// The PUM Companion export engine: how a TableForge table becomes PUM rows, when the equivalence check refuses it, and what the
/// report says. PUM's lookup (F09/F10): a roll is clamped to 1…last key, then takes the first row whose key is at least that.
/// </summary>
public class PumExportTests
{
    private const string NR = PumText.NoResult;

    private static TableEntry E(int min, int max, string text, string? display = null) => new() { Min = min, Max = max, Text = text, DisplayRange = display };

    private static RollableTable T(string dice, params TableEntry[] entries) => new()
    {
        Name = "Test Table", Dice = DiceExpression.Parse(dice), ResultSets = [new ResultSet { Entries = [.. entries] }],
    };

    private static RollableTable Clamped(RollableTable table)
    {
        table.ClampResultsToRange = true;
        return table;
    }

    private static PumTableConverter.Conversion Convert(RollableTable table, Func<long, PumLinkTarget?>? links = null) =>
        PumTableConverter.Convert(new PumSourceTable(1, table), PumNames.Normalize(table.Name), "Category", links ?? (_ => null));

    private static PumTable Exported(RollableTable table, Func<long, PumLinkTarget?>? links = null)
    {
        var conversion = Convert(table, links);
        Assert.True(conversion.Table is not null, conversion.Report.SkipReason);
        Assert.True(conversion.Report.Exported);
        Assert.Null(conversion.Report.NormalRollMismatch);
        return conversion.Table!;
    }

    private static PumTableReport Skipped(RollableTable table)
    {
        var conversion = Convert(table);
        Assert.Null(conversion.Table);
        Assert.False(conversion.Report.Exported);
        Assert.NotNull(conversion.Report.SkipReason);
        return conversion.Report;
    }

    private static List<(int Key, string Text)> Rows(PumTable table) => table.Rows.Select(r => (r.Key, r.Text)).ToList();

    private static string Pum(PumTable table, int roll) => PumEquivalence.PumLookup(table.Rows, roll).Text;

    // ---- Formula ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("d6", "1d6")]
    [InlineData("2d6", "2d6")]
    [InlineData("2d6+1", "2d6+1")]
    [InlineData("d20-2", "1d20-2")]
    [InlineData("3d8+4", "3d8+4")]
    [InlineData("d100", "1d100")]
    [InlineData("d66", "1D66")]
    [InlineData("1d66", "1d66")]
    public void The_formula_is_written_as_PUM_reads_it_with_a_capital_D_only_for_the_d66_convention(string dice, string formula)
    {
        Assert.Equal(formula, PumTableConverter.Formula(DiceExpression.Parse(dice)));
        var table = T(dice, E(-1000, 200000, "x"));
        Assert.Equal(formula, Exported(Clamped(table)).Formula);
    }

    // ---- Rows, fillers and the guard --------------------------------------------------------------------------------

    [Fact]
    public void Contiguous_rows_keep_their_upper_bounds_and_a_final_guard_row_keeps_higher_rolls_at_no_result()
    {
        var pum = Exported(T("d6", E(1, 2, "A"), E(3, 6, "B")));
        Assert.Equal([(2, "A"), (6, "B"), (7, NR)], Rows(pum));
        Assert.Equal(NR, Pum(pum, 9));   // a PUM override past the table shows No result, like TableForge, not the last row
    }

    [Fact]
    public void Leading_internal_and_trailing_gaps_become_no_result_rows()
    {
        // F10R-05's layout: TableForge rows 3-4 and 7-8 on a d8; 1-2, 5-6 and 9+ are No Match.
        var pum = Exported(T("d8", E(3, 4, "A"), E(7, 8, "B")));
        Assert.Equal([(2, NR), (4, "A"), (6, NR), (8, "B"), (9, NR)], Rows(pum));
        foreach (var (roll, shows) in new[] { (1, NR), (2, NR), (3, "A"), (5, NR), (6, NR), (7, "B"), (8, "B") })
            Assert.Equal(shows, Pum(pum, roll));
    }

    [Fact]
    public void A_trailing_gap_up_to_the_dice_maximum_is_also_the_guard()
    {
        var conversion = Convert(T("d10", E(1, 5, "A")));
        Assert.Equal([(5, "A"), (10, NR)], Rows(conversion.Table!));
        var filler = Assert.Single(conversion.Report.Messages, m => m.Kind == PumMessageKind.Conversion && m.Text.Contains("(No result)"));
        Assert.Equal(PumMessageKind.Conversion, filler.Kind);
        Assert.Contains("The last one (key 10) also catches any higher roll", filler.Text);
    }

    [Fact]
    public void Dice_with_a_modifier_get_a_filler_below_their_lowest_roll_and_the_guard_above()
    {
        var pum = Exported(T("d6+2", E(3, 8, "A")));
        Assert.Equal("1d6+2", pum.Formula);
        Assert.Equal([(2, NR), (8, "A"), (9, NR)], Rows(pum));
    }

    [Fact]
    public void A_d100_00_row_is_key_100()
    {
        var pum = Exported(T("d100", E(1, 95, "Most", "01–95"), E(96, 100, "Rare", "96–00")));
        Assert.Equal([(95, "Most"), (100, "Rare"), (101, NR)], Rows(pum));
    }

    [Fact]
    public void Extended_rows_above_the_dice_keep_their_keys_for_PUM_overrides()
    {
        var conversion = Convert(T("d6", E(1, 6, "Normal"), E(7, 10, "Extended")));
        Assert.Equal([(6, "Normal"), (10, "Extended"), (11, NR)], Rows(conversion.Table!));
        Assert.Null(conversion.Report.AboveDiceMismatch);   // even overrides into and past them match TableForge
    }

    // ---- d66 ------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_d66_gap_made_only_of_impossible_numbers_needs_no_filler()
    {
        var pum = Exported(T("d66", E(11, 16, "A"), E(21, 26, "B"), E(31, 66, "C")));
        Assert.Equal("1D66", pum.Formula);
        Assert.Equal([(16, "A"), (26, "B"), (66, "C"), (67, NR)], Rows(pum));
    }

    [Fact]
    public void A_d66_gap_with_legal_numbers_gets_a_filler()
    {
        var pum = Exported(T("d66", E(11, 16, "A"), E(31, 66, "C")));
        Assert.Equal([(16, "A"), (30, NR), (66, "C"), (67, NR)], Rows(pum));
        Assert.Equal(NR, Pum(pum, 24));
    }

    [Fact]
    public void A_genuine_66_sided_die_is_an_ordinary_die()
    {
        var pum = Exported(T("1d66", E(1, 30, "Low"), E(31, 66, "High")));
        Assert.Equal("1d66", pum.Formula);
        Assert.Equal([(30, "Low"), (66, "High"), (67, NR)], Rows(pum));
    }

    // ---- Open-ended rows --------------------------------------------------------------------------------------------

    [Fact]
    public void An_open_ended_top_row_is_the_last_row_and_PUM_clamping_gives_it_its_meaning()
    {
        var conversion = Convert(T("d20+2", E(3, 10, "Low"), E(11, RangeBounds.OpenAbove, "High", "11+")));
        Assert.Equal([(2, NR), (10, "Low"), (22, "High")], Rows(conversion.Table!));
        Assert.Null(conversion.Report.AboveDiceMismatch);   // 40 is "11+" in both
    }

    [Fact]
    public void An_open_ended_row_starting_above_the_dice_keeps_its_start_as_its_key()
    {
        var pum = Exported(T("d6", E(1, 6, "Normal"), E(10, RangeBounds.OpenAbove, "Ten or more", "10+")));
        Assert.Equal([(6, "Normal"), (9, NR), (10, "Ten or more")], Rows(pum));
    }

    [Fact]
    public void An_open_ended_bottom_row_covers_1_and_PUM_flooring_gives_it_its_meaning()
    {
        var conversion = Convert(T("d6", E(RangeBounds.OpenBelow, 1, "Low", "1 or less"), E(2, 6, "High")));
        Assert.Equal([(1, "Low"), (6, "High"), (7, NR)], Rows(conversion.Table!));
        Assert.Null(conversion.Report.BelowDiceMismatch);   // -3 is "1 or less" in both
    }

    // ---- Rolls below 1 ----------------------------------------------------------------------------------------------

    [Fact]
    public void Dice_that_roll_below_1_are_refused_when_a_row_covers_1()
    {
        var report = Skipped(T("d6-2", E(1, 4, "A")));
        Assert.Equal(new PumRollMismatch(-1, NR, "A"), report.NormalRollMismatch);
        Assert.Contains("PUM treats every roll below 1 as 1", report.SkipReason);
    }

    [Fact]
    public void Dice_that_roll_below_1_are_exported_when_every_roll_up_to_1_is_No_Match()
    {
        var pum = Exported(T("d6-2", E(2, 4, "A")));
        Assert.Equal([(1, NR), (4, "A"), (5, NR)], Rows(pum));
    }

    [Fact]
    public void Rows_at_0_or_below_that_the_dice_can_roll_are_refused()
    {
        var report = Skipped(T("d6-3", E(-2, 0, "Low"), E(1, 3, "High")));
        Assert.Equal(-2, report.NormalRollMismatch!.Roll);
        Assert.Equal(("Low", "High"), (report.NormalRollMismatch.TableForgeShows, report.NormalRollMismatch.PumShows));
    }

    [Fact]
    public void Rows_at_0_or_below_that_only_the_situational_modifier_reaches_are_left_out_with_a_warning()
    {
        var conversion = Convert(T("d6", E(-3, 0, "Fumble"), E(1, 6, "Hit")));
        Assert.Equal([(6, "Hit"), (7, NR)], Rows(conversion.Table!));
        Assert.Contains(conversion.Report.Messages, m => m.Kind == PumMessageKind.Warning && m.Text.Contains("\"-3–0\" is below 1"));
        // Reported as an outside-the-dice difference, never as a normal-roll one.
        Assert.Equal(new PumRollMismatch(0, "Fumble", "Hit"), conversion.Report.BelowDiceMismatch);
        Assert.Contains(conversion.Report.Messages, m => m.Kind == PumMessageKind.Note && m.Text.StartsWith("Below the dice's range"));
    }

    [Fact]
    public void A_table_whose_rows_are_all_below_1_is_refused()
    {
        Assert.Contains("can't roll below 1", Skipped(T("d6", E(-5, 0, "x"))).SkipReason);
    }

    [Fact]
    public void Rolls_below_the_dice_on_a_table_whose_first_row_covers_1_are_noted_not_refused()
    {
        var conversion = Convert(T("d6", E(1, 6, "A")));
        Assert.True(conversion.Report.Exported);
        Assert.Equal(new PumRollMismatch(0, NR, "A"), conversion.Report.BelowDiceMismatch);
        Assert.Null(conversion.Report.AboveDiceMismatch);
    }

    // ---- Clamp to Range -----------------------------------------------------------------------------------------------

    [Fact]
    public void Clamp_to_Range_gets_no_end_fillers_because_PUM_clamps_the_same_way()
    {
        var conversion = Convert(Clamped(T("d8", E(3, 4, "A"), E(5, 6, "B"))));
        Assert.Equal([(4, "A"), (6, "B")], Rows(conversion.Table!));
        Assert.Equal("A", Pum(conversion.Table!, 1));
        Assert.Equal("B", Pum(conversion.Table!, 8));
        Assert.Contains(conversion.Report.Messages, m => m.Kind == PumMessageKind.Note && m.Text.StartsWith("Clamp to Range is kept"));
        Assert.Null(conversion.Report.BelowDiceMismatch);
        Assert.Null(conversion.Report.AboveDiceMismatch);
    }

    [Fact]
    public void Clamp_to_Range_never_fills_internal_gaps()
    {
        var pum = Exported(Clamped(T("d8", E(1, 2, "A"), E(5, 8, "B"))));
        Assert.Equal([(2, "A"), (4, NR), (8, "B")], Rows(pum));
    }

    [Fact]
    public void Clamp_to_Range_makes_dice_that_roll_below_1_exportable()
    {
        var pum = Exported(Clamped(T("d6-2", E(1, 2, "A"), E(3, 4, "B"))));
        Assert.Equal([(2, "A"), (4, "B")], Rows(pum));
    }

    [Fact]
    public void Clamp_that_cannot_apply_is_treated_as_off()
    {
        var table = Clamped(T("d66", E(11, 36, "A"), E(41, 66, "B")));   // clamp is never available for d66
        Assert.Equal([(36, "A"), (66, "B"), (67, NR)], Rows(Exported(table)));
    }

    // ---- Tables PUM can't represent -----------------------------------------------------------------------------------

    [Fact]
    public void Overlapping_rows_are_refused()
    {
        Assert.Contains("overlap", Skipped(T("d6", E(1, 4, "A"), E(3, 6, "B"))).SkipReason);
        Assert.Contains("overlap", Skipped(T("d10", E(1, 10, "A"), E(2, 3, "B"), E(5, 6, "C"))).SkipReason);
    }

    [Fact]
    public void A_table_with_no_rows_or_a_backwards_range_is_refused()
    {
        Assert.Equal("It has no rows.", Skipped(T("d6")).SkipReason);
        Assert.Contains("runs backwards", Skipped(T("d6", E(4, 2, "x"))).SkipReason);
    }

    // ---- Result sets ------------------------------------------------------------------------------------------------

    [Fact]
    public void Result_sets_are_merged_on_every_boundary_so_one_roll_gives_every_result()
    {
        var table = T("d6");
        table.ResultSets =
        [
            new ResultSet { Name = "Ambient", Entries = [E(1, 3, "Cold"), E(4, 6, "Damp")] },
            new ResultSet { Name = "Noise", Entries = [E(1, 2, "Silence")] },
        ];
        var conversion = Convert(table);
        Assert.Equal(
            [(2, "Ambient: Cold\nNoise: Silence"), (3, $"Ambient: Cold\nNoise: {NR}"), (6, $"Ambient: Damp\nNoise: {NR}"), (7, NR)],
            Rows(conversion.Table!));
        Assert.Contains(conversion.Report.Messages, m => m.Kind == PumMessageKind.Conversion && m.Text.StartsWith("Its 2 result sets are combined"));
    }

    [Fact]
    public void Room_Features_rolls_68_in_PUM_exactly_as_in_TableForge()
    {
        var pum = Exported(Fixtures.RoomFeatures());
        Assert.Equal("Ambient: Smell of burning flesh\nNoise: Hissing\nGeneral Feature: Grated floors reveal dozens of people below", Pum(pum, 68));
    }

    [Fact]
    public void Unnamed_result_sets_have_no_heading()
    {
        var table = T("d2");
        table.ResultSets = [new ResultSet { Entries = [E(1, 2, "One")] }, new ResultSet { Entries = [E(1, 2, "Two")] }];
        Assert.Equal("One\nTwo", Pum(Exported(table), 1));
    }

    // ---- Text ---------------------------------------------------------------------------------------------------------

    private static TableEntry Styled(int min, int max, string text, params StyleRun[] runs) =>
        E(min, max, text).Also(e => e.Styles = TextStyles.FromRuns(runs, text.Length));

    [Fact]
    public void Bold_italic_and_both_become_Markdown_with_spaces_kept_outside_the_markers()
    {
        var text = "Bold: italic sentence. both plain";
        var entry = Styled(1, 6, text, new StyleRun(0, 6, TextStyle.Bold), new StyleRun(6, 17, TextStyle.Italic), new StyleRun(23, 5, TextStyle.Bold | TextStyle.Italic));
        Assert.Equal("**Bold:** *italic sentence.* ***both*** plain", Pum(Exported(T("d6", entry)), 1));
    }

    [Fact]
    public void A_formatted_stretch_across_lines_is_marked_line_by_line_and_mid_word_italic_stays_mid_word()
    {
        Assert.Equal("**one**\n**two**", PumText.Markdown("one\ntwo", TextStyles.FromRuns([new(0, 7, TextStyle.Bold)], 7), out _));
        Assert.Equal("un*believ*able", PumText.Markdown("unbelievable", TextStyles.FromRuns([new(2, 6, TextStyle.Italic)], 12), out var touch));
        Assert.False(touch);
    }

    [Fact]
    public void Formatting_that_changes_with_no_space_between_is_warned_about()
    {
        var entry = Styled(1, 6, "ab", new StyleRun(0, 1, TextStyle.Bold), new StyleRun(1, 1, TextStyle.Italic));
        var conversion = Convert(T("d6", entry));
        Assert.Equal("**a***b*", Pum(conversion.Table!, 1));
        Assert.Contains(conversion.Report.Messages, m => m.Kind == PumMessageKind.Warning && m.Text.Contains("no space between"));
    }

    [Theory]
    [InlineData("a *word* here", true)]
    [InlineData("a **bold** here", true)]
    [InlineData("an _italic_ word", true)]
    [InlineData("2 * 3 * 4 = 24", false)]
    [InlineData("snake_case_name", false)]
    [InlineData("a lone * star", false)]
    public void Plain_asterisks_and_underscores_that_Markdown_would_format_are_warned_about(string text, bool warned)
    {
        Assert.Equal(warned, PumText.HasLiteralEmphasis(text));
        var conversion = Convert(T("d6", E(1, 6, text)));
        Assert.Equal(text, Pum(conversion.Table!, 1));    // kept exactly, never escaped (PUM shows backslashes)
        Assert.Equal(warned, conversion.Report.Messages.Any(m => m.Text.Contains("italic or bold")));
    }

    [Theory]
    [InlineData("Roll on {{Other Table}} now", "{{Other Table}}")]
    [InlineData("Lose ;;1d6;; coins", ";;1d6;;")]
    [InlineData("Lose ;;D4;; coins", ";;D4;;")]
    [InlineData("You rolled ::result::", "::result::")]
    public void Text_PUM_would_run_as_a_command_is_refused(string text, string sequence)
    {
        var report = Skipped(T("d6", E(1, 6, text)));
        Assert.Contains($"\"{sequence}\"", report.SkipReason);
        Assert.Contains("never writes PUM commands", report.SkipReason);
    }

    [Theory]
    [InlineData("a 50%% chance")]
    [InlineData("close }} first, then open {{")]
    [InlineData("a;;b and ;;not dice;;")]
    [InlineData("ratio 2::1 and ::word::")]
    [InlineData("[[note]] and `code`")]
    [InlineData("Roll 1d6 rats, then D4 more")]
    [InlineData("Sword & Shield, king’s “road” – 5 < 6")]
    public void PUM_syntax_that_is_not_a_complete_command_and_inline_dice_are_kept_as_written(string text)
    {
        Assert.Equal(text, Pum(Exported(T("d6", E(1, 6, text))), 1));
    }

    [Fact]
    public void An_empty_row_is_exported_blank_with_a_warning()
    {
        var conversion = Convert(T("d6", E(1, 3, ""), E(4, 6, "x")));
        Assert.Equal("", Pum(conversion.Table!, 2));
        var warning = Assert.Single(conversion.Report.Messages, m => m.Kind == PumMessageKind.Warning);
        Assert.Equal("The row \"1–3\" has no text, so PUM shows a blank result (Remove Empty Rows in Review/Edit removes empty rows).", warning.Text);
    }

    [Fact]
    public void Many_rows_with_the_same_problem_share_one_warning()
    {
        var conversion = Convert(T("d10", Enumerable.Range(1, 8).Select(i => E(i, i, "")).Append(E(9, 10, "x")).ToArray()));
        var warning = Assert.Single(conversion.Report.Messages, m => m.Kind == PumMessageKind.Warning);
        Assert.StartsWith("The rows \"1\", \"2\", \"3\", \"4\", \"5\" and 3 more have no text", warning.Text);
    }

    [Fact]
    public void A_table_whose_every_result_is_blank_is_skipped()
    {
        const string Reason = "Every result is blank, so the table would only show empty results in PUM.";
        Assert.Equal(Reason, Skipped(T("d6", E(1, 3, ""), E(4, 6, "  \n "))).SkipReason);
        Assert.Equal(Reason, Skipped(T("d10", E(2, 3, ""), E(7, 8, "\t"))).SkipReason);   // gaps (No Match) are not content
        Assert.Equal(Reason, Skipped(T("d6", E(1, 6, "   ").Also(e => e.Styles = TextStyles.FromRuns([new StyleRun(0, 3, TextStyle.Bold)], 3)))).SkipReason);

        var sets = T("d6");
        sets.ResultSets =
        [
            new ResultSet { Name = "Weather", Entries = [E(1, 6, "")] },
            new ResultSet { Name = "Wind", Entries = [E(1, 3, " ")] },
        ];
        Assert.Equal(Reason, Skipped(sets).SkipReason);   // the set headings alone are not content
    }

    [Fact]
    public void A_table_with_one_visible_result_or_a_link_is_not_blank()
    {
        Assert.Equal([(3, ""), (6, "x"), (7, NR)], Rows(Exported(T("d6", E(1, 3, ""), E(4, 6, "x")))));
        Assert.Equal("→ Wolf Pack", Pum(Exported(T("d6", E(1, 6, "").Also(e => e.LinkedTableId = 7)), _ => new("Wolf Pack", true)), 1));
        Assert.Equal("→ (missing table)", Pum(Exported(T("d6", E(1, 6, "").Also(e => e.LinkedTableId = 7))), 1));
        Assert.Equal("→ (unresolved link)", Pum(Exported(T("d6", E(1, 6, "").Also(e => e.UnresolvedLinkName = " "))), 1));

        var sets = T("d6");
        sets.ResultSets =
        [
            new ResultSet { Name = "Weather", Entries = [E(1, 6, "")] },
            new ResultSet { Name = "Wind", Entries = [E(1, 3, "Calm")] },
        ];
        Assert.Equal("Weather: \nWind: Calm", Pum(Exported(sets), 1));
    }

    [Fact]
    public void A_blank_table_in_a_collection_is_reported_as_skipped_and_the_others_still_export()
    {
        var result = PumExporter.Export(new("C", [new(1, T("d6", E(1, 6, "x"))), new(2, T("d6", E(1, 6, "")).Also(t => t.Name = "Blank"))]), DateTime.UnixEpoch);
        Assert.Single(result.Tables);
        var skipped = Assert.Single(result.Report.Tables, t => !t.Exported);
        Assert.Equal("Blank", skipped.SourceName);
        Assert.Contains("Blank: Every result is blank", result.Report.ToText());
        Assert.Null(PumExporter.Export(new("C", [new(2, T("d6", E(1, 6, "")))]), DateTime.UnixEpoch).Bundle);
    }

    // ---- Links --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Links_become_a_text_line_naming_the_table_and_never_a_PUM_nested_roll()
    {
        var linked = E(1, 2, "Wolves").Also(e => e.LinkedTableId = 7);
        var outside = E(3, 4, "Bandits").Also(e => e.LinkedTableId = 8);
        var unresolved = E(5, 5, "Ruins").Also(e => e.UnresolvedLinkName = "Ruin Details");
        var missing = E(6, 6, "").Also(e => e.LinkedTableId = 9);
        PumLinkTarget? Target(long key) => key switch { 7 => new("Wolf Pack", true), 8 => new("Bandit Camp", false), _ => null };

        var conversion = Convert(T("d6", linked, outside, unresolved, missing), Target);
        var pum = conversion.Table!;
        Assert.Equal("Wolves\n→ Wolf Pack", Pum(pum, 1));
        Assert.Equal("Bandits\n→ Bandit Camp (not included in this export)", Pum(pum, 3));
        Assert.Equal("Ruins\n→ Ruin Details (unresolved link)", Pum(pum, 5));
        Assert.Equal("→ (missing table)", Pum(pum, 6));
        Assert.DoesNotContain(pum.Rows, r => r.Text.Contains("{{"));

        var messages = conversion.Report.Messages;
        Assert.Contains(messages, m => m.Kind == PumMessageKind.Conversion && m.Text.StartsWith("4 linked rows end with a \"→ table\" line"));
        Assert.Contains(messages, m => m.Kind == PumMessageKind.Warning && m.Text.Contains("\"Bandit Camp\", which isn't in this export"));
        Assert.Contains(messages, m => m.Kind == PumMessageKind.Warning && m.Text.Contains("unresolved link to \"Ruin Details\""));
        Assert.Contains(messages, m => m.Kind == PumMessageKind.Warning && m.Text.Contains("couldn't be found"));
        Assert.DoesNotContain(messages, m => m.Text.Contains("no text"));   // the link line is the row's text
    }

    // ---- Names and categories -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Yes/No Oracle", "Yes - No Oracle")]
    [InlineData("Odd: <chars> \"here\" | a\\b ? *", "Odd_ _chars_ _here_ _ a_b _ _")]
    [InlineData("  Many   spaces\nand a line  ", "Many spaces and a line")]
    [InlineData("Trailing dots...", "Trailing dots")]
    [InlineData("CON", "CON_")]
    [InlineData("nul.txt", "nul.txt_")]
    [InlineData("   ", PumNames.FallbackName)]
    [InlineData("King’s Road – Salt & Iron (Back`tick)", "King’s Road – Salt & Iron (Back`tick)")]
    public void Names_are_made_storable_by_PUM(string name, string expected)
    {
        Assert.Equal(expected, PumNames.Normalize(name));
    }

    [Fact]
    public void Names_are_at_most_100_characters()
    {
        Assert.Equal(new string('x', 100), PumNames.Normalize(new string('x', 150)));
        Assert.Equal(new string('x', 98), PumNames.Normalize(new string('x', 98) + " . more"));
    }

    [Fact]
    public void Names_are_made_unique_ignoring_case_in_order()
    {
        Assert.Equal(["Loot", "loot (2)", "LOOT (3)", "Loot (2) (2)"], PumNames.MakeUnique(["Loot", "loot", "LOOT", "Loot (2)"]));
        var long100 = new string('y', 100);
        Assert.Equal([long100, new string('y', 96) + " (2)"], PumNames.MakeUnique([long100, long100]));
    }

    [Theory]
    [InlineData("Of Blood and Clay", null, "Of Blood and Clay")]
    [InlineData("Of Blood and Clay", "", "Of Blood and Clay")]
    [InlineData("Of Blood and Clay", "Spark  Tables", "Of Blood and Clay - Spark Tables")]
    [InlineData(" ", "Loot", "TableForge - Loot")]
    public void The_category_is_the_collection_or_collection_and_folder(string collection, string? folder, string expected)
    {
        Assert.Equal(expected, PumNames.Category(collection, folder));
    }

    // ---- The planner ----------------------------------------------------------------------------------------------------

    private static readonly DateTime Stamp = new(2026, 10, 24, 12, 0, 0);

    private static RollableTable Named(string name, RollableTable table)
    {
        table.Name = name;
        return table;
    }

    private static PumExportRequest Request(params PumSourceTable[] tables) => new("My Collection", tables);

    [Fact]
    public void A_collection_export_names_categorises_and_ids_every_table_and_reports_each_in_order()
    {
        var result = PumExporter.Export(Request(
            new PumSourceTable(10, Named("Loot", T("d6", E(1, 6, "Gold"))), "Treasure"),
            new PumSourceTable(11, Named("loot", T("d6", E(1, 6, "Silver")))),
            new PumSourceTable(12, Named("Broken", T("d6", E(1, 4, "A"), E(3, 6, "B"))))), Stamp);

        Assert.Equal(["Loot", "loot (2)"], result.Tables.Select(t => t.Name));
        Assert.Equal(["My Collection - Treasure", "My Collection"], result.Tables.Select(t => t.Category));
        Assert.Equal(result.Tables.Select(t => PumTableConverter.IdFor(t.Category, t.Name)), result.Tables.Select(t => t.Id));
        Assert.Equal(2, result.Tables.Select(t => t.Id).Distinct().Count());

        var report = result.Report;
        Assert.Equal([10L, 11, 12], report.Tables.Select(t => t.Key));
        Assert.Equal((2, 1), (report.ExportedCount, report.SkippedCount));
        Assert.Contains(report.Tables[1].Messages, m => m == new PumMessage(PumMessageKind.Conversion, "Named \"loot (2)\" in PUM."));
        Assert.Equal(PumExporter.GeneralNotes, report.GeneralNotes);
        Assert.NotNull(result.Bundle);
    }

    [Fact]
    public void Nothing_exportable_means_no_bundle()
    {
        var result = PumExporter.Export(Request(new PumSourceTable(1, T("d6", E(1, 4, "A"), E(3, 6, "B")))), Stamp);
        Assert.Null(result.Bundle);
        Assert.Empty(result.Tables);
        Assert.Equal(1, result.Report.SkippedCount);
    }

    [Fact]
    public void A_single_table_export_is_exactly_that_tables_part_of_the_collection_export()
    {
        var a = new PumSourceTable(1, Named("Alpha", T("2d6", E(2, 7, "Low"), E(8, 12, "High"))), "F");
        var b = new PumSourceTable(2, Named("Beta", T("d66", E(11, 36, "x"), E(41, 66, "y"))));
        var c = new PumSourceTable(3, Named("Gamma", T("d6", E(1, 6, "z"))));

        var all = PumExporter.Export(Request(a, b, c), Stamp);
        var one = PumExporter.Export(Request(b), Stamp);

        var fromAll = all.Tables.Single(t => t.Name == "Beta");
        var single = Assert.Single(one.Tables);
        Assert.Equal((fromAll.Id, fromAll.Name, fromAll.Category, fromAll.Formula), (single.Id, single.Name, single.Category, single.Formula));
        Assert.Equal(fromAll.Rows, single.Rows);
        Assert.Equal(all.Report.Tables[1], one.Report.Tables[0] with { Messages = all.Report.Tables[1].Messages });
        Assert.Equal(all.Report.Tables[1].Messages, one.Report.Tables[0].Messages);
    }

    [Fact]
    public void Links_name_their_target_by_its_PUM_name_and_say_when_it_is_not_in_the_bundle()
    {
        var host = new PumSourceTable(1, Named("Host", T("d6", E(1, 2, "To target").Also(e => e.LinkedTableId = 2),
            E(3, 4, "To skipped").Also(e => e.LinkedTableId = 3), E(5, 6, "To other").Also(e => e.LinkedTableId = 4))));
        var target = new PumSourceTable(2, Named("Yes/No", T("d6", E(1, 6, "Yes"))));
        var skipped = new PumSourceTable(3, Named("Overlaps", T("d6", E(1, 4, "A"), E(3, 6, "B"))));
        var request = new PumExportRequest("C", [host, target, skipped], new Dictionary<long, string> { [4] = "  Elsewhere " });

        var pum = PumExporter.Export(request, Stamp).Tables.Single(t => t.Name == "Host");
        Assert.Equal("To target\n→ Yes - No", Pum(pum, 1));
        Assert.Equal("To skipped\n→ Overlaps (not included in this export)", Pum(pum, 3));
        Assert.Equal("To other\n→ Elsewhere (not included in this export)", Pum(pum, 5));
    }

    [Fact]
    public void Every_table_needs_its_own_key()
    {
        Assert.Throws<ArgumentException>(() =>
            PumExporter.Export(Request(new PumSourceTable(1, T("d6", E(1, 6, "a"))), new PumSourceTable(1, T("d6", E(1, 6, "b")))), Stamp));
    }

    [Fact]
    public void A_portable_collection_request_keys_tables_by_position_and_names_the_ones_left_out()
    {
        var portable = new PortableCollection("Portable", ["Folder A"],
        [
            new PortableTable(Named("First", T("d6", E(1, 6, "→ second").Also(e => e.LinkedTableId = 1))), 0),
            new PortableTable(Named("Second", T("d6", E(1, 6, "x"))), null),
        ]);

        var request = PumExportRequest.FromPortable(portable, [0]);
        Assert.Equal(new long[] { 0 }, request.Tables.Select(t => t.Key));
        Assert.Equal("Folder A", request.Tables[0].FolderName);
        Assert.Equal("Second", request.OtherTableNames![1]);

        var pum = Assert.Single(PumExporter.Export(request, Stamp).Tables);
        Assert.Equal("Portable - Folder A", pum.Category);
        Assert.Equal("→ second\n→ Second (not included in this export)", Pum(pum, 1));
    }

    [Fact]
    public void Exporting_never_changes_the_source_tables()
    {
        var portable = new PortableCollection("Source", ["Folder"],
        [
            new PortableTable(Fixtures.RoomFeatures(), 0),
            new PortableTable(Named("Formatted", T("d6", Styled(1, 3, " Bold lead ", new StyleRun(1, 4, TextStyle.Bold)), E(5, 6, "Gap before"))), null),
            new PortableTable(Clamped(Named("Clamped", T("d8", E(3, 4, "A"), E(5, 6, "B")))), 0),
            new PortableTable(Named("Linked", T("d6", E(1, 6, "x").Also(e => e.LinkedTableId = 0), E(7, RangeBounds.OpenAbove, "7+", "7+"))), null),
            new PortableTable(Named("Skipped", T("d6-2", E(1, 4, "A"))), null),
        ]);
        portable.Tables[0].Table.Description = "Kept as it is.";
        var before = PortableCollectionExporter.Write(portable);

        var result = PumExporter.Export(PumExportRequest.FromPortable(portable), Stamp);

        Assert.True(result.Report.ExportedCount >= 3);
        Assert.Equal(before, PortableCollectionExporter.Write(portable));
    }

    [Fact]
    public void The_report_text_lists_exported_tables_with_their_messages_then_skipped_tables_then_the_notes()
    {
        var result = PumExporter.Export(Request(
            new PumSourceTable(1, Named("Gappy", T("d8", E(3, 4, "A"), E(7, 8, "B")))),
            new PumSourceTable(2, Named("Yes/No", T("d6", E(1, 6, "Yes")))),
            new PumSourceTable(3, Named("Bad", T("d6-2", E(1, 4, "A"))))), Stamp);

        var text = result.Report.ToText().Split('\n');
        Assert.Equal("PUM Companion export: 2 exported (2 with changes or warnings), 1 skipped.", text[0]);
        Assert.Contains("  Gappy", text);
        Assert.Contains("  Yes/No → \"Yes - No\"", text);
        Assert.Contains(text, l => l.StartsWith("    - 3 \"(No result)\" rows added"));
        Assert.Contains(text, l => l.StartsWith("  Bad: A roll of -1 shows \"(No result)\" in Gidubba but would show \"A\" in PUM"));
        Assert.Contains("  - Table descriptions are not exported.", text);
    }

    // ---- The equivalence check ----------------------------------------------------------------------------------------

    [Fact]
    public void PUM_lookup_clamps_to_1_and_the_last_key_and_takes_the_next_key_in_a_gap()
    {
        IReadOnlyList<PumRow> rows = [new(4, "A"), new(8, "B")];
        Assert.Equal(["A", "A", "A", "B", "B", "B"], new long[] { -5, 1, 4, 5, 8, 1000 }.Select(v => PumEquivalence.PumLookup(rows, v).Text));
    }

    [Fact]
    public void The_check_finds_a_difference_in_rows_it_did_not_build_and_names_a_representative_roll()
    {
        var table = T("d8", E(3, 4, "A"), E(7, 8, "B"));
        string Render(IReadOnlyList<TableEntry?> m) => m[0]?.Text ?? NR;

        Assert.Equal(new PumRollMismatch(1, NR, "A"), PumEquivalence.Check(table, [new(4, "A"), new(6, NR), new(8, "B"), new(9, NR)], Render).Normal);
        Assert.Equal(new PumRollMismatch(5, NR, "B"), PumEquivalence.Check(table, [new(2, NR), new(4, "A"), new(8, "B"), new(9, NR)], Render).Normal);

        var noGuard = PumEquivalence.Check(table, [new(2, NR), new(4, "A"), new(6, NR), new(8, "B")], Render);
        Assert.Null(noGuard.Normal);                                   // ordinary rolls of a d8 never pass 8...
        Assert.Equal(new PumRollMismatch(9, NR, "B"), noGuard.AboveDice);  // ...but an override does, and it is reported apart
    }

    [Fact]
    public void Huge_dice_are_checked_by_their_boundaries_not_value_by_value()
    {
        // 100d1000 rolls 100-100,000; a handful of rows means a handful of stretches to check.
        var table = T("100d1000", E(100, 50_000, "Low"), E(50_002, 100_000, "High"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var pum = Exported(table);
        watch.Stop();
        Assert.Equal([(99, NR), (50_000, "Low"), (50_001, NR), (100_000, "High"), (100_001, NR)], Rows(pum));
        Assert.True(watch.ElapsedMilliseconds < 1000, $"took {watch.ElapsedMilliseconds} ms");
    }

    // ---- No WPF -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_export_engine_uses_nothing_from_WPF_the_database_or_the_view_models()
    {
        string[] forbidden = ["PresentationFramework", "PresentationCore", "WindowsBase", "System.Xaml", "Microsoft.Data.Sqlite", "Microsoft.Web.WebView2"];
        var types = typeof(PumExporter).Assembly.GetTypes().Where(t => t.Namespace == "TableForge.Export.Pum").ToList();
        Assert.NotEmpty(types);
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var type in types)
        {
            var used = type.GetFields(all).Select(f => f.FieldType)
                .Concat(type.GetProperties(all).Select(p => p.PropertyType))
                .Concat(type.GetMethods(all).SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)))
                .Append(type.BaseType ?? typeof(object));
            foreach (var t in used)
                Assert.DoesNotContain(t.Assembly.GetName().Name, forbidden);
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TableForge.slnx"))) dir = dir.Parent;
        var sources = Directory.GetFiles(Path.Combine(dir!.FullName, "TableForge", "Export", "Pum"), "*.cs");
        Assert.NotEmpty(sources);
        foreach (var file in sources)
        {
            var source = File.ReadAllText(file);
            foreach (var ns in new[] { "System.Windows", "TableForge.ViewModels", "TableForge.Views", "TableForge.Data", "TableForge.Dice", "Microsoft.Data" })
                Assert.False(source.Contains($"using {ns}"), $"{Path.GetFileName(file)} uses {ns}");
        }
    }
}
