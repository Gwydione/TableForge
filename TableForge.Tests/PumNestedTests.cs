using System.Security.Cryptography;
using System.Text;
using TableForge.Domain;
using TableForge.Export.Pum;

namespace TableForge.Tests;

/// <summary>
/// Native nested tables in a PUM Companion export (F11): with the option on, a link becomes "→ Name: {{Name}}", which PUM rolls,
/// unless it must stay text. With the option off, the export is exactly what it was before the option existed.
/// </summary>
public class PumNestedTests
{
    private static readonly DateTime Stamp = new(2026, 10, 24, 12, 0, 0);

    private static TableEntry E(int min, int max, string text) => new() { Min = min, Max = max, Text = text };

    private static TableEntry L(int min, int max, string text, long target) => E(min, max, text).Also(e => e.LinkedTableId = target);

    private static RollableTable T(string name, string dice, params TableEntry[] entries) => new()
    {
        Name = name, Dice = DiceExpression.Parse(dice), ResultSets = [new ResultSet { Entries = [.. entries] }],
    };

    private static RollableTable Manual(RollableTable table) => table.Also(t => t.IsManualSelection = true);

    private static string Pum(PumTable table, int roll) => PumEquivalence.PumLookup(table.Rows, roll).Text;

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>
    /// Every kind of link the option has to tell apart, in one Collection: ordinary, a five-level chain, manual-selection source
    /// and destination, a self-link, loops of two and three, a loop that a manual-selection link already breaks, a name another
    /// Collection also uses, two result sets, a duplicate name, a skipped destination, a name PUM would misread, a destination
    /// left out, a missing one, an unresolved one, and a row that already holds a PUM command.
    /// </summary>
    public static IReadOnlyList<PumSourceTable> World()
    {
        var sets = T("Two Sets", "d6");
        sets.ResultSets =
        [
            new ResultSet { Name = "Ambient", Entries = [L(1, 3, "Wolves", 2), E(4, 6, "Quiet")] },
            new ResultSet { Name = "Noise", Entries = [L(1, 6, "Howling", 30)] },
        ];
        var styled = L(1, 6, "Bold lead and italic.", 2).Also(e => e.Styles = TextStyles.FromRuns(
            [new StyleRun(0, 9, TextStyle.Bold), new StyleRun(14, 6, TextStyle.Italic)], "Bold lead and italic.".Length));
        return
        [
            new(1, T("Host", "d6", L(1, 2, "Roll on the target. Then roll it again and choose.", 2), E(3, 6, "Nothing")), "Folder"),
            new(2, T("Target", "d6", E(1, 3, "T low"), E(4, 6, "T high"))),
            new(3, T("Chain 1", "d4", L(1, 4, "C1", 4))),
            new(4, T("Chain 2", "d4", L(1, 4, "C2", 5))),
            new(5, T("Chain 3", "d4", L(1, 4, "C3", 6))),
            new(6, T("Chain 4", "d4", L(1, 4, "C4", 7))),
            new(7, T("Chain 5", "d4", L(1, 4, "C5", 8))),
            new(8, T("Chain End", "d4", E(1, 4, "END"))),
            new(10, Manual(T("Current Depth — Selector", "d4", L(1, 2, "Bogtown", 11), L(3, 4, "Pale", 2)))),
            new(11, T("Bogtown Encounters", "d4", E(1, 4, "Bog"))),
            new(12, T("Area Type", "d4", L(1, 2, "Choose a depth", 10), E(3, 4, "Rest"))),
            new(13, T("Self", "d6", L(1, 1, "Reroll on Self", 13), E(2, 6, "Fine"))),
            new(14, T("Loop X", "d4", L(1, 4, "to Y", 15))),
            new(15, T("Loop Y", "d4", L(1, 4, "to X", 14))),
            new(16, T("Ring P", "d4", L(1, 4, "to Q", 17))),
            new(17, T("Ring Q", "d4", L(1, 4, "to R", 18))),
            new(18, T("Ring R", "d4", L(1, 4, "to P", 16), E(5, 5, "never"))),
            new(19, T("Broken U", "d4", L(1, 4, "to V", 20))),
            new(20, T("Broken V", "d4", L(1, 4, "to the selector", 21))),
            new(21, Manual(T("Broken Selector", "d4", L(1, 4, "back to U", 19)))),
            new(22, T("Event Check", "d4", L(1, 2, "Hazard", 23), L(3, 4, "Loot", 25))),
            new(23, T("hazards", "d4", E(1, 4, "Rockfall"))),
            new(24, T("Loot", "d4", E(1, 4, "Gold"))),
            new(25, T("LOOT", "d4", E(1, 4, "Silver"))),
            new(26, T("Two Sets Holder", "d4", E(1, 4, "x"))),
            new(27, sets),
            new(28, T("Overlapping", "d6", E(1, 4, "A"), E(3, 6, "B"))),
            new(29, T("Points to Overlapping", "d4", L(1, 4, "Skipped target", 28))),
            new(30, T("Wind 100%% Strong", "d4", E(1, 4, "Gust"))),
            new(31, T("Outward", "d6", L(1, 2, "Elsewhere", 90), L(3, 4, "Gone", 91),
                E(5, 6, "Ruins").Also(e => e.UnresolvedLinkName = "Ruin Details"))),
            new(32, T("Has Command", "d4", L(1, 4, "Roll {{Target}} yourself", 2))),
            new(33, T("Styled", "d6", styled)),
        ];
    }

    public static PumExportRequest WorldRequest(bool native = false, IReadOnlyDictionary<string, string>? otherNames = null) =>
        new("World", World(), new Dictionary<long, string> { [90] = "Wilderness" }, native, otherNames);

    // ---- Off: exactly as before ------------------------------------------------------------------------------------------

    // Recorded from the exporter before the option existed (release/1.3.0 at 30f715f), so "off" is proven unchanged.
    private const string WorldBundleSha = "6092CDB83DDD7617F8B2571C6A5E739E43CA5F84FF354B3DB0373616A060F033";
    private const string WorldReportSha = "10D37542A769E815B57893D7C47805144AE7F13DB9FC8D0ED68332DD2A472A0C";
    private const string AcceptanceBundleSha = "2AA89F17DE173FCDB1E3ACB6403FA55B8C5C1F8AB7CAFA24B28156465277FAAE";
    private const string AcceptanceReportSha = "133A12DA67CEC6D33C21FF967F24F865BDC9C52458AF4C1659B7801C3755F17E";

    [Fact]
    public void With_the_option_off_the_bundle_and_report_are_exactly_what_they_were_before_it_existed()
    {
        var world = PumExporter.Export(WorldRequest(), Stamp);
        Assert.Equal(WorldBundleSha, Sha(world.Bundle!));
        Assert.Equal(WorldReportSha, Sha(Encoding.UTF8.GetBytes(world.Report.ToText())));

        var acceptance = PumExporter.Export(PumAcceptanceTests.Request(), PumAcceptanceTests.Stamp);
        Assert.Equal(AcceptanceBundleSha, Sha(acceptance.Bundle!));
        Assert.Equal(AcceptanceReportSha, Sha(Encoding.UTF8.GetBytes(acceptance.Report.ToText())));
    }

    [Fact]
    public void Names_in_other_collections_change_nothing_while_the_option_is_off()
    {
        var off = PumExporter.Export(WorldRequest(otherNames: Elsewhere), Stamp);
        Assert.Equal(WorldBundleSha, Sha(off.Bundle!));
        Assert.Equal(WorldReportSha, Sha(Encoding.UTF8.GetBytes(off.Report.ToText())));
    }

    // ---- On --------------------------------------------------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, string> Elsewhere = new Dictionary<string, string> { ["Hazards"] = "Broken Shores 2e" };

    private static PumExportResult On() => PumExporter.Export(WorldRequest(native: true, otherNames: Elsewhere), Stamp);

    private static PumTable Table(PumExportResult result, string name) => result.Tables.Single(t => t.Name == name);

    private static IReadOnlyList<PumMessage> Messages(PumExportResult result, string name) =>
        result.Report.Tables.Single(t => t.PumName == name).Messages;

    [Fact]
    public void A_link_to_a_table_in_the_bundle_becomes_a_nested_roll_under_the_full_original_text()
    {
        var on = On();
        Assert.Equal("Roll on the target. Then roll it again and choose.\n→ Target: {{Target}}", Pum(Table(on, "Host"), 1));
        Assert.Equal("Nothing", Pum(Table(on, "Host"), 3));
        Assert.Equal("**Bold lead** and *italic*.\n→ Target: {{Target}}", Pum(Table(on, "Styled"), 1));
        Assert.Equal("Loot\n→ LOOT (2): {{LOOT (2)}}", Pum(Table(on, "Event Check"), 3));   // the destination's final PUM name
        Assert.Contains(Messages(on, "Host"), m => m == new PumMessage(PumMessageKind.Conversion, "1 linked row rolls the linked table in PUM, once: \"1–2\"."));
        Assert.DoesNotContain(Messages(on, "Host"), m => m.Text.Contains("→ table\" line"));
    }

    [Fact]
    public void A_five_level_chain_is_nested_at_every_level()
    {
        var on = On();
        Assert.Equal("C1\n→ Chain 2: {{Chain 2}}", Pum(Table(on, "Chain 1"), 1));
        Assert.Equal("C4\n→ Chain 5: {{Chain 5}}", Pum(Table(on, "Chain 4"), 1));
        Assert.Equal("C5\n→ Chain End: {{Chain End}}", Pum(Table(on, "Chain 5"), 1));
    }

    [Fact]
    public void Links_from_or_to_a_manual_selection_table_stay_text()
    {
        var on = On();
        Assert.Equal("Bogtown\n→ Bogtown Encounters", Pum(Table(on, "Current Depth — Selector"), 1));
        Assert.Equal("Pale\n→ Target", Pum(Table(on, "Current Depth — Selector"), 3));   // even to an ordinary table
        Assert.Contains(Messages(on, "Current Depth — Selector"), m => m.Kind == PumMessageKind.Note
            && m.Text == "It is a manual-selection table, so its 2 links to tables in this export stay text: PUM can only roll it, not let you choose its row.");

        Assert.Equal("Choose a depth\n→ Current Depth — Selector", Pum(Table(on, "Area Type"), 1));
        Assert.Contains(Messages(on, "Area Type"), m => m.Kind == PumMessageKind.Note
            && m.Text == "The row \"1–2\" links to \"Current Depth — Selector\", a manual-selection table, so it stays text: you choose that table's row.");
    }

    [Fact]
    public void Self_links_and_loops_stay_text_but_a_loop_a_text_link_already_breaks_is_rolled()
    {
        var on = On();
        Assert.Equal("Reroll on Self\n→ Self", Pum(Table(on, "Self"), 1));
        Assert.Equal("to Y\n→ Loop Y", Pum(Table(on, "Loop X"), 1));
        Assert.Equal("to X\n→ Loop X", Pum(Table(on, "Loop Y"), 1));
        foreach (var (ring, next) in new[] { ("Ring P", "Ring Q"), ("Ring Q", "Ring R"), ("Ring R", "Ring P") })
            Assert.Equal($"to {next[^1]}\n→ {next}", Pum(Table(on, ring), 1));
        Assert.Contains(Messages(on, "Self"), m => m.Kind == PumMessageKind.Note
            && m.Text == "The row \"1\" links to \"Self\", which leads back here, so it stays text: PUM would keep rolling round the loop.");

        // U → V → (manual) selector → U: only U → V could be rolled, and it can't lead back to U.
        Assert.Equal("to V\n→ Broken V: {{Broken V}}", Pum(Table(on, "Broken U"), 1));
        Assert.Equal("to the selector\n→ Broken Selector", Pum(Table(on, "Broken V"), 1));
    }

    [Fact]
    public void A_name_another_collection_also_uses_stays_text_with_a_warning_naming_that_collection()
    {
        var on = On();
        Assert.Equal("Hazard\n→ hazards", Pum(Table(on, "Event Check"), 1));   // "hazards" here, "Hazards" there: PUM ignores case
        var warning = Assert.Single(Messages(on, "Event Check"), m => m.Kind == PumMessageKind.Warning);
        Assert.Equal("The row \"1–2\" links to \"hazards\", but \"Broken Shores 2e\" also has a table with that name, so it stays text: "
            + "PUM finds tables by name alone (in every category, ignoring case), so in PUM they would be one table.", warning.Text);
    }

    [Fact]
    public void Each_result_set_rolls_its_own_link_and_a_name_PUM_would_misread_stays_text()
    {
        var on = On();
        Assert.Equal("Ambient: Wolves\n→ Target: {{Target}}\nNoise: Howling\n→ Wind 100%% Strong", Pum(Table(on, "Two Sets"), 1));
        Assert.Equal("Ambient: Quiet\nNoise: Howling\n→ Wind 100%% Strong", Pum(Table(on, "Two Sets"), 4));
        Assert.Contains(Messages(on, "Two Sets"), m => m.Kind == PumMessageKind.Note
            && m.Text == "The row \"1–6\" (Noise) links to \"Wind 100%% Strong\", whose name PUM would misread inside a nested roll, so it stays text.");

        var sets = World()[25].Table;
        sets.ResultSets[1].Entries[0].LinkedTableId = 2;
        var both = PumExporter.Export(new PumExportRequest("C", [new(1, sets), new(2, T("Target", "d6", E(1, 6, "T")))], NativeNestedTables: true), Stamp);
        Assert.Equal("Ambient: Wolves\n→ Target: {{Target}}\nNoise: Howling\n→ Target: {{Target}}", Pum(both.Tables[0], 1));
    }

    [Fact]
    public void Links_out_of_the_bundle_missing_unresolved_or_to_a_skipped_table_are_exactly_as_with_the_option_off()
    {
        var on = On();
        var off = PumExporter.Export(WorldRequest(), Stamp);
        Assert.Equal(Table(off, "Outward").Rows, Table(on, "Outward").Rows);
        Assert.Equal(Table(off, "Points to Overlapping").Rows, Table(on, "Points to Overlapping").Rows);
        Assert.Equal(Messages(off, "Outward"), Messages(on, "Outward"));
    }

    [Fact]
    public void The_option_never_changes_which_tables_are_exported_their_rows_or_the_rolls_they_match()
    {
        var on = On();
        var off = PumExporter.Export(WorldRequest(), Stamp);
        Assert.Equal(off.Report.Tables.Select(t => (t.PumName, t.SkipReason, t.NormalRollMismatch)),
            on.Report.Tables.Select(t => (t.PumName, t.SkipReason, t.NormalRollMismatch)));
        Assert.Equal(off.Tables.Select(t => (t.Id, t.Name, t.Category, t.Formula, string.Join(",", t.Rows.Select(r => r.Key)))),
            on.Tables.Select(t => (t.Id, t.Name, t.Category, t.Formula, string.Join(",", t.Rows.Select(r => r.Key)))));

        // A row that already holds a PUM command is still skipped, for the same reason.
        Assert.Equal("The row \"1–4\" contains \"{{Target}}\", and PUM would roll another table there. The exporter never writes PUM commands.",
            on.Report.Tables.Single(t => t.SourceName == "Has Command").SkipReason);
    }

    [Fact]
    public void Every_nested_roll_names_a_table_in_the_bundle_exactly_and_none_can_lead_back_to_itself()
    {
        var on = On();
        var names = on.Tables.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var rolls = on.Tables.ToDictionary(t => t.Name, t => t.Rows
            .SelectMany(r => System.Text.RegularExpressions.Regex.Matches(r.Text, @"\{\{(.*?)\}\}").Select(m => m.Groups[1].Value)).ToHashSet());
        Assert.Equal(10, rolls.Values.Sum(r => r.Count));   // Host, Chain 1-5, Broken U, Event Check, Two Sets and Styled
        Assert.All(rolls.Values.SelectMany(r => r), name => Assert.Contains(name, names));

        foreach (var start in rolls.Keys)
        {
            var seen = new HashSet<string>();
            var stack = new Stack<string>(rolls[start]);
            while (stack.TryPop(out var at))
            {
                Assert.NotEqual(start, at);
                if (seen.Add(at)) foreach (var next in rolls[at]) stack.Push(next);
            }
        }
    }

    [Fact]
    public void The_notes_say_how_links_are_written()
    {
        var on = On();
        Assert.Same(PumExporter.NativeNestedNotes, on.Report.GeneralNotes);
        Assert.Contains(PumExporter.NameClashDetail, on.Report.GeneralNotes);
        Assert.DoesNotContain(on.Report.GeneralNotes, n => n.StartsWith("Links are written as a \"→ table\" line"));
        Assert.Same(PumExporter.GeneralNotes, PumExporter.Export(WorldRequest(), Stamp).Report.GeneralNotes);
    }

    [Fact]
    public void A_single_table_export_is_the_same_with_the_option_on()
    {
        var self = new PumSourceTable(13, T("Self", "d6", L(1, 1, "Reroll on Self", 13), E(2, 6, "Fine")));
        var off = PumExporter.Export(new PumExportRequest("C", [self]), Stamp);
        var on = PumExporter.Export(new PumExportRequest("C", [self], NativeNestedTables: true), Stamp);
        Assert.Equal(off.Tables[0].Rows, on.Tables[0].Rows);
    }
}
