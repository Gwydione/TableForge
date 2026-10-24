using System.Security.Cryptography;
using System.Text;
using TableForge.Domain;

namespace TableForge.Export.Pum;

/// <summary>A link's destination as one table's conversion sees it: its name, and whether it is in the same bundle.</summary>
public sealed record PumLinkTarget(string Name, bool Included);

/// <summary>
/// One TableForge table as one PUM weighted table, or the reason it can't be one. The rows are built from where TableForge's
/// outcome changes, over the values PUM can look up (1 and up):
/// <list type="bullet">
/// <item>Every stretch with the same outcome becomes one row, keyed at its last value. A stretch where no row covers the roll
/// becomes a <see cref="PumText.NoResult"/> row, so No Match stays no result (PUM would otherwise show the next row).</item>
/// <item>Several result sets become one table: each row lists every set's result ("Ambient: …"), so one roll still gives all.</item>
/// <item>Without Clamp to Range the last row always stands for "beyond the table": (No result), or an open-ended "26+" row. PUM
/// shows its last row for any larger value, so this keeps those rolls right too.</item>
/// <item>With Clamp to Range there are no end fillers: PUM's own clamping then matches TableForge's.</item>
/// <item>A d66 needs no filler for a gap made only of numbers a d66 can't roll (17–20).</item>
/// </list>
/// Whatever these rules produce, <see cref="PumEquivalence.Check"/> decides: a table is exported only if every roll of its dice
/// shows the same thing in PUM as in TableForge. Nothing here changes the source table.
/// </summary>
public static class PumTableConverter
{
    public sealed record Conversion(PumTable? Table, PumTableReport Report);

    /// <summary>
    /// PUM's formula for the dice: "1d6", "2d6+1", "1d20-2". The case matters to PUM (F09-04): "1D66" is the d66 convention
    /// (tens and ones), "1d66" a die with 66 sides, so only the d66 convention is ever written with a capital D.
    /// </summary>
    public static string Formula(DiceExpression dice)
    {
        if (dice.IsD66) return "1D66";
        var formula = $"{dice.Count}d{dice.Sides}";
        return dice.Modifier == 0 ? formula : dice.Modifier > 0 ? $"{formula}+{dice.Modifier}" : $"{formula}-{-dice.Modifier}";
    }

    /// <summary>A stable id: the same category and name always give the same id, so exporting a table again gives it the same one.</summary>
    public static string IdFor(string category, string name) =>
        "tf" + System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{category}\n{name}")))[..16].ToLowerInvariant();

    /// <param name="linkTarget">The destination of a link by its key; null when it is in neither the export nor the other names.</param>
    public static Conversion Convert(PumSourceTable source, string pumName, string category, Func<long, PumLinkTarget?> linkTarget)
    {
        var table = source.Table;
        var sets = table.ResultSets;
        var sourceName = table.Name.Trim();
        var setOf = new Dictionary<TableEntry, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < sets.Count; i++)
            foreach (var entry in sets[i].Entries) setOf[entry] = i;

        Conversion Skip(string reason, PumRollMismatch? mismatch = null) =>
            new(null, new PumTableReport(source.Key, sourceName, pumName, reason, mismatch, null, null, []));

        string Label(TableEntry entry) => sets.Count == 1 ? $"\"{entry.RangeLabel}\"" : $"\"{entry.RangeLabel}\" ({SetName(sets, setOf[entry])})";

        // ---- What can't be a PUM table at all --------------------------------------------------------------------------------
        if (setOf.Count == 0) return Skip("It has no rows.");
        if (setOf.Keys.FirstOrDefault(e => e.Min > e.Max) is { } backwards) return Skip($"The row {Label(backwards)} has a range that runs backwards.");
        foreach (var set in sets)
        {
            // Sorted by start, any overlap shows up between neighbours (the furthest-reaching earlier row always overlaps the next).
            var entries = set.Entries.OrderBy(e => e.Min).ToList();
            var reach = 0;
            for (var i = 1; i < entries.Count; i++)
            {
                if (entries[i].Min <= entries[reach].Max)
                    return Skip($"The rows {Label(entries[reach])} and {Label(entries[i])} overlap, so TableForge reports an ambiguous result there, which PUM can't show.");
                if (entries[i].Max > entries[reach].Max) reach = i;
            }
        }

        // ---- Each row's text ----------------------------------------------------------------------------------------------
        var issues = new Issues();
        var texts = new Dictionary<TableEntry, string>(ReferenceEqualityComparer.Instance);
        foreach (var entry in setOf.Keys)
        {
            var markdown = PumText.Markdown(entry.Text, entry.Styles, out var stylesTouch).Trim();
            if (stylesTouch) issues.Touching.Add(Label(entry));
            if (PumText.HasLiteralEmphasis(entry.Text)) issues.Emphasis.Add(Label(entry));
            var link = LinkLine(entry, linkTarget, issues, Label);
            var text = link is null ? markdown : markdown.Length == 0 ? link : $"{markdown}\n{link}";
            if (text.Length == 0) issues.Empty.Add(Label(entry));
            if (PumText.Commands(text) is [var command, ..])
                return Skip($"The row {Label(entry)} contains \"{command.Sequence}\", and {command.Effect}. The exporter never writes PUM commands.");
            texts[entry] = text;
        }
        if (texts.Values.All(t => t.Length == 0))
            return Skip("Every result is blank, so the table would only show empty results in PUM.");
        for (var i = 0; i < sets.Count && sets.Count > 1; i++)
            if (PumText.Commands(sets[i].Name) is [var command, ..])
                return Skip($"The result set name \"{sets[i].Name.Trim()}\" contains \"{command.Sequence}\", and {command.Effect}.");

        string Render(IReadOnlyList<TableEntry?> matched)
        {
            if (sets.Count == 1) return matched[0] is { } only ? texts[only] : PumText.NoResult;
            if (matched.All(m => m is null)) return PumText.NoResult;
            return string.Join("\n", sets.Select((set, i) => Heading(set) + (matched[i] is { } entry ? texts[entry] : PumText.NoResult)));
        }

        // ---- The rows -------------------------------------------------------------------------------------------------------
        var dice = table.Dice;
        var clamped = table.ClampResultsToRange && TableClamp.TryGetRange(table, out _, out _);
        var segments = Segments(table, clamped);
        if (segments.All(s => s.Matched.All(m => m is null)))
            return Skip("No row covers a roll of 1 or more, and PUM can't roll below 1.");

        if (!clamped)
        {
            // Beyond the last value considered nothing changes; PUM shows its last row for all of it, so that row must be right there.
            var beyond = sets.Select(s => s.Entries.FirstOrDefault(e => e.IsOpenAbove)).ToArray();
            if (!segments[^1].Matched.SequenceEqual(beyond)) segments.Add(new(segments[^1].End + 1, segments[^1].End + 1, beyond));
        }
        var rows = segments.Select(s => new PumRow((int)s.End, Render(s.Matched))).ToList();

        // ---- The final authority ------------------------------------------------------------------------------------------
        var check = PumEquivalence.Check(table, rows, Render);
        if (check.Normal is { } normal)
            return Skip($"A roll of {normal.Roll} shows \"{normal.TableForgeShows}\" in TableForge but would show \"{normal.PumShows}\" in PUM"
                + (normal.Roll < 1 ? " (PUM treats every roll below 1 as 1)." : "."), normal);

        // ---- What the report says about it ----------------------------------------------------------------------------------
        var messages = new List<PumMessage>();
        void Add(PumMessageKind kind, string text) => messages.Add(new(kind, text));

        if (pumName != sourceName) Add(PumMessageKind.Conversion, $"Named \"{pumName}\" in PUM.");
        if (sets.Count > 1)
            Add(PumMessageKind.Conversion, $"Its {sets.Count} result sets are combined: each row lists every set's result, so one roll still gives all of them.");
        var fillers = segments.Count(s => s.Matched.All(m => m is null));
        if (fillers > 0)
            Add(PumMessageKind.Conversion, $"{Count(fillers, "\"(No result)\" row")} added where no row covers the roll, so those rolls still show no result"
                + (!clamped && segments[^1].Matched.All(m => m is null)
                    ? $". The last one (key {rows[^1].Key}) also catches any higher roll, for which PUM would otherwise show the last row."
                    : "."));
        if (issues.Linked > 0)
            Add(PumMessageKind.Conversion, $"{Count(issues.Linked, "linked row")} end{(issues.Linked == 1 ? "s" : "")} with a \"→ table\" line. PUM won't open or roll the linked table.");
        if (clamped)
            Add(PumMessageKind.Note, "Clamp to Range is kept: PUM shows the first or last row for rolls beyond them, as TableForge does.");

        var reached = segments.SelectMany(s => s.Matched).OfType<TableEntry>().ToHashSet(ReferenceEqualityComparer.Instance);
        var unreached = setOf.Keys.Where(e => !reached.Contains(e)).Select(Label).ToList();
        Warn(messages, unreached, "is below 1, which PUM can't roll, so it isn't in the PUM table", "are below 1, which PUM can't roll, so they aren't in the PUM table");
        Warn(messages, issues.Empty, "has no text, so PUM shows a blank result (Remove Empty Rows in Review/Edit removes empty rows)",
            "have no text, so PUM shows a blank result (Remove Empty Rows in Review/Edit removes empty rows)");
        Warn(messages, issues.Emphasis, "has * or _ around words, which PUM may show as italic or bold", "have * or _ around words, which PUM may show as italic or bold");
        Warn(messages, issues.Touching, "has formatting that changes with no space between, which PUM may show differently",
            "have formatting that changes with no space between, which PUM may show differently");
        messages.AddRange(issues.LinkWarnings.Select(w => new PumMessage(PumMessageKind.Warning, w)));

        if (check.BelowDice is { } below)
            Add(PumMessageKind.Note, $"Below the dice's range (only a PUM override or the situational modifier gets there), a roll of {below.Roll} shows "
                + $"\"{below.TableForgeShows}\" in TableForge but \"{below.PumShows}\" in PUM" + (below.Roll < 1 ? ", which treats rolls below 1 as 1." : "."));
        if (check.AboveDice is { } above)
            Add(PumMessageKind.Note, $"Above the dice's range (only a PUM override or the situational modifier gets there), a roll of {above.Roll} shows "
                + $"\"{above.TableForgeShows}\" in TableForge but \"{above.PumShows}\" in PUM.");

        var pum = new PumTable(IdFor(category, pumName), pumName, category, Formula(dice), rows);
        return new(pum, new PumTableReport(source.Key, sourceName, pumName, null, null, check.BelowDice, check.AboveDice, messages));
    }

    private sealed record Segment(long Start, long End, TableEntry?[] Matched);

    /// <summary>
    /// Stretches of values from 1 up with the same outcome (which entry each result set matches), adjacent equal ones merged.
    /// They reach the dice's maximum and every row's finite bound (with Clamp, only the rows: PUM clamps to the last row anyway).
    /// </summary>
    private static List<Segment> Segments(RollableTable table, bool clamped)
    {
        var sets = table.ResultSets;
        var entries = sets.SelectMany(s => s.Entries).ToList();
        long top = 1;
        foreach (var e in entries)
            top = Math.Max(top, e.IsOpenAbove ? (e.IsOpenBelow ? 1 : e.Min) : e.Max);
        if (!clamped) top = Math.Max(top, table.Dice.Max);

        var points = new SortedSet<long> { 1, top + 1 };
        foreach (var e in entries)
        {
            if (!e.IsOpenBelow && e.Min > 1 && e.Min <= top) points.Add(e.Min);
            if (!e.IsOpenAbove && e.Max + 1L > 1 && e.Max + 1L <= top) points.Add(e.Max + 1L);
        }

        var starts = points.ToList();
        var segments = new List<Segment>();
        for (var i = 0; i + 1 < starts.Count; i++)
        {
            var start = starts[i];
            var matched = sets.Select(s => s.Entries.FirstOrDefault(e => e.Covers((int)start))).ToArray();
            segments.Add(new(start, starts[i + 1] - 1, matched));
        }

        // A leading "no row" stretch needs no filler with Clamp (TableForge shows the first row there, and so does PUM without one),
        // nor does a d66 gap made only of numbers a d66 can't roll. Removing a stretch hands its values to the next row.
        if (clamped && segments.Count > 1 && segments[0].Matched.All(m => m is null)) segments.RemoveAt(0);
        if (table.Dice.IsD66)
            for (var i = segments.Count - 2; i >= 0; i--)
                if (segments[i].Matched.All(m => m is null) && !Values(segments[i]).Any(table.Dice.IsLegal))
                    segments.RemoveAt(i);

        // Merge neighbours that now show the same thing, keeping the later key.
        var merged = new List<Segment>();
        foreach (var s in segments)
        {
            if (merged.Count > 0 && merged[^1].Matched.SequenceEqual(s.Matched)) merged[^1] = merged[^1] with { End = s.End };
            else merged.Add(s);
        }
        return merged;

        static IEnumerable<int> Values(Segment s)
        {
            for (var v = s.Start; v <= s.End; v++) yield return (int)v;
        }
    }

    private static string? LinkLine(TableEntry entry, Func<long, PumLinkTarget?> linkTarget, Issues issues, Func<TableEntry, string> label)
    {
        if (entry.LinkedTableId is { } id)
        {
            issues.Linked++;
            if (linkTarget(id) is not { } target)
            {
                issues.LinkWarnings.Add($"The row {label(entry)} links to a table that couldn't be found.");
                return "→ (missing table)";
            }
            if (target.Included) return $"→ {target.Name}";
            issues.LinkWarnings.Add($"The row {label(entry)} links to \"{target.Name}\", which isn't in this export.");
            return $"→ {target.Name} (not included in this export)";
        }
        if (entry.UnresolvedLinkName is null) return null;

        issues.Linked++;
        var name = entry.UnresolvedLinkName.Trim();
        issues.LinkWarnings.Add(name.Length > 0 ? $"The row {label(entry)} has an unresolved link to \"{name}\"." : $"The row {label(entry)} has an unresolved link.");
        return name.Length > 0 ? $"→ {name} (unresolved link)" : "→ (unresolved link)";
    }

    private static string Heading(ResultSet set) => set.Name.Trim() is { Length: > 0 } name ? $"{name}: " : "";

    private static string SetName(IReadOnlyList<ResultSet> sets, int index) => sets[index].Name.Trim() is { Length: > 0 } name ? name : $"result set {index + 1}";

    private static string Count(int n, string thing) => n == 1 ? $"1 {thing}" : $"{n} {thing}s";

    /// <summary>One warning for all the rows with the same problem: the first five by label, then how many more.</summary>
    private static void Warn(List<PumMessage> messages, List<string> labels, string one, string many)
    {
        if (labels.Count == 0) return;
        var shown = labels.Take(5).ToList();
        var list = shown.Count == 1 ? shown[0] : $"{string.Join(", ", shown[..^1])} and {shown[^1]}";
        if (labels.Count > 5) list = $"{string.Join(", ", shown)} and {labels.Count - 5} more";
        messages.Add(new(PumMessageKind.Warning, labels.Count == 1 ? $"The row {list} {one}." : $"The rows {list} {many}."));
    }

    private sealed class Issues
    {
        public List<string> Empty { get; } = [];
        public List<string> Emphasis { get; } = [];
        public List<string> Touching { get; } = [];
        public List<string> LinkWarnings { get; } = [];
        public int Linked { get; set; }
    }
}
