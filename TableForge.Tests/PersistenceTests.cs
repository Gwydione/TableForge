using System.IO;
using Microsoft.Data.Sqlite;
using TableForge.Data;
using TableForge.Domain;

namespace TableForge.Tests;

/// <summary>A real SQLite file in the temp directory, removed afterwards.</summary>
internal sealed class TempDatabase : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tableforge-test-{Guid.NewGuid():N}.db");

    private readonly List<AppDatabase> _opened = [];

    public AppDatabase Open()
    {
        var db = new AppDatabase(Path);
        _opened.Add(db);
        return db;
    }

    public void Dispose()
    {
        foreach (var db in _opened) db.Dispose(); // safe to dispose twice; releases the file for deletion
        if (File.Exists(Path)) File.Delete(Path);
    }
}

public class PersistenceTests
{
    [Fact]
    public void New_database_is_migrated_to_the_current_version()
    {
        using var temp = new TempDatabase();
        using (temp.Open()) { }

        using var raw = new SqliteConnection($"Data Source={temp.Path};Pooling=False");
        raw.Open();
        Assert.Equal(DatabaseMigrations.CurrentVersion, DatabaseMigrations.GetVersion(raw));
    }

    [Fact]
    public void Reopening_an_existing_database_does_not_reapply_migrations_or_lose_data()
    {
        using var temp = new TempDatabase();
        using (var db = temp.Open()) db.CreateCollection("Keep me");

        using var reopened = temp.Open();
        Assert.Equal(["Keep me"], reopened.GetCollections().Select(c => c.Name).ToArray());
    }

    [Fact]
    public void Database_from_a_newer_build_is_refused()
    {
        using var temp = new TempDatabase();
        using (temp.Open()) { }
        using (var raw = new SqliteConnection($"Data Source={temp.Path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version = {DatabaseMigrations.CurrentVersion + 1}";
            cmd.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(() => temp.Open());
    }

    [Fact]
    public void Collection_saves_and_reloads()
    {
        using var temp = new TempDatabase();
        long id;
        using (var db = temp.Open())
        {
            var created = db.CreateCollection("  Solo Adventures ");
            id = created.Id;
            Assert.True(id > 0);
            Assert.Equal("Solo Adventures", created.Name);
        }

        using var reopened = temp.Open();
        var loaded = Assert.Single(reopened.GetCollections());
        Assert.Equal(id, loaded.Id);
        Assert.Equal("Solo Adventures", loaded.Name);
        Assert.Equal(DateTimeKind.Utc, loaded.CreatedUtc.Kind);
        Assert.InRange(loaded.CreatedUtc, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public void Blank_collection_name_is_rejected()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Assert.Throws<ArgumentException>(() => db.CreateCollection("   "));
    }

    [Fact]
    public void Table_saves_and_reloads_with_result_sets_and_entries_in_order()
    {
        using var temp = new TempDatabase();
        long tableId;
        using (var db = temp.Open())
        {
            var collection = db.CreateCollection("Gear");
            var table = Fixtures.ParseAndBuild(Fixtures.RandomStartingGear, collection.Id);
            db.SaveTable(table);
            tableId = table.Id;
            Assert.True(tableId > 0);
        }

        using var reopened = temp.Open();
        var loaded = reopened.LoadTable(tableId)!;

        Assert.Equal("Random Starting Gear", loaded.Name);
        Assert.Equal(new DiceExpression(1, 10), loaded.Dice);
        Assert.Equal(DateTimeKind.Utc, loaded.CreatedUtc.Kind);
        var set = Assert.Single(loaded.ResultSets);
        Assert.Equal(
            [(1, 2, "Backpack"), (3, 3, "Knife"), (4, 4, "1x Torch (UD6)"), (5, 5, "Fishing rod"),
             (6, 6, "Rope (15 m)"), (7, 7, "Tinderbox"), (8, 8, "D4 Bandages"), (9, 10, "D20 Construction Supplies")],
            set.Entries.Select(e => (e.Min, e.Max, e.Text)).ToArray());
        Assert.Equal(Enumerable.Range(0, 8), set.Entries.Select(e => e.SortOrder));
    }

    [Fact]
    public void Multiple_result_sets_keep_their_own_entries_and_order()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var table = new RollableTable
        {
            CollectionId = collection.Id,
            Name = "Weather",
            Dice = DiceExpression.Parse("2d6"),
            ResultSets =
            [
                new ResultSet { Name = "Day", Entries = [new() { Min = 2, Max = 7, Text = "Sun" }, new() { Min = 8, Max = 12, Text = "Rain" }] },
                new ResultSet { Name = "Night", Entries = [new() { Min = 2, Max = 12, Text = "Stars" }] },
            ],
        };
        db.SaveTable(table);

        var loaded = db.LoadTable(table.Id)!;

        Assert.Equal(["Day", "Night"], loaded.ResultSets.Select(s => s.Name).ToArray());
        Assert.Equal(["Sun", "Rain"], loaded.ResultSets[0].Entries.Select(e => e.Text).ToArray());
        Assert.Equal(["Stars"], loaded.ResultSets[1].Entries.Select(e => e.Text).ToArray());
        Assert.Equal(new DiceExpression(2, 6), loaded.Dice);
    }

    [Fact]
    public void D100_numeric_range_and_display_range_can_differ()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var table = Fixtures.ParseAndBuild("d100 Treasure\n01-50 Copper\n51-95 Silver\n96-00 Gold\n", collection.Id);
        db.SaveTable(table);

        var entries = db.LoadTable(table.Id)!.ResultSets[0].Entries;

        Assert.Equal((1, 50, "01–50"), (entries[0].Min, entries[0].Max, entries[0].DisplayRange));
        Assert.Equal((51, 95, (string?)null), (entries[1].Min, entries[1].Max, entries[1].DisplayRange));
        Assert.Equal((96, 100, "96–00"), (entries[2].Min, entries[2].Max, entries[2].DisplayRange));
        Assert.Equal("96–00", entries[2].RangeLabel);
        Assert.Equal("51–95", entries[1].RangeLabel);
    }

    [Fact]
    public void Link_fields_round_trip()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var target = db.SaveTable(Fixtures.ParseAndBuild("d6 Target\n1-6 x", collection.Id));

        var source = Fixtures.ParseAndBuild("d6 Source\n1-3 Linked\n4-6 Dangling", collection.Id);
        source.ResultSets[0].Entries[0].LinkedTableId = target.Id;
        source.ResultSets[0].Entries[1].UnresolvedLinkName = "Deleted Table";
        db.SaveTable(source);

        var entries = db.LoadTable(source.Id)!.ResultSets[0].Entries;
        Assert.Equal((target.Id, (string?)null), (entries[0].LinkedTableId, entries[0].UnresolvedLinkName));
        Assert.Equal(((long?)null, "Deleted Table"), (entries[1].LinkedTableId, entries[1].UnresolvedLinkName));
    }

    [Fact]
    public void Saving_an_existing_table_replaces_its_contents_without_duplicating()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var table = Fixtures.ParseAndBuild("d6 Loot\n1-3 Coin\n4-6 Gem", collection.Id);
        db.SaveTable(table);
        var createdUtc = table.CreatedUtc;

        table.Name = "Better Loot";
        table.ResultSets[0].Entries.RemoveAt(1);
        table.ResultSets[0].Entries[0].Max = 6;
        db.SaveTable(table);

        var loaded = db.LoadTable(table.Id)!;
        Assert.Equal("Better Loot", loaded.Name);
        var entry = Assert.Single(Assert.Single(loaded.ResultSets).Entries);
        Assert.Equal((1, 6), (entry.Min, entry.Max));
        Assert.Single(db.GetTableSummaries(collection.Id));
        Assert.Equal(createdUtc, loaded.CreatedUtc);
        Assert.True(loaded.UpdatedUtc >= loaded.CreatedUtc);
    }

    [Fact]
    public void Table_summaries_are_scoped_to_their_collection()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var a = db.CreateCollection("A");
        var b = db.CreateCollection("B");
        db.SaveTable(Fixtures.ParseAndBuild("d6 In A\n1-6 x", a.Id));
        db.SaveTable(Fixtures.ParseAndBuild("2d6 In B\n2-12 x", b.Id));

        var summary = Assert.Single(db.GetTableSummaries(b.Id));
        Assert.Equal("In B", summary.Name);
        Assert.Equal(new DiceExpression(2, 6), summary.Dice);
    }

    [Fact]
    public void Loading_a_missing_table_returns_null()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        Assert.Null(db.LoadTable(999));
    }

    [Fact]
    public void Foreign_keys_are_enforced()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var orphan = Fixtures.ParseAndBuild("d6 Orphan\n1-6 x", collectionId: 12345);

        Assert.Throws<SqliteException>(() => db.SaveTable(orphan));
        Assert.Empty(db.GetTableSummaries(12345)); // and the failed save rolled back
    }

    [Fact]
    public void Failed_save_rolls_back_completely()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var table = Fixtures.ParseAndBuild("d6 Loot\n1-3 Coin\n4-6 Gem", collection.Id);
        db.SaveTable(table);

        table.Name = "Changed";
        table.ResultSets[0].Entries[1].LinkedTableId = 999_999; // violates the foreign key mid-save
        Assert.Throws<SqliteException>(() => db.SaveTable(table));

        var loaded = db.LoadTable(table.Id)!;
        Assert.Equal("Loot", loaded.Name);
        Assert.Equal(["Coin", "Gem"], loaded.ResultSets[0].Entries.Select(e => e.Text).ToArray());
    }

    [Fact]
    public void Text_with_quotes_and_sql_is_stored_verbatim()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("O'Brien's \"stuff\"; DROP TABLE Entries;--");
        var table = Fixtures.ParseAndBuild("d6 Bobby's Tables\n1-6 Robert'); DROP TABLE Tables;--", collection.Id);
        db.SaveTable(table);

        Assert.Equal("O'Brien's \"stuff\"; DROP TABLE Entries;--", Assert.Single(db.GetCollections()).Name);
        Assert.Equal("Robert'); DROP TABLE Tables;--", db.LoadTable(table.Id)!.ResultSets[0].Entries[0].Text);
    }
}
