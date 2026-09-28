using System.Text.RegularExpressions;

namespace TableForge.Domain;

/// <summary>How the faces of the dice become the one number a table is resolved with. There are exactly two conventions.</summary>
public enum RollConvention
{
    /// <summary>Add the faces up (then add the fixed modifier): 2d6 is 2 to 12. This is every ordinary roll.</summary>
    StandardSum = 0,

    /// <summary>
    /// Two d6 read in order as tens and ones: faces 3 then 5 make 35, not 8. The only legal results are 11-16, 21-26 ... 61-66.
    /// A d66 is a convention of two ordinary d6 dice, never a die with 66 sides.
    /// </summary>
    D66 = 1,
}

/// <summary>
/// An ordinary NdM roll with one optional fixed integer modifier: d6, 2d6, 3d8+4, d20-2, or the d66 convention.
/// Nothing else: no second term, no keep/drop, no exploding dice, no other operators.
/// </summary>
/// <param name="Modifier">Added once to the sum of the dice. Zero when the expression has none (and always zero for d66).</param>
/// <param name="Convention">
/// How the faces are read. It is part of the expression, never inferred from a table's rows: "2d6" always sums and "d66" always
/// concatenates. For <see cref="RollConvention.D66"/> the physical dice are Count 2, Sides 6.
/// </param>
public readonly partial record struct DiceExpression(int Count, int Sides, int Modifier = 0, RollConvention Convention = RollConvention.StandardSum)
{
    /// <summary>The d66 convention: two ordinary d6 read as tens and ones.</summary>
    public static DiceExpression D66 { get; } = new(2, 6, 0, RollConvention.D66);

    public bool IsD66 => Convention == RollConvention.D66;

    public const int MaxCount = 100;
    public const int MaxSides = 1000;
    public const int MaxModifier = 1000;

    /// <summary>The lowest final result, after the modifier (11 for d66).</summary>
    public int Min => IsD66 ? 11 : Count + Modifier;

    /// <summary>The highest final result, after the modifier (66 for d66).</summary>
    public int Max => IsD66 ? 66 : Count * Sides + Modifier;

    /// <summary>The dice alone, without the modifier. A provider that only rolls dice can roll this and let <see cref="Apply"/> finish the job.</summary>
    public DiceExpression Base => this with { Modifier = 0 };

    /// <summary>Turns the sum of the dice into the final result TableForge resolves.</summary>
    public int Apply(int diceSubtotal) => diceSubtotal + Modifier;

    /// <summary>
    /// True for a plain single d100, where numeric 100 may be shown as "00". A modified d100 (d100+5) produces final results
    /// rather than die faces, so it has no "00".
    /// </summary>
    public bool IsPercentile => Count == 1 && Sides == 100 && Modifier == 0;

    /// <summary>
    /// Whether this can be the result of a roll. Ordinary dice have a continuous range (Min to Max). A d66 has a discrete set:
    /// only values whose tens and ones digits are both 1-6, so 17, 20, 27 and 60 are impossible even though they lie between 11 and 66.
    /// </summary>
    public bool IsLegal(int value) =>
        value >= Min && value <= Max && (!IsD66 || (value / 10 is >= 1 and <= 6 && value % 10 is >= 1 and <= 6));

    /// <summary>The next legal result above <paramref name="value"/> (the next number for ordinary dice, 16 to 21 for d66), or null if there is none.</summary>
    public int? NextLegal(int value)
    {
        if (value >= Max) return null; // also keeps value + 1 from overflowing for an open upper bound
        for (var v = Math.Max(value + 1, Min); v <= Max; v++)
            if (IsLegal(v)) return v;
        return null;
    }

    /// <summary>
    /// Turns the faces of the physical dice, in the order they were rolled, into the final result. This is where the two conventions differ:
    /// standard dice add the faces and apply the modifier; a d66 reads the first face as tens and the second as ones.
    /// </summary>
    public int ResultFromFaces(IReadOnlyList<int> faces)
    {
        if (faces.Count != Count) throw new ArgumentException($"{this} needs {Count} faces, not {faces.Count}.", nameof(faces));
        foreach (var face in faces)
            if (face < 1 || face > Sides) throw new ArgumentException($"A d{Sides} cannot show {face}.", nameof(faces));

        return IsD66 ? faces[0] * 10 + faces[1] : Apply(faces.Sum());
    }

    public string FormatValue(int value) => IsPercentile && value == 100 ? "00" : value.ToString();

    /// <summary>Standard notation, normalized: "d20", "2d6", "2d6+1", "d20-2".</summary>
    public override string ToString()
    {
        if (IsD66) return "d66";
        // A bare "d66" means the convention, so a genuine single 66-sided die (only ever an old table) keeps its explicit count.
        var dice = Count == 1 && Sides != 66 ? $"d{Sides}" : $"{Count}d{Sides}";
        return Modifier == 0 ? dice : Modifier > 0 ? $"{dice}+{Modifier}" : $"{dice}-{-Modifier}";
    }

    public static bool TryParse(string? text, out DiceExpression dice)
    {
        dice = default;
        if (text is null) return false;

        var m = Pattern().Match(text.Trim());
        if (!m.Success) return false;

        var countText = m.Groups["count"].Value;
        var count = countText.Length == 0 ? 1 : int.Parse(countText);
        var sides = int.Parse(m.Groups["sides"].Value);
        var modifier = m.Groups["mod"].Success ? int.Parse(m.Groups["mod"].Value) : 0;
        if (m.Groups["sign"].Success && m.Groups["sign"].Value != "+") modifier = -modifier; // "-", or the minus/en dash a PDF may supply

        // "d66" with no count is the d66 convention. It takes no modifier (arithmetic on a tens-and-ones roll is not a thing tables do).
        if (countText.Length == 0 && sides == 66)
        {
            if (m.Groups["mod"].Success) return false;
            dice = D66;
            return true;
        }

        if (count < 1 || count > MaxCount || sides < 2 || sides > MaxSides || modifier > MaxModifier || modifier < -MaxModifier) return false;

        dice = new DiceExpression(count, sides, modifier);
        return true;
    }

    /// <summary>True for "d66+1" and the like: bare d66 with a modifier, refused on purpose (see <see cref="TryParse"/>).</summary>
    public static bool IsD66WithModifier(string? text) => text is not null && D66Modified().IsMatch(text.Trim());

    /// <summary>The sentence to add to the usual "not supported" message when someone writes d66 with a modifier; empty otherwise.</summary>
    public static string UnsupportedHint(string? text) => IsD66WithModifier(text) ? " A d66 takes no modifier." : "";

    public static DiceExpression Parse(string? text) =>
        TryParse(text, out var dice) ? dice : throw new FormatException($"Unsupported dice expression: '{text}'.");

    /// <summary>
    /// The exact grammar <see cref="TryParse"/> matches against (anchors included), exposed so a caller that scans free text for
    /// candidate tokens — inline dice detection — can reuse it instead of maintaining a second, divergent dice grammar.
    /// </summary>
    internal static string TokenPattern => Pattern().ToString();

    // NdM, optionally followed directly by one +N or -N. Anything longer or different does not match: it is never partly parsed.
    [GeneratedRegex("^[dD]66[+\\-\\u2212\\u2013][0-9]{1,4}$", RegexOptions.CultureInvariant)]
    private static partial Regex D66Modified();

    [GeneratedRegex("^(?<count>[0-9]{1,3})?[dD](?<sides>[0-9]{1,4})(?:(?<sign>[+\\-\\u2212\\u2013])(?<mod>[0-9]{1,4}))?$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
