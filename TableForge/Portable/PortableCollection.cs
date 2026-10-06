using TableForge.Domain;

namespace TableForge.Portable;

/// <summary>
/// One Collection as a <c>.tfcollection</c> file carries it: TableForge's own semantics, independent of any database. It is
/// what <see cref="PortableCollectionExporter"/> builds from a saved Collection and writes, and what
/// <see cref="PortableCollectionReader"/> reads (and fully validates) before <see cref="Data.AppDatabase.ImportCollection"/>
/// inserts it as a brand-new Collection.
/// <para>
/// Nothing here is a database id. Folders and tables are identified only by their position in <see cref="Folders"/> and
/// <see cref="Tables"/>; the <see cref="RollableTable"/>s carry no ids, and in this model every
/// <see cref="TableEntry.LinkedTableId"/> is the POSITION of the destination in <see cref="Tables"/> (a self-link is the table's
/// own position). Open-ended bounds use the same <see cref="RangeBounds"/> sentinels as the rest of TableForge; only the file
/// writes them as <c>null</c>.
/// </para>
/// </summary>
public sealed record PortableCollection(string Name, IReadOnlyList<string> Folders, IReadOnlyList<PortableTable> Tables);

/// <param name="Table">The table's content. Its <see cref="RollableTable.Id"/>, <see cref="RollableTable.CollectionId"/> and
/// <see cref="RollableTable.FolderId"/> are not used.</param>
/// <param name="FolderIndex">Position of its folder in <see cref="PortableCollection.Folders"/>; null for Unfiled.</param>
public sealed record PortableTable(RollableTable Table, int? FolderIndex);

/// <summary>
/// The fixed facts of the <c>.tfcollection</c> format. Its version is independent of the database schema version.
/// <para>
/// Version 1 (RC25) is everything except table descriptions. Version 2 adds an optional "description" on each table. A file is
/// written in the LOWEST version that carries all of its content (<see cref="VersionFor"/>): a Collection with no descriptions
/// stays version 1, which RC25 imports exactly, and one with any description is version 2, which RC25 refuses as a newer
/// format instead of importing it and silently dropping the descriptions.
/// </para>
/// </summary>
public static class PortableFormat
{
    public const string FormatName = "TableForgeCollection";

    /// <summary>The newest format version this TableForge reads (and the newest it ever writes).</summary>
    public const int NewestVersion = 2;

    /// <summary>The first version with table descriptions.</summary>
    public const int DescriptionsVersion = 2;

    /// <summary>The version a file holding <paramref name="collection"/> is written in: the lowest that carries all of it.</summary>
    public static int VersionFor(PortableCollection collection) =>
        collection.Tables.Any(t => t.Table.Description.Length > 0) ? DescriptionsVersion : 1;
    public const string Extension = ".tfcollection";

    /// <summary>Files larger than this are refused before they are read.</summary>
    public const long MaxFileBytes = 25L * 1024 * 1024;

    /// <summary>The largest finite range number a file may hold: <see cref="Import.RangeText"/> reads at most six digits.</summary>
    public const int MaxRangeMagnitude = 999_999;

    /// <summary>The file's style names for bold, italic and both.</summary>
    public static string StyleName(TextStyle style) => style switch
    {
        TextStyle.Bold => "b",
        TextStyle.Italic => "i",
        _ => "bi",
    };

    public static TextStyle? ParseStyleName(string? name) => name switch
    {
        "b" => TextStyle.Bold,
        "i" => TextStyle.Italic,
        "bi" => TextStyle.Bold | TextStyle.Italic,
        _ => null,
    };

    /// <summary>
    /// Case-insensitive the way SQLite's NOCASE is (ASCII letters only), so a check made here agrees exactly with the
    /// database's own unique folder names and with how Collections are compared.
    /// </summary>
    public static bool SameNoCase(string a, string b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (Fold(a[i]) != Fold(b[i])) return false;
        return true;

        static char Fold(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
    }
}
