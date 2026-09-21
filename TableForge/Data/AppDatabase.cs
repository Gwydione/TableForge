using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using TableForge.Domain;

namespace TableForge.Data;

/// <summary>SQLite persistence using direct parameterized SQL. One instance owns one open connection.</summary>
public sealed class AppDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TableForge", "tableforge.db");

    public AppDatabase(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            Pooling = false, // release the file handle on Dispose
        };
        _connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            _connection.Open();
            DatabaseMigrations.Apply(_connection);
        }
        catch
        {
            _connection.Dispose(); // don't leak the file handle if the database can't be used
            throw;
        }
    }

    public void Dispose() => _connection.Dispose();

    // ---- Collections -------------------------------------------------------------------------

    public Collection CreateCollection(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Collection name is required.", nameof(name));

        var collection = new Collection { Name = name.Trim(), CreatedUtc = DateTime.UtcNow };
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "INSERT INTO Collections (Name, CreatedUtc) VALUES ($name, $created); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$name", collection.Name);
        cmd.Parameters.AddWithValue("$created", FormatUtc(collection.CreatedUtc));
        collection.Id = (long)cmd.ExecuteScalar()!;
        return collection;
    }

    public IReadOnlyList<Collection> GetCollections()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, CreatedUtc FROM Collections ORDER BY Name COLLATE NOCASE, Id";
        using var reader = cmd.ExecuteReader();
        var list = new List<Collection>();
        while (reader.Read())
            list.Add(new Collection { Id = reader.GetInt64(0), Name = reader.GetString(1), CreatedUtc = ParseUtc(reader.GetString(2)) });
        return list;
    }

    // ---- Tables ------------------------------------------------------------------------------

    /// <summary>
    /// Inserts the table (Id == 0) or replaces the stored copy (Id != 0), all in one transaction.
    /// Ids of the table, result sets and entries are assigned on the passed object.
    /// Sort order is persisted from list position.
    /// </summary>
    public RollableTable SaveTable(RollableTable table)
    {
        using var tx = _connection.BeginTransaction();
        var now = DateTime.UtcNow;
        table.UpdatedUtc = now;

        if (table.Id == 0)
        {
            table.CreatedUtc = now;
            using var insert = Command(tx,
                """
                INSERT INTO Tables (CollectionId, Name, DiceCount, DiceSides, CreatedUtc, UpdatedUtc)
                VALUES ($collection, $name, $count, $sides, $created, $updated);
                SELECT last_insert_rowid();
                """);
            AddTableParameters(insert, table);
            insert.Parameters.AddWithValue("$created", FormatUtc(table.CreatedUtc));
            table.Id = (long)insert.ExecuteScalar()!;
        }
        else
        {
            using var update = Command(tx,
                """
                UPDATE Tables
                SET CollectionId = $collection, Name = $name, DiceCount = $count, DiceSides = $sides, UpdatedUtc = $updated
                WHERE Id = $id
                """);
            AddTableParameters(update, table);
            update.Parameters.AddWithValue("$id", table.Id);
            if (update.ExecuteNonQuery() == 0) throw new InvalidOperationException($"Table {table.Id} does not exist.");

            using var clear = Command(tx, "DELETE FROM ResultSets WHERE TableId = $id"); // entries cascade
            clear.Parameters.AddWithValue("$id", table.Id);
            clear.ExecuteNonQuery();
        }

        for (var s = 0; s < table.ResultSets.Count; s++)
        {
            var set = table.ResultSets[s];
            set.SortOrder = s;
            using (var insertSet = Command(tx,
                "INSERT INTO ResultSets (TableId, Name, SortOrder) VALUES ($table, $name, $order); SELECT last_insert_rowid();"))
            {
                insertSet.Parameters.AddWithValue("$table", table.Id);
                insertSet.Parameters.AddWithValue("$name", set.Name);
                insertSet.Parameters.AddWithValue("$order", s);
                set.Id = (long)insertSet.ExecuteScalar()!;
            }

            for (var e = 0; e < set.Entries.Count; e++)
            {
                var entry = set.Entries[e];
                entry.SortOrder = e;
                using var insertEntry = Command(tx,
                    """
                    INSERT INTO Entries (ResultSetId, MinValue, MaxValue, DisplayText, DisplayRange, LinkedTableId, UnresolvedLinkName, SortOrder)
                    VALUES ($set, $min, $max, $text, $range, $linked, $unresolved, $order);
                    SELECT last_insert_rowid();
                    """);
                insertEntry.Parameters.AddWithValue("$set", set.Id);
                insertEntry.Parameters.AddWithValue("$min", entry.Min);
                insertEntry.Parameters.AddWithValue("$max", entry.Max);
                insertEntry.Parameters.AddWithValue("$text", entry.Text);
                insertEntry.Parameters.AddWithValue("$range", (object?)entry.DisplayRange ?? DBNull.Value);
                insertEntry.Parameters.AddWithValue("$linked", (object?)entry.LinkedTableId ?? DBNull.Value);
                insertEntry.Parameters.AddWithValue("$unresolved", (object?)entry.UnresolvedLinkName ?? DBNull.Value);
                insertEntry.Parameters.AddWithValue("$order", e);
                entry.Id = (long)insertEntry.ExecuteScalar()!;
            }
        }

        tx.Commit();
        return table;
    }

    public RollableTable? LoadTable(long id)
    {
        RollableTable? table;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, CollectionId, Name, DiceCount, DiceSides, CreatedUtc, UpdatedUtc FROM Tables WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            table = new RollableTable
            {
                Id = reader.GetInt64(0),
                CollectionId = reader.GetInt64(1),
                Name = reader.GetString(2),
                Dice = new DiceExpression(reader.GetInt32(3), reader.GetInt32(4)),
                CreatedUtc = ParseUtc(reader.GetString(5)),
                UpdatedUtc = ParseUtc(reader.GetString(6)),
            };
        }

        var sets = new Dictionary<long, ResultSet>();
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, Name, SortOrder FROM ResultSets WHERE TableId = $id ORDER BY SortOrder, Id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var set = new ResultSet { Id = reader.GetInt64(0), Name = reader.GetString(1), SortOrder = reader.GetInt32(2) };
                sets[set.Id] = set;
                table.ResultSets.Add(set);
            }
        }

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT e.Id, e.ResultSetId, e.MinValue, e.MaxValue, e.DisplayText, e.DisplayRange,
                       e.LinkedTableId, e.UnresolvedLinkName, e.SortOrder
                FROM Entries e
                JOIN ResultSets r ON r.Id = e.ResultSetId
                WHERE r.TableId = $id
                ORDER BY e.SortOrder, e.Id
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                sets[reader.GetInt64(1)].Entries.Add(new TableEntry
                {
                    Id = reader.GetInt64(0),
                    Min = reader.GetInt32(2),
                    Max = reader.GetInt32(3),
                    Text = reader.GetString(4),
                    DisplayRange = reader.IsDBNull(5) ? null : reader.GetString(5),
                    LinkedTableId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
                    UnresolvedLinkName = reader.IsDBNull(7) ? null : reader.GetString(7),
                    SortOrder = reader.GetInt32(8),
                });
            }
        }

        return table;
    }

    public IReadOnlyList<TableSummary> GetTableSummaries(long collectionId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, DiceCount, DiceSides FROM Tables WHERE CollectionId = $c ORDER BY Name COLLATE NOCASE, Id";
        cmd.Parameters.AddWithValue("$c", collectionId);
        using var reader = cmd.ExecuteReader();
        var list = new List<TableSummary>();
        while (reader.Read())
            list.Add(new TableSummary(reader.GetInt64(0), reader.GetString(1), new DiceExpression(reader.GetInt32(2), reader.GetInt32(3))));
        return list;
    }

    /// <summary>
    /// Deletes a table. Entries elsewhere that link to it keep the destination's last name as an
    /// unresolved link, so the intent survives. Everything happens in one transaction: any failure rolls it all back.
    /// </summary>
    /// <returns>False if no such table exists.</returns>
    public bool DeleteTable(long id)
    {
        using var tx = _connection.BeginTransaction();

        string name;
        using (var find = Command(tx, "SELECT Name FROM Tables WHERE Id = $id"))
        {
            find.Parameters.AddWithValue("$id", id);
            if (find.ExecuteScalar() is not string found) return false;
            name = found;
        }

        using (var unlink = Command(tx,
            "UPDATE Entries SET UnresolvedLinkName = $name, LinkedTableId = NULL WHERE LinkedTableId = $id"))
        {
            unlink.Parameters.AddWithValue("$name", name);
            unlink.Parameters.AddWithValue("$id", id);
            unlink.ExecuteNonQuery();
        }

        using (var delete = Command(tx, "DELETE FROM Tables WHERE Id = $id")) // result sets and entries cascade
        {
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }

        tx.Commit();
        return true;
    }

    // ---- recent tables ------------------------------------------------------------------------

    public const int RecentTablesLimit = 5;

    /// <summary>Records that a table was opened for rolling. Saving or renaming a table does not change its recency.</summary>
    public void MarkTableUsed(long id)
    {
        // Strictly later than every earlier mark, even if the clock hasn't ticked, so ordering is never ambiguous.
        var now = DateTime.UtcNow;
        using (var latest = _connection.CreateCommand())
        {
            latest.CommandText = "SELECT MAX(LastUsedUtc) FROM Tables";
            if (latest.ExecuteScalar() is string s && ParseUtc(s) is var previous && previous >= now)
                now = previous.AddTicks(1);
        }

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE Tables SET LastUsedUtc = $now WHERE Id = $id";
        cmd.Parameters.AddWithValue("$now", FormatUtc(now));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>The collection's most recently used tables, newest first. Current names, since only the id is remembered.</summary>
    public IReadOnlyList<TableSummary> GetRecentTables(long collectionId, int limit = RecentTablesLimit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT Id, Name, DiceCount, DiceSides FROM Tables
            WHERE CollectionId = $c AND LastUsedUtc IS NOT NULL
            ORDER BY LastUsedUtc DESC, Id DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$c", collectionId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<TableSummary>();
        while (reader.Read())
            list.Add(new TableSummary(reader.GetInt64(0), reader.GetString(1), new DiceExpression(reader.GetInt32(2), reader.GetInt32(3))));
        return list;
    }

    // ---- recent roll history ------------------------------------------------------------------

    /// <summary>Recent Rolls keeps the newest ten rolls anywhere in TableForge (not per collection).</summary>
    public const int RollHistoryLimit = 10;

    /// <summary>
    /// Stores one roll snapshot and trims history to the newest <see cref="RollHistoryLimit"/> rolls.
    /// The table id is kept only if that table still exists.
    /// </summary>
    public RollHistoryItem AddRollHistory(RollSnapshot snapshot)
    {
        using var tx = _connection.BeginTransaction();
        var rolledUtc = DateTime.UtcNow;

        long id;
        long? tableId;
        using (var insert = Command(tx,
            """
            INSERT INTO RollHistory (TableId, TableName, DiceText, RollValue, ResultText, RolledUtc)
            VALUES (
                CASE WHEN EXISTS (SELECT 1 FROM Tables WHERE Id = $table) THEN $table ELSE NULL END,
                $name, $dice, $roll, $result, $rolled);
            SELECT last_insert_rowid(), TableId FROM RollHistory WHERE Id = last_insert_rowid();
            """))
        {
            insert.Parameters.AddWithValue("$table", (object?)snapshot.TableId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$name", snapshot.TableName);
            insert.Parameters.AddWithValue("$dice", snapshot.DiceText);
            insert.Parameters.AddWithValue("$roll", snapshot.RollValue);
            insert.Parameters.AddWithValue("$result", snapshot.ResultText);
            insert.Parameters.AddWithValue("$rolled", FormatUtc(rolledUtc));
            using var reader = insert.ExecuteReader();
            reader.Read();
            id = reader.GetInt64(0);
            tableId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }

        using (var trim = Command(tx,
            "DELETE FROM RollHistory WHERE Id NOT IN (SELECT Id FROM RollHistory ORDER BY Id DESC LIMIT $keep)"))
        {
            trim.Parameters.AddWithValue("$keep", RollHistoryLimit);
            trim.ExecuteNonQuery();
        }

        tx.Commit();
        return new RollHistoryItem(id, tableId, snapshot.TableName, snapshot.DiceText, snapshot.RollValue, snapshot.ResultText, rolledUtc);
    }

    /// <summary>Recent rolls, newest first.</summary>
    public IReadOnlyList<RollHistoryItem> GetRollHistory(int limit = RollHistoryLimit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT Id, TableId, TableName, DiceText, RollValue, ResultText, RolledUtc FROM RollHistory ORDER BY Id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<RollHistoryItem>();
        while (reader.Read())
            list.Add(new RollHistoryItem(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4), reader.GetString(5), ParseUtc(reader.GetString(6))));
        return list;
    }

    /// <summary>How many entries in other tables link to this one.</summary>
    public int CountLinksTo(long id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT COUNT(*)
            FROM Entries e JOIN ResultSets r ON r.Id = e.ResultSetId
            WHERE e.LinkedTableId = $id AND r.TableId <> $id
            """;
        cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // ---- helpers -----------------------------------------------------------------------------

    private SqliteCommand Command(SqliteTransaction tx, string sql)
    {
        var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    private static void AddTableParameters(SqliteCommand cmd, RollableTable table)
    {
        cmd.Parameters.AddWithValue("$collection", table.CollectionId);
        cmd.Parameters.AddWithValue("$name", table.Name);
        cmd.Parameters.AddWithValue("$count", table.Dice.Count);
        cmd.Parameters.AddWithValue("$sides", table.Dice.Sides);
        cmd.Parameters.AddWithValue("$updated", FormatUtc(table.UpdatedUtc));
    }

    /// <summary>All timestamps are stored as round-trip ISO 8601 UTC text.</summary>
    private static string FormatUtc(DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
