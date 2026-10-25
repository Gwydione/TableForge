using System.IO;
using TableForge.Domain;

namespace TableForge.Export.Pum;

/// <summary>
/// PUM Companion export: the planner for one table or a whole Collection, which share everything. It names every table
/// (<see cref="PumNames"/>), converts each one (<see cref="PumTableConverter"/>, whose equivalence check decides what is
/// exported), and writes the bundle (<see cref="PumBundleWriter"/>). It only reads the request's tables and never changes them.
/// </summary>
public static class PumExporter
{
    /// <summary>What every PUM export should say, whatever it contains.</summary>
    public static readonly IReadOnlyList<string> GeneralNotes =
    [
        "PUM looks up a roll below 1 as 1 and a roll above a table's last row as that last row, and shows that adjusted number as the roll. "
            + "Ordinary rolls of each exported table's own dice show exactly what Gidubba shows; any difference for rolls outside the dice's "
            + "range (from a PUM override or the situational modifier) is noted against the table.",
        "Inline dice such as \"1d6\" are kept as text: PUM doesn't roll them.",
        "Links are written as a \"→ table\" line: PUM doesn't open or roll the linked table.",
        "Table descriptions are not exported.",
        ReimportWarning,
    ];

    /// <summary>
    /// What importing a bundle again does (Slice 1 acceptance): PUM offers to overwrite the tables it already has, and overwriting
    /// replaces them. The same Collection, folder and table name always give the same PUM table.
    /// </summary>
    public const string ReimportWarning =
        "If these tables are already in PUM Companion from an earlier export, importing this bundle will offer to overwrite them. "
        + "Overwriting replaces those tables in PUM, including any changes made to them there.";

    /// <summary>A file name Windows accepts for the bundle, ending in .pumb.</summary>
    public static string SuggestedFileName(string name) =>
        Path.ChangeExtension(FoundryTableExporter.SuggestedFileName(name, "Gidubba tables"), ".pumb");

    /// <param name="timestamp">The date and time stored for every file in the bundle (the bundle bytes depend only on the request and this).</param>
    public static PumExportResult Export(PumExportRequest request, DateTime timestamp)
    {
        var sources = request.Tables;
        if (sources.Select(t => t.Key).Distinct().Count() != sources.Count)
            throw new ArgumentException("Each table in a PUM export needs its own key.", nameof(request));

        var names = PumNames.MakeUnique(sources.Select(t => PumNames.Normalize(t.Table.Name)).ToList());
        var categories = sources.Select(t => PumNames.Category(request.CollectionName, t.FolderName)).ToList();
        var nameOf = sources.Select((t, i) => (t.Key, Name: names[i])).ToDictionary(x => x.Key, x => x.Name);
        var others = request.OtherTableNames ?? new Dictionary<long, string>();

        // A link names its destination whether or not that table survives conversion, so whether a table converts never depends
        // on which others do; the second pass only marks links to skipped tables as not included.
        var included = sources.Select(t => t.Key).ToHashSet();
        PumTableConverter.Conversion[] conversions = [];
        for (var pass = 0; pass < 2; pass++)
        {
            conversions = sources.Select((t, i) => PumTableConverter.Convert(t, names[i], categories[i], Target)).ToArray();
            included = sources.Where((_, i) => conversions[i].Table is not null).Select(t => t.Key).ToHashSet();
        }

        var tables = conversions.Select(c => c.Table).OfType<PumTable>().ToList();
        var report = new PumExportReport(conversions.Select(c => c.Report).ToList(), GeneralNotes);
        return new(tables.Count == 0 ? null : PumBundleWriter.Write(tables, timestamp), tables, report);

        PumLinkTarget? Target(long key) =>
            nameOf.TryGetValue(key, out var name) ? new(name, included.Contains(key))
            : others.TryGetValue(key, out var other) ? new(other.Trim(), false)
            : null;
    }
}
