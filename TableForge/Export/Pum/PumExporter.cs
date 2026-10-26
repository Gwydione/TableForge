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
    public static readonly IReadOnlyList<string> GeneralNotes = Notes(
        "Links are written as a \"→ table\" line: PUM doesn't open or roll the linked table.");

    /// <summary>The notes when links are written as nested rolls (<see cref="PumExportRequest.NativeNestedTables"/>).</summary>
    public static readonly IReadOnlyList<string> NativeNestedNotes = Notes(
        "Links to tables in this bundle are written as \"→ Table: {{Table}}\", so PUM rolls the linked table there, once. Any other rolls "
            + "or choices a result describes are still yours to make. A link stays a \"→ table\" line when either table is a "
            + "manual-selection table, when following it would loop back, when its table isn't in this bundle, or when another of your "
            + "Collections has a table with the same name.",
        NameClashDetail);

    /// <summary>
    /// The short form shown in the review while native nested tables are on (F11: PUM keeps one table per name, whatever the
    /// category; an import with the same name replaces that table's rows, and on Windows keeps its old metadata).
    /// </summary>
    public const string NameClashWarning =
        "PUM finds a linked table by its name alone, in every category and ignoring case. Importing a table whose name PUM already has "
        + "replaces that table's rows, and everything that rolls the name then rolls the new rows. Gidubba can't see what PUM already "
        + "has on this PC or an iPad: if PUM offers to overwrite tables you didn't export from this Collection, cancel the import.";

    /// <summary>The full form, in the report.</summary>
    public const string NameClashDetail =
        "PUM keeps one table per name across all categories, ignoring case. Importing a table whose name PUM already has, from any "
        + "source, replaces that table's rows; its old category and description may stay, so the table can look unchanged in PUM's list. "
        + "Every nested roll of that name, from any table, then rolls the new rows. Re-importing the original bundle puts them back. "
        + "Gidubba can't see what is already in PUM on this PC or an iPad, so it can only keep links text when one of your other "
        + "Collections has the same name.";

    private static IReadOnlyList<string> Notes(params string[] links) =>
    [
        "PUM looks up a roll below 1 as 1 and a roll above a table's last row as that last row, and shows that adjusted number as the roll. "
            + "Ordinary rolls of each exported table's own dice show exactly what Gidubba shows; any difference for rolls outside the dice's "
            + "range (from a PUM override or the situational modifier) is noted against the table.",
        "Inline dice such as \"1d6\" are kept as text: PUM doesn't roll them.",
        .. links,
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
        // on which others do; the second pass only marks links to skipped tables as not included, and (with native nested tables)
        // decides which links PUM rolls. A nested roll is written after the conversion's checks, so it never changes what converts.
        var included = sources.Select(t => t.Key).ToHashSet();
        Dictionary<(long From, long To), (PumNesting Nesting, string? Other)>? nesting = null;
        PumTableConverter.Conversion[] conversions = [];
        for (var pass = 0; pass < 2; pass++)
        {
            conversions = sources.Select((t, i) => PumTableConverter.Convert(t, names[i], categories[i], key => Target(t.Key, key))).ToArray();
            included = sources.Where((_, i) => conversions[i].Table is not null).Select(t => t.Key).ToHashSet();
            if (pass == 0 && request.NativeNestedTables) nesting = Nesting(request, nameOf, included);
        }

        var tables = conversions.Select(c => c.Table).OfType<PumTable>().ToList();
        var report = new PumExportReport(conversions.Select(c => c.Report).ToList(), request.NativeNestedTables ? NativeNestedNotes : GeneralNotes);
        return new(tables.Count == 0 ? null : PumBundleWriter.Write(tables, timestamp), tables, report);

        PumLinkTarget? Target(long from, long key)
        {
            if (others.TryGetValue(key, out var other) && !nameOf.ContainsKey(key)) return new(other.Trim(), false);
            if (!nameOf.TryGetValue(key, out var name)) return null;
            var (how, collection) = nesting?.GetValueOrDefault((from, key)) ?? default;
            return new(name, included.Contains(key), how, collection);
        }
    }

    /// <summary>
    /// For every link between two tables in the bundle: whether PUM rolls it, or why it stays text. Only the tables' flags, names
    /// and links decide, never what a result says. A loop is found among the links that would otherwise be rolled: any of them
    /// that can lead back to its own table stays text, so PUM never rolls round a loop, while a loop a text link already breaks
    /// doesn't stop the rest of the chain.
    /// </summary>
    private static Dictionary<(long From, long To), (PumNesting, string?)> Nesting(
        PumExportRequest request, Dictionary<long, string> nameOf, HashSet<long> included)
    {
        var tables = request.Tables.Where(t => included.Contains(t.Key)).ToDictionary(t => t.Key, t => t.Table);
        var elsewhere = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, collection) in request.OtherCollectionTableNames ?? new Dictionary<string, string>())
            elsewhere.TryAdd(name, collection);

        var result = new Dictionary<(long, long), (PumNesting, string?)>();
        var rolled = new Dictionary<long, HashSet<long>>();
        foreach (var (from, table) in tables)
            foreach (var to in table.ResultSets.SelectMany(s => s.Entries).Select(e => e.LinkedTableId).OfType<long>().Distinct())
            {
                if (!tables.TryGetValue(to, out var target)) continue;
                var name = nameOf[to];
                (PumNesting, string?) how =
                    table.IsManualSelection ? (PumNesting.SourceIsManualSelection, null)
                    : target.IsManualSelection ? (PumNesting.TargetIsManualSelection, null)
                    : elsewhere.TryGetValue(name, out var collection) ? (PumNesting.NameInOtherCollection, (string?)collection)
                    : Unsafe(name) ? (PumNesting.NameUnsafe, null)
                    : (PumNesting.Native, null);
                result[(from, to)] = how;
                if (how.Item1 == PumNesting.Native) (rolled.TryGetValue(from, out var next) ? next : rolled[from] = []).Add(to);
            }

        foreach (var (from, targets) in rolled)
            foreach (var to in targets)
                if (Reaches(to, from)) result[(from, to)] = (PumNesting.Loop, null);
        return result;

        bool Reaches(long start, long goal)
        {
            var seen = new HashSet<long> { start };
            var stack = new Stack<long>([start]);
            while (stack.TryPop(out var at))
            {
                if (at == goal) return true;
                if (rolled.TryGetValue(at, out var next))
                    foreach (var n in next)
                        if (seen.Add(n)) stack.Push(n);
            }
            return false;
        }
    }

    /// <summary>A name PUM would read differently inside {{…}}: braces end or nest the roll, "%%" starts an override, ";;" inline dice.</summary>
    private static bool Unsafe(string name) => name.Contains("{{") || name.Contains("}}") || name.Contains("%%") || name.Contains(";;");
}
