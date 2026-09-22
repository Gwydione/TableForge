using TableForge.Domain;

namespace TableForge.DddiceSpike;

/// <summary>
/// The smallest change the spike proposes next to <c>IDiceProvider</c> (which stays as it is, synchronous): a provider that may take seconds,
/// can fail and can be cancelled. Returns the FINAL number TableForge resolves (base dice plus the modifier, via <c>DiceExpression.Apply</c>).
/// </summary>
public interface IAsyncDiceProvider
{
    Task<int> RollAsync(DiceExpression dice, CancellationToken cancellationToken);
}

/// <summary>Adapter so the built-in roller fits the same call site.</summary>
public sealed class BuiltInAsyncDiceProvider(TableForge.Dice.IDiceProvider inner) : IAsyncDiceProvider
{
    public Task<int> RollAsync(DiceExpression dice, CancellationToken cancellationToken) => inner.RollAsync(dice, cancellationToken);
}

public sealed class DddiceDiceProvider(DddiceVisualRoller roller) : IAsyncDiceProvider
{
    public async Task<int> RollAsync(DiceExpression dice, CancellationToken cancellationToken)
    {
        var result = await roller.RollAsync(DiceMapping.ToDddice(dice.Base), operatorObject: null, cancellationToken);
        return dice.Apply(DiceMapping.Subtotal(dice.Base, result.Values));
    }
}
