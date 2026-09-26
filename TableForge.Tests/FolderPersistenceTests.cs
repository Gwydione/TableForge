using Microsoft.Data.Sqlite;
using TableForge.Data;
using TableForge.Domain;

namespace TableForge.Tests;

/// <summary>One-level folders within a collection: schema, migration, naming, moving, deletion and cross-collection safety, all on real SQLite files.</summary>
public class FolderPersistenceTests
{
    // ---- migration -----------------------------------------------------------------------------

    [Fact]
    public void An_rc7_database_migrates_to_the_current_version_with_every_table_unfiled_and_all_data_intact()
    {
        using var temp = new TempDatabase();
        using (var raw = new SqliteConnection($"Data Source={temp.Path};Pooling=False;Foreign Keys=True"))
        {
            raw.Open();
            DatabaseMigrations.Apply(raw, upToVersion: 4); // exactly the RC7 schema, before folders existed
            Assert.Equal(4, DatabaseMigrations.GetVersion(raw));

            using var cmd = raw.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO Collections (Id, Name, CreatedUtc) VALUES (1, 'Broken Shores', '2026-01-01T00:00:00.0000000Z');
                INSERT INTO Tables (Id, CollectionId, Name, DiceCount, DiceSides, DiceModifier, DiceConvention, CreatedUtc, UpdatedUtc) VALUES
                    (10, 1, 'Weather', 1, 20, 0, 0, '2026-01-02T00:00:00.0000000Z', '2026-01-02T00:00:00.0000000Z'),
                    (11, 1, 'Critical Injuries', 1, 20, 0, 0, '2026-01-03T00:00:00.0000000Z', '2026-01-03T00:00:00.0000000Z');
                INSERT INTO ResultSets (Id, TableId, Name, SortOrder) VALUES (100, 10, '', 0), (101, 11, '', 0);
                INSERT INTO Entries (Id, ResultSetId, MinValue, MaxValue, DisplayText, SortOrder) VALUES
                    (1000, 100, 1, 20, 'Clear skies', 0), (1001, 101, 1, 20, 'Bruised', 0);
                """;
            cmd.ExecuteNonQuery();
        }

        using var db = temp.Open(); // migrates on open

        using (var raw = new SqliteConnection($"Data Source={temp.Path};Pooling=False"))
        {
            raw.Open();
            Assert.Equal(DatabaseMigrations.CurrentVersion, DatabaseMigrations.GetVersion(raw));
            Assert.Equal(7, DatabaseMigrations.GetVersion(raw));
        }

        // Every existing table ends up Unfiled; no default folder is invented.
        Assert.Empty(db.GetFolders(1));
        var summaries = db.GetTableSummaries(1);
        Assert.Equal(2, summaries.Count);
        Assert.All(summaries, s => Assert.Equal((null, "Unfiled"), (s.FolderId, s.FolderName)));

        // Nothing else moved.
        Assert.Equal("Broken Shores", Assert.Single(db.GetCollections()).Name);
        var weather = db.LoadTable(10)!;
        Assert.Equal("Clear skies", weather.ResultSets[0].Entries[0].Text);
        Assert.Null(weather.FolderId);
    }

    [Fact]
    public void A_new_database_starts_at_the_current_version_with_no_folders()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        Assert.Empty(db.GetFolders(collection.Id));
    }

    // ---- create / round-trip ---------------------------------------------------------------------

    [Fact]
    public void A_folder_and_a_tables_folder_assignment_round_trip()
    {
        using var temp = new TempDatabase();
        long tableId, folderId;
        using (var db = temp.Open())
        {
            var collection = db.CreateCollection("Broken Shores");
            var folder = db.CreateFolder(collection.Id, "Combat");
            folderId = folder.Id;
            Assert.True(folderId > 0);
            Assert.Equal((collection.Id, "Combat"), (folder.CollectionId, folder.Name));

            var table = Fixtures.ParseAndBuild("d6 Critical Injuries\n1-6 x", collection.Id);
            table.FolderId = folderId;
            db.SaveTable(table);
            tableId = table.Id;
        }

        using var reopened = temp.Open();
        var loaded = reopened.LoadTable(tableId)!;
        Assert.Equal(folderId, loaded.FolderId);
        var summary = Assert.Single(reopened.GetTableSummaries(loaded.CollectionId));
        Assert.Equal((folderId, "Combat"), (summary.FolderId, summary.FolderName));
        var folders = reopened.GetFolders(loaded.CollectionId);
        Assert.Equal("Combat", Assert.Single(folders).Name);
    }

    [Fact]
    public void A_table_saved_with_no_folder_is_unfiled()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var table = db.SaveTable(Fixtures.ParseAndBuild("d6 Loot\n1-6 x", collection.Id));

        Assert.Null(db.LoadTable(table.Id)!.FolderId);
        Assert.Equal((null, "Unfiled"), (Assert.Single(db.GetTableSummaries(collection.Id)).FolderId, Assert.Single(db.GetTableSummaries(collection.Id)).FolderName));
    }

    [Fact]
    public void Folders_are_listed_alphabetically_case_insensitively()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        db.CreateFolder(collection.Id, "exploration");
        db.CreateFolder(collection.Id, "Combat");
        db.CreateFolder(collection.Id, "Character Creation");

        Assert.Equal(["Character Creation", "Combat", "exploration"], db.GetFolders(collection.Id).Select(f => f.Name).ToArray());
    }

    // ---- naming ----------------------------------------------------------------------------------

    [Fact]
    public void Folder_name_is_trimmed()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var folder = db.CreateFolder(collection.Id, "  Combat  ");
        Assert.Equal("Combat", folder.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_folder_name_is_rejected(string name)
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        Assert.Throws<ArgumentException>(() => db.CreateFolder(collection.Id, name));
    }

    [Theory]
    [InlineData("combat")]
    [InlineData("COMBAT")]
    [InlineData("  Combat  ")]
    public void Duplicate_folder_name_in_the_same_collection_is_rejected_case_insensitively(string duplicate)
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        db.CreateFolder(collection.Id, "Combat");

        Assert.Throws<InvalidOperationException>(() => db.CreateFolder(collection.Id, duplicate));
        Assert.Single(db.GetFolders(collection.Id)); // the failed attempt created nothing
    }

    [Fact]
    public void The_same_folder_name_is_allowed_in_a_different_collection()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var a = db.CreateCollection("Broken Shores");
        var b = db.CreateCollection("Ker Nethalas");

        db.CreateFolder(a.Id, "Combat");
        var combatB = db.CreateFolder(b.Id, "Combat");

        Assert.Equal("Combat", Assert.Single(db.GetFolders(a.Id)).Name);
        Assert.Equal("Combat", Assert.Single(db.GetFolders(b.Id)).Name);
        Assert.NotEqual(db.GetFolders(a.Id)[0].Id, combatB.Id);
    }

    [Fact]
    public void Renaming_a_folder_changes_only_its_name()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var folder = db.CreateFolder(collection.Id, "Combat");
        var table = db.SaveTable(Fixtures.ParseAndBuild("d6 Loot\n1-6 x", collection.Id).Also(t => t.FolderId = folder.Id));

        db.RenameFolder(folder.Id, "  Skirmishes  ");

        var renamed = Assert.Single(db.GetFolders(collection.Id));
        Assert.Equal((folder.Id, "Skirmishes"), (renamed.Id, renamed.Name));
        Assert.Equal(folder.Id, db.LoadTable(table.Id)!.FolderId); // the table stayed put, through the stable id
    }

    [Fact]
    public void Renaming_a_folder_to_a_name_already_used_in_the_collection_is_rejected()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        db.CreateFolder(collection.Id, "Combat");
        var exploration = db.CreateFolder(collection.Id, "Exploration");

        Assert.Throws<InvalidOperationException>(() => db.RenameFolder(exploration.Id, "combat"));
        Assert.Equal("Exploration", db.GetFolders(collection.Id).Single(f => f.Id == exploration.Id).Name);
    }

    [Fact]
    public void Renaming_a_folder_to_its_own_current_name_is_allowed()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var folder = db.CreateFolder(collection.Id, "Combat");

        db.RenameFolder(folder.Id, "Combat");

        Assert.Equal("Combat", Assert.Single(db.GetFolders(collection.Id)).Name);
    }

    // ---- moving ------------------------------------------------------------------------------------

    [Fact]
    public void A_table_moves_from_unfiled_into_a_folder()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var folder = db.CreateFolder(collection.Id, "Combat");
        var table = db.SaveTable(Fixtures.ParseAndBuild("d6 Loot\n1-6 x", collection.Id));
        Assert.Null(db.LoadTable(table.Id)!.FolderId);

        table.FolderId = folder.Id;
        db.SaveTable(table);

        Assert.Equal(folder.Id, db.LoadTable(table.Id)!.FolderId);
    }

    [Fact]
    public void A_table_moves_between_two_folders()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var a = db.CreateFolder(collection.Id, "Combat");
        var b = db.CreateFolder(collection.Id, "Exploration");
        var table = db.SaveTable(Fixtures.ParseAndBuild("d6 Weather\n1-6 x", collection.Id).Also(t => t.FolderId = a.Id));

        table.FolderId = b.Id;
        db.SaveTable(table);

        Assert.Equal(b.Id, db.LoadTable(table.Id)!.FolderId);
    }

    [Fact]
    public void A_table_moves_from_a_folder_back_to_unfiled()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var folder = db.CreateFolder(collection.Id, "Combat");
        var table = db.SaveTable(Fixtures.ParseAndBuild("d6 Loot\n1-6 x", collection.Id).Also(t => t.FolderId = folder.Id));

        table.FolderId = null;
        db.SaveTable(table);

        Assert.Null(db.LoadTable(table.Id)!.FolderId);
    }

    [Fact]
    public void Assigning_a_new_table_to_a_folder_from_another_collection_is_rejected()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var a = db.CreateCollection("A");
        var b = db.CreateCollection("B");
        var folderInA = db.CreateFolder(a.Id, "Combat");

        var table = Fixtures.ParseAndBuild("d6 Loot\n1-6 x", b.Id);
        table.FolderId = folderInA.Id;

        Assert.Throws<InvalidOperationException>(() => db.SaveTable(table));
        Assert.Empty(db.GetTableSummaries(b.Id)); // the failed save created nothing
    }

    [Fact]
    public void Reassigning_an_existing_table_to_a_folder_from_another_collection_without_moving_it_is_rejected()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var a = db.CreateCollection("A");
        var b = db.CreateCollection("B");
        var folderInA = db.CreateFolder(a.Id, "Combat");
        var table = db.SaveTable(Fixtures.ParseAndBuild("d6 Loot\n1-6 x", b.Id));

        table.FolderId = folderInA.Id; // CollectionId (b) is not changing, only FolderId is wrong
        Assert.Throws<InvalidOperationException>(() => db.SaveTable(table));
        Assert.Null(db.LoadTable(table.Id)!.FolderId); // unchanged
    }

    [Fact]
    public void Moving_a_table_to_another_collection_clears_a_folder_that_belonged_to_the_old_one()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var a = db.CreateCollection("A");
        var b = db.CreateCollection("B");
        var folderInA = db.CreateFolder(a.Id, "Combat");
        var table = db.SaveTable(Fixtures.ParseAndBuild("d6 Loot\n1-6 x", a.Id).Also(t => t.FolderId = folderInA.Id));

        table.CollectionId = b.Id; // a genuine collection change, via any existing behavior that allows one
        db.SaveTable(table);

        var loaded = db.LoadTable(table.Id)!;
        Assert.Equal((b.Id, (long?)null), (loaded.CollectionId, loaded.FolderId));
    }

    [Fact]
    public void Moving_a_table_to_another_collection_while_choosing_one_of_its_folders_keeps_that_choice()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var a = db.CreateCollection("A");
        var b = db.CreateCollection("B");
        var folderInB = db.CreateFolder(b.Id, "Combat");
        var table = db.SaveTable(Fixtures.ParseAndBuild("d6 Loot\n1-6 x", a.Id));

        table.CollectionId = b.Id;
        table.FolderId = folderInB.Id; // the user explicitly chose a valid destination folder
        db.SaveTable(table);

        Assert.Equal(folderInB.Id, db.LoadTable(table.Id)!.FolderId);
    }

    // ---- deletion ----------------------------------------------------------------------------------

    [Fact]
    public void Deleting_an_empty_folder_removes_it()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var folder = db.CreateFolder(collection.Id, "Combat");

        Assert.True(db.DeleteFolder(folder.Id));

        Assert.Empty(db.GetFolders(collection.Id));
    }

    [Fact]
    public void Deleting_a_nonempty_folder_moves_its_tables_to_unfiled_instead_of_deleting_them()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var folder = db.CreateFolder(collection.Id, "Combat");
        var t1 = db.SaveTable(Fixtures.ParseAndBuild("d6 A\n1-6 x", collection.Id).Also(t => t.FolderId = folder.Id));
        var t2 = db.SaveTable(Fixtures.ParseAndBuild("d6 B\n1-6 x", collection.Id).Also(t => t.FolderId = folder.Id));
        var untouched = db.SaveTable(Fixtures.ParseAndBuild("d6 C\n1-6 x", collection.Id));

        Assert.True(db.DeleteFolder(folder.Id));

        Assert.Empty(db.GetFolders(collection.Id));
        Assert.Null(db.LoadTable(t1.Id)!.FolderId);
        Assert.Null(db.LoadTable(t2.Id)!.FolderId);
        Assert.Null(db.LoadTable(untouched.Id)!.FolderId); // was already unfiled; untouched either way
        Assert.Equal(3, db.GetTableSummaries(collection.Id).Count); // nothing was deleted
    }

    [Fact]
    public void Deleting_a_missing_folder_returns_false_and_changes_nothing()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        db.CreateFolder(collection.Id, "Combat");

        Assert.False(db.DeleteFolder(999_999));

        Assert.Single(db.GetFolders(collection.Id));
    }

    // ---- links survive folder moves, renames and deletion -----------------------------------------

    [Fact]
    public void A_link_survives_the_target_moving_between_folders()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var folder = db.CreateFolder(collection.Id, "Exploration");
        var (items, scavenging) = Fixtures.SeedScavenging(db, collection.Id);

        var moved = db.LoadTable(items.Id)!;
        moved.FolderId = folder.Id;
        db.SaveTable(moved);

        var entries = db.LoadTable(scavenging.Id)!.ResultSets[0].Entries;
        Assert.Equal(items.Id, entries[1].LinkedTableId);
        Assert.Equal("1x Scavenged Item", entries[1].Text);
    }

    [Fact]
    public void A_link_survives_the_targets_folder_being_renamed()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var folder = db.CreateFolder(collection.Id, "Exploration");
        var (items, scavenging) = Fixtures.SeedScavenging(db, collection.Id);
        db.SaveTable(db.LoadTable(items.Id)!.Also(t => t.FolderId = folder.Id));

        db.RenameFolder(folder.Id, "Wilderness");

        var entries = db.LoadTable(scavenging.Id)!.ResultSets[0].Entries;
        Assert.Equal(items.Id, entries[1].LinkedTableId);
    }

    [Fact]
    public void A_link_survives_the_targets_folder_being_deleted_and_the_target_becomes_unfiled()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var collection = db.CreateCollection("C");
        var folder = db.CreateFolder(collection.Id, "Exploration");
        var (items, scavenging) = Fixtures.SeedScavenging(db, collection.Id);
        db.SaveTable(db.LoadTable(items.Id)!.Also(t => t.FolderId = folder.Id));

        db.DeleteFolder(folder.Id);

        Assert.Null(db.LoadTable(items.Id)!.FolderId);
        var entries = db.LoadTable(scavenging.Id)!.ResultSets[0].Entries;
        Assert.Equal(items.Id, entries[1].LinkedTableId);
        Assert.Equal("1x Scavenged Item", entries[1].Text);
    }
}
