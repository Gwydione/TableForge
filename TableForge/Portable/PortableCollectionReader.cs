using System.IO;
using System.Text.Json;
using TableForge.Domain;
using TableForge.Import;

namespace TableForge.Portable;

/// <summary>Why a <c>.tfcollection</c> file was refused, from the most fundamental reason down.</summary>
public enum PortableReadFailure
{
    /// <summary>The file could not be read from disk at all.</summary>
    Unreadable,

    /// <summary>Larger than <see cref="PortableFormat.MaxFileBytes"/>; refused without being read.</summary>
    TooLarge,

    /// <summary>Not JSON, not a TableForge Collection, or not shaped like one (missing or mistyped properties, repeated ids).</summary>
    NotACollectionFile,

    /// <summary>A Collection file from a newer TableForge, whose format this version does not know.</summary>
    NewerFormat,

    /// <summary>Shaped correctly, but its content is not something TableForge can hold (bad dice, ranges, formatting...).</summary>
    InvalidContent,

    /// <summary>A folder or link reference that points at nothing in the file.</summary>
    InvalidRelationship,
}

/// <summary>What reading a <c>.tfcollection</c> file gave: the validated Collection, or why it was refused (with every problem found).</summary>
public sealed record PortableReadResult(PortableCollection? Collection, PortableReadFailure? Failure, IReadOnlyList<string> Problems)
{
    public bool Succeeded => Collection is not null;

    /// <summary>The sentence to show first. Every refusal ends by saying that nothing was changed, because nothing was.</summary>
    public string Summary => Failure switch
    {
        null => "",
        PortableReadFailure.Unreadable => "The file could not be read. Nothing was imported.",
        PortableReadFailure.TooLarge => "This file is too large to be a TableForge Collection (the limit is 25 MB). Nothing was imported.",
        PortableReadFailure.NewerFormat => "This Collection uses a newer file format than this version of TableForge supports. Nothing was changed.",
        PortableReadFailure.InvalidContent => "This Collection contains something TableForge can't import safely. Nothing was imported.",
        PortableReadFailure.InvalidRelationship => "This Collection contains an invalid table relationship and can't be imported safely. Nothing was imported.",
        _ => "This isn't a valid TableForge Collection file. Nothing was imported.",
    };
}

/// <summary>
/// Reads a <c>.tfcollection</c> file as untrusted data and validates ALL of it before anything is imported. It either returns a
/// <see cref="PortableCollection"/> that reproduces the file exactly, or refuses: nothing is ever trimmed, repaired, dropped or
/// guessed (which is why it does not go through <see cref="TableImportDraft.TryBuildTable"/> or <see cref="TextStyles.Parse"/>,
/// both of which deliberately normalize for their own uses). Problems are found in four layers — the file and its JSON, the
/// format's shape, TableForge's own rules, and the references between folders and tables — and every problem is reported.
/// <para>
/// Unknown properties are ignored (a later format version that adds something that must not be ignored gets a new
/// <c>formatVersion</c>); every known property is checked strictly, and a repeated property name makes the file invalid JSON.
/// </para>
/// </summary>
public static class PortableCollectionReader
{
    private enum Layer { Shape, Content, Relationship }

    public static PortableReadResult ReadFile(string path)
    {
        byte[] bytes;
        try
        {
            if (new FileInfo(path).Length > PortableFormat.MaxFileBytes)
                return new(null, PortableReadFailure.TooLarge, []);
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or NotSupportedException or ArgumentException)
        {
            return new(null, PortableReadFailure.Unreadable, [ex.Message]);
        }
        return Read(bytes);
    }

    public static PortableReadResult Read(byte[] bytes)
    {
        if (bytes.LongLength > PortableFormat.MaxFileBytes) return new(null, PortableReadFailure.TooLarge, []);

        var json = bytes.AsMemory();
        if (json.Span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) json = json[3..]; // a UTF-8 byte order mark (Notepad adds one)

        // The JSON reader checks structure, not the bytes inside strings, so the whole file is checked as UTF-8 first.
        if (!System.Text.Unicode.Utf8.IsValid(json.Span))
            return new(null, PortableReadFailure.NotACollectionFile, ["The file is not valid UTF-8 JSON."]);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return new(null, PortableReadFailure.NotACollectionFile, ["The file is not valid UTF-8 JSON."]);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != PortableFormat.FormatName)
                return new(null, PortableReadFailure.NotACollectionFile, [$"The file does not say it is a {PortableFormat.FormatName} file."]);

            if (!root.TryGetProperty("formatVersion", out var versionElement) || versionElement.ValueKind != JsonValueKind.Number
                || !versionElement.TryGetInt32(out var version) || version < 1)
                return new(null, PortableReadFailure.NotACollectionFile, ["The file's formatVersion is missing or is not a whole number of 1 or more."]);
            if (version > PortableFormat.FormatVersion)
                return new(null, PortableReadFailure.NewerFormat,
                    [$"The file uses format version {version}; this version of TableForge reads version {PortableFormat.FormatVersion}."]);

            try
            {
                return new Reading().Run(root);
            }
            catch (InvalidOperationException)
            {
                // A string the JSON reader cannot turn into text (an escaped half of a surrogate pair, "\ud800").
                return new(null, PortableReadFailure.NotACollectionFile, ["The file contains text that is not valid Unicode."]);
            }
        }
    }

    /// <summary>One pass over one file's content, collecting every problem with the layer it belongs to.</summary>
    private sealed class Reading
    {
        private readonly List<(Layer Layer, string Message)> _problems = [];

        public PortableReadResult Run(JsonElement root)
        {
            string? name = null;
            if (Object(root, "collection", "The file") is { } collection)
                name = Name(collection, "name", "The collection");

            // Folders: ids for references, names for the database (unique the way the database compares them).
            var folderIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var folderNames = new List<string>();
            foreach (var (folder, i) in Array(root, "folders", "The file"))
            {
                var where = $"Folder {i + 1}";
                if (folder.ValueKind != JsonValueKind.Object) { Shape($"{where} is not an object."); continue; }
                var id = Id(folder, where, folderIds, folderNames.Count);
                var folderName = Name(folder, "name", id is null ? where : $"Folder \"{id}\"");
                if (folderName is not null && folderNames.FirstOrDefault(n => PortableFormat.SameNoCase(n, folderName)) is { } same)
                    Content($"There are two folders named \"{same}\" and \"{folderName}\"; folder names must differ (ignoring capitals).");
                folderNames.Add(folderName ?? "");
            }

            // Tables: read everything first, so a link may point at any table (later ones, itself, or round in a cycle).
            var tableIds = new Dictionary<string, int>(StringComparer.Ordinal);
            var tables = new List<PortableTable>();
            var pendingLinks = new List<(TableEntry Entry, string TargetId, string Where)>();
            var pendingFolders = new List<(int TableIndex, string FolderId, string Where)>();
            foreach (var (element, i) in Array(root, "tables", "The file"))
            {
                if (element.ValueKind != JsonValueKind.Object) { Shape($"Table {i + 1} is not an object."); tables.Add(new(new RollableTable(), null)); continue; }
                var id = Id(element, $"Table {i + 1}", tableIds, tables.Count);
                var tableName = Name(element, "name", $"Table {i + 1}");
                var where = tableName is null ? $"Table {i + 1}" : $"Table \"{tableName}\"";
                tables.Add(ReadTable(element, tableName ?? "", where, tables.Count, pendingLinks, pendingFolders));
            }

            foreach (var (tableIndex, folderId, where) in pendingFolders)
            {
                if (folderIds.TryGetValue(folderId, out var folderIndex)) tables[tableIndex] = tables[tableIndex] with { FolderIndex = folderIndex };
                else Relationship($"{where} is in folder \"{folderId}\", which is not in the file.");
            }
            foreach (var (entry, targetId, where) in pendingLinks)
            {
                if (tableIds.TryGetValue(targetId, out var target)) entry.LinkedTableId = target;
                else Relationship($"{where} links to table \"{targetId}\", which is not in the file.");
            }

            if (_problems.Count > 0)
            {
                var worst = _problems.Min(p => p.Layer);
                var failure = worst switch
                {
                    Layer.Shape => PortableReadFailure.NotACollectionFile,
                    Layer.Content => PortableReadFailure.InvalidContent,
                    _ => PortableReadFailure.InvalidRelationship,
                };
                return new(null, failure, _problems.OrderBy(p => p.Layer).Select(p => p.Message).ToList());
            }
            return new(new PortableCollection(name!, folderNames, tables), null, []);
        }

        private PortableTable ReadTable(JsonElement element, string name, string where, int index,
            List<(TableEntry, string, string)> pendingLinks, List<(int, string, string)> pendingFolders)
        {
            var table = new RollableTable { Name = name };

            if (OptionalString(element, "folder", where, out var folderId) && folderId is not null)
                pendingFolders.Add((index, folderId, where));

            DiceExpression? dice = null;
            if (RequiredString(element, "dice", where) is { } diceText)
            {
                if (DiceExpression.TryParse(diceText, out var parsed)) dice = table.Dice = parsed;
                else Content($"{where} has dice \"{diceText}\", which TableForge does not support.{DiceExpression.UnsupportedHint(diceText)}");
            }

            var clamp = false;
            if (element.TryGetProperty("clampToRange", out var clampElement) && clampElement.ValueKind != JsonValueKind.Null)
            {
                if (clampElement.ValueKind is JsonValueKind.True or JsonValueKind.False) clamp = clampElement.GetBoolean();
                else Shape($"{where}: clampToRange must be true or false.");
            }

            var sets = Array(element, "resultSets", where).ToList();
            if (element.TryGetProperty("resultSets", out var setsElement) && setsElement.ValueKind == JsonValueKind.Array && sets.Count == 0)
                Content($"{where} has no result sets.");

            foreach (var (setElement, s) in sets)
            {
                var setWhere = sets.Count > 1 ? $"{where}, result set {s + 1}" : where;
                if (setElement.ValueKind != JsonValueKind.Object) { Shape($"{setWhere} is not an object."); continue; }
                var set = new ResultSet { SortOrder = s };
                if (OptionalString(setElement, "name", setWhere, out var setName)) set.Name = setName ?? "";

                var entries = Array(setElement, "entries", setWhere).ToList();
                if (setElement.TryGetProperty("entries", out var entriesElement) && entriesElement.ValueKind == JsonValueKind.Array && entries.Count == 0)
                    Content($"{setWhere} has no rows.");
                foreach (var (entryElement, e) in entries)
                {
                    var rowWhere = $"{setWhere}, row {e + 1}";
                    if (entryElement.ValueKind != JsonValueKind.Object) { Shape($"{rowWhere} is not an object."); continue; }
                    if (ReadEntry(entryElement, dice, rowWhere, e, pendingLinks) is { } entry) set.Entries.Add(entry);
                }
                table.ResultSets.Add(set);
            }

            // Clamp only where TableForge itself would allow it (never quietly switched off, as editing does).
            table.ClampResultsToRange = clamp;
            if (clamp && dice is not null && !TableClamp.TryGetRange(table, out _, out _, out var reason))
                Content($"{where} asks for Clamp to Range, which it cannot have: {reason}");

            return new PortableTable(table, null);
        }

        private TableEntry? ReadEntry(JsonElement element, DiceExpression? dice, string where, int order, List<(TableEntry, string, string)> pendingLinks)
        {
            var ok = true;
            var min = Bound(element, "min", where, ref ok);
            var max = Bound(element, "max", where, ref ok);
            var text = RequiredString(element, "text", where);
            OptionalString(element, "display", where, out var display);
            ok &= text is not null;

            if (ok)
            {
                if (min is null && max is null) { Content($"{where}: a range cannot be open at both ends."); ok = false; }
                else if (min is { } a && max is { } b && a > b) { Content($"{where}: the range {a}–{b} runs backwards."); ok = false; }
                if ((min is null || max is null) && dice is { IsD66: true }) { Content($"{where}: {RangeText.OpenD66Message}"); ok = false; }
                if ((min is null || max is null) && display is null) { Content($"{where}: an open-ended range must keep its written form (display)."); ok = false; }
            }

            var entry = new TableEntry
            {
                Min = min ?? RangeBounds.OpenBelow,
                Max = max ?? RangeBounds.OpenAbove,
                Text = text ?? "",
                DisplayRange = display,
                SortOrder = order,
            };

            // The written range must be exactly what TableForge's own range reader makes of it, for this table's dice.
            if (ok && display is not null && dice is { } d)
            {
                if (!RangeText.TryParse(display, d, out var parsed, out var error))
                {
                    Content($"{where}: its written range \"{display}\" is not a range TableForge reads ({error})");
                    ok = false;
                }
                else if (parsed.Min != entry.Min || parsed.Max != entry.Max)
                {
                    Content($"{where}: its written range \"{display}\" means {RangeBounds.Label(parsed.Min, parsed.Max)}, " +
                            $"not the {RangeBounds.Label(entry.Min, entry.Max)} the file gives.");
                    ok = false;
                }
            }

            if (text is not null) entry.Styles = Styles(element, text, where, ref ok);
            Link(element, entry, where, pendingLinks, ref ok);
            return ok ? entry : null;
        }

        /// <summary>A finite bound (within <see cref="PortableFormat.MaxRangeMagnitude"/>), or null for an open one. The property must be present.</summary>
        private int? Bound(JsonElement element, string name, string where, ref bool ok)
        {
            if (!element.TryGetProperty(name, out var value)) { Shape($"{where} has no \"{name}\"."); ok = false; return null; }
            if (value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            {
                Shape($"{where}: \"{name}\" must be a whole number or null.");
                ok = false;
                return null;
            }
            if (Math.Abs((long)number) > PortableFormat.MaxRangeMagnitude)
            {
                Content($"{where}: {number} is too large for a range (at most six digits).");
                ok = false;
            }
            return number;
        }

        /// <summary>
        /// Bold/italic runs, checked strictly: in order, never overlapping, never empty, inside the text, never splitting a surrogate
        /// pair, and only the three known styles. Anything else refuses the file; nothing is clipped or dropped.
        /// </summary>
        private TextStyles Styles(JsonElement element, string text, string where, ref bool ok)
        {
            if (!element.TryGetProperty("styles", out var styles) || styles.ValueKind == JsonValueKind.Null) return TextStyles.Empty;
            if (styles.ValueKind != JsonValueKind.Array) { Shape($"{where}: \"styles\" must be a list."); ok = false; return TextStyles.Empty; }

            var runs = new List<StyleRun>();
            var previousEnd = 0;
            var n = 0;
            foreach (var run in styles.EnumerateArray())
            {
                n++;
                if (run.ValueKind != JsonValueKind.Array || run.GetArrayLength() != 3
                    || run[0].ValueKind != JsonValueKind.Number || !run[0].TryGetInt32(out var start)
                    || run[1].ValueKind != JsonValueKind.Number || !run[1].TryGetInt32(out var length)
                    || run[2].ValueKind != JsonValueKind.String)
                {
                    Shape($"{where}: formatting run {n} must be [start, length, style].");
                    ok = false;
                    continue;
                }
                if (PortableFormat.ParseStyleName(run[2].GetString()) is not { } style)
                    Content($"{where}: formatting run {n} has the unknown style \"{run[2].GetString()}\" (only \"b\", \"i\" and \"bi\" exist).");
                else if (length <= 0)
                    Content($"{where}: formatting run {n} is empty.");
                else if (start < 0 || (long)start + length > text.Length)
                    Content($"{where}: formatting run {n} reaches outside the text.");
                else if (start < previousEnd)
                    Content($"{where}: formatting run {n} overlaps or comes before the run before it.");
                else if (SplitsPair(text, start) || SplitsPair(text, start + length))
                    Content($"{where}: formatting run {n} splits a character in two.");
                else
                {
                    runs.Add(new StyleRun(start, length, style));
                    previousEnd = start + length;
                    continue;
                }
                ok = false;
            }
            // Every run is valid and inside the text, so this changes nothing but merging neighbours of the same style.
            return TextStyles.FromRuns(runs, text.Length);
        }

        private static bool SplitsPair(string text, int boundary) =>
            boundary > 0 && boundary < text.Length && char.IsHighSurrogate(text[boundary - 1]) && char.IsLowSurrogate(text[boundary]);

        private void Link(JsonElement element, TableEntry entry, string where, List<(TableEntry, string, string)> pendingLinks, ref bool ok)
        {
            if (!element.TryGetProperty("link", out var link) || link.ValueKind == JsonValueKind.Null) return;
            if (link.ValueKind != JsonValueKind.Object) { Shape($"{where}: \"link\" must be an object."); ok = false; return; }

            var hasTable = OptionalString(link, "table", where, out var target) && target is not null;
            var hasName = OptionalString(link, "unresolved", where, out var unresolved) && unresolved is not null;
            if (hasTable && hasName) { Content($"{where}: a link is either to a table or an unresolved name, never both."); ok = false; }
            else if (hasTable) pendingLinks.Add((entry, target!, where));
            else if (hasName)
            {
                if (unresolved!.Trim().Length == 0) { Content($"{where}: an unresolved link needs a name."); ok = false; }
                else entry.UnresolvedLinkName = unresolved;
            }
            else { Shape($"{where}: a link must name a table or an unresolved name."); ok = false; }
        }

        // ---- shape helpers --------------------------------------------------------------------------------------------

        private string? Id(JsonElement element, string where, Dictionary<string, int> ids, int index)
        {
            if (RequiredString(element, "id", where) is not { } id) return null;
            if (id.Trim().Length == 0) { Shape($"{where} has a blank id."); return null; }
            if (!ids.TryAdd(id, index)) Shape($"The id \"{id}\" is used more than once.");
            return id;
        }

        /// <summary>A required, non-blank name, exactly as written (never trimmed).</summary>
        private string? Name(JsonElement element, string property, string where)
        {
            if (RequiredString(element, property, where) is not { } name) return null;
            if (name.Trim().Length == 0) { Content($"{where} has no name."); return null; }
            return name;
        }

        private JsonElement? Object(JsonElement element, string property, string where)
        {
            if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object) return value;
            Shape($"{where} has no \"{property}\" object.");
            return null;
        }

        private IEnumerable<(JsonElement Element, int Index)> Array(JsonElement element, string property, string where)
        {
            if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array)
                return value.EnumerateArray().Select((e, i) => (e, i)).ToList();
            Shape($"{where} has no \"{property}\" list.");
            return [];
        }

        private string? RequiredString(JsonElement element, string property, string where)
        {
            if (element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
            Shape($"{where} has no \"{property}\" text.");
            return null;
        }

        /// <summary>False only when the property is present with the wrong type; absent or null is fine and gives null.</summary>
        private bool OptionalString(JsonElement element, string property, string where, out string? value)
        {
            value = null;
            if (!element.TryGetProperty(property, out var found) || found.ValueKind == JsonValueKind.Null) return true;
            if (found.ValueKind == JsonValueKind.String) { value = found.GetString(); return true; }
            Shape($"{where}: \"{property}\" must be text.");
            return false;
        }

        private void Shape(string message) => _problems.Add((Layer.Shape, message));
        private void Content(string message) => _problems.Add((Layer.Content, message));
        private void Relationship(string message) => _problems.Add((Layer.Relationship, message));
    }
}
