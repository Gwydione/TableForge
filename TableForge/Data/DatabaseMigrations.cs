using Microsoft.Data.Sqlite;

namespace TableForge.Data;

/// <summary>Minimal schema versioning via PRAGMA user_version. Migration N brings the database to version N.</summary>
public static class DatabaseMigrations
{
    private static readonly string[] Migrations =
    [
        // 1: initial schema
        """
        CREATE TABLE Collections (
            Id         INTEGER PRIMARY KEY AUTOINCREMENT,
            Name       TEXT NOT NULL,
            CreatedUtc TEXT NOT NULL
        );

        CREATE TABLE Tables (
            Id           INTEGER PRIMARY KEY AUTOINCREMENT,
            CollectionId INTEGER NOT NULL REFERENCES Collections(Id),
            Name         TEXT NOT NULL,
            DiceCount    INTEGER NOT NULL CHECK (DiceCount >= 1),
            DiceSides    INTEGER NOT NULL CHECK (DiceSides >= 2),
            CreatedUtc   TEXT NOT NULL,
            UpdatedUtc   TEXT NOT NULL
        );
        CREATE INDEX IX_Tables_CollectionId ON Tables(CollectionId);

        CREATE TABLE ResultSets (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            TableId   INTEGER NOT NULL REFERENCES Tables(Id) ON DELETE CASCADE,
            Name      TEXT NOT NULL DEFAULT '',
            SortOrder INTEGER NOT NULL
        );
        CREATE INDEX IX_ResultSets_TableId ON ResultSets(TableId);

        CREATE TABLE Entries (
            Id                 INTEGER PRIMARY KEY AUTOINCREMENT,
            ResultSetId        INTEGER NOT NULL REFERENCES ResultSets(Id) ON DELETE CASCADE,
            MinValue           INTEGER NOT NULL,
            MaxValue           INTEGER NOT NULL,
            DisplayText        TEXT NOT NULL,
            DisplayRange       TEXT NULL,
            LinkedTableId      INTEGER NULL REFERENCES Tables(Id),
            UnresolvedLinkName TEXT NULL,
            SortOrder          INTEGER NOT NULL,
            CHECK (MinValue <= MaxValue)
        );
        CREATE INDEX IX_Entries_ResultSetId ON Entries(ResultSetId);
        CREATE INDEX IX_Entries_LinkedTableId ON Entries(LinkedTableId);
        """,

        // 2: recent tables (LastUsedUtc) and recent roll history. History rows are snapshots: they keep the
        // table's name and the rendered result as text, and only loosely point at the table (set to NULL if it is deleted).
        """
        ALTER TABLE Tables ADD COLUMN LastUsedUtc TEXT NULL;

        CREATE TABLE RollHistory (
            Id        INTEGER PRIMARY KEY AUTOINCREMENT,
            TableId   INTEGER NULL REFERENCES Tables(Id) ON DELETE SET NULL,
            TableName TEXT NOT NULL,
            DiceText  TEXT NOT NULL,
            RollValue INTEGER NOT NULL,
            ResultText TEXT NOT NULL,
            RolledUtc TEXT NOT NULL
        );
        CREATE INDEX IX_RollHistory_TableId ON RollHistory(TableId);
        """,

        // 3: one optional fixed modifier per table's dice (2d6+1, d20-2). Existing tables get 0, so they are unchanged.
        // The dice stay structured (count, sides, modifier); they are not turned into a free-form expression string.
        """
        ALTER TABLE Tables ADD COLUMN DiceModifier INTEGER NOT NULL DEFAULT 0 CHECK (DiceModifier BETWEEN -1000 AND 1000);
        """,

        // 4: how the faces of a table's dice are read. 0 = add them up (every existing table, unchanged), 1 = d66 (two d6 as tens and ones).
        // The convention is stored beside count, sides and modifier; a d66 is stored as 2 d6 with convention 1, never as a 66-sided die.
        """
        ALTER TABLE Tables ADD COLUMN DiceConvention INTEGER NOT NULL DEFAULT 0 CHECK (DiceConvention IN (0, 1));
        """,

        // 5: one-level folders within a collection. A folder holds only a name; tables opt in via Tables.FolderId,
        // which is NULL for "Unfiled". Every existing table gets NULL here, so nothing already saved moves anywhere.
        // Deleting a folder never deletes its tables: ON DELETE SET NULL sends them back to Unfiled.
        """
        CREATE TABLE Folders (
            Id           INTEGER PRIMARY KEY AUTOINCREMENT,
            CollectionId INTEGER NOT NULL REFERENCES Collections(Id),
            Name         TEXT NOT NULL
        );
        CREATE INDEX IX_Folders_CollectionId ON Folders(CollectionId);
        CREATE UNIQUE INDEX UX_Folders_CollectionId_Name ON Folders(CollectionId, Name COLLATE NOCASE);

        ALTER TABLE Tables ADD COLUMN FolderId INTEGER NULL REFERENCES Folders(Id) ON DELETE SET NULL;
        CREATE INDEX IX_Tables_FolderId ON Tables(FolderId);
        """,

        // 6: the temporary situational modifier a recorded roll used. RollValue stays the final value the table was
        // resolved with; this only records how it was reached. Every existing history row gets 0 (no modifier), which is
        // exactly what those rolls were. The live Modifier field itself is never stored anywhere.
        """
        ALTER TABLE RollHistory ADD COLUMN SituationalModifier INTEGER NOT NULL DEFAULT 0 CHECK (SituationalModifier BETWEEN -1000 AND 1000);
        """,

        // 7: Clamp to Range. Tables.ClampResultsToRange is an explicit per-table opt-in; every existing table gets 0 (off), so
        // nothing resolves differently after the upgrade. RollHistory.ClampedValue is the value a clamped roll was looked up
        // with; RollValue stays the calculated number. NULL means the roll was not clamped, which every existing row was.
        """
        ALTER TABLE Tables ADD COLUMN ClampResultsToRange INTEGER NOT NULL DEFAULT 0 CHECK (ClampResultsToRange IN (0, 1));
        ALTER TABLE RollHistory ADD COLUMN ClampedValue INTEGER NULL;
        """,

        // 8: open-ended ranges ("26+", "1 or less"). No column changes: an open bound is stored in Entries.MinValue or
        // Entries.MaxValue as the 32-bit integer minimum (-2147483648, no lower bound) or maximum (2147483647, no upper
        // bound), always with DisplayRange set to the written form. Older versions would read those as ordinary numbers,
        // so this version number exists to make them refuse the database instead. Every existing row is unchanged.
        """
        SELECT 1;
        """,
    ];

    public static int CurrentVersion => Migrations.Length;

    /// <summary>Migrates up to <paramref name="upToVersion"/> (default: everything). Older versions are used to test upgrades.</summary>
    public static void Apply(SqliteConnection connection, int upToVersion = int.MaxValue)
    {
        var target = Math.Min(upToVersion, CurrentVersion);
        var version = GetVersion(connection);
        if (version > CurrentVersion) throw new DatabaseTooNewException(version, CurrentVersion);

        for (var next = version + 1; next <= target; next++)
        {
            using var tx = connection.BeginTransaction();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = Migrations[next - 1];
            cmd.ExecuteNonQuery();
            cmd.CommandText = $"PRAGMA user_version = {next}"; // PRAGMA cannot be parameterized; next is an int we control
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }

    public static int GetVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
