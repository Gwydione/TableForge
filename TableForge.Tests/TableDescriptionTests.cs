using Microsoft.Data.Sqlite;
using TableForge.Data;
using TableForge.Dice;
using TableForge.Domain;
using TableForge.Import;
using TableForge.ViewModels;
using static TableForge.Tests.PortableFixtures;

namespace TableForge.Tests;

/// <summary>
/// RC26 Table Description: optional plain text that belongs to a table. Saved with it (schema 10), edited on Review/Edit,
/// shown while rolling — and never part of parsing, rolling, resolution, Recent Rolls or any external export.
/// </summary>
public class TableDescriptionTests
{
    private const string Unicode = "Roll when the party’s lantern gutters 🕯️ — “quietly”.\n雨の日には +1。";

    private static RollableTable D20(long collectionId, string name = "Weather", string description = "") => new()
    {
        CollectionId = collectionId,
        Name = name,
        Dice = DiceExpression.Parse("d20"),
        Description = description,
        ResultSets = [Set("", E(1, 10, "Clear"), E(11, 20, "Storm"))],
    };

    // ---- normalization (Review/Edit only) --------------------------------------------------------------------------------

    [Theory]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(" \r\n\t \n ", "")]
    [InlineData("a\r\nb\rc\nd", "a\nb\nc\nd")]
    [InlineData("  Roll  on   entering\r\n\r\n  a region.  \r\n", "Roll  on   entering\n\n  a region.")]
    [InlineData("ﬁrst light", "ﬁrst light")]
    public void Saving_normalizes_line_breaks_and_trims_the_whole_value_but_keeps_everything_inside(string typed, string saved)
    {
        Assert.Equal(saved, TableDescription.Normalize(typed));
    }

    [Fact]
    public void A_draft_carries_the_description_both_ways_and_saves_it_normalized()
    {
        var draft = TableImportDraft.FromTable(D20(1, description: "Apply +1\nif friendly."));
        Assert.Equal("Apply +1\nif friendly.", draft.Description);

        draft.Description = "  Apply +2\r\nif friendly.  ";
        Assert.True(draft.TryBuildTable(1, out var table, out _));
        Assert.Equal("Apply +2\nif friendly.", table!.Description);
    }

    [Fact]
    public void The_limit_is_2000_characters_after_normalizing()
    {
        Assert.Equal(2000, TableDescription.MaxLength);
        var draft = TableImportDraft.FromTable(D20(1));

        draft.Description = new string('x', 2000);
        Assert.True(draft.TryBuildTable(1, out var table, out _));
        Assert.Equal(2000, table!.Description.Length);

        draft.Description = "  " + new string('x', 2000) + "\r\n"; // trimmed first, so still exactly 2,000
        Assert.True(draft.TryBuildTable(1, out _, out _));

        draft.Description = new string('x', 2001);
        Assert.False(draft.TryBuildTable(1, out _, out var errors));
        Assert.Contains(TableDescription.TooLongMessage, errors);
    }

    [Fact]
    public void A_pasted_table_starts_with_no_description_even_when_the_text_has_prose()
    {
        var draft = TableTextParser.Parse("D6 WEATHER\nRoll when entering a new region or when the weather changes.\n1-3 Clear\n4-6 Rain");
        Assert.Equal("", draft.Description);
    }

    [Fact]
    public void A_description_changes_nothing_about_resolution_or_validation()
    {
        var plain = D20(1);
        var described = D20(1, description: "Roll 1–10 for “Clear”. Apply +1 when windy.");
        Assert.Equal(TableValidator.Validate(plain).Count(), TableValidator.Validate(described).Count());
        for (var roll = 1; roll <= 20; roll++)
            Assert.Equal(TableResolver.Resolve(plain, roll).Results.Select(r => (r.Status, r.Entry?.Text)),
                TableResolver.Resolve(described, roll).Results.Select(r => (r.Status, r.Entry?.Text)));
    }

    // ---- persistence ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("Roll when entering a new region\nor when the weather changes.\n\nTwice at night.")]
    [InlineData(Unicode)]
    public void A_description_is_saved_and_read_back_exactly(string description)
    {
        using var temp = new TempDatabase();
        long id;
        using (var db = temp.Open())
            id = db.SaveTable(D20(db.CreateCollection("C").Id, description: description)).Id;

        using var reopened = temp.Open();
        Assert.Equal(description, reopened.LoadTable(id)!.Description);
    }

    [Fact]
    public void Exactly_2000_characters_are_kept_and_the_database_refuses_more()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = db.CreateCollection("C").Id;
        var longest = string.Concat(Enumerable.Repeat("Roll it. ", 250))[..2000];

        var id = db.SaveTable(D20(c, description: longest)).Id;
        Assert.Equal(longest, db.LoadTable(id)!.Description);

        Assert.Throws<SqliteException>(() => db.SaveTable(D20(c, "Too long", new string('x', 2001))));
        Assert.Equal(longest, db.LoadTable(id)!.Description);                                     // the failed save changed nothing
    }

    [Fact]
    public void Editing_the_description_saves_it_and_editing_anything_else_keeps_it()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = db.CreateCollection("C");
        var id = db.SaveTable(D20(c.Id, description: "Roll at dawn.")).Id;
        RollableTable? saved = null;

        var rename = new ReviewViewModel(TableImportDraft.FromTable(db.LoadTable(id)!), c, db, t => saved = t, () => { });
        Assert.Equal("Roll at dawn.", rename.Description);
        rename.TableName = "Weather (coast)";
        rename.ClampResultsToRange = true;
        rename.SaveCommand.Execute(null);
        Assert.Equal(("Weather (coast)", "Roll at dawn."), (saved!.Name, db.LoadTable(id)!.Description));

        var edit = new ReviewViewModel(TableImportDraft.FromTable(db.LoadTable(id)!), c, db, t => saved = t, () => { });
        edit.Description = "  Roll at dusk.\r\nTwice in winter.  ";
        edit.SaveCommand.Execute(null);
        Assert.Equal("Roll at dusk.\nTwice in winter.", db.LoadTable(id)!.Description);
        Assert.Equal("Roll at dusk.\nTwice in winter.", saved!.Description);                       // what is rolled next is what was stored

        var clear = new ReviewViewModel(TableImportDraft.FromTable(db.LoadTable(id)!), c, db, t => saved = t, () => { });
        clear.Description = " \r\n  ";
        clear.SaveCommand.Execute(null);
        Assert.Equal("", db.LoadTable(id)!.Description);
    }

    [Fact]
    public void A_new_table_from_a_paste_starts_empty_and_saves_what_is_typed()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = db.CreateCollection("C");
        RollableTable? saved = null;

        var review = new ReviewViewModel(TableTextParser.Parse("D6 WEATHER\n1-3 Clear\n4-6 Rain"), c, db, t => saved = t, () => { });
        Assert.Equal("", review.Description);
        review.SaveCommand.Execute(null);
        Assert.Equal("", db.LoadTable(saved!.Id)!.Description);

        var second = new ReviewViewModel(TableTextParser.Parse("D6 WIND\n1-3 Calm\n4-6 Gale"), c, db, t => saved = t, () => { });
        second.Description = Unicode;
        second.SaveCommand.Execute(null);
        Assert.Equal(Unicode, db.LoadTable(saved!.Id)!.Description);
    }

    [Fact]
    public void A_description_over_the_limit_keeps_save_unavailable()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = db.CreateCollection("C");
        var review = new ReviewViewModel(TableTextParser.Parse("D6 WEATHER\n1-3 Clear\n4-6 Rain"), c, db, _ => { }, () => { });
        Assert.True(review.CanSave);

        review.Description = new string('x', 2001);
        Assert.False(review.CanSave);
        review.Description = new string('x', 2000);
        Assert.True(review.CanSave);
    }

    [Fact]
    public void Review_cleanup_never_touches_the_description()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = db.CreateCollection("C");
        var review = new ReviewViewModel(TableTextParser.Parse("D6  WEATHER\n1-3 Clear\n4-6 Rain"), c, db, _ => { }, () => { });
        review.TableName = "Weather  ﬁelds";
        review.Description = "Use  ﬁrst  light  only.";

        review.NormalizeTextCommand.Execute(null);
        review.RemoveEmptyRowsCommand.Execute(null);

        Assert.Equal("Weather fields", review.TableName);                                           // cleanup did run
        Assert.Equal("Use  ﬁrst  light  only.", review.Description);
    }

    // ---- schema 10 --------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_schema_is_version_10_with_a_not_null_text_description_defaulting_to_empty()
    {
        Assert.Equal(10, DatabaseMigrations.CurrentVersion); // pinned: bump deliberately with each migration

        using var temp = new TempDatabase();
        using (temp.Open()) { }
        using var raw = Raw(temp.Path);
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "SELECT type, \"notnull\", dflt_value FROM pragma_table_info('Tables') WHERE name = 'Description'";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(("TEXT", 1L, "''"), (reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
    }

    [Fact]
    public void An_rc25_version_9_database_upgrades_with_a_backup_every_table_empty_and_everything_else_unchanged()
    {
        using var temp = new TempDatabase();
        long collectionId;
        string before;
        List<RollHistoryItem> history;
        using (var db = temp.Open())
        {
            collectionId = SeedMythic(db).Id;
            db.AddRollHistory(new RollSnapshot(null, "Gone", "d20", 14, "High", 3, null));
            before = Describe(db, collectionId);
            history = db.GetRollHistory().ToList();
        }
        // Make it a real RC25 file: version 9, with no Tables.Description column.
        Execute(temp.Path, "ALTER TABLE Tables DROP COLUMN Description; PRAGMA user_version = 9");

        using (var upgraded = temp.Open())
        {
            Assert.Equal(before, Describe(upgraded, collectionId));                                // every table, folder, row, link and style
            Assert.All(upgraded.GetTableSummaries(collectionId), t => Assert.Equal("", upgraded.LoadTable(t.Id)!.Description));
            Assert.Equal(history, upgraded.GetRollHistory());
        }

        Assert.Contains(DatabaseBackup.Existing(temp.Path), p => p.EndsWith(".pre-v10-from-v9.backup.db", StringComparison.Ordinal));
        Assert.Equal(0, Count(temp.Path, "SELECT COUNT(*) FROM Tables WHERE Description <> ''"));
        using var check = Raw(temp.Path);
        Assert.Equal(10, DatabaseMigrations.GetVersion(check));
    }

    [Fact]
    public void A_version_10_database_is_newer_than_rc25_understands_so_rc25_refuses_it()
    {
        // RC25's CurrentVersion is 9; its AppDatabase refuses anything newer (DatabaseTooNewException) instead of opening a
        // database whose descriptions it cannot show. The same rule, seen from this build, one version ahead:
        using var temp = new TempDatabase();
        using (temp.Open()) { }
        using (var raw = Raw(temp.Path))
            Assert.True(DatabaseMigrations.GetVersion(raw) > 9);
        Execute(temp.Path, "PRAGMA user_version = 11");

        var ex = Assert.Throws<DatabaseTooNewException>(() => temp.Open());
        Assert.Equal((11, 10), (ex.FileVersion, ex.SupportedVersion));
        using var after = Raw(temp.Path);
        Assert.Equal(11, DatabaseMigrations.GetVersion(after));                                    // refused untouched
        Assert.Empty(DatabaseBackup.Existing(temp.Path));
    }

    // ---- rolling ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void Each_step_shows_its_own_tables_description_and_rolling_is_unchanged()
    {
        var weather = D20(1, description: "Roll on entering a new region.");
        weather.Id = 1;
        var storm = D20(1, "Storm", "");
        storm.Id = 2;
        weather.ResultSets[0].Entries[1].LinkedTableId = 2;
        var rolled = new List<RollSnapshot>();
        var session = new RollViewModel(weather, new FixedDice(15), id => id == 2 ? storm : null, rolled: rolled.Add);

        Assert.Equal(("Roll on entering a new region.", true), (session.Current.Description, session.Current.HasDescription));
        session.RollCommand.Execute(null);
        Assert.Equal("Storm", Assert.Single(session.Results).Text);
        Assert.DoesNotContain("region", rolled[0].ResultText);                                       // Recent Rolls keeps only what was rolled

        Assert.Single(session.Results).FollowCommand!.Execute(null);
        Assert.Equal(("", false), (session.Current.Description, session.Current.HasDescription));
        Assert.True(session.Steps[0].HasDescription);                                                // the first step keeps its own
    }

    [Fact]
    public void Recent_rolls_never_include_the_description()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var c = db.CreateCollection("C").Id;
        db.SaveTable(D20(c, description: "SECRET-NOTE"));
        var main = new MainViewModel(db, new FixedDice(4));
        main.SelectedTable = main.Tables.Single();
        ((RollViewModel)main.Current!).RollCommand.Execute(null);

        var recent = Assert.Single(main.RecentRolls);
        Assert.DoesNotContain("SECRET-NOTE", recent.FullText + recent.Detail + recent.RollDisplay + recent.TableName);
        Assert.DoesNotContain("SECRET-NOTE", main.Tables.Single().ToString());
    }

    // ---- external exports: unchanged ---------------------------------------------------------------------------------------

    public static TheoryData<string> ExportShapes => ["d20", "2d6", "d66", "d100", "d20+2"];

    [Theory]
    [MemberData(nameof(ExportShapes))]
    public void Every_external_export_is_identical_with_and_without_a_description(string dice)
    {
        RollableTable Build(string description)
        {
            var d = DiceExpression.Parse(dice);
            return new RollableTable
            {
                Name = "Omens", Dice = d, Description = description, ClampResultsToRange = !d.IsD66,
                ResultSets = [Set("Sky", E(d.Min, d.Max, "Clear\nand bright")), Set("Ground", E(d.Min, d.Max, "Mud"))],
            };
        }

        var plain = Build("");
        var described = Build("Roll at dawn.\nApply +1 if “windy” 🌬️.");
        for (var s = 0; s < 2; s++)
        {
            Assert.Equal(TableTextExporter.Export(plain, plain.ResultSets[s]), TableTextExporter.Export(described, described.ResultSets[s]));
            Assert.Equal(TableTextExporter.Export(plain, plain.ResultSets[s], TableTextSeparator.Space),
                TableTextExporter.Export(described, described.ResultSets[s], TableTextSeparator.Space));
            Assert.Equal(TableTextExporter.ExportRows(plain, plain.ResultSets[s]), TableTextExporter.ExportRows(described, described.ResultSets[s]));

            Assert.True(FoundryTableExporter.TryExport(plain, plain.ResultSets[s], out var f1, out var e1), e1);
            Assert.True(FoundryTableExporter.TryExport(described, described.ResultSets[s], out var f2, out _));
            Assert.Equal((f1!.Name, f1.Json, string.Join("|", f1.Warnings)), (f2!.Name, f2.Json, string.Join("|", f2.Warnings)));

            var ok1 = TablesPlusTableExporter.TryExport(plain, plain.ResultSets[s], out var t1, out var te1);
            var ok2 = TablesPlusTableExporter.TryExport(described, described.ResultSets[s], out var t2, out var te2);
            Assert.Equal((ok1, te1, t1?.Name, t1?.Json, string.Join("|", t1?.Warnings ?? [])),
                (ok2, te2, t2?.Name, t2?.Json, string.Join("|", t2?.Warnings ?? [])));
        }
        Assert.DoesNotContain("dawn", TableTextExporter.Export(described, described.ResultSets[0]));
    }

    [Fact]
    public void The_roll_screens_export_menu_copies_the_same_text_with_or_without_a_description()
    {
        List<string> CopyAll(string description)
        {
            var copied = new List<string>();
            var table = D20(1, description: description);
            var session = new RollViewModel(table, new FixedDice(3), copyText: copied.Add);
            session.CopyTableTextCommand.Execute(null);
            session.CopyTableTextSpacesCommand.Execute(null);
            session.CopyForSojourCommand.Execute(null);
            session.CopyFoundryJsonCommand.Execute(null);
            session.CopyTablesPlusJsonCommand.Execute(null);
            Assert.False(session.HasExportWarning);
            return copied;
        }

        var plain = CopyAll("");
        Assert.Equal(5, plain.Count);
        Assert.Equal(plain, CopyAll("Roll at dawn.\nApply +1 when windy."));
    }
}
