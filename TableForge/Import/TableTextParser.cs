using System.Text.RegularExpressions;
using TableForge.Domain;

namespace TableForge.Import;

/// <summary>Entries parsed from row-only text (no table heading, dice or result set detection), with reviewable issues.</summary>
/// <param name="Issues">Entry-targeted issues carry an <see cref="ParseIssue.EntryIndex"/> into <paramref name="Entries"/>.</param>
public sealed record RowsParseResult(List<EntryDraft> Entries, List<ParseIssue> Issues);

/// <summary>
/// Deterministic interpretation of copied table text: a heading line with the dice, then numbered rows,
/// optionally grouped under result set headings, optionally laid out in side-by-side columns.
/// It proposes structure conservatively; anything materially uncertain is reported as a <see cref="ParseIssue"/>.
/// </summary>
public static partial class TableTextParser
{
    private static readonly HashSet<string> SmallWords = ["a", "an", "and", "of", "the", "in", "on", "to", "for", "or"];

    /// <summary>Interprets a whole pasted table: heading, dice, result sets and rows.</summary>
    public static TableImportDraft Parse(string? rawText)
    {
        var source = rawText ?? "";
        var raw = LineBreak().Split(source).Select(NormalizeRaw).ToArray();
        var lines = raw.Select(Collapse).ToArray();

        var draft = new TableImportDraft { SourceText = source };

        var i = 0;
        var headingCopies = 1;
        while (i < lines.Length && lines[i].Length == 0) i++;

        if (i < lines.Length && !EntryLine().IsMatch(lines[i]))
        {
            headingCopies = ParseHeading(lines[i], i + 1, draft);
            i++;
        }
        else
        {
            draft.Issues.Add(new(ParseIssueCode.NoTableName, ParseIssueSeverity.Warning, ParseIssueTarget.TableName,
                "No table heading was found before the first row; enter a name."));
            draft.Issues.Add(NoDice());
        }

        DiceExpression? dice = DiceExpression.TryParse(draft.DiceText, out var parsed) ? parsed : null;
        var interpreter = new Interpreter(raw, lines, dice, allowHeadings: true, label: "Line", trackSourceLines: true, headingCopies);
        interpreter.Run(i);

        draft.ResultSets = interpreter.Sets;
        draft.Issues.AddRange(interpreter.FinishIssues());
        TryParallelOutputs(draft, raw);
        return draft;
    }

    // ---- parallel output columns --------------------------------------------------------------------
    //
    // Continuation columns (01-02 Ael | 51-52 Wulf) are more rows of ONE result set. Parallel outputs are the opposite:
    //
    //     D8 DIFFICULTY MODIFIER          one roll, resolved against two result sets
    //     1 Child's play +30              -> Difficulty: Child's play     Modifier: +30
    //     4-5 Normal +0                   -> Difficulty: Normal           Modifier: +0
    //
    // The evidence has to be structural, so it is only attempted on a plain single-set table (no other interpretation
    // issues), with at least three rows, and only when every row splits the same way:
    //   * by column gaps (two or more spaces, or a tab), giving the same number of fields on every row; or
    //   * (two headings only) by a trailing field of one recognisable shape on every row, such as +30 / -10 or 2d6.
    // The number of fields must also fit the number of words in the heading. Otherwise nothing is restructured.

    private static readonly HashSet<ParseIssueCode> ParallelBlockers =
    [
        ParseIssueCode.ContinuationJoined, ParseIssueCode.SideBySideSplit, ParseIssueCode.MultipleRangesOnLine,
        ParseIssueCode.UnrecognizedLine, ParseIssueCode.AmbiguousSectionBreak, ParseIssueCode.ProbableResultSetHeading,
        ParseIssueCode.UnnamedResultSet, ParseIssueCode.ParagraphsAttached, ParseIssueCode.MultipleParagraphs,
        ParseIssueCode.NumberedLineKeptAsText, ParseIssueCode.HeadingRepeated, ParseIssueCode.AmbiguousHeading,
        ParseIssueCode.EmptyEntryText, ParseIssueCode.NoEntries,
    ];

    private static void TryParallelOutputs(TableImportDraft draft, string[] raw)
    {
        if (draft.ResultSets.Count != 1 || draft.DiceText.Length == 0) return;
        var set = draft.ResultSets[0];
        if (set.Name.Length != 0 || set.Entries.Count < 3) return;
        if (set.Entries.Any(e => e.Text.Length == 0 || e.SourceLineStart <= 0 || e.SourceLineStart != e.SourceLineEnd)) return;
        if (draft.Issues.Any(i => ParallelBlockers.Contains(i.Code))) return;

        var words = draft.TableName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var byGaps = SplitByColumnGaps(set, raw);
        var fields = byGaps;
        var typedKind = "";
        if (fields is null && words.Length >= 2) fields = SplitByTrailingFields(set, words.Length, out typedKind);
        if (fields is null && words.Length >= 3) fields = SplitByTypedTrailingColumns(set, words.Length, out typedKind);
        if (fields is null) return;

        var columns = fields[0].Length;
        if (words.Length < columns) return; // more columns than heading words: nothing to name them from

        var named = words.Length == columns;
        draft.ResultSets = Enumerable.Range(0, columns).Select(c => new ResultSetDraft
        {
            Name = named ? words[c] : "",
            Entries = set.Entries.Select((e, i) => new EntryDraft
            {
                RangeText = e.RangeText, // every output column shares the row's roll
                Text = fields[i][c],
                SourceLineStart = e.SourceLineStart,
                SourceLineEnd = e.SourceLineEnd,
            }).ToList(),
        }).ToList();

        var how = byGaps is not null ? "separated by column gaps"
            : columns == 2 ? $"ending in a {typedKind} on every row" : $"each ending in a recognizable shape ({typedKind}) on every row";
        var (severity, advice) = !named
            ? (ParseIssueSeverity.Warning, $"The heading has {words.Length} words for {columns} columns, so the result sets are unnamed: name them.")
            : typedKind.Contains("dice expression")
                ? (ParseIssueSeverity.Warning, "If the heading is really one title, delete one of the result sets and merge the text.")
                : (ParseIssueSeverity.Info, "If the heading is really one title, delete one of the result sets and merge the text.");
        draft.Issues.Add(new(ParseIssueCode.ParallelOutputsSplit, severity, ParseIssueTarget.Source,
            $"Every row shares one roll and has {columns} outputs {how}, so each output became its own result set" +
            (named ? $" (named from the heading: {string.Join(", ", words)}). " : ". ") + advice));
    }

    /// <summary>Fields of each row split at column gaps, if every row has the same count (2 to 4) and no empty field.</summary>
    private static List<string[]>? SplitByColumnGaps(ResultSetDraft set, string[] raw)
    {
        var rows = new List<string[]>();
        foreach (var entry in set.Entries)
        {
            var m = EntryLine().Match(raw[entry.SourceLineStart - 1]);
            if (!m.Success) return null;
            var parts = ColumnGap().Split(m.Groups["text"].Value).Select(p => Collapse(p)).ToArray();
            if (parts.Length is < 2 or > 4 || parts.Any(p => p.Length == 0)) return null;
            rows.Add(parts);
        }
        return rows.All(r => r.Length == rows[0].Length) ? rows : null;
    }

    /// <summary>
    /// <paramref name="columns"/> fields per row, peeled one at a time off the end of the text: the leading field is free
    /// text (must contain a letter), and each of the trailing fields after it is of one recognisable shape (a signed
    /// number, or a dice expression) that holds consistently down its own column, on every row. This is the no-gap
    /// counterpart of <see cref="SplitByColumnGaps"/> — generalized to any number of outputs, not just two, since PDF
    /// extraction that collapses column gaps to single spaces gives no other structural evidence to split on.
    /// </summary>
    /// <param name="extended">
    /// Also accepts whole-number columns (only beside a signed-number or dice column) and a lone dash as a placeholder
    /// cell. See <see cref="SplitByTypedTrailingColumns"/>.
    /// </param>
    private static List<string[]>? SplitByTrailingFields(ResultSetDraft set, int columns, out string kind, bool extended = false)
    {
        kind = "";
        var extra = columns - 1;
        if (columns is < 2 or > 4) return null;
        if (extended && extra < 2) return null;

        var heads = new string[set.Entries.Count];
        var tails = new string[set.Entries.Count][];
        for (var r = 0; r < set.Entries.Count; r++)
        {
            var remaining = set.Entries[r].Text;
            var peeled = new string[extra];
            for (var p = extra - 1; p >= 0; p--)
            {
                var cut = remaining.LastIndexOf(' ');
                if (cut <= 0) return null;
                peeled[p] = remaining[(cut + 1)..];
                remaining = remaining[..cut].TrimEnd();
            }
            if (remaining.Length == 0 || !remaining.Any(char.IsLetter)) return null;
            heads[r] = remaining;
            tails[r] = peeled;
        }

        var shapes = extended
            ? new[] { ("signed number", SignedNumber()), ("dice expression", DiceExpressionWord()), ("whole number", WholeNumber()) }
            : new[] { ("signed number", SignedNumber()), ("dice expression", DiceExpressionWord()) };
        var kinds = new string[extra];
        for (var p = 0; p < extra; p++)
        {
            var cells = Enumerable.Range(0, set.Entries.Count).Select(r => tails[r][p]).ToList();
            var dashes = extended ? cells.Count(c => DashToken().IsMatch(c)) : 0;
            var values = cells.Where(c => !extended || !DashToken().IsMatch(c)).ToList();
            if (values.Count < 2 || values.Count <= dashes) return null; // a dash is a placeholder in a typed column, never the column's evidence
            var found = false;
            foreach (var (name, shape) in shapes)
            {
                if (!values.All(shape.IsMatch)) continue;
                kinds[p] = name;
                found = true;
                break;
            }
            if (!found) return null;
        }
        // Plain numbers end many ordinary rows ("Arrows 20"), so they only count beside a column of a stronger shape.
        if (kinds.All(k => k == "whole number")) return null;

        kind = string.Join(", ", kinds.Distinct());
        return Enumerable.Range(0, set.Entries.Count).Select(r => (string[])[heads[r], .. tails[r]]).ToList();
    }

    /// <summary>
    /// The fallback when the heading's words do not give the column count ("D100 WIND TYPE STRENGTH HULL DAMAGE CAUSED" is
    /// six words over three columns): three or four columns, whichever one alone fits, each column after the free-text first
    /// one holding a single recognisable shape all the way down. Two or more such typed columns are the structural evidence
    /// here, so one typed trailing field under a longer heading is still not guessed at. Whole numbers count only beside a
    /// signed-number or dice column, and a lone dash may stand in for a value ("–" for no hull damage). Which heading words
    /// name which column is not guessed either: the result sets stay unnamed unless the words match the columns one to one.
    /// </summary>
    private static List<string[]>? SplitByTypedTrailingColumns(ResultSetDraft set, int words, out string kind)
    {
        kind = "";
        List<string[]>? only = null;
        foreach (var columns in new[] { 3, 4 })
        {
            if (columns > words || SplitByTrailingFields(set, columns, out var k, extended: true) is not { } fields) continue;
            if (only is not null) { kind = ""; return null; } // both counts fit: ambiguous, so nothing is split
            only = fields;
            kind = k;
        }
        return only;
    }

    /// <summary>
    /// Interprets text as entry rows only, for pasting into an existing result set. It never looks for a table
    /// heading, a dice expression or further result sets: such lines are reported as not understood.
    /// </summary>
    public static RowsParseResult ParseRows(string? rawText, DiceExpression? dice)
    {
        var raw = LineBreak().Split(rawText ?? "").Select(NormalizeRaw).ToArray();
        var interpreter = new Interpreter(raw, raw.Select(Collapse).ToArray(), dice, allowHeadings: false, label: "Pasted line", trackSourceLines: false);
        interpreter.Run(0);
        var issues = interpreter.FinishIssues();
        return new RowsParseResult(interpreter.Sets[0].Entries, issues);
    }

    // ---- interpretation of rows, headings and columns ----------------------------------------

    /// <param name="headingCopies">
    /// How many times the table heading was printed side by side (1 unless it was recovered as repeated; 0 if it held different dice).
    /// </param>
    private sealed class Interpreter(string[] raw, string[] lines, DiceExpression? dice, bool allowHeadings, string label, bool trackSourceLines, int headingCopies = 1)
    {
        private sealed class Block
        {
            public int StartLine;
            public int EndLine;
            public List<List<EntryDraft>> Columns { get; } = [];
        }

        private readonly List<(ParseIssue Issue, EntryDraft? Entry)> _pending = [];
        private EntryDraft? _last;          // the entry a following wrapped line would join
        private bool _lastWasSplit;         // ...unless it came from a side-by-side line (which column would it belong to?)
        private bool _blankSinceLast;
        private Block? _block;              // side-by-side columns being collected
        private bool _lastStandalone;       // the last row was a lone number/range: the paragraph(s) after it are its text
        private bool _paragraphInfoAdded;
        private readonly HashSet<EntryDraft> _multiParagraph = [];

        public List<ResultSetDraft> Sets { get; } = [new()];
        private ResultSetDraft Current => Sets[^1];

        public void Run(int start)
        {
            for (var k = start; k < lines.Length; k++)
            {
                if (lines[k].Length == 0)
                {
                    FlushBlock();
                    _blankSinceLast = true;
                    continue;
                }

                var entries = ReadEntries(k, out var warning);
                if (entries is null) { NonEntryLine(k); continue; }
                if (LooksLikeParagraphText(entries, k)) continue;
                AddEntryLine(entries, k + 1, warning);
            }
            FlushBlock();
        }

        // ---- text that follows a row number that stands alone ------------------------------------

        /// <summary>
        /// Once a row number has stood on its own line, a later line that starts with a number is far more likely to be a
        /// wrapped line of that paragraph ("…every\n10 days…") than the next row, unless it simply continues the numbering.
        /// Such a line is kept as text and flagged; a lone number/range still always starts a new row.
        /// </summary>
        private bool LooksLikeParagraphText(List<LineEntry> entries, int k)
        {
            if (!_lastStandalone || _last is null || entries.Count != 1 || entries[0].Text.Length == 0) return false;
            if (Parse(_last.RangeText) is not { } previous || Parse(entries[0].Range) is not { } next || next.Min == previous.Max + 1) return false;
            if (dice is { } d && d.NextLegal(previous.Max) == next.Min) return false;   // 16 then 21 is continuous numbering on a d66

            AttachParagraphLine(lines[k], k + 1);
            _pending.Add((new(ParseIssueCode.NumberedLineKeptAsText, ParseIssueSeverity.Warning, ParseIssueTarget.Entry,
                $"{label} {k + 1} (“{lines[k]}”) starts with a number but does not continue the numbering, and the rows so far had their numbers on separate lines, " +
                $"so it was kept as text of row {_last.RangeText}. If it is really a new row, add it.", SourceLine: k + 1), _last));
            return true;
        }

        /// <summary>
        /// Adds one line of paragraph text to the last row. Wrapped lines of a paragraph join with a space; a further paragraph
        /// (after a blank line) is kept with a blank line between, and flagged because a page header or footer may have slipped in.
        /// </summary>
        private void AttachParagraphLine(string line, int lineNo)
        {
            var entry = _last!;
            var first = entry.Text.Length == 0;
            var newParagraph = !first && _blankSinceLast;
            entry.Text = first ? line : newParagraph ? $"{entry.Text}\n\n{line}" : $"{entry.Text} {line}";
            _blankSinceLast = false;
            if (trackSourceLines) entry.SourceLineEnd = lineNo;

            if (first && !_paragraphInfoAdded)
            {
                _paragraphInfoAdded = true;
                _pending.Add((new(ParseIssueCode.ParagraphsAttached, ParseIssueSeverity.Info, ParseIssueTarget.Entry,
                    $"Row numbers stood on their own lines, so the paragraph after each number was attached to it (first seen at row {entry.RangeText}). " +
                    "Check that each paragraph belongs to its number.", SourceLine: lineNo), entry));
            }
            if (newParagraph && _multiParagraph.Add(entry))
                _pending.Add((new(ParseIssueCode.MultipleParagraphs, ParseIssueSeverity.Warning, ParseIssueTarget.Entry,
                    $"Row {entry.RangeText} has more than one paragraph after its number (another starts at {label.ToLowerInvariant()} {lineNo}). " +
                    "A page header or footer may have been included; check the text.", SourceLine: lineNo), entry));
        }

        /// <summary>Issues in source order, with entry-targeted ones resolved to their final result set and position.</summary>
        public List<ParseIssue> FinishIssues()
        {
            foreach (var (set, s) in Sets.Select((set, s) => (set, s)))
                foreach (var (entry, e) in set.Entries.Select((entry, e) => (entry, e)))
                    if (entry.Text.Length == 0)
                        _pending.Add((new(ParseIssueCode.EmptyEntryText, ParseIssueSeverity.Warning, ParseIssueTarget.Entry,
                            $"Row {entry.RangeText} has no text.", SourceLine: entry.SourceLineStart > 0 ? entry.SourceLineStart : null), entry));

            if (Sets.All(s => s.Entries.Count == 0))
                _pending.Add((new(ParseIssueCode.NoEntries, ParseIssueSeverity.Error, ParseIssueTarget.Source,
                    "No numbered rows were found (expected lines such as \"1-2 Text\" or \"3 Text\")."), null));

            if (Sets.Count > 1 && Sets[0].Name.Length == 0)
                _pending.Add((new(ParseIssueCode.UnnamedResultSet, ParseIssueSeverity.Warning, ParseIssueTarget.ResultSet,
                    "Rows before the first heading were placed in an unnamed result set (the first tab). Name it, or move its rows.",
                    ResultSetIndex: 0), null));

            return _pending
                .Select(p => p.Entry is null ? p.Issue : Locate(p.Issue, p.Entry))
                .OrderBy(i => i.SourceLine ?? int.MaxValue) // stable: unspecific issues keep their order at the end
                .ToList();
        }

        private ParseIssue Locate(ParseIssue issue, EntryDraft entry)
        {
            for (var s = 0; s < Sets.Count; s++)
            {
                var e = Sets[s].Entries.IndexOf(entry);
                if (e >= 0) return issue with { ResultSetIndex = s, EntryIndex = e };
            }
            return issue;
        }

        // ---- rows ---------------------------------------------------------------------------

        private sealed record LineEntry(string Range, string Text);

        /// <summary>Returns the entries on a line (several for a confident side-by-side split), or null if it is not a row.</summary>
        private List<LineEntry>? ReadEntries(int k, out string? warning)
        {
            warning = null;
            var m = EntryLine().Match(lines[k]);
            if (!m.Success) return null;

            var columns = ColumnCandidates(raw[k]);
            if (columns is { Count: >= 2 })
            {
                if (ConfidentColumns(columns) && HasColumnEvidence(k, columns)) return columns;
                warning = $"{label} {k + 1} seems to hold more than one range ({string.Join(", ", columns.Select(c => c.Range))}) " +
                          "but there was not enough evidence that they are side-by-side columns (a lone line, or neighbouring lines that do not " +
                          "form consistent columns), so it is kept as one entry. If they are columns, split it.";
            }
            else if (GaplessAt(k) is { } flattened)
            {
                if (GaplessBlockIsValid(k)) return flattened.Pieces; // page columns squeezed down to single spaces, proven by the whole block
                if (flattened.Kind != GaplessKind.Spans)
                    warning = $"{label} {k + 1} seems to hold {flattened.Pieces.Count} numbered results side by side ({string.Join(", ", flattened.Pieces.Select(p => p.Range))}) " +
                              "but the lines around it do not form complete columns covering the whole dice range, so it is kept as one entry. If they are columns, split it.";
            }

            var single = new LineEntry(RangeSeparator().Replace(m.Groups["range"].Value, "-"), LeadingSeparator().Replace(m.Groups["text"].Value, ""));

            // Without a column gap there is no safe split, but a later, higher range in the text is worth a look.
            if (warning is null && LaterRange().Match(single.Text) is { Success: true } later
                && Parse(single.Range) is { } first && Parse(later.Groups["r"].Value) is { } second && second.Min > first.Max)
            {
                warning = $"{label} {k + 1} contains a second range ({later.Groups["r"].Value}) inside its text. It is kept as one entry; " +
                          "if it is really two side-by-side entries, split it.";
            }
            return [single];
        }

        /// <summary>Range/text pieces of a line where each range starts after a column gap (two spaces or a tab).</summary>
        private static List<LineEntry>? ColumnCandidates(string rawLine)
        {
            var starts = ColumnStart().Matches(rawLine);
            if (starts.Count < 2) return null;

            var pieces = new List<LineEntry>();
            for (var i = 0; i < starts.Count; i++)
            {
                var end = i + 1 < starts.Count ? starts[i + 1].Index : rawLine.Length;
                var m = EntryLine().Match(Collapse(rawLine[starts[i].Index..end]));
                if (!m.Success || m.Groups["text"].Value.Length == 0) return null;
                pieces.Add(new LineEntry(RangeSeparator().Replace(m.Groups["range"].Value, "-"), LeadingSeparator().Replace(m.Groups["text"].Value, "")));
            }
            return pieces;
        }

        /// <summary>
        /// A line's pieces are individually plausible only if each has text with a letter, each range fits the dice
        /// (when known), and the ranges strictly increase left to right.
        /// </summary>
        private bool ConfidentColumns(List<LineEntry> pieces)
        {
            var previousMax = int.MinValue;
            foreach (var piece in pieces)
            {
                if (!piece.Text.Any(char.IsLetter)) return false;
                if (!RangeText.TryParse(piece.Range, dice, out var range, out _) || range.Min <= previousMax) return false;
                if (dice is { } d && (range.Min < d.Min || range.Max > d.Max)) return false;
                previousMax = range.Max;
            }
            return true;
        }

        private readonly Dictionary<int, List<LineEntry>?> _columnCache = [];

        private List<LineEntry>? ColumnsAt(int k)
        {
            if (k < 0 || k >= lines.Length || lines[k].Length == 0) return null;
            if (!_columnCache.TryGetValue(k, out var pieces))
                _columnCache[k] = pieces = ColumnCandidates(raw[k]);
            return pieces;
        }

        /// <summary>
        /// Pieces that merely resemble entries are not enough (think "4 Rope    10 gp"). Splitting needs evidence that
        /// the layout really is columns: either adjacent lines with the same column structure whose rows run on
        /// consecutively down each column and whose columns follow one another (at least three such lines if any piece is a
        /// single value rather than a span), or, for a lone line, pieces that are all explicit spans such as 01-05.
        /// </summary>
        private bool HasColumnEvidence(int k, List<LineEntry> pieces)
        {
            var n = pieces.Count;
            bool Matches(int line) => ColumnsAt(line) is { } p && p.Count == n && ConfidentColumns(p);

            int first = k, last = k;
            while (Matches(first - 1)) first--;
            while (Matches(last + 1)) last++;

            if (first == last) return pieces.All(p => p.Range.Contains('-'));

            // Explicit spans (01-05) are distinctive enough for two matching lines. Single exact values ("4 Rope    10 gp")
            // look like columns far too easily, so they need three adjacent lines showing the same structure.
            var lineCount = last - first + 1;
            var anySingleValue = Enumerable.Range(first, lineCount).Any(line => ColumnsAt(line)!.Any(p => !p.Range.Contains('-')));
            if (anySingleValue && lineCount < 3) return false;

            var rows = Enumerable.Range(first, lineCount)
                .Select(line => ColumnsAt(line)!.Select(p => Parse(p.Range)!.Value).ToList())
                .ToList();

            for (var c = 0; c < n; c++)
                for (var r = 1; r < rows.Count; r++)
                    if (rows[r][c].Min != rows[r - 1][c].Max + 1) return false;      // rows continue straight down a column

            for (var c = 1; c < n; c++)
                if (rows.Max(row => row[c - 1].Max) >= rows.Min(row => row[c].Min)) return false;  // columns follow one another
            return true;
        }

        // ---- continuation columns squeezed to single spaces ----------------------------------------
        //
        // A table printed in page columns ("01-02 Ael 51-52 Wulf") is one result set whose later columns continue the first.
        // When extraction removes the visual gap there is nothing to look at within a line, so the evidence has to come from the
        // whole block: every line holds the same number of ranges, each column runs on consecutively down the block, and each
        // column picks up exactly where the one before it ends. Anything less stays one row per line and is flagged.
        //
        // Two explicit spans per line (01-02, also written 01 - 02) are distinctive enough on their own. Single values ("1 A 11 K")
        // look like prices or counts far too easily, so they are only read as columns with one of two further proofs:
        //   * every value on every line is followed by the same separator ("1 – Desecrate 26 – Expose", "1. A 11. K"); or
        //   * the heading is printed once per column ("D20 RESULT D20 RESULT"), as many times as each line has values;
        // and, either way, the columns are all the same length and cover the whole dice range exactly once.

        private const int MinFlattenedColumnLines = 4;
        private readonly Dictionary<int, GaplessLine?> _gaplessCache = [];
        private readonly Dictionary<int, bool> _gaplessBlocks = [];

        private enum GaplessKind { Spans, Separated, RepeatedHeading }

        private sealed record GaplessLine(GaplessKind Kind, List<LineEntry> Pieces);

        /// <summary>The line read as two or more "range text" groups, if it has one of the shapes above (else null).</summary>
        private GaplessLine? GaplessAt(int k)
        {
            if (k < 0 || k >= lines.Length || lines[k].Length == 0) return null;
            if (_gaplessCache.TryGetValue(k, out var cached)) return cached;

            var tokens = SpacedSpan().Replace(lines[k], "${a}-${b}").Split(' ');
            var result = SpanToken().IsMatch(tokens[0]) ? SpanPair(tokens) : SeparatedGroups(tokens) ?? RepeatedHeadingGroups(tokens);
            return _gaplessCache[k] = result;
        }

        /// <summary>"span text span text": exactly one further span after the first.</summary>
        private static GaplessLine? SpanPair(string[] tokens)
        {
            var spans = Enumerable.Range(1, tokens.Length - 1).Where(i => SpanToken().IsMatch(tokens[i])).ToList();
            return spans.Count == 1 ? Groups(GaplessKind.Spans, tokens, [0, spans[0]], valueTokens: 1) : null;
        }

        /// <summary>
        /// "1 – Desecrate 26 – Expose …" (a dash on its own after each value) or "1. Desecrate 26. Expose …" (a mark attached to
        /// each value): every group after the first starts with a whole number carrying exactly the separator the first one has.
        /// </summary>
        private static GaplessLine? SeparatedGroups(string[] tokens)
        {
            if (tokens.Length < 2) return null;
            string mark;
            int valueTokens;
            if (WholeNumber().IsMatch(tokens[0]) && DashToken().IsMatch(tokens[1])) (mark, valueTokens) = (tokens[1], 2);
            else if (MarkedNumber().Match(tokens[0]) is { Success: true } m) (mark, valueTokens) = (m.Groups["mark"].Value, 1);
            else return null;

            bool Starts(int i) => valueTokens == 2
                ? i + 1 < tokens.Length && WholeNumber().IsMatch(tokens[i]) && tokens[i + 1] == mark
                : MarkedNumber().Match(tokens[i]) is { Success: true } v && v.Groups["mark"].Value == mark;

            var starts = Enumerable.Range(0, tokens.Length).Where(Starts).ToList();
            return starts.Count >= 2 ? Groups(GaplessKind.Separated, tokens, starts, valueTokens) : null;
        }

        /// <summary>"1 A 11 K" under a heading printed once per column: exactly as many values on the line as heading copies.</summary>
        private GaplessLine? RepeatedHeadingGroups(string[] tokens)
        {
            if (headingCopies < 2 || dice is null || !WholeNumber().IsMatch(tokens[0])) return null;
            var starts = Enumerable.Range(0, tokens.Length).Where(i => RangeToken().IsMatch(tokens[i])).ToList();
            return starts.Count == headingCopies ? Groups(GaplessKind.RepeatedHeading, tokens, starts, valueTokens: 1) : null;
        }

        /// <summary>The groups starting at <paramref name="starts"/>, provided each has text with a letter after its value.</summary>
        private static GaplessLine? Groups(GaplessKind kind, string[] tokens, List<int> starts, int valueTokens)
        {
            var pieces = new List<LineEntry>();
            for (var g = 0; g < starts.Count; g++)
            {
                var from = starts[g] + valueTokens;
                var to = g + 1 < starts.Count ? starts[g + 1] : tokens.Length;
                if (from >= to) return null;
                var text = string.Join(' ', tokens[from..to]);
                if (!text.Any(char.IsLetter)) return null;
                var value = kind == GaplessKind.Separated && valueTokens == 1 ? MarkedNumber().Match(tokens[starts[g]]).Groups["value"].Value : tokens[starts[g]];
                pieces.Add(new(RangeSeparator().Replace(value, "-"), text));
            }
            return new GaplessLine(kind, pieces);
        }

        private bool GaplessBlockIsValid(int k)
        {
            int first = k, last = k;
            while (GaplessAt(first - 1) is not null) first--;
            while (GaplessAt(last + 1) is not null) last++;
            return _gaplessBlocks.TryGetValue(first, out var known) ? known : _gaplessBlocks[first] = EvaluateGaplessBlock(first, last);
        }

        private bool EvaluateGaplessBlock(int first, int last)
        {
            if (last - first + 1 < MinFlattenedColumnLines) return false;

            var shape = GaplessAt(first)!;
            var columns = shape.Pieces.Select(_ => new List<ParsedRange>()).ToList();
            for (var line = first; line <= last; line++)
            {
                var pieces = GaplessAt(line)!;
                if (pieces.Kind != shape.Kind || pieces.Pieces.Count != columns.Count) return false;   // one shape for the whole block
                for (var c = 0; c < columns.Count; c++)
                {
                    if (Parse(pieces.Pieces[c].Range) is not { } range) return false;
                    columns[c].Add(range);
                }
            }

            foreach (var column in columns)
                for (var i = 1; i < column.Count; i++)
                    if (!Follows(column[i - 1], column[i])) return false; // each column runs on

            // With two columns (as before), the left column may be one row longer than the right: those rows follow as ordinary
            // lines. Separated and wider layouts must have columns of equal length.
            var ends = columns.Select(c => c[^1]).ToList();
            var ranges = columns.SelectMany(c => c).ToList();
            if (columns.Count == 2 && shape.Kind != GaplessKind.Separated)
            {
                for (var n = last + 1; n < lines.Length && lines[n].Length > 0; n++)
                {
                    var m = EntryLine().Match(lines[n]);
                    if (!m.Success || Parse(RangeSeparator().Replace(m.Groups["range"].Value, "-")) is not { } extra || !Follows(ends[0], extra)) break;
                    ends[0] = extra;
                    ranges.Add(extra);
                }
            }

            for (var c = 1; c < columns.Count; c++)
                if (!Follows(ends[c - 1], columns[c][0])) return false; // each column continues exactly where the one before it ends

            if (shape.Kind != GaplessKind.Spans && headingCopies == 0) return false;   // single values need one certain die
            if (dice is not { } d) return shape.Kind == GaplessKind.Spans;
            if (!ranges.All(r => d.IsLegal(r.Min) && d.IsLegal(r.Max))) return false;   // a d66 has no 17 or 60
            return shape.Kind == GaplessKind.Spans || (columns[0][0].Min == d.Min && ends[^1].Max == d.Max);
        }

        /// <summary>Whether <paramref name="next"/> starts right after <paramref name="previous"/> ends (16 then 21 on a d66).</summary>
        private bool Follows(ParsedRange previous, ParsedRange next) =>
            dice is { IsD66: true } d ? d.NextLegal(previous.Max) == next.Min : next.Min == previous.Max + 1;

        private ParsedRange? Parse(string range) => RangeText.TryParse(range, dice, out var r, out _) ? r : null;

        private EntryDraft NewEntry(LineEntry item, int lineNo) => new()
        {
            RangeText = item.Range,
            Text = item.Text,
            SourceLineStart = trackSourceLines ? lineNo : 0,
            SourceLineEnd = trackSourceLines ? lineNo : 0,
        };

        private void AddEntryLine(List<LineEntry> items, int lineNo, string? warning)
        {
            _blankSinceLast = false;
            var split = items.Count > 1;

            if (split)
            {
                _block ??= new Block { StartLine = lineNo };
                _block.EndLine = lineNo;
                for (var c = 0; c < items.Count; c++)
                {
                    while (_block.Columns.Count <= c) _block.Columns.Add([]);
                    _last = NewEntry(items[c], lineNo);
                    _block.Columns[c].Add(_last);
                }
            }
            else
            {
                _last = NewEntry(items[0], lineNo);
                _lastStandalone = items[0].Text.Length == 0;
                if (_block is null)
                {
                    Current.Entries.Add(_last);
                }
                else
                {
                    // A row with only one piece inside a block of columns: put it under the column it continues.
                    _block.EndLine = lineNo;
                    _block.Columns[BestColumn(_block, _last)].Add(_last);
                }
                if (warning is not null)
                    _pending.Add((new(ParseIssueCode.MultipleRangesOnLine, ParseIssueSeverity.Warning, ParseIssueTarget.Entry, warning,
                        SourceLine: lineNo), _last));
            }
            _lastWasSplit = split;
            if (split) _lastStandalone = false;
        }

        private int BestColumn(Block block, EntryDraft entry)
        {
            if (Parse(entry.RangeText) is not { } range) return 0;
            var best = 0;
            var bestMax = int.MinValue;
            for (var c = 0; c < block.Columns.Count; c++)
            {
                if (block.Columns[c].Count > 0 && Parse(block.Columns[c][^1].RangeText) is { } tail && tail.Max < range.Min && tail.Max > bestMax)
                {
                    best = c;
                    bestMax = tail.Max;
                }
            }
            return best;
        }

        /// <summary>Adds collected columns to the current set column by column (reading order), noting the split once.</summary>
        private void FlushBlock()
        {
            if (_block is null) return;
            var block = _block;
            _block = null;

            foreach (var column in block.Columns) Current.Entries.AddRange(column);

            var where = block.StartLine == block.EndLine ? $"{label} {block.StartLine} held" : $"{label}s {block.StartLine}–{block.EndLine} held";
            _pending.Add((new(ParseIssueCode.SideBySideSplit, ParseIssueSeverity.Info, ParseIssueTarget.Entry,
                $"{where} {block.Columns.Count} side-by-side columns and were split into separate entries, column by column. Check the order and text.",
                SourceLine: block.StartLine), block.Columns[0][0]));
        }

        // ---- lines that are not rows -------------------------------------------------------------

        private void NonEntryLine(int k)
        {
            var line = lines[k];
            var lineNo = k + 1;
            FlushBlock();

            var next = NextNonBlank(k);
            var nextIsRow = next >= 0 && EntryLine().IsMatch(lines[next]);
            var blankBefore = k == 0 || lines[k - 1].Length == 0;
            var shaped = HeadingShape(line, out var name, out var upper);

            if (allowHeadings && nextIsRow && shaped)
            {
                var previous = Current.Entries.LastOrDefault();
                if (previous is null)
                {
                    StartSet(name);
                    return;
                }

                if (RestartsAt(previous, next))
                {
                    StartSet(name);
                    if (!upper && !blankBefore)
                        _pending.Add((new(ParseIssueCode.ProbableResultSetHeading, ParseIssueSeverity.Warning, ParseIssueTarget.ResultSet,
                            $"{label} {lineNo} (“{line}”) was treated as the start of the result set “{name}”, but nothing separated it from the rows above. Check the split.",
                            ResultSetIndex: Sets.Count - 1, SourceLine: lineNo), null));
                    return;
                }
            }

            // A row that was only a number takes the paragraph(s) after it, even across blank lines, until the next row begins.
            if (_last is not null && _lastStandalone)
            {
                AttachParagraphLine(line, lineNo);
                return;
            }

            if (_last is not null && !_blankSinceLast)
            {
                if (_lastWasSplit)
                {
                    _pending.Add((new(ParseIssueCode.UnrecognizedLine, ParseIssueSeverity.Warning, ParseIssueTarget.Source,
                        $"{label} {lineNo} (“{line}”) follows side-by-side columns. It may be wrapped text from either column, so it was not joined to any entry and is not included.",
                        SourceLine: lineNo), null));
                    _last = null;
                    return;
                }

                _last.Text = _last.Text.Length == 0 ? line : $"{_last.Text} {line}";
                if (trackSourceLines) _last.SourceLineEnd = lineNo;
                _pending.Add((new(ParseIssueCode.ContinuationJoined, ParseIssueSeverity.Warning, ParseIssueTarget.Entry,
                    $"{label} {lineNo} (“{line}”) is not a numbered row, so it was joined to row {_last.RangeText}. Check that it belongs there.",
                    SourceLine: lineNo), _last));
                return;
            }

            var maybeHeading = allowHeadings && nextIsRow && shaped && upper && Current.Entries.Count > 0;
            _pending.Add((maybeHeading
                ? new(ParseIssueCode.AmbiguousSectionBreak, ParseIssueSeverity.Warning, ParseIssueTarget.Source,
                    $"{label} {lineNo} (“{line}”) looks like a result set heading, but the numbering does not restart after it, so no new result set was started and the line is not included. " +
                    "If it is a heading, add a result set and paste its rows.", SourceLine: lineNo)
                : new(ParseIssueCode.UnrecognizedLine, ParseIssueSeverity.Warning, ParseIssueTarget.Source,
                    $"{label} {lineNo} (“{line}”) was not understood and is not included.", SourceLine: lineNo), null));
            _last = null;
        }

        private void StartSet(string name)
        {
            if (Current.Entries.Count == 0 && Current.Name.Length == 0) Current.Name = name;
            else Sets.Add(new ResultSetDraft { Name = name });
            _last = null;
            _lastStandalone = false;
            _blankSinceLast = false;
        }

        private int NextNonBlank(int k)
        {
            for (var n = k + 1; n < lines.Length; n++)
                if (lines[n].Length > 0) return n;
            return -1;
        }

        /// <summary>A new set's numbering starts over: its first row is not after the previous row.</summary>
        private bool RestartsAt(EntryDraft previous, int nextLine)
        {
            var m = EntryLine().Match(lines[nextLine]);
            return Parse(previous.RangeText) is { } before
                && Parse(RangeSeparator().Replace(m.Groups["range"].Value, "-")) is { } after
                && after.Min <= before.Min;
        }
    }

    // ---- table heading ----------------------------------------------------------------------

    /// <returns>
    /// How many times the heading was printed side by side: 1, the number of copies of a repeated heading, or 0 when it held
    /// different dice expressions (so no single die is certain).
    /// </returns>
    private static int ParseHeading(string line, int lineNo, TableImportDraft draft)
    {
        string name = line;
        string? diceText = null;

        Match m;
        if ((m = DiceThenName().Match(line)).Success || (m = NameThenDice().Match(line)).Success || (m = DiceOnly().Match(line)).Success)
        {
            diceText = m.Groups["dice"].Value;
            name = m.Groups["name"].Success ? m.Groups["name"].Value : "";
        }

        if (diceText is null)
        {
            draft.Issues.Add(NoDice());
        }
        else if (DiceExpression.TryParse(diceText, out var dice))
        {
            draft.DiceText = dice.ToString();
        }
        else
        {
            draft.DiceText = diceText;
            draft.Issues.Add(new(ParseIssueCode.UnsupportedDice, ParseIssueSeverity.Error, ParseIssueTarget.Dice,
                $"Dice expression '{diceText}' is not supported. Use a dice expression such as d20, 2d6, or 2d6+1.{DiceExpression.UnsupportedHint(diceText)}",
                SourceLine: lineNo));
        }

        name = RecoverRepeatedHeading(line, name, diceText, lineNo, draft, out var copies);
        draft.TableName = NormalizeName(name);
        if (draft.TableName.Length == 0)
            draft.Issues.Add(new(ParseIssueCode.NoTableName, ParseIssueSeverity.Warning, ParseIssueTarget.TableName,
                "The heading has no table name; enter one.", SourceLine: lineNo));
        return copies;
    }

    /// <summary>
    /// PDF extraction prints a heading once per page column: "D100 SYLLABLE D100 SYLLABLE". When the same dice and the same
    /// words repeat, that is one heading. Any other second dice expression is left alone and flagged, not guessed at.
    /// </summary>
    private static string RecoverRepeatedHeading(string line, string name, string? diceText, int lineNo, TableImportDraft draft, out int copies)
    {
        copies = 1;
        if (diceText is null || !DiceWord().IsMatch(name)) return name;

        var segments = DiceWord().Split(name).Select(s => s.Trim()).ToArray();
        var sameDice = DiceExpression.TryParse(diceText, out var first)
            && DiceWord().Matches(name).All(d => DiceExpression.TryParse(d.Value, out var other) && other == first);
        var repeated = sameDice && segments.Length >= 2 && segments[0].Length > 0
            && segments.All(s => string.Equals(s, segments[0], StringComparison.OrdinalIgnoreCase));

        if (repeated)
        {
            draft.Issues.Add(new(ParseIssueCode.HeadingRepeated, ParseIssueSeverity.Info, ParseIssueTarget.TableName,
                $"The heading “{line}” repeats once per printed column, so it was read as the single heading “{NormalizeName(segments[0])}”.",
                SourceLine: lineNo));
            copies = segments.Length;
            return segments[0];
        }

        draft.Issues.Add(new(ParseIssueCode.AmbiguousHeading, ParseIssueSeverity.Warning, ParseIssueTarget.TableName,
            $"The heading “{line}” holds more than one dice expression and does not simply repeat, so it was kept as one heading. " +
            "Check the table name and dice.", SourceLine: lineNo));
        copies = 0; // no single die is known for certain
        return name;
    }

    private static ParseIssue NoDice() =>
        new(ParseIssueCode.NoDiceExpression, ParseIssueSeverity.Error, ParseIssueTarget.Dice,
            "No dice expression was found in the heading. Use a dice expression such as d20, 2d6, or 2d6+1.");

    /// <summary>
    /// Whether a non-row line could be a result set heading: short, no sentence punctuation, and either
    /// ALL CAPS or Title Case. The caller decides from the surrounding rows whether it actually is one.
    /// </summary>
    private static bool HeadingShape(string line, out string name, out bool upper)
    {
        name = "";
        upper = false;
        var t = line.Trim().TrimEnd(':').Trim();
        if (t.Length is < 2 or > 40 || !t.Any(char.IsLetter)) return false;
        if (".,;!?".Contains(t[^1])) return false;
        if (t.Any(c => !(char.IsLetterOrDigit(c) || " '’&-/()".Contains(c)))) return false;

        var words = t.Split(' ');
        if (words.Length > 6) return false;

        upper = !t.Any(char.IsLower);
        if (!upper)
        {
            for (var w = 0; w < words.Length; w++)
            {
                var first = words[w].FirstOrDefault(char.IsLetter);
                if (first == default) continue;
                if (!char.IsUpper(first) && !(w > 0 && SmallWords.Contains(words[w].ToLowerInvariant()))) return false;
            }
        }

        name = NormalizeName(t);
        return true;
    }

    /// <summary>ALL-CAPS headings are converted to title case; anything with lowercase is left as written.</summary>
    private static string NormalizeName(string name)
    {
        if (!name.Any(char.IsLetter) || name.Any(char.IsLower)) return name;

        var words = name.Split(' ');
        for (var w = 0; w < words.Length; w++)
        {
            var lower = words[w].ToLowerInvariant();
            if (w > 0 && SmallWords.Contains(lower)) { words[w] = lower; continue; }

            var letter = lower.ToCharArray().Select((c, idx) => (c, idx)).FirstOrDefault(x => char.IsLetter(x.c));
            words[w] = char.IsLetter(letter.c) ? lower[..letter.idx] + char.ToUpperInvariant(letter.c) + lower[(letter.idx + 1)..] : lower;
        }
        return string.Join(' ', words);
    }

    /// <summary>Removes invisible characters and turns non-breaking spaces into spaces, keeping the spacing that shows columns.</summary>
    private static string NormalizeRaw(string line) =>
        line.Replace(' ', ' ').Replace("﻿", "").Replace("​", "").Replace("­", "").Trim();

    /// <summary>Collapses runs of spaces and tabs to one space.</summary>
    private static string Collapse(string line) => Spaces().Replace(line, " ").Trim();

    // A row: range, optional "." ")" ":" after it, then text.
    [GeneratedRegex(@"^(?<range>[0-9]+(?:\s*[-–—]\s*[0-9]+)?)(?:[.):]?(?:\s+(?<text>\S.*))?)$")]
    private static partial Regex EntryLine();

    // Two or more spaces, or a tab: the gap between printed columns.
    [GeneratedRegex(@"\t[ \t]*| {2,}[ \t]*")]
    private static partial Regex ColumnGap();

    // A signed number as its own field: +30, -10, −20 (with a real minus sign), +2%.
    [GeneratedRegex(@"^[+\-−–][0-9]+(?:[.,][0-9]+)?%?$")]
    private static partial Regex SignedNumber();

    // A dice expression as its own field: 2d6, d20, 2d6+1.
    [GeneratedRegex(@"^[0-9]*[dD][0-9]+(?:[+\-−–][0-9]+)?$")]
    private static partial Regex DiceExpressionWord();

    // A single explicit span token, e.g. 01-02.
    [GeneratedRegex(@"^[0-9]+[-–—][0-9]+$")]
    private static partial Regex SpanToken();

    // A span written with spaces around its dash, e.g. "01 - 02": read as the one token 01-02 when looking for gapless columns.
    [GeneratedRegex(@"(?<![0-9])(?<a>[0-9]+) [-–—] (?<b>[0-9]+)(?![0-9])")]
    private static partial Regex SpacedSpan();

    // A single whole number token, e.g. the 11 in "1 A 11 K".
    [GeneratedRegex(@"^[0-9]+$")]
    private static partial Regex WholeNumber();

    // A dash standing alone as a separator after a roll value, e.g. the "–" in "1 – Desecrate".
    [GeneratedRegex(@"^[-–—]$")]
    private static partial Regex DashToken();

    // A roll value with its separator attached, e.g. "1." "1)" "1:" or "1–".
    [GeneratedRegex(@"^(?<value>[0-9]+)(?<mark>[.):\-–—])$")]
    private static partial Regex MarkedNumber();

    // A range token of either shape: 11 or 51-52.
    [GeneratedRegex(@"^[0-9]+(?:[-–—][0-9]+)?$")]
    private static partial Regex RangeToken();

    // A dice expression (with its optional modifier) standing alone as a word, e.g. the "D100" in "D100 SYLLABLE D100 SYLLABLE".
    [GeneratedRegex(@"(?<![A-Za-z0-9])[0-9]*[dD][0-9]+(?:[+\-−–][0-9]+)?(?![A-Za-z0-9])")]
    private static partial Regex DiceWord();

    // A range that starts the line or follows a column gap (two spaces or a tab), then whitespace and text.
    [GeneratedRegex(@"(?:^|(?<=\t)|(?<=[ \t]{2}))[0-9]+(?:[ \t]*[-–—][ \t]*[0-9]+)?(?=[ \t]+\S)")]
    private static partial Regex ColumnStart();

    // A dash range in the middle of row text, e.g. the "51-55" in "Ash 51-55 Kel".
    [GeneratedRegex(@"(?<=\s)(?<r>[0-9]+\s?[-–—]\s?[0-9]+)(?=\s+\S)")]
    private static partial Regex LaterRange();

    // The dice expression in a heading is NdM with at most one attached +N / -N ("2D6+1 REACTION", "D20-2 RANDOM EVENT").
    [GeneratedRegex(@"^(?<dice>[0-9]*[dD][0-9]+(?:[+\-−–][0-9]+)?)\s+(?<name>\S.*)$")]
    private static partial Regex DiceThenName();

    [GeneratedRegex(@"^(?<name>\S.*?)\s*\(\s*(?<dice>[0-9]*[dD][0-9]+(?:[+\-−–][0-9]+)?)\s*\)$")]
    private static partial Regex NameThenDice();

    [GeneratedRegex(@"^(?<dice>[0-9]*[dD][0-9]+(?:[+\-−–][0-9]+)?)$")]
    private static partial Regex DiceOnly();

    [GeneratedRegex(@"\s*[-–—]\s*")]
    private static partial Regex RangeSeparator();

    [GeneratedRegex(@"^[-–—:.)]\s+")]
    private static partial Regex LeadingSeparator();

    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();
}
