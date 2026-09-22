using System.Text.RegularExpressions;
using TableForge.Domain;

namespace TableForge.Import;

/// <param name="DisplayRange">Set only when the written form differs from plain numbers, e.g. "96–00" or "08".</param>
public readonly record struct ParsedRange(int Min, int Max, string? DisplayRange);

/// <summary>Turns range text ("1-2", "08", "96-00") into numeric values. Resolution only ever sees the numbers.</summary>
public static partial class RangeText
{
    /// <param name="dice">Used for "00" on a plain d100, and to allow negative numbers only when the dice can produce them (d20-2).</param>
    public static bool TryParse(string? text, DiceExpression? dice, out ParsedRange range, out string? error)
    {
        range = default;
        var m = (dice is { Min: < 0 } ? SignedPattern() : Pattern()).Match(text ?? "");
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

        // The written form, with the separator normalized to an en dash; leading zeros and signs are kept as written.
        var written = m.Groups["b"].Success ? $"{aText}–{bText}" : aText;
        var plain = min == max && !m.Groups["b"].Success ? $"{min}" : $"{min}–{max}";
        range = new ParsedRange(min, max, written == plain ? null : written);
        error = null;
        return true;
    }

    /// <summary>Parses a single unsigned roll value. "00" means 100 on a d100; "08" means 8.</summary>
    public static bool TryParseValue(string? text, DiceExpression? dice, out int value)
    {
        value = 0;
        var t = text?.Trim() ?? "";
        return t.Length > 0 && t.All(char.IsAsciiDigit) && TryNumber(t, dice, out value);
    }

    /// <summary>
    /// Parses the final result a person types or rolls by hand: whole digits, with an optional leading minus (a d20-2 can give -1).
    /// It is the result AFTER any modifier, so nothing is added to it here. Whether it is legal is for the dice to say.
    /// </summary>
    public static bool TryParseRollValue(string? text, DiceExpression? dice, out int value)
    {
        value = 0;
        var t = (text ?? "").Trim();
        var negative = t.StartsWith('-') || t.StartsWith('−');
        var digits = negative ? t[1..] : t;
        return digits.Length > 0 && digits.All(char.IsAsciiDigit) && TryNumber((negative ? "-" : "") + digits, dice, out value);
    }

    private static bool TryNumber(string token, DiceExpression? dice, out int value)
    {
        value = 0;
        var negative = token.StartsWith('-');
        var digits = negative ? token[1..] : token;
        if (digits.Length is 0 or > 6) return false;

        var magnitude = digits == "00" && !negative && dice is { IsPercentile: true } ? 100 : int.Parse(digits);
        value = negative ? -magnitude : magnitude;
        return true;
    }

    [GeneratedRegex(@"^\s*(?<a>[0-9]+)\s*(?:[-–—]\s*(?<b>[0-9]+)\s*)?$")]
    private static partial Regex Pattern();

    // For dice that can produce negative results: each number may carry a leading minus ("-1", "-1-0", "-3--1").
    [GeneratedRegex(@"^\s*(?<a>-?[0-9]+)\s*(?:[-–—]\s*(?<b>-?[0-9]+)\s*)?$")]
    private static partial Regex SignedPattern();
}
