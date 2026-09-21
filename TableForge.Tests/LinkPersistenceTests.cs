using Microsoft.Data.Sqlite;
using TableForge.Data;
using TableForge.Domain;

namespace TableForge.Tests;

public class LinkPersistenceTests
{
    // ---- multiple result sets ---------------------------------------------------------------

    [Fact]
    public void Multiple_result_sets_save_and_reload_with_names_order_and_their_own_entries()
    {
        using var temp = new TempDatabase();
        long id;
        using (var db = temp.Open())
        {
            var collection = db.CreateCollection("Dungeon");
            id = db.SaveTable(Fixtures.RoomFeatures(collection.Id)).Id;
        }

        using var reopened = temp.Open();
        var loaded = reopened.LoadTable(id)!;

        // Names are deliberately not alphabetical: order must come from position, not name.
        Assert.Equal(["Ambient", "Noise", "General Feature"], loaded.ResultSets.Select(s => s.Name).ToArray());
        Assert.Equal([0, 1, 2], loaded.ResultSets.Select(s => s.SortOrder).ToArray());
        Assert.Equal(["Cold stale air", "Damp stone", "Smell of burning flesh", "Heavy incense"],
            loaded.ResultSets[0].Entries.Select(e => e.Text).ToArray());
        Assert.Equal(["Silence", "Distant scratching", "Dripping water", "Hissing", "Low chanting"],
            loaded.ResultSets[1].Entries.Select(e => e.Text).ToArray());
        Assert.Equal(["Cracked stone walls", "Broken furniture", "Grated floors reveal dozens of people below", "Carved pillars"],
            loaded.ResultSets[2].Entries.Select(e => e.Text).ToArray());

        // Ranges and d100 display forms belong to the set they were saved in.
        Assert.Equal([(1, 25, "01–25"), (26, 50, null), (51, 66, null), (67, 71, null), (72, 100, "72–00")],
            loaded.ResultSets[1].Entries.Select(e => (e.Min, e.Max, e.DisplayRange)).ToArray());
        Assert.Equal((70, 100, "70–00"), (loaded.ResultSets[2].Entries[3].Min, loaded.ResultSets[2].Entries[3].Max, loaded.ResultSets[2].Entries[3].DisplayRange));

        // Independence survives the round trip: rolling 68 still gives the three outputs.
        Assert.Equal(["Smell of burning flesh", "Hissing", "Grated floors reveal dozens of people below"],
            TableResolver.Resolve(loaded, 68).Results.Select(r => r.Entry!.Text).ToArray());
    }

    [Fact]
    public void Resaving_replaces_result_sets_in_the_new_order()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var table = db.SaveTable(Fixtures.RoomFeatures(collection.Id));

        table.ResultSets.Reverse();
        table.ResultSets.RemoveAt(2); // drop Ambient
        db.SaveTable(table);

        var loaded = db.LoadTable(table.Id)!;
        Assert.Equal(["General Feature", "Noise"], loaded.ResultSets.Select(s => s.Name).ToArray());
        Assert.Equal(4, loaded.ResultSets[0].Entries.Count);
        Assert.Equal(5, loaded.ResultSets[1].Entries.Count);
    }

    // ---- link fields ------------------------------------------------------------------------

    [Fact]
    public void Resolved_link_id_and_unresolved_name_round_trip()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var items = db.SaveTable(Fixtures.ScavengedItems(collection.Id));

        var table = Fixtures.Scavenging(collection.Id, items.Id);
        table.ResultSets[0].Entries[3].UnresolvedLinkName = "Rare Finds"; // 11-12: unresolved, no id
        db.SaveTable(table);

        var e = db.LoadTable(table.Id)!.ResultSets[0].Entries;
        Assert.Equal((null, null), (e[0].LinkedTableId, e[0].UnresolvedLinkName));
        Assert.Equal((items.Id, null), (e[1].LinkedTableId, e[1].UnresolvedLinkName));
        Assert.Equal((items.Id, null), (e[2].LinkedTableId, e[2].UnresolvedLinkName));
        Assert.Equal((null, "Rare Finds"), (e[3].LinkedTableId, e[3].UnresolvedLinkName));
        Assert.Equal("1x Scavenged Item", e[1].Text); // the text is untouched by linking
    }

    [Fact]
    public void Resolved_link_survives_renaming_the_destination()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var (items, scavenging) = Fixtures.SeedScavenging(db, collection.Id);

        var toRename = db.LoadTable(items.Id)!;
        toRename.Name = "Salvage";
        db.SaveTable(toRename);

        var entries = db.LoadTable(scavenging.Id)!.ResultSets[0].Entries;
        Assert.Equal(items.Id, entries[1].LinkedTableId);
        Assert.Equal(items.Id, entries[2].LinkedTableId);
        Assert.Null(entries[1].UnresolvedLinkName);
        Assert.Equal("Salvage", db.LoadTable(entries[1].LinkedTableId!.Value)!.Name); // link now shows the current name
    }

    // ---- delete -----------------------------------------------------------------------------

    [Fact]
    public void Deleting_a_destination_turns_links_into_unresolved_placeholders_with_its_last_name()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var (items, scavenging) = Fixtures.SeedScavenging(db, collection.Id);
        var other = db.SaveTable(Fixtures.RoomFeatures(collection.Id));

        Assert.True(db.DeleteTable(items.Id));

        Assert.Null(db.LoadTable(items.Id));
        var entries = db.LoadTable(scavenging.Id)!.ResultSets[0].Entries;
        Assert.Equal((null, "Scavenged Items"), (entries[1].LinkedTableId, entries[1].UnresolvedLinkName));
        Assert.Equal((null, "Scavenged Items"), (entries[2].LinkedTableId, entries[2].UnresolvedLinkName));
        // Unlinked entries and unrelated tables are untouched.
        Assert.Equal((null, null), (entries[0].LinkedTableId, entries[0].UnresolvedLinkName));
        Assert.Equal((null, null), (entries[3].LinkedTableId, entries[3].UnresolvedLinkName));
        Assert.Equal("1x Scavenged Item", entries[1].Text);
        Assert.NotNull(db.LoadTable(other.Id));
        Assert.Equal(new[] { "Room Features", "Scavenging" }, db.GetTableSummaries(collection.Id).Select(t => t.Name).ToArray());
    }

    [Fact]
    public void Deleted_destination_placeholder_uses_the_name_at_deletion_time_after_a_rename()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var (items, scavenging) = Fixtures.SeedScavenging(db, collection.Id);

        var renamed = db.LoadTable(items.Id)!;
        renamed.Name = "Salvage";
        db.SaveTable(renamed);
        db.DeleteTable(items.Id);

        Assert.Equal("Salvage", db.LoadTable(scavenging.Id)!.ResultSets[0].Entries[1].UnresolvedLinkName);
    }

    [Fact]
    public void Deleting_a_table_removes_its_result_sets_and_entries()
    {
        using var temp = new TempDatabase();
        long id;
        using (var db = temp.Open())
        {
            id = db.SaveTable(Fixtures.RoomFeatures(db.CreateCollection("C").Id)).Id;
            db.DeleteTable(id);
        }

        using var raw = new SqliteConnection($"Data Source={temp.Path};Pooling=False");
        raw.Open();
        foreach (var sql in new[] { "SELECT COUNT(*) FROM ResultSets", "SELECT COUNT(*) FROM Entries", "SELECT COUNT(*) FROM Tables" })
        {
            using var cmd = raw.CreateCommand();
            cmd.CommandText = sql;
            Assert.Equal(0L, cmd.ExecuteScalar());
        }
    }

    [Fact]
    public void Deleting_a_table_that_links_to_itself_works()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var table = Fixtures.Scavenging(db.CreateCollection("C").Id, null);
        db.SaveTable(table);
        table.ResultSets[0].Entries[1].LinkedTableId = table.Id;
        db.SaveTable(table);

        Assert.True(db.DeleteTable(table.Id));
        Assert.Null(db.LoadTable(table.Id));
    }

    [Fact]
    public void Deleting_a_missing_table_returns_false_and_changes_nothing()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var (_, scavenging) = Fixtures.SeedScavenging(db, collection.Id);

        Assert.False(db.DeleteTable(99_999));

        Assert.NotNull(db.LoadTable(scavenging.Id)!.ResultSets[0].Entries[1].LinkedTableId);
    }

    [Fact]
    public void Failed_delete_rolls_back_the_relinking_as_well()
    {
        using var temp = new TempDatabase();
        long itemsId, scavengingId;
        using (var db = temp.Open())
        {
            var (items, scavenging) = Fixtures.SeedScavenging(db, db.CreateCollection("C").Id);
            itemsId = items.Id;
            scavengingId = scavenging.Id;
        }

        // Make the final step of the delete fail, after the entries have already been updated.
        using (var raw = new SqliteConnection($"Data Source={temp.Path};Pooling=False"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER FailDelete BEFORE DELETE ON Tables BEGIN SELECT RAISE(ABORT, 'delete blocked'); END;";
            cmd.ExecuteNonQuery();
        }

        using var db2 = temp.Open();
        var ex = Assert.Throws<SqliteException>(() => db2.DeleteTable(itemsId));
        Assert.Contains("delete blocked", ex.Message);

        Assert.NotNull(db2.LoadTable(itemsId));
        var entries = db2.LoadTable(scavengingId)!.ResultSets[0].Entries;
        Assert.Equal((itemsId, null), (entries[1].LinkedTableId, entries[1].UnresolvedLinkName));
        Assert.Equal((itemsId, null), (entries[2].LinkedTableId, entries[2].UnresolvedLinkName));
    }

    [Fact]
    public void Foreign_key_still_blocks_a_raw_delete_that_skips_the_relinking()
    {
        using var temp = new TempDatabase();
        long itemsId;
        using (var db = temp.Open())
            itemsId = Fixtures.SeedScavenging(db, db.CreateCollection("C").Id).Items.Id;

        using var raw = new SqliteConnection($"Data Source={temp.Path};Pooling=False;Foreign Keys=True");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "DELETE FROM Tables WHERE Id = $id";
        cmd.Parameters.AddWithValue("$id", itemsId);
        Assert.Throws<SqliteException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void Links_to_a_table_are_counted_excluding_the_table_itself()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var (items, scavenging) = Fixtures.SeedScavenging(db, db.CreateCollection("C").Id);

        Assert.Equal(2, db.CountLinksTo(items.Id));
        Assert.Equal(0, db.CountLinksTo(scavenging.Id));
    }
}
