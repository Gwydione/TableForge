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

/// <summary>The fixed facts of the <c>.tfcollection</c> format. Its version is independent of the database schema version.</summary>
public static class PortableFormat
{
    public const string FormatName = "TableForgeCollection";
    public const int FormatVersion = 1;
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
