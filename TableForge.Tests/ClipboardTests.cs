using System.Text;
using System.Windows;
using System.Windows.Controls;
using TableForge.Import;

namespace TableForge.Tests;

/// <summary>Part E: which clipboard formats exist, and whether bold/italic (or richer structure) survives. Pure string analysis.</summary>
public class ClipboardAnalyzerTests
{
    private static ClipboardEntry Text(string format, string data) => new(format, data);

    /// <summary>Builds a CF_HTML clipboard string the way Windows programs do, with a fragment between the markers.</summary>
    public static string CfHtml(string fragment) =>
        "Version:0.9\r\nStartHTML:0000000105\r\nEndHTML:0000000999\r\nStartFragment:0000000141\r\nEndFragment:0000000900\r\n" +
        "<html><body>\r\n<!--StartFragment-->" + fragment + "<!--EndFragment-->\r\n</body></html>";

    private static ClipboardAnalysis Html(string fragment, string plain = "plain") =>
        ClipboardAnalyzer.Analyze([Text("UnicodeText", plain), Text("HTML Format", CfHtml(fragment))]);

    // ---- formats --------------------------------------------------------------------------------

    [Fact]
    public void Every_format_is_listed_with_its_kind_size_and_a_preview()
    {
        var analysis = ClipboardAnalyzer.Analyze(
        [
            Text("UnicodeText", "01-02 Ael\r\n03-04 Stan"),
            new ClipboardEntry("Preferred DropEffect", new byte[] { 1, 0, 0, 0 }),
            new ClipboardEntry("Weird", null),
        ]);

        Assert.Equal(["UnicodeText", "Preferred DropEffect", "Weird"], analysis.Formats.Select(f => f.Name).ToArray());
        Assert.Equal(("text", 21L), (analysis.Formats[0].Kind, analysis.Formats[0].Size));
        Assert.Equal("01-02 Ael 03-04 Stan", analysis.Formats[0].Preview);
        Assert.Equal(("bytes", 4L), (analysis.Formats[1].Kind, analysis.Formats[1].Size));
        Assert.Equal("none", analysis.Formats[2].Kind);
    }

    [Fact]
    public void What_paste_table_receives_is_the_plain_text_and_its_line_count()
    {
        var analysis = ClipboardAnalyzer.Analyze([Text("UnicodeText", "a\r\nb\r\nc\r\n"), Text("HTML Format", CfHtml("<b>x</b>"))]);

        Assert.Equal(3, analysis.PlainLineCount);
        Assert.Equal("a\r\nb\r\nc\r\n", analysis.PlainText);
    }

    [Fact]
    public void Ansi_text_is_used_when_there_is_no_unicode_text_and_nothing_when_there_is_no_text_at_all()
    {
        Assert.Equal("old", ClipboardAnalyzer.Analyze([Text("Text", "old")]).PlainText);
        Assert.Null(ClipboardAnalyzer.Analyze([new ClipboardEntry("Bitmap", null)]).PlainText);
    }

    // ---- CF_HTML fragments -------------------------------------------------------------------------

    [Fact]
    public void The_html_fragment_is_taken_from_between_the_markers()
    {
        Assert.Equal("<p>Hi <b>there</b></p>", ClipboardAnalyzer.HtmlFragmentOf(CfHtml("<p>Hi <b>there</b></p>")));
        Assert.StartsWith("<body>x</body>", ClipboardAnalyzer.HtmlFragmentOf("<html><body>x</body></html>"));   // no markers: from <body>
    }

    [Theory]
    [InlineData("<b>Ael</b>")]
    [InlineData("<strong>Ael</strong>")]
    [InlineData("<span style=\"font-weight:bold\">Ael</span>")]
    [InlineData("<span style=\"font-weight: 700; color:red\">Ael</span>")]
    [InlineData("<span style=\"FONT-WEIGHT:BOLD\">Ael</span>")]
    public void Bold_is_recognised_from_tags_and_styles(string fragment)
    {
        var a = Html(fragment);

        Assert.True(a.BoldSurvives);
        Assert.Equal(1, a.Html!.BoldRuns);
        Assert.Equal("**Ael**", a.Html.Markdown);
    }

    [Theory]
    [InlineData("<i>Ael</i>")]
    [InlineData("<em>Ael</em>")]
    [InlineData("<span style=\"font-style:italic\">Ael</span>")]
    [InlineData("<span style=\"font-style: oblique\">Ael</span>")]
    public void Italic_is_recognised_from_tags_and_styles(string fragment)
    {
        var a = Html(fragment);

        Assert.True(a.ItalicSurvives);
        Assert.False(a.BoldSurvives);
        Assert.Equal("*Ael*", a.Html!.Markdown);
    }

    [Theory]
    [InlineData("<span style=\"font-weight:normal\">Ael</span>")]
    [InlineData("<span style=\"font-weight:400\">Ael</span>")]
    [InlineData("<span style=\"font-style:normal\">Ael</span>")]
    [InlineData("<span>Ael</span>")]
    public void Ordinary_text_is_not_bold_or_italic(string fragment)
    {
        var a = Html(fragment);

        Assert.False(a.BoldSurvives);
        Assert.False(a.ItalicSurvives);
        Assert.Equal("Ael", a.Html!.Markdown);
    }

    [Fact]
    public void Nested_and_mixed_emphasis_becomes_lightweight_markers_with_spaces_left_outside()
    {
        var a = Html("<p>You <b>seek</b> a <i>permanent </i>solution, <b><i>truly</i></b>.</p>");

        Assert.Equal("You **seek** a *permanent* solution, ***truly***.", a.Html!.Markdown);
        Assert.Equal((2, 2), (a.Html.BoldRuns, a.Html.ItalicRuns));        // "seek","truly" bold; "permanent","truly" italic
    }

    [Fact]
    public void Breaks_and_blocks_keep_meaningful_line_breaks_and_entities_are_decoded()
    {
        var a = Html("<div>1 <b>Ael</b> &amp; Wulf<br>2 Stan</div><div>3 Cia</div>");

        Assert.Equal("1 **Ael** & Wulf\n2 Stan\n3 Cia", a.Html!.Markdown);
    }

    [Fact]
    public void Formatting_is_never_inferred_from_plain_text()
    {
        // Asterisks, capitals and the word "bold" in plain text are just text. Only markup counts.
        var a = ClipboardAnalyzer.Analyze([Text("UnicodeText", "**Ael** BOLD *italic* D100 SYLLABLE")]);

        Assert.False(a.BoldSurvives);
        Assert.False(a.ItalicSurvives);
        Assert.Null(a.Html);
        Assert.Null(a.RtfEmphasis);
    }

    [Fact]
    public void A_font_name_saying_bold_is_reported_as_a_hint_but_is_not_counted_as_bold()
    {
        var a = Html("<span style=\"font-family:Arial-BoldMT\">Ael</span>");

        Assert.False(a.BoldSurvives);
        Assert.Equal(1, a.Html!.FontNameHints);
    }

    // ---- RTF -----------------------------------------------------------------------------------------

    [Fact]
    public void Rtf_bold_and_italic_switches_are_counted_and_off_switches_are_not()
    {
        var a = ClipboardAnalyzer.Analyze([Text("Rich Text Format", @"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\pard {\b Ael\b0} and {\i rare\i0}\par next\tab cell\par}")]);

        Assert.Equal((1, 1), (a.RtfEmphasis!.BoldRuns, a.RtfEmphasis.ItalicRuns));
        Assert.True(a.BoldSurvives && a.ItalicSurvives);
        Assert.Equal((2, 1), (a.RtfParagraphs, a.RtfTabs));
    }

    [Theory]
    [InlineData(@"{\rtf1 \bin0 text \bullet \intbl \i0 \b0 \ilvl1 \info}")]
    [InlineData(@"{\rtf1 plain \ul underline \ulnone}")]
    public void Other_control_words_that_start_with_b_or_i_are_not_emphasis(string rtf)
    {
        var a = ClipboardAnalyzer.Analyze([Text("Rich Text Format", rtf)]);

        Assert.False(a.BoldSurvives);
        Assert.False(a.ItalicSurvives);
    }

    [Fact]
    public void Rtf_switches_with_parameters_count_only_when_not_zero()
    {
        Assert.Equal(1, ClipboardAnalyzer.RtfSwitchOns(@"{\b1 x}", 'b'));
        Assert.Equal(0, ClipboardAnalyzer.RtfSwitchOns(@"{\b0 x}", 'b'));
        Assert.Equal(2, ClipboardAnalyzer.RtfSwitchOns(@"{\b x}{\b\i y}", 'b'));
    }

    // ---- structure ---------------------------------------------------------------------------------------

    [Fact]
    public void Plain_only_or_simple_markup_is_not_richer_than_plain_text()
    {
        Assert.False(ClipboardAnalyzer.Analyze([Text("UnicodeText", "a\r\nb")]).RicherStructure);
        Assert.False(Html("a<br>b", "a\r\nb").RicherStructure);
    }

    [Fact]
    public void Positioned_styles_tables_tabs_and_extra_lines_count_as_richer_structure()
    {
        Assert.True(Html("<span style=\"position:absolute; left:72pt; top:100pt\">Ael</span>").RicherStructure);
        Assert.True(Html("<table><tr><td>1</td><td>Ael</td></tr></table>").RicherStructure);
        Assert.True(ClipboardAnalyzer.Analyze([Text("UnicodeText", "x"), Text("Rich Text Format", @"{\rtf1 a\tab b}")]).RicherStructure);
        Assert.True(Html("<p>a</p><p>b</p><p>c</p><p>d</p>", "a b c d").RicherStructure);   // more lines in the markup than in the text
    }

    [Fact]
    public void The_report_says_plainly_what_was_found()
    {
        var report = ClipboardAnalyzer.Analyze(
            [Text("UnicodeText", "Ael Wulf"), Text("HTML Format", CfHtml("<b>Ael</b> <i>Wulf</i>"))]).ToReport("PDFXEdit (pid 1)");

        Assert.Contains("Clipboard owner: PDFXEdit (pid 1)", report);
        Assert.Contains("UnicodeText", report);
        Assert.Contains("HTML Format", report);
        Assert.Contains("Bold survives: YES    Italic survives: YES", report);
        Assert.Contains("| **Ael** *Wulf*", report);
    }
}

/// <summary>Part F: the real Windows clipboard, and the real Paste Table path.</summary>
[Collection("UI")] // the clipboard is process-wide, so these must not overlap other UI tests
public class ClipboardPathTests
{
    /// <summary>Puts several formats on the real clipboard (as a PDF reader would) and restores any earlier text afterwards.</summary>
    private sealed class ClipboardScope : IDisposable
    {
        private readonly string? _previous;

        public ClipboardScope(string plain, string? html = null, string? rtf = null)
        {
            _previous = Try(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, plain);
            if (html is not null) data.SetData(DataFormats.Html, html);
            if (rtf is not null) data.SetData(DataFormats.Rtf, rtf);
            Retry(() => Clipboard.SetDataObject(data, copy: true));
        }

        public void Dispose()
        {
            if (_previous is not null) Try<object?>(() => { Clipboard.SetText(_previous); return null; });
        }

        private static T? Try<T>(Func<T> f) { try { return f(); } catch { return default; } }

        private static void Retry(Action a)
        {
            for (var i = 0; ; i++)
            {
                try { a(); return; }
                catch (System.Runtime.InteropServices.COMException) when (i < 15) { Thread.Sleep(80); }
            }
        }
    }

    private const string Fragment = "<b>Ael</b> and <i>Wulf</i>";

    [Fact]
    public void The_real_clipboard_reports_every_format_and_where_bold_and_italic_survive()
    {
        Sta.Run(() =>
        {
            using var _ = new ClipboardScope("01-02 Ael 51-52 Wulf", ClipboardAnalyzerTests.CfHtml(Fragment), @"{\rtf1 {\b Ael\b0} {\i Wulf\i0}}");

            var snapshot = ClipboardReader.Read();
            var analysis = ClipboardAnalyzer.Analyze(snapshot.Entries);

            var names = analysis.Formats.Select(f => f.Name).ToList();
            Assert.Contains("UnicodeText", names);
            Assert.Contains("HTML Format", names);
            Assert.Contains("Rich Text Format", names);
            Assert.Equal("01-02 Ael 51-52 Wulf", analysis.PlainText);
            Assert.True(analysis.BoldSurvives && analysis.ItalicSurvives);
            Assert.Equal("**Ael** and *Wulf*", analysis.Html!.Markdown);
            Assert.False(string.IsNullOrEmpty(snapshot.Owner));
        });
    }

    [Fact]
    public void Paste_Table_receives_only_the_plain_text_even_when_html_and_rtf_are_also_on_the_clipboard()
    {
        Sta.Run(() =>
        {
            var table = "D8 DIFFICULTY MODIFIER\r\n1 Child's play +30\r\n2 Effortless +20\r\n3-8 Easy +10";
            using var _ = new ClipboardScope(table, ClipboardAnalyzerTests.CfHtml("<b>D8 DIFFICULTY MODIFIER</b><br>1 <i>Child's play</i> +30"), @"{\rtf1 {\b D8}}");
            using var ui = new UiHarness(1, (db, c) => { });

            ui.Click("Paste Table…");
            var box = ui.One<TextBox>(t => t.Name == "PasteSourceBox");
            box.Focus();
            box.Paste();                                          // the real clipboard path, as Ctrl+V does
            ui.Layout();

            Assert.Equal(table, box.Text);                        // exactly the plain text: no markup, no asterisks
            Assert.DoesNotContain("<", box.Text);
            Assert.DoesNotContain("**", box.Text);

            ui.Click("Interpret");
            var review = (ViewModels.ReviewViewModel)ui.Main.Current!;
            Assert.Equal(["Difficulty", "Modifier"], review.ResultSets.Select(s => s.DisplayName).ToArray());   // and it imports
        });
    }

    [Fact]
    public void Pasting_a_multi_format_clipboard_of_a_standalone_number_table_imports_the_paragraphs()
    {
        Sta.Run(() =>
        {
            using var _ = new ClipboardScope(StandaloneParagraphTests.WhyDoYouGoOn.Replace("\n", "\r\n"),
                ClipboardAnalyzerTests.CfHtml("<p>1</p><p>You are <b>terrified</b>…</p>"));
            using var ui = new UiHarness(1, (db, c) => { });

            ui.Click("Paste Table…");
            var box = ui.One<TextBox>(t => t.Name == "PasteSourceBox");
            box.Focus();
            box.Paste();
            ui.Click("Interpret");

            var review = (ViewModels.ReviewViewModel)ui.Main.Current!;
            Assert.Equal("Why Do You Go On?", review.TableName);
            Assert.Equal(2, review.Rows.Count);
            Assert.StartsWith("You are terrified of reality", review.Rows[0].Text);
        });
    }
}
