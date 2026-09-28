using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TableForge.Domain;

/// <summary>The only formatting result text can carry: bold, italic, or both.</summary>
[Flags]
public enum TextStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
}

/// <summary>One formatted stretch of a result's text: <see cref="Length"/> UTF-16 characters from <see cref="Start"/>, all in <see cref="Style"/>.</summary>
public readonly record struct StyleRun(int Start, int Length, TextStyle Style)
{
    public int End => Start + Length;
}

/// <summary>A piece of text to show in one style: what a result's text and its <see cref="TextStyles"/> render as.</summary>
public sealed record FormattedSegment(string Text, TextStyle Style)
{
    public bool IsBold => Style.HasFlag(TextStyle.Bold);
    public bool IsItalic => Style.HasFlag(TextStyle.Italic);
}

/// <summary>
/// Bold/italic formatting for one result's text, kept beside the text and never inside it: the text itself
/// (<see cref="TableEntry.Text"/>) stays the plain, semantic source that resolution, inline dice, exports and history use,
/// so anything that does not know about formatting simply sees plain text. Presentation only.
/// <para>
/// Immutable. The runs are always canonical: sorted, non-overlapping, never empty or unstyled, and adjacent runs of the
/// same style merged — so two values describing the same formatting are equal. Positions are UTF-16 indexes into the text
/// they were made for; every operation that changes that text (typing, Save's trim, Join, Dehyphenate, Normalize) goes
/// through <see cref="ApplyEdit"/>, <see cref="Remap"/> or <see cref="Join"/> so the formatting follows its characters.
/// </para>
/// Stored as <see cref="Serialize"/> writes it; <see cref="Parse"/> reads anything malformed or stale as no formatting at
/// all, so a damaged value can never stop a table from loading.
/// </summary>
public sealed class TextStyles : IEquatable<TextStyles>
{
    /// <summary>The stored format's version. Anything else is read as no formatting.</summary>
    public const int FormatVersion = 1;

    public static readonly TextStyles Empty = new([]);

    private readonly StyleRun[] _runs;

    private TextStyles(StyleRun[] runs) => _runs = runs;

    public IReadOnlyList<StyleRun> Runs => _runs;
    public bool IsEmpty => _runs.Length == 0;

    /// <summary>Canonical formatting from any runs, clipped to a text of <paramref name="textLength"/> characters. Later runs win where they overlap.</summary>
    public static TextStyles FromRuns(IEnumerable<StyleRun> runs, int textLength)
    {
        var styles = new TextStyle[textLength];
        foreach (var run in runs)
            for (var i = Math.Max(0, run.Start); i < Math.Min(textLength, run.End); i++)
                styles[i] = run.Style & (TextStyle.Bold | TextStyle.Italic);
        return FromArray(styles);
    }

    /// <summary>The style of each of the first <paramref name="length"/> characters.</summary>
    public TextStyle[] ToArray(int length)
    {
        var styles = new TextStyle[length];
        foreach (var run in _runs)
            for (var i = run.Start; i < Math.Min(length, run.End); i++)
                styles[i] = run.Style;
        return styles;
    }

    public static TextStyles FromArray(ReadOnlySpan<TextStyle> styles)
    {
        var runs = new List<StyleRun>();
        var i = 0;
        while (i < styles.Length)
        {
            var style = styles[i];
            var start = i;
            while (i < styles.Length && styles[i] == style) i++;
            if (style != TextStyle.None) runs.Add(new StyleRun(start, i - start, style));
        }
        return runs.Count == 0 ? Empty : new TextStyles([.. runs]);
    }

    public TextStyle StyleAt(int index)
    {
        foreach (var run in _runs)
            if (index >= run.Start && index < run.End) return run.Style;
        return TextStyle.None;
    }

    // ---- formatting the selection ----------------------------------------------------------------------------------

    /// <summary>
    /// Bold or italic for the selected characters: removed when every one of them already has it, otherwise added to all of
    /// them (the usual word-processor rule). The other style is left exactly as it was, so bold+italic is bold, then italic.
    /// The selection is widened to whole characters, so a surrogate pair or a letter with its combining marks is never split.
    /// An empty selection changes nothing.
    /// </summary>
    public TextStyles Toggle(string text, int start, int length, TextStyle style)
    {
        if (!Snap(text, ref start, ref length)) return this;
        var styles = ToArray(text.Length);
        var all = true;
        for (var i = start; i < start + length; i++)
            if (!styles[i].HasFlag(style)) { all = false; break; }
        for (var i = start; i < start + length; i++)
            styles[i] = all ? styles[i] & ~style : styles[i] | style;
        return FromArray(styles);
    }

    /// <summary>Removes all formatting from the selected characters (widened to whole characters, like <see cref="Toggle"/>).</summary>
    public TextStyles Clear(string text, int start, int length)
    {
        if (!Snap(text, ref start, ref length)) return this;
        var styles = ToArray(text.Length);
        Array.Fill(styles, TextStyle.None, start, length);
        return FromArray(styles);
    }

    /// <summary>
    /// True when any selected character has formatting. Used to say why Clear did nothing.
    /// </summary>
    public bool Any(int start, int length) => _runs.Any(r => r.Start < start + length && r.End > start);

    /// <summary>Clamps a selection to the text and widens it to whole text elements; false when nothing is selected.</summary>
    private static bool Snap(string text, ref int start, ref int length)
    {
        start = Math.Clamp(start, 0, text.Length);
        var end = Math.Clamp(start + Math.Max(0, length), start, text.Length);
        if (end == start) return false;

        var boundaries = StringInfo.ParseCombiningCharacters(text); // the start of every text element
        var first = 0;
        foreach (var b in boundaries)
        {
            if (b > start) break;
            first = b;
        }
        var last = text.Length;
        foreach (var b in boundaries)
            if (b >= end) { last = b; break; }
        start = first;
        length = last - first;
        return true;
    }

    // ---- following the text as it changes -------------------------------------------------------------------------

    /// <summary>
    /// One contiguous edit of a text that was <paramref name="lengthBefore"/> characters long: <paramref name="removed"/>
    /// characters at <paramref name="offset"/> replaced by <paramref name="added"/> new ones (typing, deleting, pasting over a
    /// selection). Characters before and after keep their formatting. New characters are formatted only with what the
    /// characters on BOTH sides of them share, so typing inside a bold word stays bold, but typing at the edge of a formatted
    /// run — or at the start or end of the text — never extends it.
    /// </summary>
    public TextStyles ApplyEdit(int lengthBefore, int offset, int removed, int added)
    {
        if (IsEmpty) return this;
        var before = ToArray(lengthBefore);
        offset = Math.Clamp(offset, 0, lengthBefore);
        removed = Math.Clamp(removed, 0, lengthBefore - offset);
        var left = offset > 0 ? before[offset - 1] : TextStyle.None;
        var right = offset + removed < lengthBefore ? before[offset + removed] : TextStyle.None;
        var inherited = left & right;

        var after = new TextStyle[lengthBefore - removed + added];
        before.AsSpan(0, offset).CopyTo(after);
        after.AsSpan(offset, added).Fill(inherited);
        before.AsSpan(offset + removed).CopyTo(after.AsSpan(offset + added));
        return FromArray(after);
    }

    /// <summary>
    /// The formatting after <paramref name="oldText"/> became <paramref name="newText"/> through one of TableForge's own
    /// edits. It is not a general diff: the unchanged start and end are found first, and the changed middle is then read as
    /// either the character-level cleanup TableForge makes (Save's trim, Dehyphenate, Normalize Text: characters dropped,
    /// a non-breaking space or tab turned into a space, the ﬁ/ﬂ ligatures expanded — each surviving character keeps its own
    /// formatting) or, failing that, as one ordinary edit (<see cref="ApplyEdit"/>). Join is not a single-text edit and has
    /// its own <see cref="Join"/>.
    /// </summary>
    public TextStyles Remap(string oldText, string newText)
    {
        if (IsEmpty || oldText == newText) return this;

        var min = Math.Min(oldText.Length, newText.Length);
        var prefix = 0;
        while (prefix < min && oldText[prefix] == newText[prefix]) prefix++;
        var suffix = 0;
        while (suffix < min - prefix && oldText[oldText.Length - 1 - suffix] == newText[newText.Length - 1 - suffix]) suffix++;

        var oldEnd = oldText.Length - suffix;
        var newEnd = newText.Length - suffix;
        var before = ToArray(oldText.Length);
        var after = new TextStyle[newText.Length];
        before.AsSpan(0, prefix).CopyTo(after);
        before.AsSpan(oldEnd).CopyTo(after.AsSpan(newEnd));

        if (!TryAlignCleanup(oldText, prefix, oldEnd, newText, prefix, newEnd, before, after))
            return ApplyEdit(oldText.Length, prefix, oldEnd - prefix, newEnd - prefix);
        return FromArray(after);
    }

    /// <summary>
    /// Reads new[newStart..newEnd) as old[oldStart..oldEnd) with some characters dropped, spaces normalized and ligatures
    /// expanded, copying each surviving character's formatting across. False when the new text has anything else in it.
    /// </summary>
    private static bool TryAlignCleanup(string oldText, int oldStart, int oldEnd, string newText, int newStart, int newEnd,
        TextStyle[] before, TextStyle[] after)
    {
        var i = oldStart;
        var j = newStart;
        while (j < newEnd)
        {
            if (i >= oldEnd) return false;
            var o = oldText[i];
            var n = newText[j];
            if (o == n || (n == ' ' && o is ' ' or '\t'))
            {
                after[j++] = before[i++];
                continue;
            }
            if (Ligature(o) is { } expanded && j + expanded.Length <= newEnd && newText.AsSpan(j, expanded.Length).SequenceEqual(expanded))
            {
                after.AsSpan(j, expanded.Length).Fill(before[i]);
                j += expanded.Length;
                i++;
                continue;
            }
            i++; // dropped by the cleanup
        }
        return true;
    }

    private static string? Ligature(char c) => c switch { 'ﬁ' => "fi", 'ﬂ' => "fl", _ => null };

    /// <summary>
    /// Join With Previous Row: the formatting of exactly the text <c>TextCleanup.JoinContinuationText</c> makes (the previous
    /// text without trailing whitespace, one space, the continuation without leading whitespace). Each part keeps its own
    /// formatting; the joining space has none.
    /// </summary>
    public static TextStyles Join(string previous, TextStyles previousStyles, string continuation, TextStyles continuationStyles)
    {
        var a = previous.TrimEnd().Length;
        var skip = continuation.Length - continuation.TrimStart().Length;
        var b = continuation.Length - skip;
        var first = previousStyles.ToArray(previous.Length).AsSpan(0, a);
        var second = continuationStyles.ToArray(continuation.Length).AsSpan(skip, b);
        if (a == 0) return FromArray(second);
        if (b == 0) return FromArray(first);

        var joined = new TextStyle[a + 1 + b];
        first.CopyTo(joined);
        second.CopyTo(joined.AsSpan(a + 1));
        return FromArray(joined);
    }

    // ---- display ---------------------------------------------------------------------------------------------------

    /// <summary>The text split where its formatting changes; one unformatted segment when there is none (nothing for "").</summary>
    public IReadOnlyList<FormattedSegment> Segments(string text)
    {
        if (text.Length == 0) return [];
        if (IsEmpty) return [new FormattedSegment(text, TextStyle.None)];

        var segments = new List<FormattedSegment>();
        var position = 0;
        foreach (var run in _runs)
        {
            if (run.Start >= text.Length) break;
            if (run.Start > position) segments.Add(new FormattedSegment(text[position..run.Start], TextStyle.None));
            var end = Math.Min(run.End, text.Length);
            segments.Add(new FormattedSegment(text[run.Start..end], run.Style));
            position = end;
        }
        if (position < text.Length) segments.Add(new FormattedSegment(text[position..], TextStyle.None));
        return segments;
    }

    // ---- storage ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// What is stored beside <paramref name="text"/> (Entries.TextFormatting): null for no formatting, otherwise
    /// <c>{"v":1,"len":57,"runs":[[18,9,1],[43,10,2]]}</c> — each run is start, length and style (1 bold, 2 italic, 3 both),
    /// and len is the text's length, so formatting that no longer fits its text is recognised and dropped on load.
    /// </summary>
    public string? Serialize(string text)
    {
        var runs = FromRuns(_runs, text.Length)._runs; // never store a run past the end of the text
        if (runs.Length == 0) return null;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"{{\"v\":{FormatVersion},\"len\":{text.Length},\"runs\":[");
        for (var r = 0; r < runs.Length; r++)
        {
            if (r > 0) sb.Append(',');
            sb.Append(CultureInfo.InvariantCulture, $"[{runs[r].Start},{runs[r].Length},{(int)runs[r].Style}]");
        }
        return sb.Append("]}").ToString();
    }

    /// <summary>
    /// Reads what <see cref="Serialize"/> stored for <paramref name="text"/>. Anything that is not exactly a valid value for
    /// this text — missing, unreadable, another version, a different text length (stale), runs out of order, overlapping,
    /// empty, out of range, splitting a surrogate pair, or with an unknown style — is no formatting at all: the text is
    /// shown plain rather than wrongly, and loading never fails because of it.
    /// </summary>
    public static TextStyles Parse(string? stored, string text)
    {
        if (string.IsNullOrWhiteSpace(stored)) return Empty;
        try
        {
            using var doc = JsonDocument.Parse(stored);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Empty;
            if (!root.TryGetProperty("v", out var v) || !TryInt(v, out var version) || version != FormatVersion) return Empty;
            if (!root.TryGetProperty("len", out var len) || !TryInt(len, out var length) || length != text.Length) return Empty;
            if (!root.TryGetProperty("runs", out var runsElement) || runsElement.ValueKind != JsonValueKind.Array) return Empty;

            var runs = new List<StyleRun>();
            var previousEnd = 0;
            foreach (var item in runsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 3) return Empty;
                if (!TryInt(item[0], out var start) || !TryInt(item[1], out var count) || !TryInt(item[2], out var flags)) return Empty;
                if (start < previousEnd || count <= 0 || start > text.Length - count) return Empty;
                if (flags is < 1 or > 3) return Empty;
                if (SplitsPair(text, start) || SplitsPair(text, start + count)) return Empty;
                runs.Add(new StyleRun(start, count, (TextStyle)flags));
                previousEnd = start + count;
            }
            return FromRuns(runs, text.Length);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Empty; // belt and braces: nothing stored in this column may ever stop a table loading
        }
    }

    /// <summary>A whole number that fits an int; false (never an exception) for a string, a fraction or anything else.</summary>
    private static bool TryInt(JsonElement element, out int value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value);
    }

    private static bool SplitsPair(string text, int boundary) =>
        boundary > 0 && boundary < text.Length && char.IsHighSurrogate(text[boundary - 1]) && char.IsLowSurrogate(text[boundary]);

    // ---- equality --------------------------------------------------------------------------------------------------

    public bool Equals(TextStyles? other) => other is not null && _runs.AsSpan().SequenceEqual(other._runs);
    public override bool Equals(object? obj) => Equals(obj as TextStyles);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var run in _runs) hash.Add(run);
        return hash.ToHashCode();
    }

    /// <summary>Readable form for test failures: "B 18+9, I 43+10".</summary>
    public override string ToString() => IsEmpty ? "(none)"
        : string.Join(", ", _runs.Select(r => $"{(r.Style == (TextStyle.Bold | TextStyle.Italic) ? "BI" : r.Style == TextStyle.Bold ? "B" : "I")} {r.Start}+{r.Length}"));
}
