using System.Globalization;

namespace TableForge.Domain;

/// <summary>
/// Open-ended ranges ("26+", "1 or less") are stored in the ordinary numeric bounds as two sentinels: <see cref="OpenBelow"/>
/// for a row with no lower bound and <see cref="OpenAbove"/> for a row with no upper bound. A typed range number has at most
/// six digits, so a sentinel can never be an authored value. They are internal only: a label is always made by <see cref="Label"/>
/// (or is the row's written form), and "the value after" a bound is always <see cref="Next"/>, never a bare + 1.
/// </summary>
public static class RangeBounds
{
    public const int OpenBelow = int.MinValue;
    public const int OpenAbove = int.MaxValue;

    public static bool IsOpen(int bound) => bound is OpenBelow or OpenAbove;

    /// <summary>The value right after <paramref name="bound"/>; null after an open upper bound, which nothing follows.</summary>
    public static int? Next(int bound) => bound == OpenAbove ? null : bound + 1;

    /// <summary>
    /// A range as TableForge shows it: "5", "1–5", "-10–0", and for open bounds "26+" or "1 or less". Numbers are shown as the
    /// <paramref name="dice"/> show them when given (a d100's 100 is "00"). Never shows a sentinel.
    /// </summary>
    public static string Label(int min, int max, DiceExpression? dice = null)
    {
        string F(int v) => dice is { } d ? d.FormatValue(v) : v.ToString(CultureInfo.InvariantCulture);
        if (min == OpenBelow && max == OpenAbove) return "any value";
        if (min == OpenBelow) return $"{F(max)} or less";
        if (max == OpenAbove) return $"{F(min)}+";
        return min == max ? F(min) : $"{F(min)}–{F(max)}";
    }
}
