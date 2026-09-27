using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using TableForge.Domain;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>What <see cref="FoundryTableExporter"/> produces, checked as JSON (names, formula, ranges, text), not as spelling.</summary>
public class FoundryTableExporterTests
{
    private static TableEntry E(int min, int max, string text, string? display = null) => new() { Min = min, Max = max, Text = text, DisplayRange = display };

    private static RollableTable Table(string name, string dice, params TableEntry[] entries) => new()
    {
        Name = name, Dice = DiceExpression.Parse(dice), ResultSets = [new ResultSet { Entries = [.. entries] }],
    };

    private static FoundryExport Export(RollableTable table, int set = 0)
    {
        Assert.True(FoundryTableExporter.TryExport(table, table.ResultSets[set], out var export, out var error), error);
        return export!;
    }

    private static JsonElement Json(RollableTable table, int set = 0) => JsonDocument.Parse(Export(table, set).Json).RootElement;

    private static List<(int Min, int Max, string Text)> Results(JsonElement json) => json.GetProperty("results").EnumerateArray()
        .Select(r => (r.GetProperty("range")[0].GetInt32(), r.GetProperty("range")[1].GetInt32(), r.GetProperty("text").GetString()!))
        .ToList();

    // ---- Formula ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("d6", "1d6")]
    [InlineData("d20", "1d20")]
    [InlineData("2d6", "2d6")]
    [InlineData("2d6+1", "2d6+1")]
    [InlineData("d20-2", "1d20-2")]
    [InlineData("3d8+4", "3d8+4")]
    [InlineData("d100", "1d100")]
    public void Ordinary_dice_become_a_canonical_Foundry_formula(string dice, string formula)
    {
        Assert.Equal(formula, FoundryTableExporter.Formula(DiceExpression.Parse(dice)));
        Assert.Equal(formula, Json(Table("T", dice, E(1, 1, "x"))).GetProperty("formula").GetString());
    }

    [Fact]
    public void The_d66_convention_is_tens_plus_ones()
    {
        Assert.Equal("1d6 * 10 + 1d6", FoundryTableExporter.Formula(DiceExpression.D66));
        Assert.Equal("1d6 * 10 + 1d6", Json(Table("T", "d66", E(11, 66, "x"))).GetProperty("formula").GetString());
    }

    [Fact]
    public void A_genuine_66_sided_die_stays_1d66_and_is_not_the_d66_convention()
    {
        var real = DiceExpression.Parse("1d66");
        Assert.Equal((1, 66, false), (real.Count, real.Sides, real.IsD66));                       // the regression guard: same count and sides as
        Assert.Equal("1d66", FoundryTableExporter.Formula(real));                                 // d66's display, but a different roll entirely
        Assert.Equal("1d66", FoundryTableExporter.Formula(new DiceExpression(1, 66)));
        Assert.Equal("1d6 * 10 + 1d6", FoundryTableExporter.Formula(new DiceExpression(2, 6, 0, RollConvention.D66)));
    }

    [Fact]
    public void The_d66_formula_gives_exactly_the_legal_d66_results_each_once()
    {
        // 1d6 * 10 + 1d6 over every pair of faces is the same as TableForge's tens-and-ones reading: 36 outcomes, 11-66, none like 17 or 20.
        var outcomes = (from tens in Enumerable.Range(1, 6) from ones in Enumerable.Range(1, 6) select tens * 10 + ones).ToList();
        var legal = Enumerable.Range(0, 100).Where(DiceExpression.D66.IsLegal).ToList();
        Assert.Equal(legal, outcomes.Order());
        Assert.Equal(36, outcomes.Distinct().Count());
        Assert.All(outcomes, v => Assert.Equal(v, DiceExpression.D66.ResultFromFaces([v / 10, v % 10])));
    }

    // ---- Ranges -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Ranges_are_the_numbers_TableForge_resolves_with_never_the_written_notation()
    {
        var table = Table("OMENS", "d100", E(1, 5, "Cold", "01–05"), E(8, 8, "Odd", "08"), E(9, 95, "Mild"), E(96, 100, "Doom", "96–00"));
        Assert.Equal([(1, 5, "Cold"), (8, 8, "Odd"), (9, 95, "Mild"), (96, 100, "Doom")], Results(Json(table)));
        Assert.DoesNotContain("\"08\"", Export(table).Json);
        Assert.DoesNotContain("–", Export(table).Json);
    }

    [Fact]
    public void Exact_values_and_spans()
    {
        Assert.Equal([(4, 4, "One"), (4, 8, "Span")], Results(Json(Table("T", "d10", E(4, 4, "One"), E(4, 8, "Span")))));
    }

    [Fact]
    public void Gaps_and_overlaps_are_exported_as_they_are_in_their_order()
    {
        var table = Table("T", "d10", E(1, 4, "A"), E(7, 10, "B"), E(3, 5, "C"));
        Assert.Equal([(1, 4, "A"), (7, 10, "B"), (3, 5, "C")], Results(Json(table)));
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
    [InlineData("1x Torch (UD6)")]
    [InlineData("Ünïcödé 竜 🐉")]
    [InlineData("<b>not HTML to TableForge</b>")]
    public void Result_text_round_trips_exactly(string text)
    {
        Assert.Equal(text, Results(Json(Table("T", "d6", E(1, 6, text))))[0].Text);
    }

    [Fact]
    public void Multiline_text_keeps_its_line_breaks_and_is_only_trimmed()
    {
        Assert.Equal("Line one\nLine two", Results(Json(Table("T", "d6", E(1, 6, "  Line one\nLine two \n"))))[0].Text);
        Assert.Equal("Line one\r\nLine two", Results(Json(Table("T", "d6", E(1, 6, "Line one\r\nLine two"))))[0].Text);
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
    public void Only_name_formula_and_results_with_range_and_text_are_exported()
    {
        var table = Fixtures.Scavenging(3, itemsTableId: 5).Also(t => { t.Id = 42; t.ClampResultsToRange = true; });
        foreach (var (entry, i) in table.ResultSets[0].Entries.Select((e, i) => (e, i))) entry.Id = 100 + i;
        var json = Json(table);

        Assert.Equal(["name", "formula", "results"], json.EnumerateObject().Select(p => p.Name));
        Assert.All(json.GetProperty("results").EnumerateArray(), r => Assert.Equal(["range", "text"], r.EnumerateObject().Select(p => p.Name)));
        Assert.Equal(("Scavenging", "2d6"), (json.GetProperty("name").GetString(), json.GetProperty("formula").GetString()));
        Assert.Equal([(2, 5, "Nothing useful"), (6, 8, "1x Scavenged Item"), (9, 10, "2x Scavenged Items"), (11, 12, "Valuable find")], Results(json));
    }

    [Fact]
    public void A_link_exports_only_the_authored_text_never_its_destination()
    {
        var table = Fixtures.Scavenging(3, itemsTableId: null, unresolvedName: "Loot Pile");
        var json = Export(table).Json;
        Assert.DoesNotContain("Loot Pile", json);
        Assert.Equal("1x Scavenged Item", Results(JsonDocument.Parse(json).RootElement)[1].Text);
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
        Assert.Equal("1d100", json.GetProperty("formula").GetString());
        Assert.Equal([(1, 25, "Silence"), (26, 50, "Distant scratching"), (51, 66, "Dripping water"), (67, 71, "Hissing"), (72, 100, "Low chanting")],
            Results(json));
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
    }

    // ---- Refused ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_result_set_from_another_table_is_refused()
    {
        var table = Table("T", "d6", E(1, 6, "x"));
        Assert.False(FoundryTableExporter.TryExport(table, new ResultSet { Entries = [E(1, 6, "x")] }, out var export, out var error));
        Assert.Null(export);
        Assert.Equal("Choose a result set to export.", error);
    }

    [Fact]
    public void Empty_backwards_and_nameless_tables_are_refused()
    {
        var empty = Table("T", "d6");
        Assert.False(FoundryTableExporter.TryExport(empty, empty.ResultSets[0], out _, out var error));
        Assert.Equal("This result set has no rows.", error);

        var backwards = Table("T", "d6", E(1, 2, "ok"), E(5, 3, "bad", "5–3"));
        Assert.False(FoundryTableExporter.TryExport(backwards, backwards.ResultSets[0], out _, out error));
        Assert.Equal("The row \"5–3\" has a range that runs backwards.", error);

        var nameless = Table("  ", "d6", E(1, 6, "x"));
        Assert.False(FoundryTableExporter.TryExport(nameless, nameless.ResultSets[0], out _, out error));
        Assert.Equal("The table needs a name.", error);
    }

    // ---- Clamp warning ----------------------------------------------------------------------------------------------

    [Fact]
    public void Clamp_that_covers_every_roll_is_not_mentioned()
    {
        Assert.Empty(Export(Table("T", "d20", E(1, 20, "x")).Also(t => t.ClampResultsToRange = true)).Warnings);
    }

    [Fact]
    public void Clamp_with_rolls_beyond_the_rows_warns_but_still_exports()
    {
        var export = Export(Table("T", "d20+2", E(1, 20, "x")).Also(t => t.ClampResultsToRange = true));
        Assert.Equal([FoundryTableExporter.ClampWarning], export.Warnings);
        Assert.Equal("1d20+2", JsonDocument.Parse(export.Json).RootElement.GetProperty("formula").GetString());

        var below = Export(Table("T", "d20-2", E(1, 20, "x")).Also(t => t.ClampResultsToRange = true));
        Assert.Equal([FoundryTableExporter.ClampWarning], below.Warnings);
    }

    [Fact]
    public void Without_Clamp_or_when_Clamp_cannot_apply_there_is_no_clamp_warning()
    {
        Assert.Empty(Export(Table("T", "d20+2", E(1, 20, "x"))).Warnings);                         // Clamp off

        var disagreeing = new RollableTable                                                         // Clamp on but unavailable: TableForge doesn't clamp either
        {
            Name = "T", Dice = DiceExpression.Parse("d20+2"), ClampResultsToRange = true,
            ResultSets = [new ResultSet { Entries = [E(1, 20, "a")] }, new ResultSet { Entries = [E(1, 10, "b")] }],
        };
        Assert.Empty(Export(disagreeing).Warnings);
    }

    // ---- File name --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Table: A/B? C*", "Table_ A_B_ C_.json")]
    [InlineData("Treasure: Weapons / Armor?", "Treasure_ Weapons _ Armor_.json")]
    [InlineData("Quote \"this\" <now> | \\ back", "Quote _this_ _now_ _ _ back.json")]
    [InlineData("Room Features — Noise", "Room Features — Noise.json")]
    [InlineData("Ends with dots... ", "Ends with dots.json")]
    [InlineData("CON", "CON_.json")]
    [InlineData("nul", "nul_.json")]
    [InlineData("...", "Foundry table.json")]
    public void The_suggested_file_name_is_safe_for_Windows(string name, string file)
    {
        Assert.Equal(file, FoundryTableExporter.SuggestedFileName(name));
        Assert.True(file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    [Fact]
    public void Only_the_file_name_is_made_safe_never_the_name_inside_the_json()
    {
        var export = Export(Table("Table: A/B? C*", "d6", E(1, 6, "x")));
        Assert.Equal("Table: A/B? C*", JsonDocument.Parse(export.Json).RootElement.GetProperty("name").GetString());
        Assert.Equal("Table_ A_B_ C_.json", FoundryTableExporter.SuggestedFileName(export.Name));
    }
}

/// <summary>Copy Foundry JSON and Save Foundry JSON… on the Roll screen: one exporter, two destinations, nothing else changes.</summary>
public class FoundryExportRollTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("tableforge-foundry-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Copying_puts_the_json_on_the_clipboard_and_leaves_rolls_and_history_alone()
    {
        var copied = new List<string>();
        var history = new List<RollSnapshot>();
        var table = Fixtures.Table(DiceExpression.Parse("d6"), (1, 3, "You gain +1d4 Armor"), (4, 6, "Nothing"));
        var session = new RollViewModel(table, new SequenceDice(2, 5, 3), rolled: history.Add, copyText: copied.Add);
        session.ModifierText = "+1";
        session.RollCommand.Execute(null);
        session.LatestRolls[0].Lines[0].InlineActions[0].RollCommand!.Execute(null);
        var before = (session.RollDisplay, session.ModifierText, history.Count, session.LatestRolls[0].Lines[0].ResolvedText);

        session.CopyFoundryJsonCommand.Execute(null);

        Assert.Equal(FoundryJson(table, 0), Assert.Single(copied));
        Assert.Contains("You gain +1d4 Armor", copied[0]);                                          // source text, not the resolved "+3 Armor"
        Assert.Equal("Foundry JSON copied.", session.CopyMessage);
        Assert.False(session.HasExportWarning);
        Assert.Equal(before, (session.RollDisplay, session.ModifierText, history.Count, session.LatestRolls[0].Lines[0].ResolvedText));
    }

    [Fact]
    public void Saving_writes_exactly_the_copied_json_as_UTF8_where_the_person_chose()
    {
        var copied = new List<string>();
        var suggested = new List<string>();
        var path = Path.Combine(_folder, "chosen.json");
        var session = new RollViewModel(Fixtures.RoomFeatures(), new SequenceDice(1), copyText: copied.Add,
            chooseSaveFile: name => { suggested.Add(name); return path; });
        session.CopyResultSetIndex = 2;

        session.SaveFoundryJsonCommand.Execute(null);
        session.CopyFoundryJsonCommand.Execute(null);

        Assert.Equal(["Room Features — General Feature.json"], suggested);
        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));                   // no BOM
        Assert.Equal(Assert.Single(copied), Encoding.UTF8.GetString(bytes));                        // the same bytes as the clipboard
        Assert.Equal(FoundryJson(Fixtures.RoomFeatures(), 2), copied[0]);
    }

    [Fact]
    public void Save_reports_where_it_saved_and_cancelling_does_nothing()
    {
        var path = Path.Combine(_folder, "Weather.json");
        string? choice = null;
        var session = new RollViewModel(Fixtures.Table(DiceExpression.Parse("d6"), (1, 6, "x")), new SequenceDice(1), chooseSaveFile: _ => choice);

        session.SaveFoundryJsonCommand.Execute(null);
        Assert.Equal("", session.CopyMessage);
        Assert.Empty(Directory.GetFiles(_folder));

        choice = path;
        session.SaveFoundryJsonCommand.Execute(null);
        Assert.Equal("Foundry JSON saved to Weather.json.", session.CopyMessage);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void A_failed_save_or_busy_clipboard_is_reported_not_thrown()
    {
        var missing = Path.Combine(_folder, "no such folder", "x.json");
        var session = new RollViewModel(Fixtures.Table(DiceExpression.Parse("d6"), (1, 6, "x")), new SequenceDice(1),
            copyText: _ => throw new System.Runtime.InteropServices.COMException("OpenClipboard failed"), chooseSaveFile: _ => missing);

        session.CopyFoundryJsonCommand.Execute(null);
        Assert.Equal("The Foundry JSON could not be copied: OpenClipboard failed", session.CopyMessage);

        session.SaveFoundryJsonCommand.Execute(null);
        Assert.StartsWith("The Foundry JSON could not be saved: ", session.CopyMessage);
    }

    [Fact]
    public void A_table_Foundry_cannot_take_says_why_and_nothing_is_copied_or_saved()
    {
        var copied = new List<string>();
        var asked = 0;
        var table = Fixtures.Table(DiceExpression.Parse("d6"), (1, 2, "ok"), (5, 3, "bad"));
        var session = new RollViewModel(table, new SequenceDice(1), copyText: copied.Add, chooseSaveFile: _ => { asked++; return null; });

        session.CopyFoundryJsonCommand.Execute(null);
        Assert.Equal("This table cannot be exported to Foundry: The row \"5–3\" has a range that runs backwards.", session.CopyMessage);
        session.SaveFoundryJsonCommand.Execute(null);
        Assert.Empty(copied);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void The_clamp_warning_shows_after_a_Foundry_export_and_clears_with_the_next_choice()
    {
        var copied = new List<string>();
        var table = Fixtures.Table(DiceExpression.Parse("d20+2"), (1, 20, "x")).Also(t => t.ClampResultsToRange = true);
        var session = new RollViewModel(table, new SequenceDice(1), copyText: copied.Add);
        Assert.False(session.HasExportWarning);                                                     // nothing until an export

        session.CopyFoundryJsonCommand.Execute(null);
        Assert.Equal((FoundryTableExporter.ClampWarning, "Foundry JSON copied."), (session.ExportWarning, session.CopyMessage));
        Assert.True(session.HasExportWarning);

        session.CopyTableTextCommand.Execute(null);                                                 // plain text has no such caveat
        Assert.False(session.HasExportWarning);
    }

    [Fact]
    public void Changing_the_result_set_or_following_a_link_clears_the_export_message_and_warning()
    {
        var rooms = Fixtures.RoomFeatures().Also(t => { t.Dice = DiceExpression.Parse("d100+5"); t.ClampResultsToRange = true; });
        rooms.ResultSets[0].Entries[0].LinkedTableId = 5;
        var linked = Fixtures.Table(DiceExpression.Parse("d4"), (1, 4, "Anything")).Also(t => { t.Id = 5; t.Name = "Cellar"; });
        var session = new RollViewModel(rooms, new SequenceDice(1), _ => linked, copyText: _ => { });

        session.CopyFoundryJsonCommand.Execute(null);
        Assert.True(session.HasExportWarning);
        session.CopyResultSetIndex = 1;
        Assert.Equal(("", ""), (session.CopyMessage, session.ExportWarning));

        session.CopyFoundryJsonCommand.Execute(null);
        session.RollCommand.Execute(null);
        session.Results[0].FollowCommand!.Execute(null);
        Assert.Equal(("", "", 0), (session.CopyMessage, session.ExportWarning, session.CopyResultSetIndex));
    }

    private static string FoundryJson(RollableTable table, int set)
    {
        Assert.True(FoundryTableExporter.TryExport(table, table.ResultSets[set], out var export, out _));
        return export!.Json;
    }
}

/// <summary>Export… through the real window.</summary>
[Collection("UI")]
public class FoundryExportViewTests
{
    [Fact]
    public void Export_offers_text_and_Foundry_json_and_Review_keeps_its_own_Copy_Table_Text()
    {
        Sta.Run(() =>
        {
            var folder = Directory.CreateTempSubdirectory("tableforge-foundry-ui-").FullName;
            try
            {
                using var ui = new UiHarness(3, (db, c) =>
                    db.SaveTable(Fixtures.Table(DiceExpression.Parse("d20+2"), (1, 10, "Sword & Shield"), (11, 20, "Value < 10"))
                        .Also(t => { t.Name = "Loot: Arms?"; t.CollectionId = c.Id; t.ClampResultsToRange = true; })));
                ui.SelectTable("Loot: Arms?");
                ui.Click("Roll");
                Assert.False(ui.HasVisibleButton("Copy Table Text"));
                Assert.Equal(["Copy Table Text", "Copy for Sojour", "Copy Foundry JSON", "Save Foundry JSON…"],
                    ui.One<Button>(b => b.Name == "ExportButton").ContextMenu.Items.OfType<MenuItem>().Select(i => i.Header as string));

                ui.ChooseExport("Copy Foundry JSON");
                var json = JsonDocument.Parse(Assert.Single(ui.Copied)).RootElement;
                Assert.Equal(("Loot: Arms?", "1d20+2"), (json.GetProperty("name").GetString(), json.GetProperty("formula").GetString()));
                Assert.Contains(ui.Texts(), t => t.Name == "CopyMessageText" && t.Text == "Foundry JSON copied.");
                Assert.Contains(ui.Texts(), t => t.Name == "ExportWarningText" && t.Text == FoundryTableExporter.ClampWarning);
                Assert.Single(ui.Db.GetRollHistory());                                              // the roll is still the only one

                ui.SavePath = Path.Combine(folder, "loot.json");
                ui.ChooseExport("Save Foundry JSON…");
                Assert.Equal(["Loot_ Arms_.json"], ui.SuggestedFileNames);
                Assert.Equal(ui.Copied[0], File.ReadAllText(ui.SavePath));
                Assert.Contains(ui.Texts(), t => t.Name == "CopyMessageText" && t.Text == "Foundry JSON saved to loot.json.");

                ui.ChooseExport("Copy Table Text");
                Assert.StartsWith("Loot: Arms?\r\nd20+2\r\n", ui.Copied[^1]);
                Assert.DoesNotContain(ui.Texts(), t => t.Name == "ExportWarningText");

                ui.Click("Edit table");                                                             // Review: unchanged, and no Foundry export
                Assert.True(ui.HasVisibleButton("Copy Table Text"));
                Assert.False(ui.HasVisibleButton("Export…"));
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        });
    }
}
