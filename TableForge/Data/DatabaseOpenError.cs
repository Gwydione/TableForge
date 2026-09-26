using System.IO;
using Microsoft.Data.Sqlite;

namespace TableForge.Data;

/// <summary>The data file was written by a newer TableForge. It is never downgraded or changed.</summary>
public sealed class DatabaseTooNewException(int fileVersion, int supportedVersion) : InvalidOperationException(
    "This TableForge data file was created by a newer version of TableForge. Please install the newer version to open it safely.")
{
    public int FileVersion { get; } = fileVersion;
    public int SupportedVersion { get; } = supportedVersion;
    public string Details => $"Data format version {FileVersion}; this version of TableForge reads up to version {SupportedVersion}.";
}

/// <summary>The data needed updating, but the safety copy made first could not be written, so nothing was updated.</summary>
public sealed class DatabaseBackupException(string backupPath, Exception inner) : IOException(
    "TableForge needs to update your data for this version, but could not make a safety copy of it first, so nothing was changed.", inner)
{
    public string BackupPath { get; } = backupPath;
}

/// <summary>Updating the data failed after its safety copy had been made. The copy is kept.</summary>
public sealed class DatabaseMigrationException(string backupPath, Exception inner) : Exception(
    "TableForge could not update your data safely. Your original database backup has been preserved.", inner)
{
    public string BackupPath { get; } = backupPath;
}

/// <summary>Turns a failure to open or upgrade the database into something a person can act on.</summary>
public static class DatabaseOpenError
{
    // SQLite result codes we can explain in plain words.
    private const int Busy = 5;
    private const int Locked = 6;
    private const int NotADatabase = 26;

    /// <summary>
    /// A plain explanation first; any technical detail comes last, after "Details:", and never as the main message.
    /// </summary>
    public static string Describe(string path, Exception error)
    {
        switch (error)
        {
            case DatabaseTooNewException tooNew:
                return $"{tooNew.Message}\n\n{path}\n\nNothing in the data file was changed.\n\nDetails: {tooNew.Details}";
            case DatabaseMigrationException failed:
                return $"{failed.Message}\n\nBackup:\n{failed.BackupPath}\n\nYour data file:\n{path}\n\n" +
                       $"To go back to the backup, close TableForge and copy it over the data file.\n\nDetails: {Detail(failed.InnerException)}";
            case DatabaseBackupException backup:
                return $"{backup.Message}\n\n{path}\n\nCheck that the TableForge data folder can be written to (and that the disk is not full), then try again." +
                       $"\n\nDetails: {Detail(backup.InnerException)}";
        }

        var (reason, details) = error switch
        {
            SqliteException { SqliteErrorCode: NotADatabase } =>
                ("The file is not a TableForge database, or it is damaged.", null),
            SqliteException { SqliteErrorCode: Busy or Locked } =>
                ("The database is in use by another program (or another copy of TableForge).", null),
            SqliteException sqlite => ("The database could not be read.", Detail(sqlite)),
            UnauthorizedAccessException or IOException =>
                ($"The file or its folder cannot be accessed. {error.Message}", null),
            _ => ("Something unexpected went wrong while opening it.", Detail(error)),
        };

        return $"TableForge could not open its database.\n\n{path}\n\n{reason}\n\n" +
               "Nothing in the database was changed. If this keeps happening, close other copies of TableForge and try again." +
               (details is null ? "" : $"\n\nDetails: {details}");
    }

    private static string Detail(Exception? error) => error switch
    {
        null => "none",
        SqliteException sqlite => $"SQLite error {sqlite.SqliteErrorCode}: {sqlite.Message}",
        _ => error.Message,
    };
}
