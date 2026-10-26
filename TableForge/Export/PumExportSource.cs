using TableForge.Data;
using TableForge.Domain;
using TableForge.Export.Pum;

namespace TableForge.Export;

/// <summary>
/// What a PUM Companion export reads from the database: the saved tables, keyed by their database ids (which is what their links
/// refer to), with their folder names and the names of any link targets that are not being exported. It only reads: nothing is
/// written, and Recent Tables is not touched. Kept outside <c>Export/Pum</c> so the export engine itself never needs the database.
/// </summary>
public static class PumExportSource
{
    /// <summary>
    /// Every table in the Collection, in the order TableForge lists them, with the PUM names every other Collection's tables would
    /// export with (for native nested tables: PUM can't tell two tables with the same name apart).
    /// </summary>
    public static PumExportRequest ForCollection(AppDatabase db, Collection collection)
    {
        var folders = db.GetFolders(collection.Id).ToDictionary(f => f.Id, f => f.Name);
        var tables = db.GetTableSummaries(collection.Id)
            .Select(s => db.LoadTable(s.Id) ?? throw new InvalidOperationException($"The table \"{s.Name}\" could not be read."))
            .ToList();
        var elsewhere = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var other in db.GetCollections().Where(c => c.Id != collection.Id))
            foreach (var name in PumNames.MakeUnique(db.GetTableSummaries(other.Id).Select(s => PumNames.Normalize(s.Name)).ToList()))
                elsewhere.TryAdd(name, other.Name);
        return Request(db, collection.Name, tables, folders) with { OtherCollectionTableNames = elsewhere };
    }

    /// <summary>
    /// One table, as saved, in its own Collection and folder: the same PUM name and category a Collection export gives it, so
    /// exporting it again updates the same PUM table. A table that was never saved is exported as it is.
    /// </summary>
    public static PumExportRequest ForTable(AppDatabase db, RollableTable table)
    {
        var saved = table.Id > 0 ? db.LoadTable(table.Id) ?? table : table;
        var collection = db.GetCollections().FirstOrDefault(c => c.Id == saved.CollectionId)?.Name ?? "";
        var folders = db.GetFolders(saved.CollectionId).ToDictionary(f => f.Id, f => f.Name);
        return Request(db, collection, [saved], folders);
    }

    private static PumExportRequest Request(AppDatabase db, string collectionName, IReadOnlyList<RollableTable> tables, Dictionary<long, string> folders)
    {
        var sources = tables.Select(t => new PumSourceTable(t.Id, t, t.FolderId is { } f && folders.TryGetValue(f, out var name) ? name : null)).ToList();
        var included = tables.Select(t => t.Id).ToHashSet();
        var others = new Dictionary<long, string>();
        foreach (var id in tables.SelectMany(t => t.ResultSets).SelectMany(s => s.Entries).Select(e => e.LinkedTableId).OfType<long>().Distinct())
            if (!included.Contains(id) && db.LoadTable(id) is { } target)
                others[id] = target.Name;   // in another Collection, or not part of this export; a deleted one stays missing
        return new(collectionName, sources, others);
    }
}
