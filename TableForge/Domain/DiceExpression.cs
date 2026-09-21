using System.Text.RegularExpressions;

namespace TableForge.Domain;

/// <summary>An ordinary NdM roll (d6, d100, 2d6, 3d8). No modifiers or special syntax.</summary>
public readonly partial record struct DiceExpression(int Count, int Sides)
{
    public const int MaxCount = 100;
    public const int MaxSides = 1000;

    public int Min => Count;
    public int Max => Count * Sides;

    /// <summary>True for a single d100, where numeric 100 may be shown as "00".</summary>
    public bool IsPercentile => Count == 1 && Sides == 100;

    public bool IsLegal(int value) => value >= Min && value <= Max;

    public string FormatValue(int value) => IsPercentile && value == 100 ? "00" : value.ToString();

    public override string ToString() => Count == 1 ? $"d{Sides}" : $"{Count}d{Sides}";

    public static bool TryParse(string? text, out DiceExpression dice)
    {
        dice = default;
        if (text is null) return false;

        var m = Pattern().Match(text.Trim());
        if (!m.Success) return false;

        var countText = m.Groups["count"].Value;
        var count = countText.Length == 0 ? 1 : int.Parse(countText);
        var sides = int.Parse(m.Groups["sides"].Value);
        if (count < 1 || count > MaxCount || sides < 2 || sides > MaxSides) return false;

        dice = new DiceExpression(count, sides);
        return true;
    }

    public static DiceExpression Parse(string? text) =>
        TryParse(text, out var dice) ? dice : throw new FormatException($"Unsupported dice expression: '{text}'.");

    [GeneratedRegex("^(?<count>[0-9]{1,3})?[dD](?<sides>[0-9]{1,4})$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
