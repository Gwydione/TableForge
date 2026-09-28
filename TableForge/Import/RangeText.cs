using System.Text.RegularExpressions;
using TableForge.Domain;

namespace TableForge.Import;

/// <param name="DisplayRange">Set only when the written form differs from plain numbers, e.g. "96–00" or "08"; always set for an open-ended range ("26+").</param>
public readonly record struct ParsedRange(int Min, int Max, string? DisplayRange)
{
    public bool IsOpenBelow => Min == RangeBounds.OpenBelow;
    public bool IsOpenAbove => Max == RangeBounds.OpenAbove;
}

/// <summary>Turns range text ("1-2", "08", "96-00", "-10-0", "26+", "1 or less") into numeric values. Resolution only ever sees the numbers.</summary>
public static partial class RangeText
{
    public const string OpenD66Message = "Open-ended ranges are not available for d66 tables.";

    /// <summary>
    /// A range is a number (5), a span (5-8, with a hyphen, en dash or em dash between), or open-ended: "26+" or "20 or more"
    /// (no upper bound), "1 or less" (no lower bound). Each number may carry a leading minus, a hyphen-minus or a real minus sign
    /// (−), on any dice: a table authored for modified rolls may cover values its dice alone never give ("-10-0" on a d20).
    /// An en or em dash is never read as a minus sign. Open bounds are stored as <see cref="RangeBounds"/> sentinels.
    /// </summary>
    /// <param name="dice">Used for "00" on a plain d100, and to refuse open-ended ranges on d66.</param>
    public static bool TryParse(string? text, DiceExpression? dice, out ParsedRange range, out string? error)
    {
        range = default;
        var open = OpenPattern().Match(text ?? "");
        if (open.Success)
        {
            if (dice is { IsD66: true })
            {
                error = OpenD66Message;
                return false;
            }
            var nText = Sign(open.Groups["n"].Value);
            if (!TryNumber(nText, dice, out var n))
            {
                error = "Range number is too large.";
                return false;
            }
            var above = !open.Groups["less"].Success;
            var written = open.Groups["plus"].Success ? $"{nText}+" : above ? $"{nText} or more" : $"{nText} or less";
            range = above ? new ParsedRange(n, RangeBounds.OpenAbove, written) : new ParsedRange(RangeBounds.OpenBelow, n, written);
            error = null;
            return true;
        }

        var m = Pattern().Match(text ?? "");
        if (!m.Success)
        {
            error = "Range must be a number (5) or a span (5-8).";
            return false;
        }

        var aText = Sign(m.Groups["a"].Value);
        var bText = m.Groups["b"].Success ? Sign(m.Groups["b"].Value) : aText;
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

        // The written form, with the separator normalized to an en dash and a real minus sign to "-"; leading zeros and signs are kept as written.
        var writtenSpan = m.Groups["b"].Success ? $"{aText}–{bText}" : aText;
        var plain = min == max && !m.Groups["b"].Success ? $"{min}" : $"{min}–{max}";
        range = new ParsedRange(min, max, writtenSpan == plain ? null : writtenSpan);
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

    /// <summary>A real minus sign (−) as a leading hyphen-minus, so each number has one spelling.</summary>
    private static string Sign(string number) => number.StartsWith('−') ? "-" + number[1..] : number;

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

    // Each number may carry a leading minus ("-1", "-1-0", "-3--1", "−10–0"); the separator between two numbers is a hyphen or dash.
    [GeneratedRegex(@"^\s*(?<a>[-−]?[0-9]+)\s*(?:[-–—]\s*(?<b>[-−]?[0-9]+)\s*)?$")]
    private static partial Regex Pattern();

    // Open-ended: "26+", "20 or more", "1 or less", "-5 or less".
    [GeneratedRegex(@"^\s*(?<n>[-−]?[0-9]+)(?:(?<plus>\+)|\s+or\s+(?:(?<less>less)|(?<more>more)))\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OpenPattern();
}
