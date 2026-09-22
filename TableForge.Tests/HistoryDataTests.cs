using Microsoft.Data.Sqlite;
using TableForge.Data;
using TableForge.Domain;

namespace TableForge.Tests;

/// <summary>Schema v2: Recent Tables (LastUsedUtc) and Recent Roll History, on real temporary SQLite files.</summary>
public class HistoryDataTests
{
    private static RollSnapshot Snap(long? tableId, string name, int roll, string result = "Result", string dice = "d20") =>
        new(tableId, name, dice, roll, result);

    // ---- migration from the Milestone 1-3 schema ----------------------------------------------

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void A_version_1_database_migrates_to_the_current_version_and_keeps_all_existing_data()
    {
        using var temp = new TempDatabase();
        using (var raw = new SqliteConnection($"Data Source={temp.Path};Pooling=False;Foreign Keys=True"))
        {
            raw.Open();
            DatabaseMigrations.Apply(raw, upToVersion: 1);
            Assert.Equal(1, DatabaseMigrations.GetVersion(raw));

            // Data exactly as Milestone 1-3 builds wrote it: two collections, linked tables, two result sets, a d100 display range.
            Exec(raw, """
                INSERT INTO Collections (Id, Name, CreatedUtc) VALUES (1, 'Dungeon', '2026-01-01T00:00:00.0000000Z'), (2, 'Wilds', '2026-01-02T00:00:00.0000000Z');
                INSERT INTO Tables (Id, CollectionId, Name, DiceCount, DiceSides, CreatedUtc, UpdatedUtc) VALUES
                    (10, 1, 'Scavenged Items', 1, 20, '2026-01-03T00:00:00.0000000Z', '2026-01-03T00:00:00.0000000Z'),
                    (11, 1, 'Scavenging', 2, 6, '2026-01-04T00:00:00.0000000Z', '2026-01-05T00:00:00.0000000Z'),
                    (12, 2, 'Room Features', 1, 100, '2026-01-06T00:00:00.0000000Z', '2026-01-06T00:00:00.0000000Z');
                INSERT INTO ResultSets (Id, TableId, Name, SortOrder) VALUES (100, 10, '', 0), (101, 11, '', 0), (102, 12, 'Ambient', 0), (103, 12, 'Noise', 1);
                INSERT INTO Entries (Id, ResultSetId, MinValue, MaxValue, DisplayText, DisplayRange, LinkedTableId, UnresolvedLinkName, SortOrder) VALUES
                    (1000, 100, 1, 20, 'Rusty nails', NULL, NULL, NULL, 0),
                    (1001, 101, 2, 5, 'Nothing useful', NULL, NULL, NULL, 0),
                    (1002, 101, 6, 8, '1x Scavenged Item', NULL, 10, NULL, 1),
                    (1003, 101, 9, 12, 'Ask around', NULL, NULL, 'Rumours', 2),
                    (1004, 102, 1, 100, 'Heavy incense', '01–00', NULL, NULL, 0),
                    (1005, 103, 1, 100, 'Silence', NULL, NULL, NULL, 0);
                """);
        }

        using var db = temp.Open(); // migrates on open

        using (var raw = new SqliteConnection($"Data Source={temp.Path};Pooling=False"))
        {
            raw.Open();
            Assert.Equal(4, DatabaseMigrations.GetVersion(raw));
            Assert.Equal(DatabaseMigrations.CurrentVersion, DatabaseMigrations.GetVersion(raw));
        }

        Assert.Equal(["Dungeon", "Wilds"], db.GetCollections().Select(c => c.Name).ToArray());
        Assert.Equal(["Scavenged Items", "Scavenging"], db.GetTableSummaries(1).Select(t => t.Name).ToArray());

        var scavenging = db.LoadTable(11)!;
        Assert.Equal((new DiceExpression(2, 6), 1L), (scavenging.Dice, scavenging.CollectionId));
        Assert.Equal(DateTime.Parse("2026-01-05T00:00:00Z").ToUniversalTime(), scavenging.UpdatedUtc);
        var entries = scavenging.ResultSets[0].Entries;
        Assert.Equal([(2, 5, "Nothing useful", null, null, null), (6, 8, "1x Scavenged Item", null, 10L, null), (9, 12, "Ask around", null, null, "Rumours")],
            entries.Select(e => (e.Min, e.Max, e.Text, e.DisplayRange, e.LinkedTableId, e.UnresolvedLinkName)).ToArray());

        var rooms = db.LoadTable(12)!;
        Assert.Equal(["Ambient", "Noise"], rooms.ResultSets.Select(s => s.Name).ToArray());
        Assert.Equal((1, 100, "01–00"), (rooms.ResultSets[0].Entries[0].Min, rooms.ResultSets[0].Entries[0].Max, rooms.ResultSets[0].Entries[0].DisplayRange));

        // The new features start empty: nothing is "recent" yet, and history exists but has no rows.
        Assert.Empty(db.GetRecentTables(1));
        Assert.Empty(db.GetRecentTables(2));
        Assert.Empty(db.GetRollHistory());

        // Existing links still behave: deleting the linked destination relinks as before.
        Assert.True(db.DeleteTable(10));
        Assert.Equal((null, "Scavenged Items"), (db.LoadTable(11)!.ResultSets[0].Entries[1].LinkedTableId, db.LoadTable(11)!.ResultSets[0].Entries[1].UnresolvedLinkName));
    }

    [Fact]
    public void Opening_a_migrated_database_again_does_not_migrate_twice_or_lose_history()
    {
        using var temp = new TempDatabase();
        using (var db = temp.Open())
        {
            var t = db.SaveTable(Fixtures.ScavengedItems(db.CreateCollection("C").Id));
            db.AddRollHistory(Snap(t.Id, "Scavenged Items", 12, "D4 rations"));
            db.MarkTableUsed(t.Id);
        }

        using var again = temp.Open();
        Assert.Single(again.GetRollHistory());
        Assert.Single(again.GetRecentTables(1));
    }

    // ---- roll history -------------------------------------------------------------------------

    [Fact]
    public void A_stored_roll_reads_back_as_a_snapshot()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var table = db.SaveTable(Fixtures.RoomFeatures(db.CreateCollection("C").Id));

        var stored = db.AddRollHistory(new RollSnapshot(table.Id, "Room Features", "d100", 68,
            "Ambient: Smell of burning flesh\nNoise: Hissing\nGeneral Feature: Grated floors reveal dozens of people below"));

        var item = Assert.Single(db.GetRollHistory());
        Assert.Equal(stored, item);
        Assert.Equal((table.Id, "Room Features", "d100", 68), (item.TableId, item.TableName, item.DiceText, item.RollValue));
        Assert.Equal(DateTimeKind.Utc, item.RolledUtc.Kind);
        Assert.InRange(item.RolledUtc, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal("Room Features\nd100 → 68\n\nAmbient: Smell of burning flesh\nNoise: Hissing\nGeneral Feature: Grated floors reveal dozens of people below", item.FullText);
    }

    [Fact]
    public void A_d100_roll_of_100_displays_as_00_but_is_stored_numerically()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        db.AddRollHistory(Snap(null, "Treasure", 100, "Gold", "d100"));

        var item = Assert.Single(db.GetRollHistory());
        Assert.Equal((100, "00"), (item.RollValue, item.RollDisplay));
    }

    [Fact]
    public void History_lists_newest_first_and_keeps_only_the_latest_10()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var table = db.SaveTable(Fixtures.ScavengedItems(db.CreateCollection("C").Id));

        for (var i = 1; i <= 25; i++)
        {
            db.AddRollHistory(Snap(table.Id, "Scavenged Items", i));
            Assert.True(db.GetRollHistory(100).Count <= AppDatabase.RollHistoryLimit); // never above the limit, at any point
        }

        var history = db.GetRollHistory(100);
        Assert.Equal(10, AppDatabase.RollHistoryLimit);
        Assert.Equal(10, history.Count);
        Assert.Equal(Enumerable.Range(16, 10).Reverse(), history.Select(h => h.RollValue));
    }

    [Fact]
    public void Deleting_a_table_keeps_its_history_readable_and_detaches_it()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var (items, scavenging) = Fixtures.SeedScavenging(db, db.CreateCollection("C").Id);
        db.AddRollHistory(Snap(items.Id, "Scavenged Items", 12, "D4 rations"));
        db.AddRollHistory(Snap(scavenging.Id, "Scavenging", 6, "1x Scavenged Item", "2d6"));

        db.DeleteTable(items.Id);

        var history = db.GetRollHistory();
        Assert.Equal(2, history.Count);
        var gone = history.Single(h => h.TableName == "Scavenged Items");
        Assert.Null(gone.TableId);
        Assert.Equal((12, "D4 rations"), (gone.RollValue, gone.ResultText));
        Assert.Equal("Scavenged Items\nd20 → 12\n\nD4 rations", gone.FullText);
        Assert.Equal(scavenging.Id, history.Single(h => h.TableName == "Scavenging").TableId); // others stay attached
    }

    [Fact]
    public void Existing_link_relinking_on_delete_still_works_with_history_present()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var (items, scavenging) = Fixtures.SeedScavenging(db, db.CreateCollection("C").Id);
        db.AddRollHistory(Snap(items.Id, "Scavenged Items", 3));

        db.DeleteTable(items.Id);

        Assert.Equal((null, "Scavenged Items"),
            (db.LoadTable(scavenging.Id)!.ResultSets[0].Entries[1].LinkedTableId, db.LoadTable(scavenging.Id)!.ResultSets[0].Entries[1].UnresolvedLinkName));
    }

    [Fact]
    public void Renaming_or_editing_a_table_never_changes_stored_history()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var table = db.SaveTable(Fixtures.ScavengedItems(db.CreateCollection("C").Id));
        db.AddRollHistory(Snap(table.Id, "Scavenged Items", 12, "D4 rations"));

        table.Name = "Salvage";
        table.ResultSets[0].Entries[2].Text = "Three days of food";
        db.SaveTable(table);

        var item = Assert.Single(db.GetRollHistory());
        Assert.Equal(("Scavenged Items", "D4 rations", table.Id), (item.TableName, item.ResultText, item.TableId));
    }

    [Fact]
    public void A_roll_for_a_table_that_no_longer_exists_is_still_recorded_without_a_reference()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();

        db.AddRollHistory(Snap(9999, "Ghost Table", 4, "Boo"));

        var item = Assert.Single(db.GetRollHistory());
        Assert.Equal((null, "Ghost Table", "Boo"), (item.TableId, item.TableName, item.ResultText));
    }

    // ---- recent tables ------------------------------------------------------------------------

    private static (AppDatabase Db, long Collection, long[] Tables) Tables(TempDatabase temp, int count)
    {
        var db = temp.Open();
        var c = db.CreateCollection("C");
        var ids = Enumerable.Range(1, count)
            .Select(i => db.SaveTable(Fixtures.ScavengedItems(c.Id).Also(t => t.Name = $"Table {i}")).Id).ToArray();
        return (db, c.Id, ids);
    }

    [Fact]
    public void Tables_only_become_recent_when_marked_used_and_appear_newest_first_without_duplicates()
    {
        using var temp = new TempDatabase();
        var (db, c, t) = Tables(temp, 3);

        Assert.Empty(db.GetRecentTables(c));                    // existing in the collection is not enough

        db.MarkTableUsed(t[0]);
        db.MarkTableUsed(t[1]);
        db.MarkTableUsed(t[0]);                                 // used again: moves up, not duplicated

        Assert.Equal(["Table 1", "Table 2"], db.GetRecentTables(c).Select(x => x.Name).ToArray());
    }

    [Fact]
    public void Rapid_marks_keep_a_strict_order_even_within_one_clock_tick()
    {
        using var temp = new TempDatabase();
        var (db, c, t) = Tables(temp, 3);

        for (var round = 0; round < 20; round++)
            foreach (var id in t) db.MarkTableUsed(id);

        Assert.Equal(["Table 3", "Table 2", "Table 1"], db.GetRecentTables(c).Select(x => x.Name).ToArray());
    }

    [Fact]
    public void Recent_tables_are_limited_to_five()
    {
        using var temp = new TempDatabase();
        var (db, c, t) = Tables(temp, 7);
        foreach (var id in t) db.MarkTableUsed(id);

        var recent = db.GetRecentTables(c);

        Assert.Equal(5, AppDatabase.RecentTablesLimit);
        Assert.Equal(["Table 7", "Table 6", "Table 5", "Table 4", "Table 3"], recent.Select(x => x.Name).ToArray());
    }

    [Fact]
    public void Recent_tables_are_scoped_to_their_collection()
    {
        using var temp = new TempDatabase();
        var (db, a, t) = Tables(temp, 2);
        var b = db.CreateCollection("B");
        var other = db.SaveTable(Fixtures.ScavengedItems(b.Id).Also(x => x.Name = "In B"));
        db.MarkTableUsed(t[0]);
        db.MarkTableUsed(other.Id);

        Assert.Equal(["Table 1"], db.GetRecentTables(a).Select(x => x.Name).ToArray());
        Assert.Equal(["In B"], db.GetRecentTables(b.Id).Select(x => x.Name).ToArray());
    }

    [Fact]
    public void Recent_tables_show_the_current_name_and_saving_does_not_change_their_order()
    {
        using var temp = new TempDatabase();
        var (db, c, t) = Tables(temp, 2);
        db.MarkTableUsed(t[0]);
        db.MarkTableUsed(t[1]);

        var first = db.LoadTable(t[0])!;
        first.Name = "Renamed";
        db.SaveTable(first);                                    // edits are not "use"

        Assert.Equal(["Table 2", "Renamed"], db.GetRecentTables(c).Select(x => x.Name).ToArray());
    }

    [Fact]
    public void Deleting_a_table_removes_it_from_recent_tables()
    {
        using var temp = new TempDatabase();
        var (db, c, t) = Tables(temp, 2);
        db.MarkTableUsed(t[0]);
        db.MarkTableUsed(t[1]);

        db.DeleteTable(t[1]);

        Assert.Equal(["Table 1"], db.GetRecentTables(c).Select(x => x.Name).ToArray());
    }
}
