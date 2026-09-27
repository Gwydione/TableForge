using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using TableForge.Domain;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>What <see cref="TablesPlusTableExporter"/> produces, checked as JSON (name, type, dice, entries), not as spelling.</summary>
public class TablesPlusTableExporterTests
{
    private static TableEntry E(int min, int max, string text, string? display = null) => new() { Min = min, Max = max, Text = text, DisplayRange = display };

    internal static RollableTable Table(string name, string dice, params TableEntry[] entries) => Table(name, DiceExpression.Parse(dice), entries);

    internal static RollableTable Table(string name, DiceExpression dice, params TableEntry[] entries) => new()
    {
        Name = name, Dice = dice, ResultSets = [new ResultSet { Entries = [.. entries] }],
    };

    private static TablesPlusExport Export(RollableTable table, int set = 0)
    {
        Assert.True(TablesPlusTableExporter.TryExport(table, table.ResultSets[set], out var export, out var error), error);
        return export!;
    }

    private static JsonElement Json(RollableTable table, int set = 0) => JsonDocument.Parse(Export(table, set).Json).RootElement;

    private static (string Type, string Dice) TypeAndDice(JsonElement json) => (json.GetProperty("type").GetString()!, json.GetProperty("dice").GetString()!);

    private static List<(int Low, int High, string Text)> Entries(JsonElement json) => json.GetProperty("entries").EnumerateArray()
        .Select(r => (r.GetProperty("low").GetInt32(), r.GetProperty("high").GetInt32(), r.GetProperty("text").GetString()!))
        .ToList();

    private static string Refused(RollableTable table)
    {
        Assert.False(TablesPlusTableExporter.TryExport(table, table.ResultSets[0], out var export, out var error));
        Assert.Null(export);
        return error!;
    }

    // ---- Type and dice ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("d6", "weighted", "1d6")]
    [InlineData("d20", "weighted", "1d20")]
    [InlineData("d100", "weighted", "1d100")]
    [InlineData("d20+2", "weighted", "1d20+2")]
    [InlineData("d20-2", "weighted", "1d20-2")]
    [InlineData("d100+5", "weighted", "1d100+5")]
    [InlineData("2d6", "bell-curve", "2d6")]
    [InlineData("3d6", "bell-curve", "3d6")]
    [InlineData("2d10", "bell-curve", "2d10")]
    public void Dice_become_a_Tables_plus_type_and_canonical_dice(string dice, string type, string formula)
    {
        Assert.True(TablesPlusTableExporter.TryMapDice(DiceExpression.Parse(dice), out var t, out var f));
        Assert.Equal((type, formula), (t, f));
        Assert.Equal((type, formula), TypeAndDice(Json(Table("T", dice, E(1, 1, "x")))));
    }

    [Fact]
    public void The_d66_convention_is_weighted_T66_and_never_2d6_or_1d66()
    {
        var json = Export(Table("T", "d66", E(11, 66, "x"))).Json;
        Assert.Equal(("weighted", "T66"), TypeAndDice(JsonDocument.Parse(json).RootElement));
        Assert.DoesNotContain("2d6", json);
        Assert.DoesNotContain("1d66", json);
        Assert.Equal(("weighted", "T66"), TypeAndDice(Json(Table("T", new DiceExpression(2, 6, 0, RollConvention.D66), E(11, 66, "x")))));
    }

    [Fact]
    public void A_genuine_66_sided_die_is_weighted_1d66_and_is_not_the_d66_convention()
    {
        var real = DiceExpression.Parse("1d66");
        Assert.Equal((1, 66, false), (real.Count, real.Sides, real.IsD66));
        Assert.Equal(("weighted", "1d66"), TypeAndDice(Json(Table("T", real, E(1, 66, "x")))));
        Assert.Equal(("weighted", "1d66"), TypeAndDice(Json(Table("T", new DiceExpression(1, 66), E(1, 66, "x")))));
    }

    [Theory]
    [InlineData("2d6+1")]
    [InlineData("2d6-1")]
    [InlineData("3d8+4")]
    [InlineData("2d10-3")]
    public void Several_dice_with_a_fixed_modifier_are_refused_never_exported_without_it(string dice)
    {
        Assert.False(TablesPlusTableExporter.TryMapDice(DiceExpression.Parse(dice), out _, out _));
        Assert.Equal(TablesPlusTableExporter.ModifiedDiceError, Refused(Table("T", dice, E(3, 13, "x"))));
    }

    // ---- Ranges -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Low_and_high_are_the_stored_numbers_never_the_written_notation()
    {
        var table = Table("OMENS", "d100", E(1, 5, "Cold", "01–05"), E(8, 8, "Odd", "08"), E(9, 95, "Mild"), E(96, 100, "Doom", "96–00"));
        Assert.Equal([(1, 5, "Cold"), (8, 8, "Odd"), (9, 95, "Mild"), (96, 100, "Doom")], Entries(Json(table)));
        Assert.DoesNotContain("\"08\"", Export(table).Json);
        Assert.DoesNotContain("\"96-00\"", Export(table).Json);
        Assert.DoesNotContain("–", Export(table).Json);
    }

    [Fact]
    public void A_d66_range_covering_unrollable_numbers_is_kept_as_written()
    {
        var table = Table("T", "d66", E(11, 14, "Low"), E(15, 22, "Across"), E(23, 66, "High"));
        Assert.Equal([(11, 14, "Low"), (15, 22, "Across"), (23, 66, "High")], Entries(Json(table)));
    }

    [Fact]
    public void Gaps_overlaps_and_order_are_exported_unchanged_and_not_warned_about()
    {
        var export = Export(Table("T", "d10", E(1, 4, "A"), E(7, 10, "B"), E(3, 5, "C")));
        Assert.Equal([(1, 4, "A"), (7, 10, "B"), (3, 5, "C")], Entries(JsonDocument.Parse(export.Json).RootElement));
        Assert.Empty(export.Warnings);
    }

    [Fact]
    public void Signed_ranges_are_kept_exactly_and_never_shifted_for_the_modifier()
    {
        var down = Json(Table("T", "d4-2", E(-1, 0, "Bad"), E(1, 1, "Meh"), E(2, 2, "Good")));
        Assert.Equal(("weighted", "1d4-2"), TypeAndDice(down));
        Assert.Equal([(-1, 0, "Bad"), (1, 1, "Meh"), (2, 2, "Good")], Entries(down));

        var up = Json(Table("T", "d20+2", E(3, 10, "Low"), E(11, 22, "High")));
        Assert.Equal([(3, 10, "Low"), (11, 22, "High")], Entries(up));
    }

    // ---- Text -------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("It's a trap")]
    [InlineData("The king’s sword — broken")]
    [InlineData("Sword & Shield")]
    [InlineData("Value < 10 > 2")]
    [InlineData("He said \"run\"")]
    [InlineData(@"C:\dungeon\map")]
    [InlineData("Obtain 1d6 trinkets.")]
    [InlineData("{2d6} goblins")]
    [InlineData("Roll twice #reroll")]
    [InlineData("Ünïcödé 竜 🐉")]
    [InlineData("<b>not HTML to TableForge</b>")]
    public void Result_text_round_trips_exactly(string text)
    {
        Assert.Equal(text, Entries(Json(Table("T", "d6", E(1, 6, text))))[0].Text);
    }

    [Fact]
    public void Multiline_text_keeps_its_line_breaks_and_is_only_trimmed_like_Foundry()
    {
        Assert.Equal("Line one\nLine two", Entries(Json(Table("T", "d6", E(1, 6, "  Line one\nLine two \n"))))[0].Text);
        Assert.Equal("Line one\r\nLine two", Entries(Json(Table("T", "d6", E(1, 6, "Line one\r\nLine two"))))[0].Text);
    }

    [Fact]
    public void The_json_stays_readable_rather_than_escaping_ordinary_characters()
    {
        var json = Export(Table("The king’s table", "d6", E(1, 6, "Sword & Shield < 10"))).Json;
        Assert.Contains("The king’s table", json);
        Assert.Contains("Sword & Shield < 10", json);
    }

    // ---- What is and is not exported --------------------------------------------------------------------------------

    [Fact]
    public void Only_name_type_dice_and_entries_with_low_high_and_text_are_exported()
    {
        var table = Fixtures.Scavenging(3, itemsTableId: 5).Also(t => { t.Id = 42; t.ClampResultsToRange = true; });
        foreach (var (entry, i) in table.ResultSets[0].Entries.Select((e, i) => (e, i))) entry.Id = 100 + i;
        var json = Json(table);

        Assert.Equal(["name", "type", "dice", "entries"], json.EnumerateObject().Select(p => p.Name));
        Assert.All(json.GetProperty("entries").EnumerateArray(), r => Assert.Equal(["low", "high", "text"], r.EnumerateObject().Select(p => p.Name)));
        Assert.Equal(("Scavenging", "bell-curve", "2d6"), (json.GetProperty("name").GetString(), json.GetProperty("type").GetString(), json.GetProperty("dice").GetString()));
        Assert.Equal([(2, 5, "Nothing useful"), (6, 8, "1x Scavenged Item"), (9, 10, "2x Scavenged Items"), (11, 12, "Valuable find")], Entries(json));
        Assert.DoesNotContain("42", Export(table).Json);
        Assert.DoesNotContain("100", Export(table).Json);
    }

    [Fact]
    public void A_link_exports_only_the_authored_text_never_its_destination()
    {
        var table = Fixtures.Scavenging(3, itemsTableId: null, unresolvedName: "Loot Pile");
        var json = Export(table).Json;
        Assert.DoesNotContain("Loot Pile", json);
        Assert.Equal("1x Scavenged Item", Entries(JsonDocument.Parse(json).RootElement)[1].Text);
    }

    [Fact]
    public void Clamp_never_changes_the_json()
    {
        var off = Table("T", "d20+2", E(1, 20, "x"));
        var on = Table("T", "d20+2", E(1, 20, "x")).Also(t => t.ClampResultsToRange = true);
        Assert.Equal(Export(off).Json, Export(on).Json);
    }

    [Fact]
    public void The_same_table_always_gives_the_same_json()
    {
        Assert.Equal(Export(Fixtures.RoomFeatures(), 1).Json, Export(Fixtures.RoomFeatures(), 1).Json);
    }

    // ---- Result sets and names --------------------------------------------------------------------------------------

    [Fact]
    public void One_result_set_is_named_after_the_table()
    {
        Assert.Equal("Scavenged Items", Json(Fixtures.ScavengedItems()).GetProperty("name").GetString());
        Assert.Equal("Scavenged Items", Export(Fixtures.ScavengedItems()).Name);
    }

    [Fact]
    public void Several_result_sets_export_only_the_chosen_one_named_table_dash_set()
    {
        var json = Json(Fixtures.RoomFeatures(), 1);
        Assert.Equal("Room Features — Noise", json.GetProperty("name").GetString());
        Assert.Equal(("weighted", "1d100"), TypeAndDice(json));
        Assert.Equal([(1, 25, "Silence"), (26, 50, "Distant scratching"), (51, 66, "Dripping water"), (67, 71, "Hissing"), (72, 100, "Low chanting")],
            Entries(json));
    }

    [Fact]
    public void An_unnamed_result_set_among_several_is_named_by_its_position()
    {
        var table = new RollableTable
        {
            Name = " Two ", Dice = DiceExpression.Parse("d4"),
            ResultSets = [new ResultSet { Entries = [E(1, 4, "a")] }, new ResultSet { Name = " Second ", Entries = [E(1, 4, "b")] }],
        };
        Assert.Equal("Two — Result set 1", Export(table, 0).Name);
        Assert.Equal("Two — Second", Export(table, 1).Name);
        Assert.Equal([(1, 4, "b")], Entries(Json(table, 1)));
    }

    // ---- Refused, as Foundry refuses --------------------------------------------------------------------------------

    [Fact]
    public void A_result_set_from_another_table_is_refused()
    {
        var table = Table("T", "d6", E(1, 6, "x"));
        Assert.False(TablesPlusTableExporter.TryExport(table, new ResultSet { Entries = [E(1, 6, "x")] }, out var export, out var error));
        Assert.Null(export);
        Assert.Equal("Choose a result set to export.", error);
    }

    [Fact]
    public void Empty_backwards_and_nameless_tables_are_refused_with_the_Foundry_reasons()
    {
        foreach (var table in new[] { Table("T", "d6"), Table("T", "d6", E(1, 2, "ok"), E(5, 3, "bad", "5–3")), Table("  ", "d6", E(1, 6, "x")) })
        {
            Assert.False(FoundryTableExporter.TryExport(table, table.ResultSets[0], out _, out var foundry));
            Assert.Equal(foundry, Refused(table));
        }
        Assert.Equal("The row \"5–3\" has a range that runs backwards.", Refused(Table("T", "d6", E(1, 2, "ok"), E(5, 3, "bad", "5–3"))));
    }

    // ---- Clamp warning ----------------------------------------------------------------------------------------------

    [Fact]
    public void Clamp_with_rolls_beyond_the_rows_warns_in_Tables_plus_words_but_still_exports()
    {
        Assert.Equal("Tables+ JSON does not include TableForge’s Clamp to Range setting; rolls outside the exported ranges may have no matching entry.",
            TablesPlusTableExporter.ClampWarning);

        var export = Export(Table("T", "d20+2", E(1, 20, "x")).Also(t => t.ClampResultsToRange = true));
        Assert.Equal([TablesPlusTableExporter.ClampWarning], export.Warnings);
        Assert.Equal("1d20+2", JsonDocument.Parse(export.Json).RootElement.GetProperty("dice").GetString());

        Assert.Equal([TablesPlusTableExporter.ClampWarning], Export(Table("T", "d20-2", E(1, 20, "x")).Also(t => t.ClampResultsToRange = true)).Warnings);
    }

    [Fact]
    public void Clamp_is_mentioned_exactly_when_Foundry_would_mention_it()
    {
        var cases = new[]
        {
            Table("T", "d20", E(1, 20, "x")).Also(t => t.ClampResultsToRange = true),        // covers every roll
            Table("T", "d20+2", E(1, 20, "x")),                                                // Clamp off
            Table("T", "d20+2", E(1, 20, "x")).Also(t => t.ClampResultsToRange = true),       // applies
            new RollableTable                                                                   // on but unavailable
            {
                Name = "T", Dice = DiceExpression.Parse("d20+2"), ClampResultsToRange = true,
                ResultSets = [new ResultSet { Entries = [E(1, 20, "a")] }, new ResultSet { Entries = [E(1, 10, "b")] }],
            },
        };
        Assert.Equal([false, false, true, false], cases.Select(t => Export(t).Warnings.Count > 0));
        foreach (var table in cases)
        {
            Assert.True(FoundryTableExporter.TryExport(table, table.ResultSets[0], out var foundry, out _));
            Assert.Equal(foundry!.Warnings.Count, Export(table).Warnings.Count);
        }
    }

    // ---- File name --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_suggested_file_name_is_made_like_Foundry_s_with_its_own_fallback()
    {
        Assert.Equal("Table_ A_B_ C_.json", TablesPlusTableExporter.SuggestedFileName("Table: A/B? C*"));
        Assert.Equal("Tables+ table.json", TablesPlusTableExporter.SuggestedFileName("..."));
        Assert.Equal("Foundry table.json", FoundryTableExporter.SuggestedFileName("..."));
    }
}

/// <summary>
/// The external-import fixtures in docs/external-tests/tables-plus must be exactly what the exporter writes today. Set
/// TABLEFORGE_WRITE_FIXTURES=1 and run this test to rewrite them after a deliberate change. Matching fixtures prove the JSON
/// shape only: they say nothing about whether Tables+ imports or rolls them as intended.
/// </summary>
public class TablesPlusFixtureTests
{
    private static TableEntry E(int min, int max, string text, string? display = null) => new() { Min = min, Max = max, Text = text, DisplayRange = display };

    private static RollableTable T(string name, string dice, params TableEntry[] entries) => TablesPlusTableExporterTests.Table(name, dice, entries);

    /// <summary>One fixture per behavior to check in Tables+. 2d6+1 is deliberately absent: it is refused until Tables+ is known to handle it.</summary>
    public static readonly (string File, RollableTable Table)[] Cases =
    [
        ("d20.json", T("TF Test d20", "d20", E(1, 5, "Wolf pack"), E(6, 15, "Nothing"), E(16, 20, "Travelling merchant"))),
        ("d100.json", T("TF Test d100", "d100", E(1, 5, "Cold", "01–05"), E(6, 95, "Mild"), E(96, 100, "Doom", "96–00"))),
        ("genuine-1d66.json", T("TF Test 1d66 die", "1d66", E(1, 33, "Low"), E(34, 65, "High"), E(66, 66, "Top"))),
        ("d66-T66.json", T("TF Test d66", "d66",
            E(11, 16, "Tens 1"), E(21, 26, "Tens 2"), E(31, 36, "Tens 3"), E(41, 46, "Tens 4"), E(51, 56, "Tens 5"), E(61, 66, "Tens 6"))),
        ("2d6.json", T("TF Test 2d6", "2d6", E(2, 5, "Low"), E(6, 8, "Middle"), E(9, 12, "High"))),
        ("d20-plus-2.json", T("TF Test d20+2", "d20+2", E(3, 12, "Lower half"), E(13, 22, "Upper half"))),
        ("d4-minus-2-signed.json", T("TF Test d4-2", "d4-2", E(-1, 0, "Negative or zero"), E(1, 1, "One"), E(2, 2, "Two"))),
        ("gaps-overlaps.json", T("TF Test gaps and overlaps", "d10", E(1, 4, "A (1-4)"), E(7, 10, "B (7-10)"), E(3, 5, "C (3-5, overlaps A)"))),
        ("text-inline-2d6.json", T("TF Test inline {2d6}", "d4", E(1, 2, "{2d6} goblins"), E(3, 4, "Plain text"))),
        ("text-reroll.json", T("TF Test #reroll", "d4", E(1, 2, "Roll again #reroll"), E(3, 4, "Plain text"))),
    ];

    private static string FixtureFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TableForge.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root."),
            "docs", "external-tests", "tables-plus");
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');

    [Fact]
    public void Every_fixture_matches_the_exporter_and_there_are_no_others()
    {
        var folder = FixtureFolder();
        var write = Environment.GetEnvironmentVariable("TABLEFORGE_WRITE_FIXTURES") == "1";
        if (write) Directory.CreateDirectory(folder);
        foreach (var (file, table) in Cases)
        {
            Assert.True(TablesPlusTableExporter.TryExport(table, table.ResultSets[0], out var export, out var error), $"{file}: {error}");
            var path = Path.Combine(folder, file);
            if (write) File.WriteAllText(path, export!.Json + "\n", new UTF8Encoding(false));
            Assert.True(File.Exists(path), $"{file} is missing");
            Assert.Equal(Normalize(export!.Json), Normalize(File.ReadAllText(path)));
        }
        Assert.Equal(Cases.Select(c => c.File).Order(), Directory.GetFiles(folder, "*.json").Select(f => Path.GetFileName(f)!).Order());
    }

    [Fact]
    public void The_fixtures_cover_every_mapping_and_both_special_strings()
    {
        var mapped = Cases.Select(c => JsonDocument.Parse(Export(c.Table)).RootElement)
            .Select(j => (j.GetProperty("type").GetString(), j.GetProperty("dice").GetString())).ToHashSet();
        foreach (var expected in new[] { ("weighted", "1d20"), ("weighted", "1d100"), ("weighted", "1d66"), ("weighted", "T66"),
                     ("bell-curve", "2d6"), ("weighted", "1d20+2"), ("weighted", "1d4-2") })
            Assert.Contains(expected, mapped);

        var texts = string.Join("\n", Cases.Select(c => Export(c.Table)));
        Assert.Contains("{2d6} goblins", texts);
        Assert.Contains("#reroll", texts);
        Assert.DoesNotContain(Cases, c => c.Table.Dice.Count > 1 && c.Table.Dice.Modifier != 0);
    }

    private static string Export(RollableTable table)
    {
        Assert.True(TablesPlusTableExporter.TryExport(table, table.ResultSets[0], out var export, out _));
        return export!.Json;
    }
}

/// <summary>Copy Tables+ JSON and Save Tables+ JSON… on the Roll screen: the Foundry pattern, for Tables+.</summary>
public class TablesPlusExportRollTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("tableforge-tablesplus-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static string TablesPlusJson(RollableTable table, int set)
    {
        Assert.True(TablesPlusTableExporter.TryExport(table, table.ResultSets[set], out var export, out _));
        return export!.Json;
    }

    [Fact]
    public void Copying_puts_the_json_on_the_clipboard_and_leaves_rolls_and_history_alone()
    {
        var copied = new List<string>();
        var history = new List<RollSnapshot>();
        var table = Fixtures.Table(DiceExpression.Parse("d6"), (1, 3, "You gain +1d4 Armor"), (4, 6, "Nothing"));
        var session = new RollViewModel(table, new SequenceDice(2, 5, 3), rolled: history.Add, copyText: copied.Add);
        session.ModifierText = "+1";
        session.RollCount = 2;
        session.RollCommand.Execute(null);
        session.LatestRolls[0].Lines[0].InlineActions[0].RollCommand!.Execute(null);
        var before = (session.RollDisplay, session.ModifierText, session.RollCount, history.Count, session.LatestRolls.Count,
            session.LatestRolls[0].Lines[0].ResolvedText);

        session.CopyTablesPlusJsonCommand.Execute(null);

        Assert.Equal(TablesPlusJson(table, 0), Assert.Single(copied));
        Assert.Contains("You gain +1d4 Armor", copied[0]);                                          // source text, not the resolved value
        Assert.DoesNotContain("+1\"", copied[0]);                                                   // no situational modifier
        Assert.Equal("Tables+ JSON copied.", session.CopyMessage);
        Assert.False(session.HasExportWarning);
        Assert.Equal(before, (session.RollDisplay, session.ModifierText, session.RollCount, history.Count, session.LatestRolls.Count,
            session.LatestRolls[0].Lines[0].ResolvedText));
    }

    [Fact]
    public void Saving_writes_exactly_the_copied_json_as_UTF8_with_a_Tables_plus_dialog()
    {
        var copied = new List<string>();
        var requests = new List<SaveFileRequest>();
        var path = Path.Combine(_folder, "chosen.json");
        var session = new RollViewModel(Fixtures.RoomFeatures(), new SequenceDice(1), copyText: copied.Add,
            chooseSaveFile: request => { requests.Add(request); return path; });
        session.CopyResultSetIndex = 2;

        session.SaveTablesPlusJsonCommand.Execute(null);
        session.CopyTablesPlusJsonCommand.Execute(null);

        Assert.Equal([new SaveFileRequest("Save Tables+ JSON", "Room Features — General Feature.json")], requests);
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));                   // no BOM
        Assert.Equal(Assert.Single(copied), Encoding.UTF8.GetString(bytes));
        Assert.Equal(TablesPlusJson(Fixtures.RoomFeatures(), 2), copied[0]);
        Assert.Equal("Tables+ JSON copied.", session.CopyMessage);
    }

    [Fact]
    public void Save_reports_where_it_saved_and_cancelling_does_nothing()
    {
        string? choice = null;
        var history = new List<RollSnapshot>();
        var session = new RollViewModel(Fixtures.Table(DiceExpression.Parse("d6"), (1, 6, "x")), new SequenceDice(1), rolled: history.Add,
            chooseSaveFile: _ => choice);

        session.SaveTablesPlusJsonCommand.Execute(null);
        Assert.Equal(("", false), (session.CopyMessage, session.HasExportWarning));
        Assert.Empty(Directory.GetFiles(_folder));
        Assert.Empty(history);

        choice = Path.Combine(_folder, "Weather.json");
        session.SaveTablesPlusJsonCommand.Execute(null);
        Assert.Equal("Tables+ JSON saved to Weather.json.", session.CopyMessage);
        Assert.True(File.Exists(choice));
        Assert.Empty(history);
    }

    [Fact]
    public void A_failed_save_or_busy_clipboard_is_reported_not_thrown()
    {
        var missing = Path.Combine(_folder, "no such folder", "x.json");
        var session = new RollViewModel(Fixtures.Table(DiceExpression.Parse("d6"), (1, 6, "x")), new SequenceDice(1),
            copyText: _ => throw new System.Runtime.InteropServices.COMException("OpenClipboard failed"), chooseSaveFile: _ => missing);

        session.CopyTablesPlusJsonCommand.Execute(null);
        Assert.Equal("The Tables+ JSON could not be copied: OpenClipboard failed", session.CopyMessage);

        session.SaveTablesPlusJsonCommand.Execute(null);
        Assert.StartsWith("The Tables+ JSON could not be saved: ", session.CopyMessage);
    }

    [Fact]
    public void A_2d6_plus_1_table_says_why_and_nothing_is_copied_or_saved_while_Foundry_still_exports_it()
    {
        var copied = new List<string>();
        var asked = 0;
        var table = Fixtures.Table(DiceExpression.Parse("2d6+1"), (3, 13, "x"));
        var session = new RollViewModel(table, new SequenceDice(1), copyText: copied.Add, chooseSaveFile: _ => { asked++; return null; });

        session.CopyTablesPlusJsonCommand.Execute(null);
        Assert.Equal($"This table cannot be exported to Tables+: {TablesPlusTableExporter.ModifiedDiceError}", session.CopyMessage);
        session.SaveTablesPlusJsonCommand.Execute(null);
        Assert.Empty(copied);
        Assert.Equal(0, asked);

        session.CopyFoundryJsonCommand.Execute(null);
        Assert.Equal("Foundry JSON copied.", session.CopyMessage);
    }

    [Fact]
    public void The_clamp_warning_shows_after_a_Tables_plus_export_and_clears_with_the_next_choice()
    {
        var table = Fixtures.RoomFeatures().Also(t => { t.Dice = DiceExpression.Parse("d100+5"); t.ClampResultsToRange = true; });
        var session = new RollViewModel(table, new SequenceDice(1), copyText: _ => { });
        Assert.False(session.HasExportWarning);

        session.CopyTablesPlusJsonCommand.Execute(null);
        Assert.Equal((TablesPlusTableExporter.ClampWarning, "Tables+ JSON copied."), (session.ExportWarning, session.CopyMessage));

        session.CopyFoundryJsonCommand.Execute(null);                                                // each format gives its own words
        Assert.Equal(FoundryTableExporter.ClampWarning, session.ExportWarning);

        session.CopyTablesPlusJsonCommand.Execute(null);
        session.CopyResultSetIndex = 1;
        Assert.Equal(("", ""), (session.CopyMessage, session.ExportWarning));
    }
}

/// <summary>Copy Tables+ JSON and Save Tables+ JSON… through the real window.</summary>
[Collection("UI")]
public class TablesPlusExportViewTests
{
    [Fact]
    public void Export_offers_Tables_plus_json_after_Foundry_and_both_actions_work()
    {
        Sta.Run(() =>
        {
            var folder = Directory.CreateTempSubdirectory("tableforge-tablesplus-ui-").FullName;
            try
            {
                using var ui = new UiHarness(3, (db, c) =>
                    db.SaveTable(Fixtures.Table(DiceExpression.Parse("d20+2"), (1, 10, "Sword & Shield"), (11, 20, "Value < 10"))
                        .Also(t => { t.Name = "Loot: Arms?"; t.CollectionId = c.Id; t.ClampResultsToRange = true; })));
                ui.SelectTable("Loot: Arms?");
                ui.Click("Roll");

                var menu = ui.One<Button>(b => b.Name == "ExportButton").ContextMenu;
                Assert.Equal(["Copy Table Text", "Copy for Sojour", "|", "Copy Foundry JSON", "Save Foundry JSON…", "|", "Copy Tables+ JSON", "Save Tables+ JSON…"],
                    menu.Items.Cast<object>().Select(i => i is MenuItem m ? m.Header as string : "|"));
                Assert.All(menu.Items.OfType<MenuItem>().Where(i => (i.Header as string)!.Contains("Tables+")),
                    i => Assert.Contains("Owlbear Rodeo (Tables+)", i.ToolTip as string));

                ui.ChooseExport("Copy Tables+ JSON");
                var json = JsonDocument.Parse(Assert.Single(ui.Copied)).RootElement;
                Assert.Equal(("Loot: Arms?", "weighted", "1d20+2"),
                    (json.GetProperty("name").GetString(), json.GetProperty("type").GetString(), json.GetProperty("dice").GetString()));
                Assert.Contains(ui.Texts(), t => t.Name == "CopyMessageText" && t.Text == "Tables+ JSON copied.");
                Assert.Contains(ui.Texts(), t => t.Name == "ExportWarningText" && t.Text == TablesPlusTableExporter.ClampWarning);
                Assert.Single(ui.Db.GetRollHistory());                                              // the roll is still the only one

                ui.SavePath = Path.Combine(folder, "loot.json");
                ui.ChooseExport("Save Tables+ JSON…");
                Assert.Equal(["Loot_ Arms_.json"], ui.SuggestedFileNames);
                Assert.Equal(["Save Tables+ JSON"], ui.SaveTitles);
                Assert.Equal(ui.Copied[0], File.ReadAllText(ui.SavePath));
                Assert.Contains(ui.Texts(), t => t.Name == "CopyMessageText" && t.Text == "Tables+ JSON saved to loot.json.");
                Assert.Single(ui.Db.GetRollHistory());
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        });
    }
}
