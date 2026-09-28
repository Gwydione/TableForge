using TableForge.Domain;
using TableForge.Import;

namespace TableForge.Tests;

/// <summary>
/// RC24: bold/italic as style runs beside the plain text (<see cref="TextStyles"/>). Pure: toggling, clearing, following the
/// text through TableForge's own edits and cleanups, joining, display segments, and the stored form — including that anything
/// malformed or stale reads as no formatting instead of failing.
/// </summary>
public class TextStylesTests
{
    private const TextStyle B = TextStyle.Bold;
    private const TextStyle I = TextStyle.Italic;
    private const TextStyle BI = TextStyle.Bold | TextStyle.Italic;

    /// <summary>Formatting written in a readable way: "**bold**", "*italic*", "***both***" — test input only; nothing in TableForge reads markup.</summary>
    internal static (string Text, TextStyles Styles) Md(string marked)
    {
        var text = new System.Text.StringBuilder();
        var styles = new List<TextStyle>();
        var current = TextStyle.None;
        for (var i = 0; i < marked.Length;)
        {
            if (marked.AsSpan(i).StartsWith("***")) { current ^= BI; i += 3; continue; }
            if (marked.AsSpan(i).StartsWith("**")) { current ^= B; i += 2; continue; }
            if (marked[i] == '*') { current ^= I; i++; continue; }
            text.Append(marked[i]);
            styles.Add(current);
            i++;
        }
        return (text.ToString(), TextStyles.FromArray([.. styles]));
    }

    /// <summary>The reverse of <see cref="Md"/>, for readable assertions.</summary>
    internal static string ToMd(string text, TextStyles styles) => string.Concat(styles.Segments(text).Select(s => s.Style switch
    {
        BI => $"***{s.Text}***",
        B => $"**{s.Text}**",
        I => $"*{s.Text}*",
        _ => s.Text,
    }));

    private static string Toggle(string marked, string select, TextStyle style, int occurrence = 0)
    {
        var (text, styles) = Md(marked);
        var start = IndexOf(text, select, occurrence);
        return ToMd(text, styles.Toggle(text, start, select.Length, style));
    }

    private static int IndexOf(string text, string part, int occurrence = 0)
    {
        var index = -1;
        for (var n = 0; n <= occurrence; n++) index = text.IndexOf(part, index + 1, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{part}' not in '{text}'");
        return index;
    }

    private static string Remap(string marked, string newText)
    {
        var (text, styles) = Md(marked);
        return ToMd(newText, styles.Remap(text, newText));
    }

    // ---- toggling and clearing ----------------------------------------------------------------------------------------

    [Fact]
    public void Plain_text_has_no_formatting_and_shows_as_one_plain_segment()
    {
        Assert.True(TextStyles.Empty.IsEmpty);
        var segment = Assert.Single(TextStyles.Empty.Segments("The creature gains +2 Armor."));
        Assert.Equal(new FormattedSegment("The creature gains +2 Armor.", TextStyle.None), segment);
        Assert.Empty(TextStyles.Empty.Segments(""));
    }

    [Fact]
    public void Part_of_the_text_can_be_made_bold()
    {
        Assert.Equal("The creature gains **+2 Armor** until the next dawn.",
            Toggle("The creature gains +2 Armor until the next dawn.", "+2 Armor", B));
    }

    [Fact]
    public void Part_of_the_text_can_be_made_italic()
    {
        Assert.Equal("The creature gains +2 Armor until the *next dawn*.",
            Toggle("The creature gains +2 Armor until the next dawn.", "next dawn", I));
    }

    [Fact]
    public void Bold_and_italic_combine_on_the_same_text_and_come_off_independently()
    {
        var both = Toggle("gains **+2 Armor** now", "+2 Armor", I);
        Assert.Equal("gains ***+2 Armor*** now", both);
        Assert.Equal("gains *+2 Armor* now", Toggle(both, "+2 Armor", B));
        Assert.Equal("gains **+2 Armor** now", Toggle(both, "+2 Armor", I));
        Assert.Equal("gains ***+2***** Armor** now", Toggle("gains **+2 Armor** now", "+2", I)); // the space stays bold
    }

    [Fact]
    public void Toggling_a_fully_formatted_selection_removes_it_and_a_partly_formatted_one_completes_it()
    {
        Assert.Equal("a +2 Armor b", Toggle("a **+2 Armor** b", "+2 Armor", B));
        Assert.Equal("a **+2 Armor** b", Toggle("a **+2** Armor b", "+2 Armor", B));  // partly bold → all bold
        Assert.Equal("a **+2 **Armor b", Toggle("a **+2 Armor** b", "Armor", B));     // part of a run: the rest, space included, stays bold
    }

    [Fact]
    public void Adjacent_runs_of_the_same_style_merge_so_equal_formatting_is_equal()
    {
        var (text, styles) = Md("**ab**cd");
        var merged = styles.Toggle(text, 2, 2, B);
        Assert.Equal([new StyleRun(0, 4, B)], merged.Runs);
        Assert.Equal(Md("**abcd**").Styles, merged);
    }

    [Fact]
    public void Clear_removes_bold_and_italic_from_only_the_selection()
    {
        var (text, styles) = Md("***one*** **two** *three*");
        Assert.Equal("one two *three*", ToMd(text, styles.Clear(text, 0, IndexOf(text, "three") - 1)));
        Assert.Equal("***o***ne **two** *three*", ToMd(text, styles.Clear(text, 1, 2)));
        Assert.True(styles.Clear(text, 0, text.Length).IsEmpty);
    }

    [Fact]
    public void An_empty_selection_changes_nothing()
    {
        var (text, styles) = Md("a **b** c");
        Assert.Same(styles, styles.Toggle(text, 2, 0, B));
        Assert.Same(styles, styles.Clear(text, 2, 0));
        Assert.Same(styles, styles.Toggle(text, text.Length, 5, B)); // past the end: nothing selected
    }

    [Fact]
    public void A_selection_never_splits_a_surrogate_pair_or_a_letter_from_its_combining_mark()
    {
        const string dragon = "Summon 🐉 now";
        var start = dragon.IndexOf("🐉", StringComparison.Ordinal);                     // two UTF-16 units
        var half = TextStyles.Empty.Toggle(dragon, start + 1, 1, B); // only the low surrogate selected
        Assert.Equal([new StyleRun(start, 2, B)], half.Runs);

        const string accent = "café noir";              // "café" with a combining acute accent
        var e = accent.IndexOf('e');
        Assert.Equal([new StyleRun(e, 2, I)], TextStyles.Empty.Toggle(accent, e, 1, I).Runs);
    }

    [Fact]
    public void Unicode_text_formats_and_segments_by_character()
    {
        Assert.Equal("Le **château** brûle — *très* vite ✨", Toggle("Le **château** brûle — très vite ✨", "très", I));
        Assert.Equal("Гоблин **напал** 🐉", Toggle("Гоблин напал 🐉", "напал", B));
    }

    [Fact]
    public void Multiline_text_keeps_its_line_breaks_inside_and_across_runs()
    {
        var text = "First paragraph.\n\nSecond one.";
        var styles = TextStyles.Empty.Toggle(text, 6, "paragraph.\n\nSecond".Length, B);
        Assert.Equal(["First ", "paragraph.\n\nSecond", " one."], styles.Segments(text).Select(s => s.Text).ToArray());
        Assert.Equal(text, string.Concat(styles.Segments(text).Select(s => s.Text)));
    }

    // ---- typing and editing -------------------------------------------------------------------------------------------

    [Fact]
    public void Typing_inside_a_formatted_run_is_formatted_like_the_run()
    {
        Assert.Equal("gains **+2 Heavy Armor** now", Remap("gains **+2 Armor** now", "gains +2 Heavy Armor now"));
    }

    [Fact]
    public void Typing_at_the_edge_of_a_formatted_run_does_not_extend_it()
    {
        Assert.Equal("gains **+2 Armor**s now", Remap("gains **+2 Armor** now", "gains +2 Armors now"));
        Assert.Equal("gains X**+2 Armor** now", Remap("gains **+2 Armor** now", "gains X+2 Armor now"));
        Assert.Equal("**bold**!", Remap("**bold**", "bold!"));   // at the end of the text
        Assert.Equal("!**bold**", Remap("**bold**", "!bold"));   // at the start of the text
    }

    [Fact]
    public void Typing_between_bold_and_bold_italic_keeps_only_what_both_sides_share()
    {
        var styles = TextStyles.FromArray([B, B, BI, BI]);             // "ab" bold, "cd" bold+italic
        var after = styles.Remap("abcd", "abXcd");
        Assert.Equal([B, B, B, BI, BI], after.ToArray(5));              // bold is on both sides; italic only on one
    }

    [Fact]
    public void Deleting_text_keeps_the_formatting_of_what_is_left()
    {
        Assert.Equal("gains **+2 Ar** now", Remap("gains **+2 Armor** now", "gains +2 Ar now"));
        Assert.Equal("gains now", Remap("gains **+2 Armor** now", "gains now"));
        Assert.True(Md("**all**").Styles.Remap("all", "").IsEmpty);
    }

    [Fact]
    public void Replacing_a_whole_formatted_run_leaves_the_new_text_plain()
    {
        Assert.Equal("gains a Shield now", Remap("gains **+2 Armor** now", "gains a Shield now"));
        Assert.Equal("gains **+**3 Shield now", Remap("gains **+2 Armor** now", "gains +3 Shield now")); // an unchanged character keeps its own
    }

    [Fact]
    public void Replacing_part_of_a_run_keeps_the_new_characters_in_the_run()
    {
        Assert.Equal("gains **+2 Armour** now", Remap("gains **+2 Armor** now", "gains +2 Armour now"));
        Assert.Equal("gains **+5 Armor** now", Remap("gains **+2 Armor** now", "gains +5 Armor now"));
    }

    [Fact]
    public void An_exact_edit_puts_a_repeated_character_on_the_side_it_was_typed()
    {
        // "a" then bold "bb": typing "b" just before the bold run. A text comparison alone would guess it went at the end.
        var (text, styles) = Md("a**bb**");
        Assert.Equal("a**bb**b", ToMd("abbb", styles.Remap(text, "abbb")));          // the comparison's guess
        Assert.Equal("ab**bb**", ToMd("abbb", styles.ApplyEdit(text.Length, 1, 0, 1))); // what the editor reports
    }

    [Fact]
    public void Unchanged_text_keeps_the_same_formatting()
    {
        var (text, styles) = Md("a **b** c");
        Assert.Same(styles, styles.Remap(text, text));
        Assert.Same(TextStyles.Empty, TextStyles.Empty.Remap("a", "b"));
    }

    // ---- TableForge's cleanups ---------------------------------------------------------------------------------------

    [Fact]
    public void Save_trimming_moves_formatting_with_its_characters()
    {
        var (text, styles) = Md("   The **+2 Armor** lasts *until dawn*.  ");
        var trimmed = text.Trim();
        Assert.Equal("The **+2 Armor** lasts *until dawn*.", ToMd(trimmed, styles.Remap(text, trimmed)));
    }

    [Fact]
    public void Dehyphenate_keeps_formatting_on_the_rejoined_word()
    {
        var (text, styles) = Md("A **magnifi- cent** sword, *well-made*.");
        Assert.True(TextCleanup.TryDehyphenate(text, out var result));
        Assert.Equal("A **magnificent** sword, *well-made*.", ToMd(result, styles.Remap(text, result)));
    }

    [Fact]
    public void Dehyphenate_across_a_line_break_keeps_formatting()
    {
        var (text, styles) = Md("*A long shimmer-\ning veil*");
        Assert.True(TextCleanup.TryDehyphenate(text, out var result));
        Assert.Equal("*A long shimmering veil*", ToMd(result, styles.Remap(text, result)));
    }

    [Fact]
    public void Normalize_keeps_formatting_through_spaces_ligatures_and_trimming()
    {
        var (text, styles) = Md("  The **ﬁne sword**   of *ﬂame*\tand   ash ");
        var normalized = TextCleanup.NormalizeGeneralText(text);
        Assert.Equal("The fine sword of flame and ash", normalized);
        Assert.Equal("The **fine sword** of *flame* and ash", ToMd(normalized, styles.Remap(text, normalized)));
    }

    [Fact]
    public void Join_keeps_each_rows_formatting_on_its_own_part()
    {
        var (a, aStyles) = Md("You find a **damaged** chest   ");
        var (b, bStyles) = Md("  containing *old coins*.");
        const string joined = "You find a damaged chest containing old coins."; // what Join With Previous Row makes of the two texts
        Assert.Equal("You find a **damaged** chest containing *old coins*.", ToMd(joined, TextStyles.Join(a, aStyles, b, bStyles)));
    }

    [Fact]
    public void Join_with_an_empty_side_keeps_the_other_side()
    {
        var (b, bStyles) = Md("  **only** this");
        Assert.Equal("**only** this", ToMd("only this", TextStyles.Join("  ", TextStyles.Empty, b, bStyles)));
        var (a, aStyles) = Md("*just* that ");
        Assert.Equal("*just* that", ToMd("just that", TextStyles.Join(a, aStyles, " ", TextStyles.Empty)));
    }

    // ---- storage ------------------------------------------------------------------------------------------------------

    [Fact]
    public void No_formatting_is_stored_as_null()
    {
        Assert.Null(TextStyles.Empty.Serialize("anything"));
        Assert.True(TextStyles.Parse(null, "anything").IsEmpty);
    }

    [Fact]
    public void Formatting_is_stored_as_versioned_runs_with_the_text_length_and_read_back_exactly()
    {
        var (text, styles) = Md("The creature gains **+2 Armor** until the *next dawn*, ***twice***.");
        var stored = styles.Serialize(text);
        Assert.Equal("""{"v":1,"len":55,"runs":[[19,8,1],[38,9,2],[49,5,3]]}""", stored);
        Assert.Equal(styles, TextStyles.Parse(stored, text));
    }

    [Fact]
    public void A_run_past_the_end_of_the_text_is_never_stored()
    {
        var styles = TextStyles.FromRuns([new StyleRun(2, 50, B)], 50);
        Assert.Equal("""{"v":1,"len":5,"runs":[[2,3,1]]}""", styles.Serialize("abcde"));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"len":5,"runs":[[0,1,1]]}""")]                   // no version
    [InlineData("""{"v":2,"len":5,"runs":[[0,1,1]]}""")]             // a future version
    [InlineData("""{"v":"1","len":5,"runs":[[0,1,1]]}""")]           // wrong type
    [InlineData("""{"v":1,"len":6,"runs":[[0,1,1]]}""")]             // stale: made for another text
    [InlineData("""{"v":1,"runs":[[0,1,1]]}""")]                     // no length
    [InlineData("""{"v":1,"len":5}""")]                              // no runs
    [InlineData("""{"v":1,"len":5,"runs":{}}""")]
    [InlineData("""{"v":1,"len":5,"runs":[[0,1]]}""")]               // a run without a style
    [InlineData("""{"v":1,"len":5,"runs":[[0,1,1,9]]}""")]
    [InlineData("""{"v":1,"len":5,"runs":[[0,0,1]]}""")]             // empty run
    [InlineData("""{"v":1,"len":5,"runs":[[-1,2,1]]}""")]            // negative
    [InlineData("""{"v":1,"len":5,"runs":[[3,3,1]]}""")]             // past the end
    [InlineData("""{"v":1,"len":5,"runs":[[0,1,4]]}""")]             // unknown style
    [InlineData("""{"v":1,"len":5,"runs":[[0,1,0]]}""")]             // no style
    [InlineData("""{"v":1,"len":5,"runs":[[0,3,1],[2,2,2]]}""")]     // overlapping
    [InlineData("""{"v":1,"len":5,"runs":[[3,1,1],[0,1,2]]}""")]     // out of order
    [InlineData("""{"v":1,"len":5,"runs":[[0,1.5,1]]}""")]
    [InlineData("""{"v":1,"len":5,"runs":[[2147483647,1,1]]}""")]
    [InlineData("""{"v":1,"len":5,"runs":[[0,1,1]]""")]              // truncated
    [InlineData("")]
    [InlineData("   ")]
    public void Malformed_or_stale_formatting_reads_as_none(string stored)
    {
        Assert.True(TextStyles.Parse(stored, "abcde").IsEmpty);
    }

    [Fact]
    public void A_stored_run_that_splits_a_surrogate_pair_reads_as_none()
    {
        const string text = "a🐉b"; // 'a', high, low, 'b'
        Assert.True(TextStyles.Parse("""{"v":1,"len":4,"runs":[[0,2,1]]}""", text).IsEmpty);
        Assert.Equal([new StyleRun(1, 2, B)], TextStyles.Parse("""{"v":1,"len":4,"runs":[[1,2,1]]}""", text).Runs);
    }

    [Fact]
    public void Unknown_extra_fields_in_a_version_1_value_are_ignored()
    {
        Assert.Equal([new StyleRun(0, 2, I)], TextStyles.Parse("""{"v":1,"len":5,"runs":[[0,2,2]],"note":"x"}""", "abcde").Runs);
    }

    [Fact]
    public void Equal_formatting_is_equal_and_readable_in_failures()
    {
        Assert.Equal(Md("**a**b*c*").Styles, Md("**a**b*c*").Styles);
        Assert.NotEqual(Md("**a**b").Styles, Md("*a*b").Styles);
        Assert.Equal("B 0+1, I 2+1", Md("**a**b*c*").Styles.ToString());
        Assert.Equal("(none)", TextStyles.Empty.ToString());
    }
}
