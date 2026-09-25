using System.Text;
using System.Text.RegularExpressions;
using TableForge.Domain;

namespace TableForge.Dice;

/// <summary>
/// Finds dice expressions TableForge already knows how to roll inside a displayed result's free text, so the roll screen can
/// offer an auxiliary "Roll d20"-style action beside the text without ever changing the text itself.
/// This is deliberately NOT a second, more permissive dice grammar: it only locates candidate tokens with strict word
/// boundaries (so "UD6" and "4d6kh3" cannot leak a false "d6"), and <see cref="DiceExpression.TryParse"/> — the exact same
/// parser a table's own dice use — decides whether each candidate is actually supported. Its job stops there: it never
/// infers quantities, labels or intent from the surrounding sentence.
/// </summary>
public static class InlineDiceDetector
{
    // Candidates share DiceExpression's own grammar (its regex, anchors stripped), wrapped in "not adjacent to another
    // word character" so a token embedded in a larger word (UD6, 4d6kh3) never matches.
    private static readonly Regex Scanner = new(
        $@"(?<!\w){DiceExpression.TokenPattern[1..^1]}(?!\w)", RegexOptions.CultureInvariant);

    /// <summary>Every supported expression found in <paramref name="text"/>, each one once, in order of first appearance.</summary>
    public static IReadOnlyList<DiceExpression> Detect(string? text) => FindAll(text).Select(m => m.Expression).Distinct().ToList();

    /// <summary>
    /// Every occurrence of a supported expression in <paramref name="text"/>, in order, with where it sits in the text. Unlike
    /// <see cref="Detect"/>, a repeated expression is reported once per occurrence, so each span can be resolved in place.
    /// </summary>
    public static IReadOnlyList<InlineDiceMatch> FindAll(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var found = new List<InlineDiceMatch>();
        foreach (Match m in Scanner.Matches(text))
            if (DiceExpression.TryParse(m.Value, out var dice))
                found.Add(new InlineDiceMatch(dice, m.Index, m.Length));
        return found;
    }

    /// <summary>
    /// A transient copy of <paramref name="text"/> with each matched span replaced by its expression's current value, or kept as
    /// written when <paramref name="valueOf"/> has none for it yet. Only the matched spans change: the surrounding characters
    /// ("+", ",", ".") are copied as they are, and nothing is re-worded. <paramref name="matches"/> must come from
    /// <see cref="FindAll"/> on the same text.
    /// </summary>
    public static string Substitute(string text, IReadOnlyList<InlineDiceMatch> matches, Func<DiceExpression, string?> valueOf)
    {
        var resolved = new StringBuilder(text.Length);
        var copied = 0;
        foreach (var match in matches)
        {
            resolved.Append(text, copied, match.Index - copied);
            resolved.Append(valueOf(match.Expression) ?? text.Substring(match.Index, match.Length));
            copied = match.Index + match.Length;
        }
        return resolved.Append(text, copied, text.Length - copied).ToString();
    }
}

/// <summary>One supported expression found in a result's text, and the exact characters it occupies there.</summary>
public readonly record struct InlineDiceMatch(DiceExpression Expression, int Index, int Length);
