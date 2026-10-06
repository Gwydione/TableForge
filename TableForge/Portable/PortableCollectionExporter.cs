using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using TableForge.Data;
using TableForge.Domain;

namespace TableForge.Portable;

/// <summary>What exporting a Collection gave: the file's bytes, or every reason it cannot be exported.</summary>
public sealed record PortableExport(byte[]? Json, IReadOnlyList<string> Problems)
{
    public bool Succeeded => Json is not null;
}

/// <summary>
/// Export Collection: one saved Collection as a <c>.tfcollection</c> file (UTF-8 JSON, no byte order mark), preserving TableForge's
/// own semantics — unlike the external exporters, which translate one result set into someone else's format. Only the
/// Collection's content travels (see <see cref="PortableCollection"/>): never database ids, timestamps, recent use, roll history
/// or settings.
/// <para>
/// The output is deterministic: folders and tables are written in the order TableForge lists them (alphabetical, then oldest
/// first) with ids f1, f2... and t1, t2... in that order, result sets and rows in their own order, and no timestamp.
/// </para>
/// <para>
/// Nothing is ever quietly lost. A Collection that cannot be carried faithfully is refused with the reason: a link to a table in
/// another Collection or to a table that no longer exists, dice that cannot be written back, or anything else
/// <see cref="PortableCollectionReader"/> would refuse — every export is read back through it before it is returned, so
/// TableForge never writes a file it would not import.
/// </para>
/// </summary>
public static class PortableCollectionExporter
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // "king’s sword" and "Sword & Shield" stay readable (still valid JSON)
    };

    public static PortableExport Export(AppDatabase db, Collection collection)
    {
        if (!TryBuild(db, collection, out var portable, out var problems)) return new(null, problems);

        var json = Write(portable!);
        var check = PortableCollectionReader.Read(json);
        return check.Succeeded ? new(json, []) : new(null, check.Problems);
    }

    /// <summary>A file name Windows accepts for the Collection, ending in .tfcollection.</summary>
    public static string SuggestedFileName(string collectionName) =>
        Path.ChangeExtension(FoundryTableExporter.SuggestedFileName(collectionName, "Collection"), PortableFormat.Extension);

    /// <summary>The Collection's content with database ids replaced by positions, or false with every reason it cannot travel.</summary>
    public static bool TryBuild(AppDatabase db, Collection collection, out PortableCollection? portable, out List<string> problems)
    {
        portable = null;
        problems = [];

        var folders = db.GetFolders(collection.Id);
        var folderIndex = folders.Select((f, i) => (f.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var summaries = db.GetTableSummaries(collection.Id);
        var tableIndex = summaries.Select((t, i) => (t.Id, i)).ToDictionary(x => x.Id, x => x.i);

        foreach (var folder in folders) CheckText(folder.Name, $"Folder \"{folder.Name}\"", problems);
        CheckText(collection.Name, "The collection's name", problems);

        var tables = new List<PortableTable>();
        foreach (var summary in summaries)
        {
            var table = db.LoadTable(summary.Id) ?? throw new InvalidOperationException($"The table \"{summary.Name}\" could not be read.");
            var where = $"Table \"{table.Name}\"";
            CheckText(table.Name, where, problems);
            CheckText(table.Description, $"{where}'s description", problems);

            if (!DiceExpression.TryParse(table.Dice.ToString(), out var back) || back != table.Dice)
                problems.Add($"{where} has dice ({table.Dice}) that cannot be written to a Collection file.");

            int? folder = null;
            if (table.FolderId is { } folderId)
            {
                if (folderIndex.TryGetValue(folderId, out var f)) folder = f;
                else problems.Add($"{where} is in a folder that is not part of this collection.");
            }

            var copy = new RollableTable
            {
                Name = table.Name,
                Dice = table.Dice,
                ClampResultsToRange = table.ClampResultsToRange,
                Description = table.Description,
            };
            for (var s = 0; s < table.ResultSets.Count; s++)
            {
                var set = table.ResultSets[s];
                CheckText(set.Name, $"{where}, result set {s + 1}", problems);
                var setCopy = new ResultSet { Name = set.Name, SortOrder = s };
                for (var e = 0; e < set.Entries.Count; e++)
                {
                    var entry = set.Entries[e];
                    var rowWhere = $"{where}, {SetLabel(table, s)}row {e + 1} ({entry.RangeLabel})";
                    CheckText(entry.Text, rowWhere, problems);
                    if (entry.DisplayRange is { } display) CheckText(display, rowWhere, problems);
                    if (entry.UnresolvedLinkName is { } unresolved) CheckText(unresolved, rowWhere, problems);

                    long? link = null;
                    if (entry.LinkedTableId is not null && entry.UnresolvedLinkName is not null) // never made by TableForge itself
                        problems.Add($"{rowWhere} has both a link and an unresolved link name. Edit the row to keep one, then export again.");
                    if (entry.LinkedTableId is { } targetId)
                    {
                        if (tableIndex.TryGetValue(targetId, out var target)) link = target;
                        else problems.Add(LinkOutsideProblem(db, rowWhere, targetId));
                    }

                    setCopy.Entries.Add(new TableEntry
                    {
                        Min = entry.Min,
                        Max = entry.Max,
                        DisplayRange = entry.DisplayRange,
                        Text = entry.Text,
                        Styles = entry.Styles,
                        LinkedTableId = link,
                        UnresolvedLinkName = entry.UnresolvedLinkName,
                        SortOrder = e,
                    });
                }
                copy.ResultSets.Add(setCopy);
            }
            tables.Add(new PortableTable(copy, folder));
        }

        if (problems.Count > 0) return false;
        portable = new PortableCollection(collection.Name, folders.Select(f => f.Name).ToList(), tables);
        return true;
    }

    private static string SetLabel(RollableTable table, int s) =>
        table.ResultSets.Count <= 1 ? "" : table.ResultSets[s].Name.Trim().Length > 0 ? $"result set \"{table.ResultSets[s].Name}\", " : $"result set {s + 1}, ";

    /// <summary>A link this Collection cannot carry: to a table in another Collection, or to one that no longer exists.</summary>
    private static string LinkOutsideProblem(AppDatabase db, string where, long targetId)
    {
        var target = db.LoadTable(targetId);
        if (target is null)
            return $"{where} links to a table that no longer exists. Edit the row to remove or change the link, then export again.";
        var owner = db.GetCollections().FirstOrDefault(c => c.Id == target.CollectionId)?.Name ?? "another collection";
        return $"{where} links to \"{target.Name}\" in the collection \"{owner}\". A Collection file can only carry links between its own tables.";
    }

    /// <summary>Text with a broken character (half of a surrogate pair) cannot be written as UTF-8 at all, so it is refused, never altered.</summary>
    private static void CheckText(string text, string where, List<string> problems)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
            if (char.IsSurrogate(text[i]))
            {
                problems.Add($"{where} contains a broken character that cannot be saved in a file. Edit it, then export again.");
                return;
            }
        }
    }

    // ---- writing ---------------------------------------------------------------------------------------------------------

    /// <summary>The file's bytes. Open bounds are written as null, never as the sentinels TableForge keeps them as internally.</summary>
    public static byte[] Write(PortableCollection portable)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteString("format", PortableFormat.FormatName);
            w.WriteNumber("formatVersion", PortableFormat.VersionFor(portable));
            w.WriteString("generator", $"TableForge {AppInfo.Version}"); // information only; never read back
            w.WriteStartObject("collection");
            w.WriteString("name", portable.Name);
            w.WriteEndObject();

            w.WriteStartArray("folders");
            for (var f = 0; f < portable.Folders.Count; f++)
            {
                w.WriteStartObject();
                w.WriteString("id", FolderId(f));
                w.WriteString("name", portable.Folders[f]);
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("tables");
            for (var t = 0; t < portable.Tables.Count; t++) WriteTable(w, t, portable.Tables[t]);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteTable(Utf8JsonWriter w, int index, PortableTable portable)
    {
        var table = portable.Table;
        w.WriteStartObject();
        w.WriteString("id", TableId(index));
        w.WriteString("name", table.Name);
        if (portable.FolderIndex is { } folder) w.WriteString("folder", FolderId(folder));
        w.WriteString("dice", table.Dice.ToString());
        w.WriteBoolean("clampToRange", table.ClampResultsToRange);
        if (table.Description.Length > 0) w.WriteString("description", table.Description); // only ever in a version 2 file
        w.WriteStartArray("resultSets");
        foreach (var set in table.ResultSets)
        {
            w.WriteStartObject();
            w.WriteString("name", set.Name);
            w.WriteStartArray("entries");
            foreach (var entry in set.Entries) WriteEntry(w, entry);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteEntry(Utf8JsonWriter w, TableEntry entry)
    {
        w.WriteStartObject();
        if (entry.IsOpenBelow) w.WriteNull("min"); else w.WriteNumber("min", entry.Min);
        if (entry.IsOpenAbove) w.WriteNull("max"); else w.WriteNumber("max", entry.Max);
        if (entry.DisplayRange is { } display) w.WriteString("display", display);
        w.WriteString("text", entry.Text);
        if (!entry.Styles.IsEmpty)
        {
            w.WriteStartArray("styles");
            foreach (var run in entry.Styles.Runs)
            {
                w.WriteStartArray();
                w.WriteNumberValue(run.Start);
                w.WriteNumberValue(run.Length);
                w.WriteStringValue(PortableFormat.StyleName(run.Style));
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
        if (entry.LinkedTableId is { } target)
        {
            w.WriteStartObject("link");
            w.WriteString("table", TableId((int)target));
            w.WriteEndObject();
        }
        else if (entry.UnresolvedLinkName is { } unresolved)
        {
            w.WriteStartObject("link");
            w.WriteString("unresolved", unresolved);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    private static string FolderId(int index) => $"f{index + 1}";
    private static string TableId(int index) => $"t{index + 1}";
}
