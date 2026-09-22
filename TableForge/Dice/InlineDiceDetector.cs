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
    public static IReadOnlyList<DiceExpression> Detect(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var seen = new HashSet<DiceExpression>();
        var found = new List<DiceExpression>();
        foreach (Match m in Scanner.Matches(text))
            if (DiceExpression.TryParse(m.Value, out var dice) && seen.Add(dice))
                found.Add(dice);
        return found;
    }
}
