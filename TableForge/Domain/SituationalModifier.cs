using System.Globalization;
using System.Text.RegularExpressions;

namespace TableForge.Domain;

/// <summary>
/// The Roll screen's temporary situational modifier: one signed whole number, typed by hand, that TableForge adds to the
/// result of the next successful primary table roll. It is not part of the table's dice, is never saved with a table, and
/// carries no game rules: the sum is used exactly as calculated, even when no row covers it.
/// </summary>
public static partial class SituationalModifier
{
    /// <summary>The largest modifier either way: -1000 through +1000.</summary>
    public const int Limit = 1000;

    /// <summary>
    /// Reads the Modifier field. Blank is 0; otherwise one optional sign and whole-number digits ("3", "+3", "-2", "-0"),
    /// with surrounding spaces ignored, from -<see cref="Limit"/> to +<see cref="Limit"/>. Anything else ("+", "2.5",
    /// "1d4", "+2-1") is refused rather than read as 0.
    /// </summary>
    public static bool TryParse(string? text, out int value)
    {
        value = 0;
        var t = (text ?? "").Trim();
        if (t.Length == 0) return true;

        var m = Shape().Match(t);
        if (!m.Success || !int.TryParse(m.Groups["digits"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size > Limit)
            return false;
        value = t[0] == '-' ? -size : size;
        return true;
    }

    /// <summary>The modifier with its sign, as the roll breakdown shows it: "+3", "-2".</summary>
    public static string Signed(int modifier) => modifier >= 0 ? $"+{modifier}" : modifier.ToString(CultureInfo.InvariantCulture);

    /// <summary>What the Modifier field says when it cannot be read.</summary>
    public const string InvalidMessage = "The modifier must be a whole number from -1000 to +1000.";

    // ASCII digits only: \d would also accept digits from other scripts.
    [GeneratedRegex(@"^[+\-]?(?<digits>[0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();
}
