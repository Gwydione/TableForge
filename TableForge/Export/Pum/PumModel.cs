using System.Text;
using TableForge.Domain;
using TableForge.Portable;

namespace TableForge.Export.Pum;

/// <summary>
/// One table offered for a PUM Companion export. <paramref name="Key"/> identifies it within the request, and is what a
/// <see cref="TableEntry.LinkedTableId"/> in the request refers to: the table's database id for a saved Collection, or its
/// position for a <see cref="PortableCollection"/>.
/// </summary>
/// <param name="FolderName">The table's folder, or null for Unfiled. It only chooses the PUM category.</param>
public sealed record PumSourceTable(long Key, RollableTable Table, string? FolderName = null);

/// <summary>
/// What to export: one table (single-table export) or many (a whole Collection), always from one Collection. The tables are
/// only read, never changed.
/// </summary>
/// <param name="OtherTableNames">
/// Names of link targets that are not being exported, by key, so a link to one can still name it ("→ Wilderness Events (not
/// included in this export)"). A link whose target is in neither list is reported as a link to a missing table.
/// </param>
public sealed record PumExportRequest(string CollectionName, IReadOnlyList<PumSourceTable> Tables, IReadOnlyDictionary<long, string>? OtherTableNames = null)
{
    /// <summary>
    /// The tables of a <c>.tfcollection</c> (all of them, or the chosen positions), keyed by position, which is exactly what its
    /// links refer to. The tables that are left out still name their links' targets.
    /// </summary>
    public static PumExportRequest FromPortable(PortableCollection collection, IEnumerable<int>? positions = null)
    {
        var chosen = (positions ?? Enumerable.Range(0, collection.Tables.Count)).Distinct().ToList();
        var tables = chosen.Select(i => new PumSourceTable(i, collection.Tables[i].Table,
            collection.Tables[i].FolderIndex is { } folder ? collection.Folders[folder] : null)).ToList();
        var others = Enumerable.Range(0, collection.Tables.Count).Except(chosen)
            .ToDictionary(i => (long)i, i => collection.Tables[i].Table.Name);
        return new(collection.Name, tables, others);
    }
}

/// <summary>One row of a PUM weighted table: it covers every value above the previous row's <see cref="Key"/> up to its own.</summary>
public sealed record PumRow(int Key, string Text);

/// <summary>A table exactly as it is written into the bundle.</summary>
public sealed record PumTable(string Id, string Name, string Category, string Formula, IReadOnlyList<PumRow> Rows);

public enum PumMessageKind
{
    /// <summary>The PUM table is written differently from the TableForge one, without changing what a roll shows.</summary>
    Conversion,

    /// <summary>Something the user should check: the export may not look or behave as they expect.</summary>
    Warning,

    /// <summary>Information, such as how rolls outside the dice's own range behave in PUM.</summary>
    Note,
}

public sealed record PumMessage(PumMessageKind Kind, string Text);

/// <summary>A roll that shows something different in PUM than in TableForge. Texts are shortened to one line.</summary>
public sealed record PumRollMismatch(int Roll, string TableForgeShows, string PumShows);

/// <summary>
/// What happened to one table. Compatibility is reported in two separate parts:
/// <list type="bullet">
/// <item><see cref="NormalRollMismatch"/>: rolls of the table's own dice. Any mismatch here means the table is skipped
/// (<see cref="SkipReason"/>); an exported table has none.</item>
/// <item><see cref="BelowDiceMismatch"/> and <see cref="AboveDiceMismatch"/>: rolls outside the dice's range, which only a PUM
/// override (<c>{{Table%%1d6+10}}</c>) or TableForge's situational modifier can produce. They never stop an export; they are
/// reported so the difference is known.</item>
/// </list>
/// </summary>
public sealed record PumTableReport(
    long Key,
    string SourceName,
    string PumName,
    string? SkipReason,
    PumRollMismatch? NormalRollMismatch,
    PumRollMismatch? BelowDiceMismatch,
    PumRollMismatch? AboveDiceMismatch,
    IReadOnlyList<PumMessage> Messages)
{
    public bool Exported => SkipReason is null;
    public bool HasConversions => Messages.Any(m => m.Kind == PumMessageKind.Conversion);
    public bool HasWarnings => Messages.Any(m => m.Kind == PumMessageKind.Warning);
}

/// <summary>The whole export: one entry per requested table, in request order, and the notes that apply to every PUM export.</summary>
public sealed record PumExportReport(IReadOnlyList<PumTableReport> Tables, IReadOnlyList<string> GeneralNotes)
{
    public int ExportedCount => Tables.Count(t => t.Exported);
    public int SkippedCount => Tables.Count(t => !t.Exported);

    /// <summary>The report as plain text: exported tables with their conversions and warnings, then skipped tables, then the notes.</summary>
    public string ToText()
    {
        var text = new StringBuilder();
        var changed = Tables.Count(t => t.Exported && (t.HasConversions || t.HasWarnings));
        text.Append($"PUM Companion export: {ExportedCount} exported ({changed} with changes or warnings), {SkippedCount} skipped.\n");

        if (ExportedCount > 0) text.Append("\nExported\n");
        foreach (var table in Tables.Where(t => t.Exported))
        {
            text.Append(table.PumName == table.SourceName ? $"  {table.SourceName}\n" : $"  {table.SourceName} → \"{table.PumName}\"\n");
            foreach (var message in table.Messages) text.Append($"    - {Label(message.Kind)}{message.Text}\n");
        }

        if (SkippedCount > 0) text.Append("\nSkipped\n");
        foreach (var table in Tables.Where(t => !t.Exported)) text.Append($"  {table.SourceName}: {table.SkipReason}\n");

        if (GeneralNotes.Count > 0) text.Append("\nNotes\n");
        foreach (var note in GeneralNotes) text.Append($"  - {note}\n");
        return text.ToString();

        static string Label(PumMessageKind kind) => kind switch
        {
            PumMessageKind.Warning => "Warning: ",
            PumMessageKind.Note => "Note: ",
            _ => "",
        };
    }
}

/// <param name="Bundle">The <c>.pumb</c> bytes; null when no table could be exported (the report says why).</param>
/// <param name="Tables">The tables in the bundle, in bundle order.</param>
public sealed record PumExportResult(byte[]? Bundle, IReadOnlyList<PumTable> Tables, PumExportReport Report);
