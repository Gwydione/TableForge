using System.Text;
using System.Text.RegularExpressions;

namespace TableForge.Import;

/// <summary>
/// Deterministic, conservative fixes for text that copied imperfectly from a PDF: whitespace damage, ligatures,
/// line-wrap hyphenation and the Unicode replacement character. Nothing here guesses at meaning or rewrites prose;
/// every rule is a fixed character-level substitution the user asks for explicitly on the Review screen.
/// </summary>
public static partial class TextCleanup
{
    /// <summary>
    /// General-purpose cleanup for a table name, result-set name or result text: non-breaking spaces become ordinary
    /// spaces, runs of horizontal whitespace collapse to one space, the two Latin ligatures PDFs commonly emit are
    /// expanded, and leading/trailing whitespace is trimmed. Never touches letters, digits or punctuation otherwise.
    /// </summary>
    public static string NormalizeGeneralText(string? text)
    {
        var t = text ?? "";
        t = t.Replace(' ', ' ').Replace("ﬁ", "fi").Replace("ﬂ", "fl");
        t = Whitespace().Replace(t, " ");
        return t.Trim();
    }

    /// <summary>
    /// Cosmetic cleanup for a range field: non-breaking spaces and repeated whitespace are normalized as in
    /// <see cref="NormalizeGeneralText"/>, and when the text is recognizably a single number or a two-number span
    /// (whatever the spacing, and whatever hyphen/dash/minus variant separates the numbers), the span separator is
    /// rewritten to a plain hyphen with no surrounding spaces. Digits, leading zeros and signs are kept exactly as
    /// written; nothing here changes what the range means, only how it is spelled. Text that is not a recognizable
    /// number or span is returned only whitespace-cleaned, unchanged otherwise.
    /// </summary>
    public static string NormalizeRangeText(string? text)
    {
        var t = NormalizeGeneralText(text);
        var m = RangeShape().Match(t);
        if (!m.Success) return t;

        // A real minus sign (−) leading a number is spelled as a hyphen-minus.
        static string Sign(string n) => n.StartsWith('−') ? "-" + n[1..] : n;
        var a = Sign(m.Groups["a"].Value);
        var b = m.Groups["b"];
        return b.Success ? $"{a}-{Sign(b.Value)}" : a;
    }

    /// <summary>True if the text contains a Unicode replacement character (U+FFFD): the mark a broken PDF font mapping leaves behind.</summary>
    public static bool ContainsReplacementCharacter(string? text) => text is not null && text.Contains('�');

    /// <summary>How many Unicode replacement characters (U+FFFD) the text contains.</summary>
    public static int CountReplacementCharacters(string? text)
    {
        if (text is null) return 0;
        var count = 0;
        foreach (var c in text) if (c == '�') count++;
        return count;
    }

    /// <summary>
    /// Normalizes raw multi-line pasted text one line at a time: each line's content gets the same treatment as
    /// <see cref="NormalizeGeneralText"/> (NBSP, repeated whitespace, ligatures, leading/trailing trim), but every
    /// original line break is kept exactly as it was (mixed CRLF/LF included), so the table's row structure survives.
    /// </summary>
    public static string NormalizePastedText(string? text)
    {
        var t = text ?? "";
        var sb = new StringBuilder(t.Length);
        foreach (var line in SplitLines(t))
        {
            sb.Append(NormalizeGeneralText(t.Substring(line.ContentStart, line.ContentLength)));
            if (line.TerminatorLength > 0) sb.Append(t, line.ContentStart + line.ContentLength, line.TerminatorLength);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Ctrl+J on the raw Paste Table editor: joins the line the caret is on into the line immediately above it, the
    /// same way <see cref="TableForge.ViewModels.ReviewViewModel"/>'s Join With Previous Row does for a structured row,
    /// but working directly on caret position in raw text instead of on a selected row. Makes no judgement about
    /// whether the current line is really a continuation — the user chose this command on this line deliberately.
    /// Returns false, with <paramref name="result"/> and <paramref name="newCaretIndex"/> equal to the input, when the
    /// caret is on the first line: there is nothing above it to join into.
    /// </summary>
    public static bool TryJoinLineWithPrevious(string text, int caretIndex, out string result, out int newCaretIndex, out string message)
    {
        var t = text ?? "";
        caretIndex = Math.Clamp(caretIndex, 0, t.Length);
        var lines = SplitLines(t);

        var lineIndex = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].ContentStart > caretIndex) break;
            lineIndex = i;
        }

        if (lineIndex == 0)
        {
            result = t;
            newCaretIndex = caretIndex;
            message = "This is the first line: there is no previous line to join into.";
            return false;
        }

        var previous = lines[lineIndex - 1];
        var current = lines[lineIndex];
        var previousText = t.Substring(previous.ContentStart, previous.ContentLength);
        var currentText = t.Substring(current.ContentStart, current.ContentLength);
        var joined = JoinContinuationText(previousText, currentText);

        var trimmedPrevious = previousText.TrimEnd();
        var trimmedCurrent = currentText.TrimStart();
        var caretInJoined = trimmedPrevious.Length == 0 ? 0 : trimmedCurrent.Length == 0 ? joined.Length : trimmedPrevious.Length + 1;

        var currentContentEnd = current.ContentStart + current.ContentLength;
        result = t[..previous.ContentStart] + joined + t[currentContentEnd..];
        newCaretIndex = previous.ContentStart + caretInJoined;
        message = "Joined the current line with the previous line.";
        return true;
    }

    /// <summary>
    /// Ctrl+J with a selection on the raw Paste Table editor: joins every line the selection touches into one line. Each
    /// line is trimmed, blank lines are dropped, and the rest are joined with exactly one space — a literal join, with no
    /// dehyphenation and no judgement about paragraphs. It works on whole lines: a selection that starts or ends partway
    /// through a line still joins that entire line, so the unselected part of it is kept, never cut off. A selection that
    /// ends at the very start of a line (having taken only the line break before it) does not include that line.
    /// Returns false, changing nothing, unless the selection touches two or more lines; the caller then falls back to
    /// <see cref="TryJoinLineWithPrevious"/>. On success, the text from <paramref name="replaceStart"/> for
    /// <paramref name="replaceLength"/> characters is to be replaced by <paramref name="replacement"/>; everything outside
    /// that span, including the line breaks before and after it, stays exactly as it was.
    /// </summary>
    public static bool TryJoinSelectedLines(string text, int selectionStart, int selectionLength,
        out int replaceStart, out int replaceLength, out string replacement, out string message)
    {
        var t = text ?? "";
        var start = Math.Clamp(selectionStart, 0, t.Length);
        var end = Math.Clamp(selectionStart + selectionLength, start, t.Length);
        var lines = SplitLines(t);

        int LineAt(int index)
        {
            var found = 0;
            for (var i = 0; i < lines.Count && lines[i].ContentStart <= index; i++) found = i;
            return found;
        }

        var first = LineAt(start);
        var last = LineAt(end);
        if (end > start && last > first && end == lines[last].ContentStart) last--; // only the line break before it was selected

        if (last <= first)
        {
            (replaceStart, replaceLength, replacement, message) = (start, 0, "", "");
            return false;
        }

        replaceStart = lines[first].ContentStart;
        replaceLength = lines[last].ContentStart + lines[last].ContentLength - replaceStart;
        replacement = string.Join(' ', lines.Skip(first).Take(last - first + 1)
            .Select(l => t.Substring(l.ContentStart, l.ContentLength).Trim())
            .Where(s => s.Length > 0));
        message = $"Joined {last - first + 1} selected lines into one.";
        return true;
    }

    /// <summary>Trims trailing whitespace off <paramref name="previous"/>, leading whitespace off <paramref name="continuation"/>, and joins them with exactly one space (or just the non-empty side, if the other is blank).</summary>
    internal static string JoinContinuationText(string previous, string continuation)
    {
        var a = previous.TrimEnd();
        var b = continuation.TrimStart();
        return a.Length == 0 ? b : b.Length == 0 ? a : $"{a} {b}";
    }

    // One line of raw multi-line text: where its content starts and how long it (and its line break) are. The last
    // line has TerminatorLength 0. Content never includes the line break itself, so callers can rebuild the original
    // text exactly, or replace just a line's content while keeping every line break untouched.
    private readonly record struct RawLine(int ContentStart, int ContentLength, int TerminatorLength);

    private static List<RawLine> SplitLines(string text)
    {
        var lines = new List<RawLine>();
        var pos = 0;
        foreach (Match m in LineBreak().Matches(text))
        {
            lines.Add(new RawLine(pos, m.Index - pos, m.Length));
            pos = m.Index + m.Length;
        }
        lines.Add(new RawLine(pos, text.Length - pos, 0));
        return lines;
    }

    /// <summary>
    /// Removes PDF line-wrap hyphenation from <paramref name="text"/>: a letter, a hyphen, whitespace, then a
    /// lowercase letter continuing the word ("magnifi- cent") becomes the joined word ("magnificent"). Returns false,
    /// leaving <paramref name="result"/> equal to the input, when nothing matches — legitimate hyphenated words such
    /// as "well-made" or "half-orc" have no whitespace after the hyphen and are never touched.
    /// </summary>
    public static bool TryDehyphenate(string? text, out string result)
    {
        var t = text ?? "";
        result = LineWrapHyphen().Replace(t, "");
        return result != t;
    }

    // Non-breaking space already normalized to ' ' by the time this runs; this just collapses runs of it and tabs.
    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Whitespace();

    // A plain number, or two numbers separated by a hyphen/en dash/em dash/minus sign with optional surrounding whitespace.
    // Either number may lead with a hyphen-minus or a real minus sign.
    [GeneratedRegex(@"^(?<a>[-−]?[0-9]+)(?:\s*[-–—−]\s*(?<b>[-−]?[0-9]+))?$")]
    private static partial Regex RangeShape();

    // A letter, then a hyphen with no space before it, then either horizontal whitespace or a single real line break
    // (each with optional horizontal whitespace around it, but never two line breaks — that would cross a paragraph
    // break, not just a PDF line wrap), then a lowercase letter: the line-wrap shape, on one row's text or across the
    // raw pasted text's own line breaks.
    [GeneratedRegex(@"(?<=\p{L})-(?:[ \t]*(?:\r\n|\r|\n)[ \t]*|[ \t]+)(?=\p{Ll})")]
    private static partial Regex LineWrapHyphen();

    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex LineBreak();
}
