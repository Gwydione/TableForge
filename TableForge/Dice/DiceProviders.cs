using TableForge.Domain;

namespace TableForge.Dice;

public interface IDiceProvider
{
    /// <summary>Returns a numeric result in the expression's legal range.</summary>
    int Roll(DiceExpression dice);
}

public sealed class BuiltInDiceProvider(Random? random = null) : IDiceProvider
{
    private readonly Random _random = random ?? Random.Shared;

    public int Roll(DiceExpression dice)
    {
        var total = 0;
        for (var i = 0; i < dice.Count; i++)
            total += _random.Next(1, dice.Sides + 1);
        return total;
    }
}
