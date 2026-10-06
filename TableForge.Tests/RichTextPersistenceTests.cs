using Microsoft.Data.Sqlite;
using TableForge.Data;
using TableForge.Domain;
using TableForge.Import;

namespace TableForge.Tests;

/// <summary>
/// RC24: bold/italic is stored beside the result text in Entries.TextFormatting (schema 9). DisplayText stays exactly the
/// plain text; rows without formatting store NULL; anything malformed or stale in the column loads as plain text.
/// </summary>
public class RichTextPersistenceTests
{
    internal static RollableTable Formatted(long collectionId)
    {
        var (armor, armorStyles) = TextStylesTests.Md("The creature gains **+2 Armor** until the *next dawn*.");
        var (dice, diceStyles) = TextStylesTests.Md("You gain ***+1d4 Armor*** until *next dawn*.");
        return new RollableTable
        {
            CollectionId = collectionId,
            Name = "Boons",
            Dice = DiceExpression.Parse("d6"),
            ResultSets =
            [
                new ResultSet
                {
                    Entries =
                    [
                        new TableEntry { Min = 1, Max = 2, Text = armor, Styles = armorStyles },
                        new TableEntry { Min = 3, Max = 4, Text = dice, Styles = diceStyles },
                        new TableEntry { Min = 5, Max = 6, Text = "Nothing happens." },
                    ],
                },
            ],
        };
    }

    private static SqliteConnection Raw(string path)
    {
        var raw = new SqliteConnection($"Data Source={path};Pooling=False");
        raw.Open();
        return raw;
    }

    private static List<(string Text, string? Formatting)> StoredEntries(string path)
    {
        using var raw = Raw(path);
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT DisplayText, TextFormatting FROM Entries ORDER BY Id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<(string, string?)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return rows;
    }

    private static void SetFormatting(string path, string text, string? formatting)
    {
        using var raw = Raw(path);
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "UPDATE Entries SET TextFormatting = $f WHERE DisplayText = $t";
        cmd.Parameters.AddWithValue("$f", (object?)formatting ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", text);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    [Fact]
    public void Formatting_survives_save_and_reload_and_the_text_is_stored_plain()
    {
        using var temp = new TempDatabase();
        long id;
        var original = Formatted(0);
        using (var db = temp.Open())
            id = db.SaveTable(Formatted(db.CreateCollection("C").Id)).Id;

        using (var db = temp.Open())
        {
            var loaded = db.LoadTable(id)!;
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(original.ResultSets[0].Entries[i].Text, loaded.ResultSets[0].Entries[i].Text);
                Assert.Equal(original.ResultSets[0].Entries[i].Styles, loaded.ResultSets[0].Entries[i].Styles);
            }
        }

        var stored = StoredEntries(temp.Path);
        Assert.Equal("The creature gains +2 Armor until the next dawn.", stored[0].Text);           // no markup in DisplayText, ever
        Assert.Equal("""{"v":1,"len":48,"runs":[[19,8,1],[38,9,2]]}""", stored[0].Formatting);
        Assert.Equal("You gain +1d4 Armor until next dawn.", stored[1].Text);
        Assert.Null(stored[2].Formatting);                                                          // a plain row stores NULL
    }

    [Fact]
    public void Saving_again_replaces_the_formatting_and_removing_it_stores_null()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var table = db.SaveTable(Formatted(db.CreateCollection("C").Id));

        table.ResultSets[0].Entries[0].Styles = TextStyles.Empty;
        db.SaveTable(table);

        Assert.True(db.LoadTable(table.Id)!.ResultSets[0].Entries[0].Styles.IsEmpty);
        Assert.Null(StoredEntries(temp.Path)[0].Formatting);
    }

    [Fact]
    public void Plain_tables_save_and_load_exactly_as_before_with_null_formatting()
    {
        using var temp = new TempDatabase();
        long id;
        using (var db = temp.Open())
            id = db.SaveTable(Fixtures.ParseAndBuild(Fixtures.RandomStartingGear, db.CreateCollection("C").Id)).Id;

        Assert.All(StoredEntries(temp.Path), e => Assert.Null(e.Formatting));
        using var reopened = temp.Open();
        Assert.All(reopened.LoadTable(id)!.ResultSets.SelectMany(s => s.Entries), e => Assert.True(e.Styles.IsEmpty));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("""{"v":1,"len":999,"runs":[[0,3,1]]}""")]   // stale: made for a different text
    [InlineData("""{"v":7,"len":48,"runs":[[0,3,1]]}""")]    // a future version
    [InlineData("""{"v":"1","len":48,"runs":[[0,3,1]]}""")]
    [InlineData("""{"v":1,"len":48,"runs":[[40,20,1]]}""")]  // past the end
    public void Malformed_or_stale_formatting_in_the_database_loads_the_row_plain_and_never_blocks_loading(string stored)
    {
        using var temp = new TempDatabase();
        long id;
        using (var db = temp.Open())
            id = db.SaveTable(Formatted(db.CreateCollection("C").Id)).Id;
        SetFormatting(temp.Path, "The creature gains +2 Armor until the next dawn.", stored);

        using var reopened = temp.Open();
        var table = reopened.LoadTable(id)!;
        Assert.True(table.ResultSets[0].Entries[0].Styles.IsEmpty);
        Assert.Equal("The creature gains +2 Armor until the next dawn.", table.ResultSets[0].Entries[0].Text);
        Assert.False(table.ResultSets[0].Entries[1].Styles.IsEmpty);                              // other rows are unaffected
    }

    [Fact]
    public void Text_changed_outside_TableForge_makes_its_formatting_stale_and_it_is_dropped()
    {
        using var temp = new TempDatabase();
        long id;
        using (var db = temp.Open())
            id = db.SaveTable(Formatted(db.CreateCollection("C").Id)).Id;
        using (var raw = Raw(temp.Path))
        {
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "UPDATE Entries SET DisplayText = 'Shorter.' WHERE DisplayText LIKE 'The creature%'";
            cmd.ExecuteNonQuery();
        }

        using var reopened = temp.Open();
        Assert.True(reopened.LoadTable(id)!.ResultSets[0].Entries[0].Styles.IsEmpty);
    }

    // ---- schema 9 -----------------------------------------------------------------------------------------------------

    [Fact]
    public void Since_version_9_the_schema_has_one_nullable_text_formatting_column()
    {
        Assert.Equal(10, DatabaseMigrations.CurrentVersion); // pinned: bump deliberately with each migration (10, RC26, added Tables.Description)

        using var temp = new TempDatabase();
        using var db = temp.Open();
        using var raw = Raw(temp.Path);
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT name, type, \"notnull\", dflt_value FROM pragma_table_info('Entries') WHERE name = 'TextFormatting'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("TEXT", reader.GetString(1));
        Assert.Equal(0, reader.GetInt32(2));
        Assert.True(reader.IsDBNull(3));
    }

    [Fact]
    public void An_rc23_version_8_database_upgrades_with_a_backup_and_every_row_unchanged_and_plain()
    {
        using var temp = new TempDatabase();
        long gearId, roomId;
        List<(string Text, string? Formatting)> before;
        using (var db = temp.Open())
        {
            var c = db.CreateCollection("C").Id;
            gearId = db.SaveTable(Fixtures.ParseAndBuild(Fixtures.RandomStartingGear, c)).Id;
            roomId = db.SaveTable(Fixtures.RoomFeatures(c)).Id;
        }
        // Make it a real RC23 file: version 8, with neither TextFormatting (9) nor Tables.Description (10).
        using (var raw = Raw(temp.Path))
        {
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "ALTER TABLE Entries DROP COLUMN TextFormatting; ALTER TABLE Tables DROP COLUMN Description; PRAGMA user_version = 8";
            cmd.ExecuteNonQuery();
        }
        using (var raw = Raw(temp.Path))
        {
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "SELECT DisplayText FROM Entries ORDER BY Id";
            using var reader = cmd.ExecuteReader();
            before = [];
            while (reader.Read()) before.Add((reader.GetString(0), null));
        }

        using (var upgraded = temp.Open())
        {
            var gear = upgraded.LoadTable(gearId)!;
            Assert.Equal(["Backpack", "Knife", "1x Torch (UD6)", "Fishing rod", "Rope (15 m)", "Tinderbox", "D4 Bandages", "D20 Construction Supplies"],
                gear.ResultSets[0].Entries.Select(e => e.Text).ToArray());
            Assert.All(gear.ResultSets.Concat(upgraded.LoadTable(roomId)!.ResultSets).SelectMany(s => s.Entries), e => Assert.True(e.Styles.IsEmpty));
        }

        Assert.Equal(before, StoredEntries(temp.Path));                      // every DisplayText identical, every TextFormatting NULL
        Assert.Contains(DatabaseBackup.Existing(temp.Path), p => p.EndsWith($".pre-v{DatabaseMigrations.CurrentVersion}-from-v8.backup.db", StringComparison.Ordinal));
        using var check = Raw(temp.Path);
        Assert.Equal(DatabaseMigrations.CurrentVersion, DatabaseMigrations.GetVersion(check));
    }

    [Fact]
    public void A_database_newer_than_this_build_understands_is_refused_as_rc23_refuses_version_9()
    {
        // RC23's CurrentVersion is 8; its AppDatabase refuses anything newer (DatabaseTooNewException) instead of re-saving
        // tables and silently dropping their formatting. The same rule, seen from this build, one version ahead:
        using var temp = new TempDatabase();
        using (temp.Open()) { }
        using (var raw = Raw(temp.Path))
        {
            Assert.Equal(DatabaseMigrations.CurrentVersion, DatabaseMigrations.GetVersion(raw));
            Assert.True(DatabaseMigrations.GetVersion(raw) > 8);
            using var cmd = raw.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version = {DatabaseMigrations.CurrentVersion + 1}";
            cmd.ExecuteNonQuery();
        }
        var ex = Assert.Throws<DatabaseTooNewException>(() => temp.Open());
        Assert.Equal((DatabaseMigrations.CurrentVersion + 1, DatabaseMigrations.CurrentVersion), (ex.FileVersion, ex.SupportedVersion));
    }

    // ---- editing a saved table ----------------------------------------------------------------------------------------

    [Fact]
    public void Editing_a_saved_table_carries_its_formatting_into_the_draft_and_back_out()
    {
        var table = Formatted(1);
        var draft = TableImportDraft.FromTable(table);
        Assert.Equal(table.ResultSets[0].Entries[0].Styles, draft.ResultSets[0].Entries[0].Styles);

        Assert.True(draft.TryBuildTable(1, out var rebuilt, out _));
        Assert.Equal(table.ResultSets[0].Entries.Select(e => e.Styles), rebuilt!.ResultSets[0].Entries.Select(e => e.Styles));
    }

    [Fact]
    public void Save_trims_the_text_and_the_formatting_moves_with_it()
    {
        var (text, styles) = TextStylesTests.Md("   gains **+2 Armor** now  ");
        var draft = new TableImportDraft
        {
            TableName = "T", DiceText = "d6",
            ResultSets = [new ResultSetDraft { Entries = [new EntryDraft { RangeText = "1-6", Text = text, Styles = styles }] }],
        };

        Assert.True(draft.TryBuildTable(1, out var table, out _));
        var entry = table!.ResultSets[0].Entries[0];
        Assert.Equal("gains **+2 Armor** now", TextStylesTests.ToMd(entry.Text, entry.Styles));
    }
}

/// <summary>
/// RC24 plain-table regression: with no formatting anywhere, everything RC23 did is unchanged — every parser corpus table
/// builds, saves and reloads with the same text, no formatting and NULL in the new column, and renders as one plain segment.
/// </summary>
public class PlainTextRegressionTests
{
    public static IEnumerable<object[]> CorpusNames =>
        ParserCorpus.Cases.Concat(ParserCorpus.Rc2Cases).Where(c => !c.RowsOnly).Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(CorpusNames))]
    public void A_plain_corpus_table_round_trips_with_no_formatting(string name)
    {
        var source = ParserCorpus.Cases.Concat(ParserCorpus.Rc2Cases).Single(c => c.Name == name).Source;
        var draft = TableTextParser.Parse(source);
        Assert.All(draft.ResultSets.SelectMany(s => s.Entries), e => Assert.True(e.Styles.IsEmpty));
        if (!draft.TryBuildTable(1, out var built, out _)) return; // a corpus case that cannot form a table has nothing to store

        using var temp = new TempDatabase();
        using var db = temp.Open();
        built!.CollectionId = db.CreateCollection("C").Id;
        var loaded = db.LoadTable(db.SaveTable(built).Id)!;

        Assert.Equal(built.ResultSets.SelectMany(s => s.Entries).Select(e => e.Text), loaded.ResultSets.SelectMany(s => s.Entries).Select(e => e.Text));
        foreach (var entry in loaded.ResultSets.SelectMany(s => s.Entries))
        {
            Assert.True(entry.Styles.IsEmpty);
            Assert.Null(entry.Styles.Serialize(entry.Text));
            Assert.All(entry.Styles.Segments(entry.Text), s => Assert.Equal(TextStyle.None, s.Style));
            Assert.Equal(entry.Text, string.Concat(entry.Styles.Segments(entry.Text).Select(s => s.Text)));
        }
    }
}
