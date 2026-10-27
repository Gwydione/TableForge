using TableForge.Data;
using TableForge.Domain;
using TableForge.Import;
using TableForge.Portable;
using TableForge.ViewModels;
using static TableForge.Tests.PortableFixtures;

namespace TableForge.Tests;

/// <summary>
/// Unique Results, storage (schema 12): every row's stable <see cref="TableEntry.RowKey"/>, the per-row
/// <see cref="TableEntry.IsUnique"/> flag, used results kept apart from the table's definition, and RollHistory.RerolledCount.
/// A row keeps its key, and so its used state, through every edit; only deleting it (or Reset) clears that state.
/// </summary>
public class UniqueResultsPersistenceTests
{
    private const string DropSchema12 =
        "ALTER TABLE Entries DROP COLUMN RowKey; ALTER TABLE Entries DROP COLUMN IsUnique; DROP TABLE UsedResults; ALTER TABLE RollHistory DROP COLUMN RerolledCount; ";

    private static TableEntry U(TableEntry entry)
    {
        entry.IsUnique = true;
        return entry;
    }

    private static IEnumerable<TableEntry> Rows(RollableTable table) => table.ResultSets.SelectMany(s => s.Entries);
    private static List<Guid> Keys(RollableTable table) => Rows(table).Select(e => e.RowKey).ToList();
    private static Guid[] Used(AppDatabase db, long tableId) => db.GetUsedRowKeys(tableId).Order().ToArray();
    private static Guid[] Sorted(params Guid[] keys) => keys.Order().ToArray();

    private static RollableTable Encounters(AppDatabase db, long collectionId) =>
        Save(db, collectionId, "Encounters", "d6", null, Set("", U(E(1, 2, "Goblin")), U(E(3, 4, "Ogre")), U(E(5, 6, "Dragon"))));

    /// <summary>Edits a saved table the way the Edit screen does: draft from the stored table, change, build, save, reload.</summary>
    private static RollableTable Edit(AppDatabase db, long id, Action<TableImportDraft> change)
    {
        var stored = db.LoadTable(id)!;
        var draft = TableImportDraft.FromTable(stored);
        change(draft);
        Assert.True(draft.TryBuildTable(stored.CollectionId, out var table, out var errors), string.Join(" ", errors));
        db.SaveTable(table!);
        return db.LoadTable(id)!;
    }

    private static (string Type, long NotNull, string? Default) Column(string path, string table, string name)
    {
        using var raw = Raw(path);
        using var cmd = raw.CreateCommand();
        cmd.CommandText = $"SELECT type, \"notnull\", dflt_value FROM pragma_table_info('{table}') WHERE name = '{name}'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read(), $"{table}.{name} is missing.");
        return (reader.GetString(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    // ---- schema 12 -------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_schema_is_version_12_with_row_keys_unique_flags_used_results_and_a_reroll_count()
    {
        Assert.Equal(12, DatabaseMigrations.CurrentVersion); // pinned: bump deliberately with each migration

        using var temp = new TempDatabase();
        using (temp.Open()) { }
        Assert.Equal(("TEXT", 1L, "''"), Column(temp.Path, "Entries", "RowKey"));
        Assert.Equal(("INTEGER", 1L, "0"), Column(temp.Path, "Entries", "IsUnique"));
        Assert.Equal(("INTEGER", 1L, "0"), Column(temp.Path, "RollHistory", "RerolledCount"));
        Assert.Equal(("INTEGER", 1L, null), Column(temp.Path, "UsedResults", "TableId"));
        Assert.Equal(("TEXT", 1L, null), Column(temp.Path, "UsedResults", "RowKey"));
        Assert.Equal(("TEXT", 1L, null), Column(temp.Path, "UsedResults", "UsedUtc"));
    }

    [Fact]
    public void A_version_11_database_upgrades_with_a_backup_a_distinct_key_for_every_row_and_nothing_unique_or_used()
    {
        using var temp = new TempDatabase();
        long collectionId;
        string before;
        List<RollHistoryItem> history;
        using (var db = temp.Open())
        {
            collectionId = SeedMythic(db).Id;
            db.AddRollHistory(new RollSnapshot(null, "Gone", "d20", 14, "High", 3, null));
            db.AddRollHistory(new RollSnapshot(null, "Clamped", "d20", 25, "Top", 0, 20));
            before = Describe(db, collectionId);
            history = db.GetRollHistory().ToList();
        }
        // Make it a real Gidubba 1.3.0 file: version 11, with none of schema 12.
        Execute(temp.Path, DropSchema12 + "PRAGMA user_version = 11");

        using (var upgraded = temp.Open())
        {
            Assert.Equal(before, Describe(upgraded, collectionId));                  // every table, row, link and style unchanged
            var tables = upgraded.GetTableSummaries(collectionId).Select(t => upgraded.LoadTable(t.Id)!).ToList();
            var rows = tables.SelectMany(Rows).ToList();
            Assert.All(rows, e => Assert.NotEqual(Guid.Empty, e.RowKey));
            Assert.Equal(rows.Count, rows.Select(e => e.RowKey).Distinct().Count());   // one key per row, never shared
            Assert.All(rows, e => Assert.False(e.IsUnique));                          // every row stays repeatable
            Assert.All(tables, t => Assert.Empty(upgraded.GetUsedRowKeys(t.Id)));
            Assert.Equal(history, upgraded.GetRollHistory());                         // RerolledCount 0, everything else as it was
        }

        Assert.Contains(DatabaseBackup.Existing(temp.Path), p => p.EndsWith(".pre-v12-from-v11.backup.db", StringComparison.Ordinal));
        Assert.Equal(0, Count(temp.Path, "SELECT COUNT(*) FROM Entries WHERE length(RowKey) <> 36 OR RowKey <> lower(RowKey) OR substr(RowKey, 15, 1) <> '4'"));
        using var check = Raw(temp.Path);
        Assert.Equal(12, DatabaseMigrations.GetVersion(check));
    }

    [Fact]
    public void Keys_the_migration_gave_are_kept_by_later_saves_so_used_state_marked_on_them_survives()
    {
        using var temp = new TempDatabase();
        long id;
        using (var db = temp.Open()) id = Encounters(db, db.CreateCollection("C").Id).Id;
        Execute(temp.Path, DropSchema12 + "PRAGMA user_version = 11");

        using var upgraded = temp.Open();
        var keys = Keys(upgraded.LoadTable(id)!);
        upgraded.MarkRowsUsed(id, [keys[1]]);
        var edited = Edit(upgraded, id, d => d.ResultSets[0].Entries[1].Text = "Two ogres");

        Assert.Equal(keys, Keys(edited));
        Assert.Equal([keys[1]], Used(upgraded, id));
    }

    /// <summary>
    /// Whichever of migration 12's statements fails, the failure is reported and the whole migration is rolled back. (After an
    /// ADD COLUMN with a CHECK, a later statement's failure in the same script goes unreported, which is why that one is last.)
    /// </summary>
    [Theory]
    [InlineData("CREATE TABLE UsedResults (Anything TEXT)")]                    // the first statement
    [InlineData("ALTER TABLE RollHistory ADD COLUMN RerolledCount INTEGER")]   // one after RowKey has been added and filled
    [InlineData("ALTER TABLE Entries ADD COLUMN IsUnique INTEGER")]            // the last
    public void A_migration_that_fails_part_way_is_reported_and_leaves_the_version_11_database_as_it_was(string obstruction)
    {
        using var temp = new TempDatabase();
        using (var db = temp.Open()) Encounters(db, db.CreateCollection("C").Id);
        Execute(temp.Path, DropSchema12 + "PRAGMA user_version = 11");
        Execute(temp.Path, obstruction);

        Assert.Throws<DatabaseMigrationException>(() => temp.Open());
        Assert.Equal(11, Count(temp.Path, "PRAGMA user_version"));
        Assert.Equal(0, Count(temp.Path, "SELECT COUNT(*) FROM pragma_table_info('Entries') WHERE name = 'RowKey'")); // all rolled back
        Assert.Contains(DatabaseBackup.Existing(temp.Path), p => p.EndsWith(".pre-v12-from-v11.backup.db", StringComparison.Ordinal));
    }

    // ---- row keys and the Unique flag ------------------------------------------------------------------------------------

    [Fact]
    public void A_new_table_gives_every_row_its_own_key_on_the_passed_object_and_stores_the_unique_flags()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var saved = Save(db, db.CreateCollection("C").Id, "Mixed", "d6", null,
            Set("Who", U(E(1, 3, "Hermit")), E(4, 6, "Nobody")), Set("Where", E(1, 3, "Cave"), U(E(4, 6, "Tower"))));

        var keys = Keys(saved);
        Assert.All(keys, k => Assert.NotEqual(Guid.Empty, k));
        Assert.Equal(4, keys.Distinct().Count());
        var loaded = db.LoadTable(saved.Id)!;
        Assert.Equal(keys, Keys(loaded));
        Assert.Equal([true, false, false, true], Rows(loaded).Select(e => e.IsUnique).ToArray());
        Assert.Empty(db.GetUsedRowKeys(saved.Id));
    }

    [Fact]
    public void Editing_text_ranges_formatting_dice_and_links_keeps_every_rows_key_and_used_state_although_row_ids_change()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = db.CreateCollection("C");
        var table = Encounters(db, c.Id);
        var other = Save(db, c.Id, "Lairs", "d4", null, Set("", E(1, 4, "Cave")));
        var keys = Keys(table);
        var ids = Rows(table).Select(e => e.Id).ToList();
        db.MarkRowsUsed(table.Id, [keys[2]]);

        var edited = Edit(db, table.Id, d =>
        {
            d.TableName = "Encounters II";
            d.DiceText = "d8";
            var rows = d.ResultSets[0].Entries;
            rows[0].RangeText = "1-3";
            rows[1].RangeText = "4-5";
            rows[2].RangeText = "6-8";
            rows[2].Text = "A red dragon";
            rows[2].Styles = Runs("A red dragon".Length, (2, 3, TextStyle.Bold));
            rows[2].LinkedTableId = other.Id;
        });

        Assert.Equal(keys, Keys(edited));
        Assert.Empty(Rows(edited).Select(e => e.Id).Intersect(ids));               // a save replaces the stored rows...
        Assert.Equal([keys[2]], Used(db, table.Id));                              // ...but the used row is still the used row
        Assert.Equal(("A red dragon", 6, 8), (Rows(edited).Last().Text, Rows(edited).Last().Min, Rows(edited).Last().Max));
    }

    [Fact]
    public void Reordering_rows_moves_each_key_with_its_row()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var table = Encounters(db, db.CreateCollection("C").Id);
        var byText = Rows(table).ToDictionary(e => e.Text, e => e.RowKey);

        var edited = Edit(db, table.Id, d => d.ResultSets[0].Entries.Reverse());

        Assert.Equal(["Dragon", "Ogre", "Goblin"], Rows(edited).Select(e => e.Text).ToArray());
        Assert.All(Rows(edited), e => Assert.Equal(byText[e.Text], e.RowKey));
    }

    [Fact]
    public void Used_results_survive_closing_and_reopening_the_database()
    {
        using var temp = new TempDatabase();
        long id;
        Guid[] used;
        using (var db = temp.Open())
        {
            var table = Encounters(db, db.CreateCollection("C").Id);
            id = table.Id;
            used = Sorted(Keys(table)[0], Keys(table)[2]);
            db.MarkRowsUsed(id, used);
        }

        using var reopened = temp.Open();
        Assert.Equal(used, Used(reopened, id));
    }

    [Fact]
    public void Deleting_a_row_removes_its_used_state_and_a_new_row_gets_a_new_key_and_starts_unused()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var table = Encounters(db, db.CreateCollection("C").Id);
        var keys = Keys(table);
        db.MarkRowsUsed(table.Id, [keys[0], keys[2]]);

        var edited = Edit(db, table.Id, d =>
        {
            d.ResultSets[0].Entries.RemoveAt(2);                                                       // Dragon, used
            d.ResultSets[0].Entries.Add(new EntryDraft { RangeText = "5-6", Text = "Wyvern", IsUnique = true });
        });

        var wyvern = Rows(edited).Last();
        Assert.Equal("Wyvern", wyvern.Text);
        Assert.True(wyvern.IsUnique);
        Assert.DoesNotContain(wyvern.RowKey, keys);
        Assert.Equal([keys[0]], Used(db, table.Id));                                                   // Goblin is still used
        Assert.Equal(1, Count(temp.Path, "SELECT COUNT(*) FROM UsedResults"));
    }

    [Fact]
    public void Deleting_a_result_set_removes_the_used_state_of_its_rows_only()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var table = Save(db, db.CreateCollection("C").Id, "Pair", "d4", null,
            Set("A", U(E(1, 2, "A1")), U(E(3, 4, "A2"))), Set("B", U(E(1, 2, "B1")), U(E(3, 4, "B2"))));
        var keys = Keys(table);
        db.MarkRowsUsed(table.Id, [keys[0], keys[2]]);

        Edit(db, table.Id, d => d.ResultSets.RemoveAt(1));

        Assert.Equal([keys[0]], Used(db, table.Id));
    }

    [Fact]
    public void Turning_unique_off_keeps_the_used_state_and_turning_it_back_on_finds_the_row_still_used()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var table = Encounters(db, db.CreateCollection("C").Id);
        var dragon = Keys(table)[2];
        db.MarkRowsUsed(table.Id, [dragon]);

        var off = Edit(db, table.Id, d => d.ResultSets[0].Entries[2].IsUnique = false);
        Assert.False(Rows(off).Last().IsUnique);
        Assert.Equal([dragon], Used(db, table.Id));                               // kept, though a repeatable row ignores it

        var on = Edit(db, table.Id, d => d.ResultSets[0].Entries[2].IsUnique = true);
        Assert.True(Rows(on).Last().IsUnique);
        Assert.Equal([dragon], Used(db, table.Id));
    }

    [Fact]
    public void A_key_repeated_within_one_table_is_kept_by_its_first_row_and_replaced_on_the_others()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var shared = Guid.NewGuid();
        var first = E(1, 3, "First");
        var second = E(4, 6, "Second");
        first.RowKey = shared;
        second.RowKey = shared;

        var saved = Save(db, db.CreateCollection("C").Id, "Twins", "d6", null, Set("", first), Set("Other", second));

        Assert.Equal(shared, first.RowKey);
        Assert.NotEqual(shared, second.RowKey);
        Assert.NotEqual(Guid.Empty, second.RowKey);
        Assert.Equal([shared, second.RowKey], Keys(db.LoadTable(saved.Id)!));
    }

    [Fact]
    public void A_malformed_stored_key_loads_as_none_and_the_next_save_gives_the_row_a_new_one()
    {
        using var temp = new TempDatabase();
        long id;
        using (var db = temp.Open()) id = Encounters(db, db.CreateCollection("C").Id).Id;
        Execute(temp.Path, "UPDATE Entries SET RowKey = 'not a key' WHERE DisplayText = 'Ogre'");

        using var reopened = temp.Open();
        var loaded = reopened.LoadTable(id)!;
        Assert.Equal(Guid.Empty, Rows(loaded).ElementAt(1).RowKey);
        reopened.SaveTable(loaded);
        Assert.NotEqual(Guid.Empty, Rows(reopened.LoadTable(id)!).ElementAt(1).RowKey);
        Assert.Equal(3, Keys(reopened.LoadTable(id)!).Distinct().Count());
    }

    // ---- the Edit screen -------------------------------------------------------------------------------------------------

    private static ReviewViewModel EditScreen(AppDatabase db, Collection c, long id) =>
        new(TableImportDraft.FromTable(db.LoadTable(id)!), c, db, _ => { }, () => { });

    [Fact]
    public void On_the_edit_screen_typing_over_a_row_keeps_its_key_and_deleting_it_removes_its_used_state()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = db.CreateCollection("C");
        var table = Encounters(db, c.Id);
        var keys = Keys(table);
        db.MarkRowsUsed(table.Id, keys);

        var review = EditScreen(db, c, table.Id);
        review.Rows[0].Text = "Goblin king";
        review.Rows[0].RangeText = "1-3";
        review.Rows[1].RangeText = "4";
        review.Rows[1].DeleteCommand.Execute(null);                              // Ogre
        review.Rows[1].RangeText = "4-6";                                        // Dragon, now the second row
        Assert.True(review.CanSave, string.Join(" ", review.ValidationNotes));
        review.SaveCommand.Execute(null);

        var saved = db.LoadTable(table.Id)!;
        Assert.Equal(["Goblin king", "Dragon"], Rows(saved).Select(e => e.Text).ToArray());
        Assert.Equal([keys[0], keys[2]], Keys(saved));
        Assert.Equal(Sorted(keys[0], keys[2]), Used(db, table.Id));
    }

    [Fact]
    public void Replacing_all_rows_with_pasted_rows_gives_new_unused_rows()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = db.CreateCollection("C");
        var table = Encounters(db, c.Id);
        var keys = Keys(table);
        db.MarkRowsUsed(table.Id, keys);

        var review = EditScreen(db, c, table.Id);
        review.PasteRowsText = "1-3 Bandits\n4-6 Wolves";
        review.ReplacePastedRowsCommand.Execute(null);
        review.SaveCommand.Execute(null);

        var saved = db.LoadTable(table.Id)!;
        Assert.Equal(["Bandits", "Wolves"], Rows(saved).Select(e => e.Text).ToArray());
        Assert.Empty(Keys(saved).Intersect(keys));
        Assert.All(Rows(saved), e => Assert.False(e.IsUnique));
        Assert.Empty(db.GetUsedRowKeys(table.Id));
    }

    // ---- used results ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Marking_is_idempotent_and_ignores_keys_that_name_no_row_of_the_table()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = db.CreateCollection("C");
        var table = Encounters(db, c.Id);
        var other = Encounters(db, c.Id);
        var key = Keys(table)[0];

        db.MarkRowsUsed(table.Id, [key, key]);
        db.MarkRowsUsed(table.Id, [key, Guid.NewGuid(), Keys(other)[1]]);       // a stranger, and another table's row

        Assert.Equal([key], Used(db, table.Id));
        Assert.Empty(db.GetUsedRowKeys(other.Id));
        Assert.Equal(1, Count(temp.Path, "SELECT COUNT(*) FROM UsedResults"));
    }

    [Fact]
    public void Reset_clears_only_that_tables_used_results()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = db.CreateCollection("C");
        var table = Encounters(db, c.Id);
        var other = Encounters(db, c.Id);
        db.MarkRowsUsed(table.Id, Keys(table));
        db.MarkRowsUsed(other.Id, [Keys(other)[0]]);

        Assert.Equal(3, db.ResetUsedResults(table.Id));

        Assert.Empty(db.GetUsedRowKeys(table.Id));
        Assert.Equal([Keys(other)[0]], Used(db, other.Id));
        Assert.Equal(0, db.ResetUsedResults(table.Id));
        Assert.Equal(Keys(table), Keys(db.LoadTable(table.Id)!));                 // the table itself is untouched
    }

    [Fact]
    public void Deleting_a_table_or_its_collection_removes_its_used_results()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = db.CreateCollection("C");
        var keep = db.CreateCollection("Keep");
        var gone = Encounters(db, c.Id);
        var alsoGone = Encounters(db, c.Id);
        var kept = Encounters(db, keep.Id);
        foreach (var t in new[] { gone, alsoGone, kept }) db.MarkRowsUsed(t.Id, [Keys(t)[0]]);

        db.DeleteTable(gone.Id);
        Assert.Equal(2, Count(temp.Path, "SELECT COUNT(*) FROM UsedResults"));
        db.DeleteCollection(c.Id);
        Assert.Equal(1, Count(temp.Path, "SELECT COUNT(*) FROM UsedResults"));
        Assert.Equal([Keys(kept)[0]], Used(db, kept.Id));
    }

    [Fact]
    public void Saving_a_table_never_marks_or_unmarks_anything_by_itself()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var table = Encounters(db, db.CreateCollection("C").Id);
        db.MarkRowsUsed(table.Id, [Keys(table)[1]]);
        var before = Snapshot(temp.Path).Split("[UsedResults]")[1].Split("[RollHistory]")[0];

        db.SaveTable(db.LoadTable(table.Id)!);
        db.SaveTable(db.LoadTable(table.Id)!);

        Assert.Equal(before, Snapshot(temp.Path).Split("[UsedResults]")[1].Split("[RollHistory]")[0]);
    }

    [Fact]
    public void An_imported_table_gets_new_keys_and_starts_unused_whatever_keys_the_import_carries()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var source = Encounters(db, db.CreateCollection("Source").Id);
        var sourceKeys = Keys(source);
        db.MarkRowsUsed(source.Id, sourceKeys);

        var carried = db.LoadTable(source.Id)!;                                  // its rows still hold the source's keys
        var imported = db.ImportCollection(new PortableCollection("Copy", [], [new PortableTable(carried, null)]));

        var table = db.LoadTable(db.GetTableSummaries(imported.Id).Single().Id)!;
        Assert.Empty(Keys(table).Intersect(sourceKeys));
        Assert.Equal(3, Keys(table).Distinct().Count());
        Assert.Empty(db.GetUsedRowKeys(table.Id));
        Assert.Equal(Sorted([.. sourceKeys]), Used(db, source.Id));              // the source is untouched
    }

    // ---- Recent Rolls ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Recent_rolls_keep_the_reroll_count_and_an_ordinary_roll_has_none()
    {
        using var temp = new TempDatabase();
        using (var db = temp.Open())
        {
            db.AddRollHistory(new RollSnapshot(null, "Plain", "d6", 3, "Ogre"));
            var added = db.AddRollHistory(new RollSnapshot(null, "Unique", "d6", 5, "Dragon", RerolledCount: 2));
            Assert.Equal(2, added.RerolledCount);
        }

        using var reopened = temp.Open();
        Assert.Equal([2, 0], reopened.GetRollHistory().Select(h => h.RerolledCount).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => reopened.AddRollHistory(new RollSnapshot(null, "Bad", "d6", 1, "x", RerolledCount: -1)));
        Assert.Equal(2, reopened.GetRollHistory().Count);
    }

    [Fact]
    public void The_database_itself_refuses_a_negative_reroll_count()
    {
        using var temp = new TempDatabase();
        using (temp.Open()) { }
        var ex = Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => Execute(temp.Path,
            "INSERT INTO RollHistory (TableId, TableName, DiceText, RollValue, ResultText, RolledUtc, RerolledCount) " +
            "VALUES (NULL, 'Bad', 'd6', 1, 'x', '2026-01-01T00:00:00.0000000Z', -1)"));
        Assert.Contains("CHECK constraint", ex.Message);
        Assert.Equal(0, Count(temp.Path, "SELECT COUNT(*) FROM RollHistory"));
    }
}
