using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using TableForge.Data;
using TableForge.Domain;
using TableForge.Import;
using TableForge.Portable;
using TableForge.ViewModels;
using static TableForge.Tests.PortableFixtures;

namespace TableForge.Tests;

/// <summary>
/// The Manual-selection table property: schema 11 (Tables.ManualSelection), editing, and an optional "manualSelection" in
/// .tfcollection files that needs no new format version. Existing tables and files are unchanged by it.
/// </summary>
public class ManualSelectionTableTests
{
    private static RollableTable Load(AppDatabase db, Collection c, string name) =>
        db.LoadTable(db.GetTableSummaries(c.Id).First(s => s.Name == name).Id)!;

    private static void SetManual(AppDatabase db, Collection c, string name, bool on)
    {
        var table = Load(db, c, name);
        table.IsManualSelection = on;
        db.SaveTable(table);
    }

    // ---- schema 11 -------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_schema_is_version_11_with_a_not_null_flag_defaulting_to_off()
    {
        Assert.Equal(12, DatabaseMigrations.CurrentVersion); // pinned: bump deliberately with each migration

        using var temp = new TempDatabase();
        using (temp.Open()) { }
        using var raw = Raw(temp.Path);
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT type, \"notnull\", dflt_value FROM pragma_table_info('Tables') WHERE name = 'ManualSelection'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(("INTEGER", 1L, "0"), (reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
    }

    [Fact]
    public void A_version_10_database_upgrades_with_a_backup_every_table_off_and_everything_else_unchanged()
    {
        using var temp = new TempDatabase();
        long collectionId;
        string before;
        List<RollHistoryItem> history;
        using (var db = temp.Open())
        {
            var c = SeedMythic(db);
            collectionId = c.Id;
            var weather = Load(db, c, "Weather");
            weather.Description = "Roll at dawn.";
            db.SaveTable(weather);
            db.AddRollHistory(new RollSnapshot(null, "Gone", "d20", 14, "High", 3, null));
            before = Describe(db, collectionId);
            history = db.GetRollHistory().ToList();
        }
        // Make it a real Gidubba 1.2.0 file: version 10, with no Tables.ManualSelection column.
        Execute(temp.Path, "ALTER TABLE Entries DROP COLUMN RowKey; ALTER TABLE Entries DROP COLUMN IsUnique; DROP TABLE UsedResults; ALTER TABLE RollHistory DROP COLUMN RerolledCount; ALTER TABLE Tables DROP COLUMN ManualSelection; PRAGMA user_version = 10");

        using (var upgraded = temp.Open())
        {
            Assert.Equal(before, Describe(upgraded, collectionId));                    // every table, description, row, link and style
            Assert.All(upgraded.GetTableSummaries(collectionId), t => Assert.False(upgraded.LoadTable(t.Id)!.IsManualSelection));
            Assert.Equal(history, upgraded.GetRollHistory());
        }

        Assert.Contains(DatabaseBackup.Existing(temp.Path), p => p.EndsWith(".pre-v12-from-v10.backup.db", StringComparison.Ordinal));
        using var check = Raw(temp.Path);
        Assert.Equal(12, DatabaseMigrations.GetVersion(check));
    }

    [Fact]
    public void The_flag_is_saved_loaded_and_changed_like_any_table_property()
    {
        using var temp = new TempDatabase();
        Collection c;
        using (var db = temp.Open())
        {
            c = SeedMythic(db);
            Assert.All(db.GetTableSummaries(c.Id), t => Assert.False(db.LoadTable(t.Id)!.IsManualSelection)); // new tables start off
            SetManual(db, c, "Weather", true);
        }
        using (var db = temp.Open())
        {
            Assert.True(Load(db, c, "Weather").IsManualSelection);
            Assert.False(Load(db, c, "Encounter").IsManualSelection);
            SetManual(db, c, "Weather", false);
            Assert.False(Load(db, c, "Weather").IsManualSelection);
        }
    }

    [Fact]
    public void Editing_a_table_keeps_the_flag_unless_the_person_changes_it()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = SeedMythic(db);
        SetManual(db, c, "Weather", true);

        var draft = TableImportDraft.FromTable(Load(db, c, "Weather"));
        Assert.True(draft.IsManualSelection);
        draft.TableName = "Weather (edited)";
        Assert.True(draft.TryBuildTable(c.Id, out var built, out _));
        Assert.True(built!.IsManualSelection);

        draft.IsManualSelection = false;
        Assert.True(draft.TryBuildTable(c.Id, out built, out _));
        Assert.False(built!.IsManualSelection);

        Assert.False(new TableImportDraft().IsManualSelection);                         // a pasted table starts off
    }

    // ---- .tfcollection ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_collection_without_the_flag_is_written_exactly_as_before()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var bytes = ExportBytes(db, SeedMythic(db));

        Assert.DoesNotContain("manualSelection", Text(bytes));
        Assert.Equal(1, (int?)JsonNode.Parse(bytes)!["formatVersion"]);
    }

    [Fact]
    public void The_flag_is_written_only_on_its_tables_without_a_newer_format_version_and_round_trips()
    {
        using var tempA = new TempDatabase();
        using var tempB = new TempDatabase();
        using var a = tempA.Open();
        var source = SeedMythic(a);
        SetManual(a, source, "Weather", true);

        var bytes = ExportBytes(a, source);
        var file = JsonNode.Parse(bytes)!;
        Assert.Equal(1, (int?)file["formatVersion"]);                                    // no description: still version 1
        var flagged = file["tables"]!.AsArray().Where(t => t!["manualSelection"] is not null).ToList();
        Assert.Equal("Weather", (string?)Assert.Single(flagged)!["name"]);
        Assert.True((bool)flagged[0]!["manualSelection"]!);

        using var b = tempB.Open();
        var imported = b.ImportCollection(Read(bytes));
        Assert.Equal(Describe(a, source.Id, includeName: false), Describe(b, imported.Id, includeName: false));
        Assert.True(Load(b, imported, "Weather").IsManualSelection);

        // And back out again, unchanged.
        Assert.Equal(Text(bytes).Split('\n').Where(l => !l.Contains("\"generator\"")),
            Text(ExportBytes(b, imported)).Split('\n').Where(l => !l.Contains("\"generator\"")));
    }

    [Fact]
    public void A_file_says_true_false_or_nothing_and_anything_else_is_refused()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var doc = JsonNode.Parse(ExportBytes(db, SeedMythic(db)))!;
        var table = doc["tables"]![0]!;

        table["manualSelection"] = true;
        Assert.True(Read(Encoding.UTF8.GetBytes(doc.ToJsonString())).Tables[0].Table.IsManualSelection);
        table["manualSelection"] = false;
        Assert.False(Read(Encoding.UTF8.GetBytes(doc.ToJsonString())).Tables[0].Table.IsManualSelection);
        table["manualSelection"] = null;
        Assert.False(Read(Encoding.UTF8.GetBytes(doc.ToJsonString())).Tables[0].Table.IsManualSelection);

        table["manualSelection"] = "yes";
        var read = PortableCollectionReader.Read(Encoding.UTF8.GetBytes(doc.ToJsonString()));
        Assert.False(read.Succeeded);
        Assert.Contains(read.Problems, p => p.Contains("manualSelection must be true or false", StringComparison.Ordinal));
    }

    [Fact]
    public void A_reader_that_does_not_know_the_property_still_reads_the_file_as_before()
    {
        // Gidubba 1.2.0 ignores properties it does not know. The same rule, seen from this build with a property from the future:
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = SeedMythic(db);
        SetManual(db, c, "Weather", true);
        var doc = JsonNode.Parse(ExportBytes(db, c))!;
        doc["tables"]![0]!["someFutureSetting"] = 42;

        var read = PortableCollectionReader.Read(Encoding.UTF8.GetBytes(doc.ToJsonString()));
        Assert.True(read.Succeeded);
    }
}

/// <summary>The Manual-selection property and Auto-follow on screen: the editor's checkbox, the Roll screen's toggle, hint and Stop.</summary>
[Collection("UI")]
public class AutoFollowViewTests
{
    /// <summary>"Area Type" 5 → "Depth Selector" (d4) → 2 is "Bogtown Encounters".</summary>
    private static UiHarness Open(int roll, bool selectorIsManual) => new(roll, (db, c) =>
    {
        var bogtown = db.SaveTable(Fixtures.Table(Domain.DiceExpression.Parse("d6"), (1, 6, "Bog lurker")).Also(t => { t.Name = "Bogtown Encounters"; t.CollectionId = c.Id; }));
        var selector = Fixtures.Table(Domain.DiceExpression.Parse("d4"), (1, 1, "Gutters"), (2, 2, "Bogtown"), (3, 4, "Deeper")).Also(t =>
        {
            t.Name = "Depth Selector";
            t.CollectionId = c.Id;
            t.IsManualSelection = selectorIsManual;
            t.ResultSets[0].Entries[1].LinkedTableId = bogtown.Id;
        });
        selector = db.SaveTable(selector);
        db.SaveTable(Fixtures.Table(Domain.DiceExpression.Parse("d6"), (1, 4, "Corridor"), (5, 6, "Encounter")).Also(t =>
        {
            t.Name = "Area Type";
            t.CollectionId = c.Id;
            t.ResultSets[0].Entries[1].LinkedTableId = selector.Id;
        }));
    });

    private static CheckBox Box(UiHarness ui, string name) => ui.One<CheckBox>(c => c.Name == name);

    [Fact]
    public void The_editor_offers_Manual_selection_table_and_saves_it()
    {
        Sta.Run(() =>
        {
            using var ui = Open(2, selectorIsManual: false);
            ui.SelectTable("Depth Selector");
            ui.Click("Edit table");
            var box = Box(ui, "ManualSelectionBox");
            Assert.True(box.IsVisible && box.IsEnabled);
            Assert.Equal("Manual-selection table", box.Content);
            Assert.False(box.IsChecked);
            box.IsChecked = true;
            ui.Layout();
            ui.Click("Save Table");

            ui.SelectTable("Depth Selector");
            ui.Click("Edit table");
            Assert.True(Box(ui, "ManualSelectionBox").IsChecked);                     // saved and reopened
            ui.Click("Cancel");

            ui.SelectTable("Depth Selector");
            Assert.Contains(ui.Texts(), t => t.Name == "ManualSelectionHint");
        });
    }

    [Fact]
    public void The_Roll_screen_shows_the_toggle_off_and_no_hint_or_Stop_for_an_ordinary_table()
    {
        Sta.Run(() =>
        {
            using var ui = Open(5, selectorIsManual: true);
            ui.SelectTable("Area Type");

            var toggle = Box(ui, "AutoFollowBox");
            Assert.True(toggle.IsVisible);
            Assert.Equal("Auto-follow linked tables", toggle.Content);
            Assert.False(toggle.IsChecked);
            Assert.DoesNotContain(ui.Texts(), t => t.Name == "ManualSelectionHint");
            Assert.False(ui.HasVisibleButton("Stop"));

            ui.Click("Roll");                                                         // off: exactly as before
            Assert.True(ui.HasVisibleButton("Open Depth Selector"));
            Assert.Equal(1, ui.Dice.Calls);
        });
    }

    [Fact]
    public void With_the_toggle_on_a_roll_opens_the_Manual_selection_table_unrolled_and_a_chosen_result_continues()
    {
        Sta.Run(() =>
        {
            using var ui = Open(5, selectorIsManual: true);
            ui.SelectTable("Area Type");
            Box(ui, "AutoFollowBox").IsChecked = true;
            ui.Layout();
            Assert.True(ui.Main.AutoFollow.IsOn);

            ui.Click("Roll");

            Assert.Contains(ui.Texts(), t => t.Text == "Depth Selector" && t.FontSize == 24);
            Assert.Contains(ui.Texts(), t => t.Name == "AutoFollowedFromText" && t.Text == "Auto-followed from Area Type (5–6)");
            Assert.Contains(ui.Texts(), t => t.Name == "ManualSelectionHint");
            Assert.Equal(1, ui.Dice.Calls);                                           // the selector was not rolled

            var manual = ui.One<TextBox>(t => t.Name == "ManualBox");
            manual.Text = "2";
            ui.Click("Resolve");

            Assert.Contains(ui.Texts(), t => t.Text == "Bogtown Encounters" && t.FontSize == 24);
            Assert.Contains(ui.Texts(), t => t.Text == "Bog lurker" && t.FontSize == 26);
            Assert.Equal(2, ui.Dice.Calls);
            Assert.False(ui.HasVisibleButton("Stop"));

            ui.SelectTable("Area Type");                                              // the setting is shared by every Roll screen
            Assert.True(Box(ui, "AutoFollowBox").IsChecked);
        });
    }
}
