using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace TableForge.Import;

/// <summary>One clipboard format as read: its name and its data (a string, bytes, or something else).</summary>
public sealed record ClipboardEntry(string Format, object? Data);

public sealed record ClipboardFormatInfo(string Name, string Kind, long Size, string Preview);

/// <summary>Bold/italic found in source-supplied markup. Nothing here is ever inferred from plain text.</summary>
/// <param name="Markdown">The text with **bold**, *italic* and ***both*** markers, if the markup could be read as runs.</param>
public sealed record EmphasisFacts(int BoldRuns, int ItalicRuns, string? Markdown, int FontNameHints);

/// <summary>What a copy from some other program put on the clipboard, and whether it holds anything richer than plain text.</summary>
public sealed record ClipboardAnalysis(
    IReadOnlyList<ClipboardFormatInfo> Formats,
    string? PlainText,
    int PlainLineCount,
    string? HtmlFragment,
    EmphasisFacts? Html,
    int HtmlLineBreaks,
    int HtmlBlocks,
    int HtmlTables,
    bool HtmlHasPositioning,
    string? Rtf,
    EmphasisFacts? RtfEmphasis,
    int RtfParagraphs,
    int RtfTabs)
{
    public bool HasHtml => HtmlFragment is not null;
    public bool HasRtf => Rtf is not null;
    public bool BoldSurvives => (Html?.BoldRuns ?? 0) > 0 || (RtfEmphasis?.BoldRuns ?? 0) > 0;
    public bool ItalicSurvives => (Html?.ItalicRuns ?? 0) > 0 || (RtfEmphasis?.ItalicRuns ?? 0) > 0;

    /// <summary>Lines the HTML would give if it were flattened to text (breaks and blocks), to compare with the plain text's lines.</summary>
    public int HtmlLineEstimate => HtmlLineBreaks + HtmlBlocks;

    /// <summary>Whether the rich formats carry line/column structure that plain text lacks (positions, tables, tabs).</summary>
    public bool RicherStructure => HtmlHasPositioning || HtmlTables > 0 || RtfTabs > 0
        || (HasHtml && HtmlLineEstimate > PlainLineCount + 1);

    public string ToReport(string? owner = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TableForge clipboard diagnostic");
        sb.AppendLine(new string('=', 31));
        sb.AppendLine($"Clipboard owner: {owner ?? "(unknown)"}");
        sb.AppendLine();
        sb.AppendLine($"Formats present ({Formats.Count}):");
        foreach (var f in Formats)
            sb.AppendLine($"  {f.Name,-28} {f.Kind,-7} {f.Size,9:N0}  {f.Preview}");
        sb.AppendLine();
        sb.AppendLine("What Paste Table receives (plain text only, whatever else is on the clipboard):");
        sb.AppendLine(PlainText is null ? "  (no text format present)" : $"  {PlainLineCount} line(s), {PlainText.Length:N0} characters");
        sb.AppendLine();
        sb.AppendLine("Formatting supplied by the source:");
        sb.AppendLine($"  HTML: {(HasHtml ? "present" : "absent")}" + (Html is null ? "" : $"  bold runs: {Html.BoldRuns}  italic runs: {Html.ItalicRuns}  font-name hints (Bold/Italic/Oblique in a font name): {Html.FontNameHints}"));
        sb.AppendLine($"  RTF:  {(HasRtf ? "present" : "absent")}" + (RtfEmphasis is null ? "" : $"  bold switches: {RtfEmphasis.BoldRuns}  italic switches: {RtfEmphasis.ItalicRuns}  font-name hints: {RtfEmphasis.FontNameHints}"));
        sb.AppendLine($"  Bold survives: {(BoldSurvives ? "YES" : "no")}    Italic survives: {(ItalicSurvives ? "YES" : "no")}");
        sb.AppendLine();
        sb.AppendLine("Line / column structure beyond plain text:");
        sb.AppendLine($"  plain text lines: {PlainLineCount}");
        if (HasHtml) sb.AppendLine($"  HTML: line breaks {HtmlLineBreaks}, block elements {HtmlBlocks}, tables {HtmlTables}, absolutely positioned/coordinate styles: {(HtmlHasPositioning ? "yes" : "no")}");
        if (HasRtf) sb.AppendLine($"  RTF: paragraphs {RtfParagraphs}, tabs {RtfTabs}");
        sb.AppendLine($"  Richer than plain text: {(RicherStructure ? "YES" : "no")}");

        if (Html?.Markdown is { Length: > 0 } md)
        {
            sb.AppendLine();
            sb.AppendLine("HTML read as TableForge-style emphasis (**bold**, *italic*):");
            foreach (var line in md.Split('\n').Take(40)) sb.AppendLine("  | " + line);
        }
        return sb.ToString();
    }
}

/// <summary>
/// Reads what a clipboard holds and reports whether bold/italic (or richer structure) survives, without any dependence on
/// the clipboard itself, so it can be tested with plain strings. It never guesses formatting from plain text.
/// </summary>
public static partial class ClipboardAnalyzer
{
    public static ClipboardAnalysis Analyze(IEnumerable<ClipboardEntry> entries)
    {
        var list = entries.ToList();
        var formats = list.Select(Describe).ToList();

        var plain = Text(list, "UnicodeText") ?? Text(list, "Text") ?? Text(list, "OEMText");
        var htmlRaw = Text(list, "HTML Format");
        var fragment = htmlRaw is null ? null : HtmlFragmentOf(htmlRaw);
        var rtf = Text(list, "Rich Text Format");

        EmphasisFacts? html = null;
        int breaks = 0, blocks = 0, tables = 0;
        var positioned = false;
        if (fragment is not null)
        {
            var runs = HtmlRuns(fragment);
            html = new EmphasisFacts(
                CountRuns(runs, r => r.Bold), CountRuns(runs, r => r.Italic), ToMarkdown(runs), FontNameHints(fragment));
            breaks = BreakTag().Matches(fragment).Count;
            blocks = BlockTag().Matches(fragment).Count;
            tables = TableTag().Matches(fragment).Count;
            positioned = PositionStyle().IsMatch(fragment);
        }

        EmphasisFacts? rtfFacts = null;
        int paragraphs = 0, tabs = 0;
        if (rtf is not null)
        {
            rtfFacts = new EmphasisFacts(RtfSwitchOns(rtf, 'b'), RtfSwitchOns(rtf, 'i'), null, FontNameHints(FontTable(rtf)));
            paragraphs = RtfControl("par").Matches(rtf).Count;
            tabs = RtfControl("tab").Matches(rtf).Count;
        }

        return new ClipboardAnalysis(formats, plain, plain is null ? 0 : LineCount(plain), fragment, html, breaks, blocks, tables, positioned,
            rtf, rtfFacts, paragraphs, tabs);
    }

    // ---- formats ------------------------------------------------------------------------------

    private static string? Text(List<ClipboardEntry> list, string format) =>
        list.FirstOrDefault(e => string.Equals(e.Format, format, StringComparison.OrdinalIgnoreCase))?.Data as string;

    private static ClipboardFormatInfo Describe(ClipboardEntry e) => e.Data switch
    {
        string s => new(e.Format, "text", s.Length, Preview(s)),
        byte[] b => new(e.Format, "bytes", b.Length, ""),
        null => new(e.Format, "none", 0, "(no data)"),
        var other => new(e.Format, "other", 0, other.GetType().Name),
    };

    private static string Preview(string s)
    {
        var flat = Regex.Replace(s, @"\s+", " ").Trim();
        return flat.Length <= 60 ? flat : flat[..60] + "…";
    }

    private static int LineCount(string text) => text.Length == 0 ? 0 : Regex.Split(text.TrimEnd('\r', '\n'), @"\r\n|\r|\n").Length;

    // ---- CF_HTML ----------------------------------------------------------------------------------

    /// <summary>The fragment of a CF_HTML clipboard string: between its StartFragment/EndFragment markers, or all of it if there are none.</summary>
    public static string HtmlFragmentOf(string cfHtml)
    {
        var start = cfHtml.IndexOf("<!--StartFragment-->", StringComparison.OrdinalIgnoreCase);
        var end = cfHtml.IndexOf("<!--EndFragment-->", StringComparison.OrdinalIgnoreCase);
        if (start >= 0 && end > start) return cfHtml[(start + "<!--StartFragment-->".Length)..end];

        var body = cfHtml.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        return body >= 0 ? cfHtml[body..] : cfHtml;
    }

    public readonly record struct Run(string Text, bool Bold, bool Italic);

    /// <summary>Reads markup into text runs carrying only bold/italic, taken from tags (b, strong, i, em) and styles (font-weight, font-style).</summary>
    public static List<Run> HtmlRuns(string html)
    {
        var runs = new List<Run>();
        var stack = new List<(string Tag, bool Bold, bool Italic)>();
        bool Bold() => stack.Count > 0 && stack[^1].Bold;
        bool Italic() => stack.Count > 0 && stack[^1].Italic;

        foreach (Match m in HtmlToken().Matches(html))
        {
            if (m.Groups["text"].Success)
            {
                var text = WebUtility.HtmlDecode(Regex.Replace(m.Groups["text"].Value, @"\s+", " "));
                if (text.Length > 0) runs.Add(new Run(text, Bold(), Italic()));
                continue;
            }

            var tag = m.Groups["name"].Value.ToLowerInvariant();
            var attributes = m.Groups["attrs"].Value;
            if (tag == "br") { runs.Add(new Run("\n", false, false)); continue; }
            if (m.Groups["close"].Value == "/")
            {
                var open = stack.FindLastIndex(t => t.Tag == tag);
                if (open >= 0) stack.RemoveRange(open, stack.Count - open);
                if (BlockNames.Contains(tag)) runs.Add(new Run("\n", false, false));
                continue;
            }
            if (m.Value.EndsWith("/>") || VoidNames.Contains(tag)) continue;

            var (bold, italic) = (Bold(), Italic());
            if (tag is "b" or "strong" || StyleBold().IsMatch(attributes)) bold = true;
            if (tag is "i" or "em" || StyleItalic().IsMatch(attributes)) italic = true;
            stack.Add((tag, bold, italic));
        }
        return runs;
    }

    private static readonly HashSet<string> BlockNames = ["p", "div", "li", "tr", "h1", "h2", "h3", "h4", "h5", "h6"];
    private static readonly HashSet<string> VoidNames = ["br", "hr", "img", "meta", "link", "input"];

    private static int CountRuns(List<Run> runs, Func<Run, bool> flag) => Merge(runs).Count(r => flag(r) && r.Text.Trim().Length > 0);

    private static List<Run> Merge(List<Run> runs)
    {
        var merged = new List<Run>();
        foreach (var r in runs)
        {
            if (merged.Count > 0 && merged[^1].Bold == r.Bold && merged[^1].Italic == r.Italic)
                merged[^1] = merged[^1] with { Text = merged[^1].Text + r.Text };
            else
                merged.Add(r);
        }
        return merged;
    }

    /// <summary>TableForge-style emphasis: **bold**, *italic*, ***both***. Markers hug the words; spaces stay outside them.</summary>
    public static string ToMarkdown(List<Run> runs)
    {
        var sb = new StringBuilder();
        foreach (var run in Merge(runs))
        {
            var marker = run.Bold && run.Italic ? "***" : run.Bold ? "**" : run.Italic ? "*" : "";
            var core = run.Text.Trim(' ');
            if (marker.Length == 0 || core.Length == 0 || core == "\n") { sb.Append(run.Text); continue; }
            sb.Append(run.Text[..(run.Text.Length - run.Text.TrimStart(' ').Length)]).Append(marker).Append(core).Append(marker)
              .Append(run.Text[(run.Text.TrimEnd(' ').Length)..]);
        }
        return Regex.Replace(sb.ToString().Trim(), @"[ \t]*\n[ \t]*", "\n");
    }

    /// <summary>Font names that themselves say Bold/Italic/Oblique (e.g. "Arial-BoldMT"): source-supplied, but indirect, so reported apart.</summary>
    private static int FontNameHints(string text) => FontHint().Matches(text).Count;

    private static string FontTable(string rtf)
    {
        var start = rtf.IndexOf("{\\fonttbl", StringComparison.Ordinal);
        return start < 0 ? "" : rtf[start..Math.Min(rtf.Length, start + 4000)];
    }

    // ---- RTF --------------------------------------------------------------------------------------

    /// <summary>How many times bold (\b) or italic (\i) is switched on. "\b0"/"\i0" switch it off; "\bin", "\intbl" and the like are other words.</summary>
    public static int RtfSwitchOns(string rtf, char control)
    {
        var count = 0;
        foreach (Match m in Regex.Matches(rtf, @"\\" + control + @"(?![A-Za-z])(-?\d+)?"))
            if (!m.Groups[1].Success || m.Groups[1].Value != "0") count++;
        return count;
    }

    private static Regex RtfControl(string word) => new(@"\\" + word + @"(?![A-Za-z])", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"<(?<close>/?)(?<name>[A-Za-z][A-Za-z0-9]*)(?<attrs>[^>]*)>|(?<text>[^<]+)", RegexOptions.Singleline)]
    private static partial Regex HtmlToken();

    [GeneratedRegex(@"font-weight\s*:\s*(bold|bolder|[6-9]00)", RegexOptions.IgnoreCase)]
    private static partial Regex StyleBold();

    [GeneratedRegex(@"font-style\s*:\s*(italic|oblique)", RegexOptions.IgnoreCase)]
    private static partial Regex StyleItalic();

    [GeneratedRegex(@"<br\b", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    [GeneratedRegex(@"<(p|div|li|tr)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTag();

    [GeneratedRegex(@"<table\b", RegexOptions.IgnoreCase)]
    private static partial Regex TableTag();

    [GeneratedRegex(@"position\s*:\s*absolute|\b(left|top)\s*:\s*-?\d+(\.\d+)?(px|pt)", RegexOptions.IgnoreCase)]
    private static partial Regex PositionStyle();

    [GeneratedRegex(@"(Bold|Italic|Oblique)", RegexOptions.IgnoreCase)]
    private static partial Regex FontHint();
}
