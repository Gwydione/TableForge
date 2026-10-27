using Microsoft.Data.Sqlite;
using TableForge.Data;
using static TableForge.Tests.PortableFixtures;

namespace TableForge.Tests;

/// <summary>
/// The migration runner: every statement of a migration runs on its own (<see cref="DatabaseMigrations.ExecuteScript"/>), so any
/// failure is reported and the whole migration, schema version included, is rolled back — never a partly upgraded database marked
/// as upgraded. Statements are found by SQLite's own parser, never by splitting on semicolons.
/// </summary>
public class MigrationRunnerTests
{
    private static SqliteConnection Memory()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }

    private static void Run(SqliteConnection connection, string script)
    {
        using var tx = connection.BeginTransaction();
        DatabaseMigrations.ExecuteScript(connection, tx, script);
        tx.Commit();
    }

    private static string Tables(SqliteConnection connection, SqliteTransaction? tx = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT group_concat(name, ',') FROM (SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name)";
        return cmd.ExecuteScalar() as string ?? "";
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    // ---- finding statements ----------------------------------------------------------------------------------------------

    [Fact]
    public void Semicolons_in_strings_quoted_names_comments_and_trigger_bodies_are_not_statement_boundaries()
    {
        using var c = Memory();
        Run(c, """
            -- a comment; with a semicolon
            CREATE TABLE "odd;name" (Text TEXT);
            /* a block comment; also with one */
            INSERT INTO "odd;name" VALUES ('one; two');
            CREATE TABLE Log (Entry TEXT);
            CREATE TRIGGER Logged AFTER INSERT ON "odd;name" BEGIN
                INSERT INTO Log VALUES ('first;');
                INSERT INTO Log VALUES ('second');
            END;
            INSERT INTO "odd;name" VALUES ('three');
            -- only a comment is left after this
            """);

        Assert.Equal("Log,odd;name", Tables(c));
        Assert.Equal("one; two|three", Scalar(c, "SELECT group_concat(Text, '|') FROM \"odd;name\""));
        Assert.Equal("first;|second", Scalar(c, "SELECT group_concat(Entry, '|') FROM Log"));
    }

    [Fact]
    public void A_statement_can_use_what_an_earlier_statement_of_the_same_script_created()
    {
        using var c = Memory();
        Run(c, "CREATE TABLE E (V INTEGER); INSERT INTO E VALUES (1), (2); ALTER TABLE E ADD COLUMN W INTEGER NOT NULL DEFAULT 0; UPDATE E SET W = V * 10;");
        Assert.Equal(30L, Scalar(c, "SELECT SUM(W) FROM E"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  ")]
    [InlineData("-- nothing here\n/* or here */")]
    [InlineData("SELECT 1")]
    [InlineData("SELECT 1;;")]
    public void An_empty_script_a_comment_or_a_query_runs_without_changing_anything(string script)
    {
        using var c = Memory();
        Run(c, script);
        Assert.Equal("", Tables(c));
    }

    // ---- failures are reported ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The case that was silently skipped when a whole migration ran as one command: a failure after an ADD COLUMN with a CHECK
    /// (with rows in the table or without).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_failure_after_an_added_column_with_a_check_is_reported_and_nothing_after_it_runs(bool withRows)
    {
        using var c = Memory();
        Run(c, "CREATE TABLE E (V INTEGER); CREATE TABLE U (A INTEGER);" + (withRows ? " INSERT INTO E VALUES (0);" : ""));

        using (var tx = c.BeginTransaction())
        {
            var ex = Assert.Throws<SqliteException>(() => DatabaseMigrations.ExecuteScript(c, tx,
                "ALTER TABLE E ADD COLUMN Q INTEGER NOT NULL DEFAULT 0 CHECK (Q IN (0, 1)); CREATE TABLE U (X INTEGER); CREATE TABLE Z (X INTEGER);"));
            Assert.Contains("already exists", ex.Message);
            Assert.Equal("E,U", Tables(c, tx));                                  // Z never ran
        }                                                                       // not committed: rolled back

        Assert.Equal(0L, Scalar(c, "SELECT COUNT(*) FROM pragma_table_info('E') WHERE name = 'Q'"));
    }

    [Theory]
    [InlineData("CREATE TABLE A (X INTEGER); SELECT * FROM Missing; CREATE TABLE B (X INTEGER);", "no such table")]
    [InlineData("CREATE TABLE A (X INTEGER); CREATE TABEL B (X INTEGER); CREATE TABLE C (X INTEGER);", "syntax error")]
    [InlineData("CREATE TABLE A (X INTEGER CHECK (X > 0)); INSERT INTO A VALUES (-1); CREATE TABLE B (X INTEGER);", "CHECK constraint")]
    public void A_failing_statement_in_the_middle_of_a_script_stops_it_with_an_error(string script, string message)
    {
        using var c = Memory();
        using var tx = c.BeginTransaction();
        var ex = Assert.Throws<SqliteException>(() => DatabaseMigrations.ExecuteScript(c, tx, script));
        Assert.Contains(message, ex.Message);
        Assert.Equal("A", Tables(c, tx));
    }

    // ---- real migrations -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Migration 7 adds Tables.ClampResultsToRange (with a CHECK) and then RollHistory.ClampedValue. Run as one command, a failure
    /// of the second statement was skipped and the database was marked version 7 without ClampedValue.
    /// </summary>
    [Fact]
    public void A_migration_7_that_fails_in_its_second_statement_is_reported_and_leaves_the_version_6_database_as_it_was()
    {
        using var temp = new TempDatabase();
        using (var raw = Raw(temp.Path)) DatabaseMigrations.Apply(raw, upToVersion: 6);
        Execute(temp.Path, "INSERT INTO Collections (Name, CreatedUtc) VALUES ('C', '2026-01-01T00:00:00.0000000Z')");
        Execute(temp.Path, "ALTER TABLE RollHistory ADD COLUMN ClampedValue INTEGER NULL"); // in the second statement's way

        var ex = Assert.Throws<DatabaseMigrationException>(() => temp.Open());
        Assert.Contains("ClampedValue", ex.InnerException!.Message);
        Assert.Equal(6, Count(temp.Path, "PRAGMA user_version"));
        Assert.Equal(0, Count(temp.Path, "SELECT COUNT(*) FROM pragma_table_info('Tables') WHERE name = 'ClampResultsToRange'"));
        Assert.Equal(1, Count(temp.Path, "SELECT COUNT(*) FROM Collections"));
        Assert.Contains(DatabaseBackup.Existing(temp.Path), p => p.EndsWith($".pre-v{DatabaseMigrations.CurrentVersion}-from-v6.backup.db", StringComparison.Ordinal));

        Execute(temp.Path, "ALTER TABLE RollHistory DROP COLUMN ClampedValue");    // the cause is fixed: the next open upgrades
        using (temp.Open()) { }
        Assert.Equal(DatabaseMigrations.CurrentVersion, Count(temp.Path, "PRAGMA user_version"));
        Assert.Equal(1, Count(temp.Path, "SELECT COUNT(*) FROM pragma_table_info('RollHistory') WHERE name = 'ClampedValue'"));
    }

    [Fact]
    public void A_failed_migration_keeps_the_ones_before_it_and_marks_the_database_with_the_last_that_succeeded()
    {
        using var temp = new TempDatabase();
        using (var raw = Raw(temp.Path)) DatabaseMigrations.Apply(raw, upToVersion: 10);
        Execute(temp.Path, "CREATE TABLE UsedResults (Anything TEXT)");   // migration 12's first statement will fail

        Assert.Throws<DatabaseMigrationException>(() => temp.Open());
        Assert.Equal(11, Count(temp.Path, "PRAGMA user_version"));                // 11 committed on its own
        Assert.Equal(1, Count(temp.Path, "SELECT COUNT(*) FROM pragma_table_info('Tables') WHERE name = 'ManualSelection'"));
        Assert.Equal(0, Count(temp.Path, "SELECT COUNT(*) FROM pragma_table_info('Entries') WHERE name = 'RowKey'"));
    }

    /// <summary>The schema SQLite holds, as text: every table, index and trigger with the SQL that defines it.</summary>
    private static string Schema(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY type, name";
        using var reader = cmd.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read()) lines.Add($"{reader.GetString(0)} {reader.GetString(1)} on {reader.GetString(2)}: {(reader.IsDBNull(3) ? "" : reader.GetString(3))}");
        return string.Join("\n", lines);
    }

    [Fact]
    public void Every_older_version_upgrades_to_exactly_the_schema_a_new_database_gets()
    {
        string fresh;
        using (var c = Memory())
        {
            DatabaseMigrations.Apply(c);
            fresh = Schema(c);
        }
        Assert.Contains("UsedResults", fresh);
        Assert.Contains("ClampedValue", fresh);

        for (var version = 1; version < DatabaseMigrations.CurrentVersion; version++)
        {
            using var c = Memory();
            DatabaseMigrations.Apply(c, upToVersion: version);
            Assert.Equal(version, DatabaseMigrations.GetVersion(c));
            DatabaseMigrations.Apply(c);
            Assert.Equal(DatabaseMigrations.CurrentVersion, DatabaseMigrations.GetVersion(c));
            Assert.Equal(fresh, Schema(c));
        }
    }
}
