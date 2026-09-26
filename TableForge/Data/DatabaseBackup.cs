using System.IO;
using Microsoft.Data.Sqlite;

namespace TableForge.Data;

/// <summary>
/// The safety copy made before TableForge updates an existing database to a newer format (schema). It is made only when an
/// update is actually needed (never for a new, empty database, one already current, or one from a newer TableForge), before
/// the first update step, and if it cannot be made nothing is updated. It lives beside the database, in the same data folder:
/// <c>tableforge.pre-v8-from-v7.backup.db</c> (the format being updated to, and the one it came from). The newest
/// <see cref="Keep"/> copies are kept; older ones are removed only after an update has succeeded.
/// </summary>
public static class DatabaseBackup
{
    public const int Keep = 3;

    public static string PathFor(string databasePath, int fromVersion, int toVersion) =>
        Path.Combine(Folder(databasePath), $"{Stem(databasePath)}.pre-v{toVersion}-from-v{fromVersion}.backup.db");

    /// <summary>Every migration backup of this database, newest first.</summary>
    public static IReadOnlyList<string> Existing(string databasePath)
    {
        var folder = Folder(databasePath);
        if (!Directory.Exists(folder)) return [];
        return new DirectoryInfo(folder).GetFiles($"{Stem(databasePath)}.pre-v*.backup.db")
            .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .Select(f => f.FullName).ToList();
    }

    /// <summary>
    /// Copies the open database with SQLite's own backup (a consistent copy, even of an open file) into a temporary file, which
    /// then replaces any earlier copy of the same name. Throws <see cref="DatabaseBackupException"/> if it cannot.
    /// </summary>
    public static string Create(SqliteConnection source, string databasePath, int fromVersion, int toVersion)
    {
        var target = PathFor(databasePath, fromVersion, toVersion);
        var temp = target + ".tmp";
        try
        {
            if (File.Exists(temp)) File.Delete(temp);
            var builder = new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false };
            using (var copy = new SqliteConnection(builder.ConnectionString))
            {
                copy.Open();
                source.BackupDatabase(copy);
            }
            File.Move(temp, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow); // the newest copy sorts first even when it replaced an older one
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SqliteException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { /* nothing more to do */ }
            throw new DatabaseBackupException(target, ex);
        }
    }

    /// <summary>After a successful update: keeps the newest <see cref="Keep"/> copies. Never stops TableForge from opening.</summary>
    public static void Prune(string databasePath)
    {
        foreach (var old in Existing(databasePath).Skip(Keep))
        {
            try { File.Delete(old); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Folder(string databasePath) => Path.GetDirectoryName(Path.GetFullPath(databasePath))!;
    private static string Stem(string databasePath) => Path.GetFileNameWithoutExtension(databasePath);
}
