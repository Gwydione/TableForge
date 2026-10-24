using System.Text;
using System.Text.RegularExpressions;
using TableForge.Domain;

namespace TableForge.Export.Pum;

/// <summary>
/// Result text for PUM. PUM renders Markdown bold and italic (F09-09) and leaves its other syntax characters alone when they
/// are not a complete command (F10R-D5: lone <c>%%</c>, unmatched braces, <c>;;not dice;;</c>, <c>::word::</c>, brackets,
/// backticks, <c>2 * 3</c>, snake_case, backslashes). Three complete sequences do run as commands: <c>{{Table}}</c> rolls a table,
/// <c>;;1d6;;</c> rolls dice and <c>::result::</c> inserts the roll. There is no way to escape them.
/// </summary>
public static partial class PumText
{
    /// <summary>The text of a row standing for TableForge's No Match.</summary>
    public const string NoResult = "(No result)";

    /// <summary>
    /// The text with its bold and italic as Markdown: <c>**bold**</c>, <c>*italic*</c>, <c>***both***</c>. Whitespace at the edges
    /// of a formatted stretch is kept outside the markers (<c>** x**</c> would not render), and a stretch that spans lines is
    /// marked line by line. <paramref name="stylesTouch"/> is true when two differently formatted stretches meet with no space
    /// between them (<c>**a***b*</c>), which PUM may read differently.
    /// </summary>
    public static string Markdown(string text, TextStyles styles, out bool stylesTouch)
    {
        stylesTouch = false;
        var result = new StringBuilder(text.Length + 16);
        var segments = styles.Segments(text);
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.Style == TextStyle.None)
            {
                result.Append(segment.Text);
                continue;
            }

            var previous = i > 0 ? segments[i - 1] : null;
            if (previous is { Style: not TextStyle.None } && !char.IsWhiteSpace(previous.Text[^1]) && !char.IsWhiteSpace(segment.Text[0]))
                stylesTouch = true;

            var marker = segment.Style switch
            {
                TextStyle.Bold => "**",
                TextStyle.Italic => "*",
                _ => "***",
            };
            var lines = segment.Text.Split('\n');
            for (var j = 0; j < lines.Length; j++)
            {
                if (j > 0) result.Append('\n');
                var line = lines[j];
                var lead = line.Length - line.TrimStart().Length;
                var core = line.Trim();
                if (core.Length == 0)
                {
                    result.Append(line);
                    continue;
                }
                result.Append(line, 0, lead).Append(marker).Append(core).Append(marker).Append(line, lead + core.Length, line.Length - lead - core.Length);
            }
        }
        return result.ToString();
    }

    /// <summary>The PUM commands in <paramref name="text"/>, each with what PUM would do: none for text that is safe to write.</summary>
    public static IReadOnlyList<(string Sequence, string Effect)> Commands(string text)
    {
        var found = new List<(string, string)>();
        foreach (Match m in NestedRoll().Matches(text)) found.Add((m.Value, "PUM would roll another table there"));
        foreach (Match m in InlineRoll().Matches(text)) found.Add((m.Value, "PUM would roll dice there"));
        foreach (Match m in RollToken().Matches(text)) found.Add((m.Value, "PUM would insert the roll there"));
        return found;
    }

    /// <summary>
    /// True when plain text has asterisks or underscores that Markdown reads as emphasis (<c>*word*</c>, <c>_word_</c>,
    /// <c>**word**</c>), so PUM may show it in italic or bold. <c>2 * 3</c> and <c>snake_case</c> are not.
    /// </summary>
    public static bool HasLiteralEmphasis(string text) => StarEmphasis().IsMatch(text) || UnderscoreEmphasis().IsMatch(text);

    [GeneratedRegex(@"\{\{.*?\}\}", RegexOptions.Singleline)]
    private static partial Regex NestedRoll();

    [GeneratedRegex(@";;[^;\n]*?[0-9]*[dD][0-9]+[^;\n]*?;;")]
    private static partial Regex InlineRoll();

    [GeneratedRegex("::result::", RegexOptions.IgnoreCase)]
    private static partial Regex RollToken();

    [GeneratedRegex(@"\*(?=[^\s*])[^*\n]*?(?<=[^\s*])\*")]
    private static partial Regex StarEmphasis();

    [GeneratedRegex(@"(?<![\p{L}\p{N}_])_(?=[^\s_])[^_\n]*?(?<=[^\s_])_(?![\p{L}\p{N}_])")]
    private static partial Regex UnderscoreEmphasis();
}
