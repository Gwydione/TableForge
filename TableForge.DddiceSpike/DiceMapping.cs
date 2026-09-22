using TableForge.Domain;

namespace TableForge.DddiceSpike;

/// <summary>
/// TableForge NdM to dddice dice. Observed (see the spike report): d4/d6/d8/d10/d12/d20 are single dice with raw faces 1..N;
/// d100 is not a die type - it is a "d10x" (tens die, faces 1..10 where 10 means 00) plus a d10 (faces 1..10, 10 counts as 10),
/// and dddice's own total for the pair is a uniform 1..100. Values come back in the order the dice were requested.
/// </summary>
public static class DiceMapping
{
    public static bool IsSupported(DiceExpression dice) => dice.Sides is 4 or 6 or 8 or 10 or 12 or 20 or 100;

    /// <summary>The dddice die list for the *base* dice (the modifier never goes to dddice).</summary>
    public static IReadOnlyList<string> ToDddice(DiceExpression dice)
    {
        if (!IsSupported(dice)) throw new NotSupportedException($"dddice cannot roll a d{dice.Sides} visually.");
        var list = new List<string>();
        for (var i = 0; i < dice.Count; i++)
        {
            if (dice.Sides == 100) { list.Add("d10x"); list.Add("d10"); }
            else list.Add($"d{dice.Sides}");
        }
        return list;
    }

    /// <summary>The base-dice subtotal, computed from the raw faces (not from dddice's total, so nothing depends on how it adds).</summary>
    public static int Subtotal(DiceExpression dice, IReadOnlyList<RestDie> values)
    {
        var expected = ToDddice(dice);
        if (values.Count != expected.Count || !values.Select(v => v.Type).SequenceEqual(expected))
            throw new DddiceException($"dddice returned {string.Join(",", values.Select(v => v.Type))} for {string.Join(",", expected)}.");

        if (dice.Sides != 100) return values.Sum(v => v.Value);

        var total = 0;
        for (var i = 0; i < values.Count; i += 2)
            total += (values[i].Value % 10) * 10 + values[i + 1].Value; // d10x 10 = "00" -> 0 tens; d10 keeps 1..10, so a pair is 1..100
        return total;
    }
}
