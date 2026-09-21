using System.Text.RegularExpressions;
using TableForge.Domain;

namespace TableForge.Import;

/// <param name="DisplayRange">Set only when the written form differs from plain numbers, e.g. "96–00" or "08".</param>
public readonly record struct ParsedRange(int Min, int Max, string? DisplayRange);

/// <summary>Turns range text ("1-2", "08", "96-00") into numeric values. Resolution only ever sees the numbers.</summary>
public static partial class RangeText
{
    public static bool TryParse(string? text, DiceExpression? dice, out ParsedRange range, out string? error)
    {
        range = default;
        var m = Pattern().Match(text ?? "");
        if (!m.Success)
        {
            error = "Range must be a number (5) or a span (5-8).";
            return false;
        }

        var aText = m.Groups["a"].Value;
        var bText = m.Groups["b"].Success ? m.Groups["b"].Value : aText;
        if (!TryNumber(aText, dice, out var min) || !TryNumber(bText, dice, out var max))
        {
            error = "Range number is too large.";
            return false;
        }
        if (min > max)
        {
            error = $"Range {min}–{max} runs backwards.";
            return false;
        }

        var written = Whitespace().Replace(text!, "").Replace('-', '–').Replace('—', '–');
        var plain = min == max && !m.Groups["b"].Success ? $"{min}" : $"{min}–{max}";
        range = new ParsedRange(min, max, written == plain ? null : written);
        error = null;
        return true;
    }

    /// <summary>Parses a single roll value. "00" means 100 on a d100; "08" means 8.</summary>
    public static bool TryParseValue(string? text, DiceExpression? dice, out int value)
    {
        value = 0;
        var t = text?.Trim() ?? "";
        return t.Length > 0 && t.All(char.IsAsciiDigit) && TryNumber(t, dice, out value);
    }

    private static bool TryNumber(string token, DiceExpression? dice, out int value)
    {
        value = 0;
        if (token.Length > 6) return false;
        value = token == "00" && dice is { IsPercentile: true } ? 100 : int.Parse(token);
        return true;
    }

    [GeneratedRegex(@"^\s*(?<a>[0-9]+)\s*(?:[-–—]\s*(?<b>[0-9]+)\s*)?$")]
    private static partial Regex Pattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
