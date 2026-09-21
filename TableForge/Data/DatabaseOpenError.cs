using System.IO;
using Microsoft.Data.Sqlite;

namespace TableForge.Data;

/// <summary>Turns a failure to open or upgrade the database into something a person can act on.</summary>
public static class DatabaseOpenError
{
    // SQLite result codes we can explain in plain words.
    private const int Busy = 5;
    private const int Locked = 6;
    private const int NotADatabase = 26;

    /// <summary>
    /// Opening and upgrading happen in transactions, so a failure here leaves the file as it was: the message says so.
    /// </summary>
    public static string Describe(string path, Exception error)
    {
        var reason = error switch
        {
            SqliteException { SqliteErrorCode: NotADatabase } =>
                "The file is not a TableForge database, or it is damaged.",
            SqliteException { SqliteErrorCode: Busy or Locked } =>
                "The database is in use by another program (or another copy of TableForge).",
            InvalidOperationException => error.Message, // "created by a newer version": already worded for people
            UnauthorizedAccessException or IOException =>
                $"The file or its folder cannot be accessed. {error.Message}",
            _ => error.Message,
        };

        return $"TableForge could not open its database.\n\n{path}\n\n{reason}\n\n" +
               "Nothing in the database was changed. If this keeps happening, close other copies of TableForge and try again.";
    }
}
