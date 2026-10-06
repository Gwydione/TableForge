using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using TableForge.Domain;

namespace TableForge.Data;

/// <summary>SQLite persistence using direct parameterized SQL. One instance owns one open connection.</summary>
public sealed class AppDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>Developer/test override: a folder to use instead of %LOCALAPPDATA%\TableForge for ALL of TableForge's data (database, dice choice, WebView2 profile).</summary>
    public const string DataFolderVariable = "TABLEFORGE_DATA_DIR";

    public static string DefaultPath => Path.Combine(ResolveDataFolder(Environment.GetEnvironmentVariable(DataFolderVariable)), "tableforge.db");

    /// <summary>The data folder: <paramref name="overrideFolder"/> when one is given, otherwise the normal per-user location.</summary>
    public static string ResolveDataFolder(string? overrideFolder) =>
        string.IsNullOrWhiteSpace(overrideFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TableForge")
            : Path.GetFullPath(overrideFolder.Trim());

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
            Upgrade(path);
        }
        catch
        {
            _connection.Dispose(); // don't leak the file handle if the database can't be used
            throw;
        }
    }

    /// <summary>
    /// Brings an older database up to date. A newer one is refused untouched; an existing older one is first copied
    /// (<see cref="DatabaseBackup"/>), and if that copy cannot be made nothing is updated. A brand-new, empty database has
    /// nothing to protect and is simply created.
    /// </summary>
    private void Upgrade(string path)
    {
        var version = DatabaseMigrations.GetVersion(_connection);
        if (version > DatabaseMigrations.CurrentVersion) throw new DatabaseTooNewException(version, DatabaseMigrations.CurrentVersion);
        if (version == DatabaseMigrations.CurrentVersion) return;
        if (version == 0)
        {
            DatabaseMigrations.Apply(_connection);
            return;
        }

        var backup = DatabaseBackup.Create(_connection, path, version, DatabaseMigrations.CurrentVersion);
        try
        {
            DatabaseMigrations.Apply(_connection);
        }
        catch (Exception ex) when (ex is not DatabaseTooNewException)
        {
            throw new DatabaseMigrationException(backup, ex);
        }
        DatabaseBackup.Prune(path);
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

    /// <summary>
    /// The name an imported Collection called <paramref name="name"/> gets: the name itself if no Collection has it, otherwise
    /// "name (2)", "name (3)"... — the lowest number free. Compared the way the database compares names (NOCASE). A name that
    /// already ends in "(2)" is not read specially: it simply gets another suffix.
    /// </summary>
    public string UniqueCollectionName(string name)
    {
        using var tx = _connection.BeginTransaction();
        return UniqueCollectionName(tx, name);
    }

    private string UniqueCollectionName(SqliteTransaction tx, string name)
    {
        using var taken = Command(tx, "SELECT 1 FROM Collections WHERE Name = $name COLLATE NOCASE LIMIT 1");
        var parameter = taken.Parameters.AddWithValue("$name", name);
        var candidate = name;
        for (var n = 2; taken.ExecuteScalar() is not null; n++)
            parameter.Value = candidate = $"{name} ({n})";
        return candidate;
    }

    /// <summary>
    /// Import Collection: creates ONE new Collection holding everything in <paramref name="portable"/> (already fully validated by
    /// <see cref="Portable.PortableCollectionReader"/>), in a single transaction, and returns it. It never touches an existing
    /// Collection; any failure before the commit leaves the database exactly as it was. The new Collection's name is made unique
    /// (<see cref="UniqueCollectionName(string)"/>). Tables are inserted first, in file order, so every link can then be stored
    /// with its destination's new id — including links to a later table, to the table itself, and links that form a cycle.
    /// </summary>
    public Collection ImportCollection(Portable.PortableCollection portable)
    {
        using var tx = _connection.BeginTransaction();
        var now = DateTime.UtcNow;
        var collection = new Collection { Name = UniqueCollectionName(tx, portable.Name), CreatedUtc = now };
        using (var insert = Command(tx, "INSERT INTO Collections (Name, CreatedUtc) VALUES ($name, $created); SELECT last_insert_rowid();"))
        {
            insert.Parameters.AddWithValue("$name", collection.Name);
            insert.Parameters.AddWithValue("$created", FormatUtc(now));
            collection.Id = (long)insert.ExecuteScalar()!;
        }

        var folderIds = new List<long>();
        foreach (var name in portable.Folders)
        {
            using var insert = Command(tx, "INSERT INTO Folders (CollectionId, Name) VALUES ($collection, $name); SELECT last_insert_rowid();");
            insert.Parameters.AddWithValue("$collection", collection.Id);
            insert.Parameters.AddWithValue("$name", name);
            folderIds.Add((long)insert.ExecuteScalar()!);
        }

        // Pass A: every table row, so each file position has its new id before any entry refers to it.
        var tableIds = new List<long>();
        foreach (var item in portable.Tables)
        {
            tableIds.Add(InsertTableRow(tx, new RollableTable
            {
                CollectionId = collection.Id,
                Name = item.Table.Name,
                Dice = item.Table.Dice,
                FolderId = item.FolderIndex is { } f ? folderIds[f] : null,
                ClampResultsToRange = item.Table.ClampResultsToRange,
                Description = item.Table.Description,
                CreatedUtc = now,
                UpdatedUtc = now,
            }));
        }

        // Pass B: result sets and entries, with each link translated from a file position to the new table id.
        for (var t = 0; t < portable.Tables.Count; t++)
            InsertResultSets(tx, tableIds[t], portable.Tables[t].Table.ResultSets, link => link is { } position ? tableIds[(int)position] : null);

        tx.Commit();
        return collection;
    }

    /// <summary>How many entries in OTHER Collections link to a table in this one (only a damaged database has any).</summary>
    public int CountLinksIntoCollection(long collectionId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT COUNT(*)
            FROM Entries e JOIN ResultSets r ON r.Id = e.ResultSetId JOIN Tables source ON source.Id = r.TableId
            JOIN Tables target ON target.Id = e.LinkedTableId
            WHERE target.CollectionId = $c AND source.CollectionId <> $c
            """;
        cmd.Parameters.AddWithValue("$c", collectionId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Deletes a Collection with all its folders, tables, result sets and entries, in one transaction: any failure rolls it all
    /// back. Other Collections are never changed, except that an entry elsewhere linking to one of these tables (which only a
    /// damaged database can have) keeps the destination's name as an unresolved link, exactly as <see cref="DeleteTable"/> does.
    /// Recent Rolls from these tables stay as readable records (their table id becomes NULL, as the schema says).
    /// </summary>
    /// <returns>False if no such Collection exists.</returns>
    public bool DeleteCollection(long collectionId)
    {
        using var tx = _connection.BeginTransaction();

        using (var find = Command(tx, "SELECT COUNT(*) FROM Collections WHERE Id = $id"))
        {
            find.Parameters.AddWithValue("$id", collectionId);
            if (Convert.ToInt64(find.ExecuteScalar()) == 0) return false;
        }

        // Every link to one of these tables becomes unresolved first. Links inside the Collection are about to be deleted
        // anyway; links from outside it keep their intent, and none is left pointing at a table that no longer exists.
        using (var unlink = Command(tx,
            """
            UPDATE Entries
            SET UnresolvedLinkName = (SELECT t.Name FROM Tables t WHERE t.Id = Entries.LinkedTableId), LinkedTableId = NULL
            WHERE LinkedTableId IN (SELECT Id FROM Tables WHERE CollectionId = $id)
            """))
        {
            unlink.Parameters.AddWithValue("$id", collectionId);
            unlink.ExecuteNonQuery();
        }

        foreach (var sql in new[]
                 {
                     "DELETE FROM Tables WHERE CollectionId = $id", // result sets and entries cascade; Recent Rolls are set to NULL
                     "DELETE FROM Folders WHERE CollectionId = $id",
                     "DELETE FROM Collections WHERE Id = $id",
                 })
        {
            using var delete = Command(tx, sql);
            delete.Parameters.AddWithValue("$id", collectionId);
            delete.ExecuteNonQuery();
        }

        tx.Commit();
        return true;
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
        var isNew = table.Id == 0;
        ValidateOrNormalizeFolder(tx, table, isNew);

        if (isNew)
        {
            table.CreatedUtc = now;
            table.Id = InsertTableRow(tx, table);
        }
        else
        {
            using var update = Command(tx,
                """
                UPDATE Tables
                SET CollectionId = $collection, Name = $name, DiceCount = $count, DiceSides = $sides, DiceModifier = $modifier, DiceConvention = $convention, FolderId = $folder, ClampResultsToRange = $clamp, Description = $description, UpdatedUtc = $updated
                WHERE Id = $id
                """);
            AddTableParameters(update, table);
            update.Parameters.AddWithValue("$id", table.Id);
            if (update.ExecuteNonQuery() == 0) throw new InvalidOperationException($"Table {table.Id} does not exist.");

            using var clear = Command(tx, "DELETE FROM ResultSets WHERE TableId = $id"); // entries cascade
            clear.Parameters.AddWithValue("$id", table.Id);
            clear.ExecuteNonQuery();
        }

        InsertResultSets(tx, table.Id, table.ResultSets, link => link);
        tx.Commit();
        return table;
    }

    /// <summary>Inserts one Tables row (CreatedUtc and UpdatedUtc as the table holds them) and returns its new id.</summary>
    private long InsertTableRow(SqliteTransaction tx, RollableTable table)
    {
        using var insert = Command(tx,
            """
            INSERT INTO Tables (CollectionId, Name, DiceCount, DiceSides, DiceModifier, DiceConvention, FolderId, ClampResultsToRange, Description, CreatedUtc, UpdatedUtc)
            VALUES ($collection, $name, $count, $sides, $modifier, $convention, $folder, $clamp, $description, $created, $updated);
            SELECT last_insert_rowid();
            """);
        AddTableParameters(insert, table);
        insert.Parameters.AddWithValue("$created", FormatUtc(table.CreatedUtc));
        return (long)insert.ExecuteScalar()!;
    }

    /// <summary>
    /// Inserts a table's result sets and their entries in list order, assigning ids and sort orders on the passed objects.
    /// Each entry's <see cref="TableEntry.LinkedTableId"/> is stored as <paramref name="link"/> maps it (unchanged for a
    /// normal save; from a file position to a new table id for an import).
    /// </summary>
    private void InsertResultSets(SqliteTransaction tx, long tableId, List<ResultSet> resultSets, Func<long?, long?> link)
    {
        for (var s = 0; s < resultSets.Count; s++)
        {
            var set = resultSets[s];
            set.SortOrder = s;
            using (var insertSet = Command(tx,
                "INSERT INTO ResultSets (TableId, Name, SortOrder) VALUES ($table, $name, $order); SELECT last_insert_rowid();"))
            {
                insertSet.Parameters.AddWithValue("$table", tableId);
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
                    INSERT INTO Entries (ResultSetId, MinValue, MaxValue, DisplayText, DisplayRange, LinkedTableId, UnresolvedLinkName, SortOrder, TextFormatting)
                    VALUES ($set, $min, $max, $text, $range, $linked, $unresolved, $order, $formatting);
                    SELECT last_insert_rowid();
                    """);
                insertEntry.Parameters.AddWithValue("$set", set.Id);
                insertEntry.Parameters.AddWithValue("$min", entry.Min);
                insertEntry.Parameters.AddWithValue("$max", entry.Max);
                insertEntry.Parameters.AddWithValue("$text", entry.Text);
                insertEntry.Parameters.AddWithValue("$range", (object?)entry.DisplayRange ?? DBNull.Value);
                insertEntry.Parameters.AddWithValue("$linked", (object?)link(entry.LinkedTableId) ?? DBNull.Value);
                insertEntry.Parameters.AddWithValue("$unresolved", (object?)entry.UnresolvedLinkName ?? DBNull.Value);
                insertEntry.Parameters.AddWithValue("$order", e);
                insertEntry.Parameters.AddWithValue("$formatting", (object?)entry.Styles.Serialize(entry.Text) ?? DBNull.Value);
                entry.Id = (long)insertEntry.ExecuteScalar()!;
            }
        }
    }

    public RollableTable? LoadTable(long id)
    {
        RollableTable? table;
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = "SELECT Id, CollectionId, Name, DiceCount, DiceSides, DiceModifier, CreatedUtc, UpdatedUtc, DiceConvention, FolderId, ClampResultsToRange, Description FROM Tables WHERE Id = $id";
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            table = new RollableTable
            {
                Id = reader.GetInt64(0),
                CollectionId = reader.GetInt64(1),
                Name = reader.GetString(2),
                Dice = new DiceExpression(reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), (RollConvention)reader.GetInt32(8)),
                CreatedUtc = ParseUtc(reader.GetString(6)),
                UpdatedUtc = ParseUtc(reader.GetString(7)),
                FolderId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                ClampResultsToRange = reader.GetInt64(10) != 0,
                Description = reader.GetString(11),
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
                       e.LinkedTableId, e.UnresolvedLinkName, e.SortOrder, e.TextFormatting
                FROM Entries e
                JOIN ResultSets r ON r.Id = e.ResultSetId
                WHERE r.TableId = $id
                ORDER BY e.SortOrder, e.Id
                """;
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var text = reader.GetString(4);
                sets[reader.GetInt64(1)].Entries.Add(new TableEntry
                {
                    Id = reader.GetInt64(0),
                    Min = reader.GetInt32(2),
                    Max = reader.GetInt32(3),
                    Text = text,
                    // Malformed or stale formatting reads as none: the row loads, plain, and never stops the table loading.
                    Styles = TextStyles.Parse(reader.IsDBNull(9) ? null : reader.GetString(9), text),
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
        cmd.CommandText =
            """
            SELECT t.Id, t.Name, t.DiceCount, t.DiceSides, t.DiceModifier, t.DiceConvention, t.FolderId, f.Name
            FROM Tables t LEFT JOIN Folders f ON f.Id = t.FolderId
            WHERE t.CollectionId = $c
            ORDER BY t.Name COLLATE NOCASE, t.Id
            """;
        cmd.Parameters.AddWithValue("$c", collectionId);
        using var reader = cmd.ExecuteReader();
        var list = new List<TableSummary>();
        while (reader.Read()) list.Add(ReadTableSummary(reader));
        return list;
    }

    // ---- Folders -------------------------------------------------------------------------------

    public Folder CreateFolder(long collectionId, string name)
    {
        var trimmed = RequireFolderName(name);
        using var tx = _connection.BeginTransaction();
        EnsureUniqueFolderName(tx, collectionId, trimmed, excludeId: null);

        using var insert = Command(tx, "INSERT INTO Folders (CollectionId, Name) VALUES ($collection, $name); SELECT last_insert_rowid();");
        insert.Parameters.AddWithValue("$collection", collectionId);
        insert.Parameters.AddWithValue("$name", trimmed);
        var id = (long)insert.ExecuteScalar()!;
        tx.Commit();
        return new Folder { Id = id, CollectionId = collectionId, Name = trimmed };
    }

    public void RenameFolder(long folderId, string name)
    {
        var trimmed = RequireFolderName(name);
        using var tx = _connection.BeginTransaction();

        long collectionId;
        using (var find = Command(tx, "SELECT CollectionId FROM Folders WHERE Id = $id"))
        {
            find.Parameters.AddWithValue("$id", folderId);
            if (find.ExecuteScalar() is not long found) throw new InvalidOperationException($"Folder {folderId} does not exist.");
            collectionId = found;
        }
        EnsureUniqueFolderName(tx, collectionId, trimmed, excludeId: folderId);

        using var update = Command(tx, "UPDATE Folders SET Name = $name WHERE Id = $id");
        update.Parameters.AddWithValue("$name", trimmed);
        update.Parameters.AddWithValue("$id", folderId);
        update.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>Deletes a folder. Its tables are never deleted: they become Unfiled first, in the same transaction.</summary>
    /// <returns>False if no such folder exists.</returns>
    public bool DeleteFolder(long folderId)
    {
        using var tx = _connection.BeginTransaction();

        using (var unfile = Command(tx, "UPDATE Tables SET FolderId = NULL WHERE FolderId = $id"))
        {
            unfile.Parameters.AddWithValue("$id", folderId);
            unfile.ExecuteNonQuery();
        }

        using var delete = Command(tx, "DELETE FROM Folders WHERE Id = $id");
        delete.Parameters.AddWithValue("$id", folderId);
        var deleted = delete.ExecuteNonQuery() > 0;
        tx.Commit();
        return deleted;
    }

    /// <summary>A collection's folders, alphabetical case-insensitive (folders never nest, so this is the whole navigation list).</summary>
    public IReadOnlyList<Folder> GetFolders(long collectionId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT Id, CollectionId, Name FROM Folders WHERE CollectionId = $c ORDER BY Name COLLATE NOCASE, Id";
        cmd.Parameters.AddWithValue("$c", collectionId);
        using var reader = cmd.ExecuteReader();
        var list = new List<Folder>();
        while (reader.Read())
            list.Add(new Folder { Id = reader.GetInt64(0), CollectionId = reader.GetInt64(1), Name = reader.GetString(2) });
        return list;
    }

    private static string RequireFolderName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Folder name is required.", nameof(name));
        return name.Trim();
    }

    /// <summary>Folder names are unique within a collection, case-insensitively; the same name is fine in a different collection.</summary>
    private void EnsureUniqueFolderName(SqliteTransaction tx, long collectionId, string name, long? excludeId)
    {
        using var check = Command(tx,
            "SELECT COUNT(*) FROM Folders WHERE CollectionId = $c AND Name = $name COLLATE NOCASE AND ($exclude IS NULL OR Id <> $exclude)");
        check.Parameters.AddWithValue("$c", collectionId);
        check.Parameters.AddWithValue("$name", name);
        check.Parameters.AddWithValue("$exclude", (object?)excludeId ?? DBNull.Value);
        if (Convert.ToInt64(check.ExecuteScalar()) > 0)
            throw new InvalidOperationException($"A folder named \"{name}\" already exists in this collection.");
    }

    /// <summary>
    /// Enforces that a table's folder always belongs to its own collection. A brand-new table (or an existing one
    /// whose collection is not changing) with a foreign folder is a plain mistake and is rejected outright. An
    /// existing table whose <see cref="RollableTable.CollectionId"/> is genuinely changing keeps working: its old
    /// folder cannot possibly belong to the new collection, so it is reset to Unfiled rather than rejected.
    /// </summary>
    private void ValidateOrNormalizeFolder(SqliteTransaction tx, RollableTable table, bool isNew)
    {
        if (table.FolderId is not { } folderId) return;

        using var find = Command(tx, "SELECT CollectionId FROM Folders WHERE Id = $id");
        find.Parameters.AddWithValue("$id", folderId);
        if (find.ExecuteScalar() is not long folderCollectionId)
            throw new InvalidOperationException($"Folder {folderId} does not exist.");

        if (folderCollectionId == table.CollectionId) return;
        if (isNew) throw new InvalidOperationException("The selected folder does not belong to this table's collection.");

        using var previous = Command(tx, "SELECT CollectionId FROM Tables WHERE Id = $id");
        previous.Parameters.AddWithValue("$id", table.Id);
        var previousCollectionId = (long)previous.ExecuteScalar()!;

        if (previousCollectionId == table.CollectionId)
            throw new InvalidOperationException("The selected folder does not belong to this table's collection.");

        table.FolderId = null; // the table's collection changed out from under its old folder; it does not follow
    }

    private static TableSummary ReadTableSummary(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1),
        new DiceExpression(reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), (RollConvention)reader.GetInt32(5)),
        reader.IsDBNull(6) ? null : reader.GetInt64(6),
        reader.IsDBNull(7) ? "Unfiled" : reader.GetString(7));

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
            SELECT t.Id, t.Name, t.DiceCount, t.DiceSides, t.DiceModifier, t.DiceConvention, t.FolderId, f.Name
            FROM Tables t LEFT JOIN Folders f ON f.Id = t.FolderId
            WHERE t.CollectionId = $c AND t.LastUsedUtc IS NOT NULL
            ORDER BY t.LastUsedUtc DESC, t.Id DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$c", collectionId);
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<TableSummary>();
        while (reader.Read()) list.Add(ReadTableSummary(reader));
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
            INSERT INTO RollHistory (TableId, TableName, DiceText, RollValue, ResultText, RolledUtc, SituationalModifier, ClampedValue)
            VALUES (
                CASE WHEN EXISTS (SELECT 1 FROM Tables WHERE Id = $table) THEN $table ELSE NULL END,
                $name, $dice, $roll, $result, $rolled, $situational, $clamped);
            SELECT last_insert_rowid(), TableId FROM RollHistory WHERE Id = last_insert_rowid();
            """))
        {
            insert.Parameters.AddWithValue("$table", (object?)snapshot.TableId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$name", snapshot.TableName);
            insert.Parameters.AddWithValue("$dice", snapshot.DiceText);
            insert.Parameters.AddWithValue("$roll", snapshot.RollValue);
            insert.Parameters.AddWithValue("$result", snapshot.ResultText);
            insert.Parameters.AddWithValue("$rolled", FormatUtc(rolledUtc));
            insert.Parameters.AddWithValue("$situational", snapshot.SituationalModifier);
            insert.Parameters.AddWithValue("$clamped", (object?)snapshot.ClampedValue ?? DBNull.Value);
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
        return new RollHistoryItem(id, tableId, snapshot.TableName, snapshot.DiceText, snapshot.RollValue, snapshot.ResultText, rolledUtc,
            snapshot.SituationalModifier, snapshot.ClampedValue);
    }

    /// <summary>Recent rolls, newest first.</summary>
    public IReadOnlyList<RollHistoryItem> GetRollHistory(int limit = RollHistoryLimit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            "SELECT Id, TableId, TableName, DiceText, RollValue, ResultText, RolledUtc, SituationalModifier, ClampedValue FROM RollHistory ORDER BY Id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        var list = new List<RollHistoryItem>();
        while (reader.Read())
            list.Add(new RollHistoryItem(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt32(4), reader.GetString(5), ParseUtc(reader.GetString(6)), reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetInt32(8)));
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
        cmd.Parameters.AddWithValue("$modifier", table.Dice.Modifier);
        cmd.Parameters.AddWithValue("$convention", (int)table.Dice.Convention);
        cmd.Parameters.AddWithValue("$folder", (object?)table.FolderId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$clamp", table.ClampResultsToRange ? 1 : 0);
        cmd.Parameters.AddWithValue("$description", table.Description);
        cmd.Parameters.AddWithValue("$updated", FormatUtc(table.UpdatedUtc));
    }

    /// <summary>All timestamps are stored as round-trip ISO 8601 UTC text.</summary>
    private static string FormatUtc(DateTime utc) => utc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseUtc(string text) =>
        DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
