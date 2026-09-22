using TableForge.Domain;

namespace TableForge.Dice;

/// <summary>
/// Produces a roll for a dice expression. The contract is the boundary between "how dice are rolled" and the rest of TableForge:
/// a provider returns the single FINAL number (dice summed, fixed modifier applied) and the resolver never learns which provider
/// produced it. There are exactly two providers: <see cref="BuiltInDiceProvider"/> and <see cref="DddiceDiceProvider"/>.
/// Rolling is asynchronous because a visual provider takes seconds, needs the network and can fail; the built-in provider
/// simply returns an already-completed task.
/// </summary>
public interface IDiceProvider
{
    /// <summary>
    /// Returns a final numeric result the expression can produce (<see cref="DiceExpression.IsLegal"/>): a value from Min to Max for ordinary dice,
    /// one of the 36 tens-and-ones values for a d66.
    /// A visual provider completes only after the dice have settled. Throws if the roll cannot be made; never returns a partial result.
    /// </summary>
    Task<int> RollAsync(DiceExpression expression, CancellationToken cancellationToken);
}

/// <summary>A provider that must be made ready (connections, rendering) before it can roll.</summary>
public interface IPreparableDiceProvider : IDiceProvider
{
    /// <summary>Gets the provider ready. Safe to call again after a failure. Throws with a message a person can read.</summary>
    Task PrepareAsync(CancellationToken cancellationToken);
}

public sealed class BuiltInDiceProvider(Random? random = null) : IDiceProvider
{
    private readonly Random _random = random ?? Random.Shared;

    /// <summary>
    /// Rolls immediately. <see cref="RollAsync"/> is this, already completed, so callers see the result with no delay.
    /// Every die is rolled independently and kept in order, so a d66 can read them as tens and ones instead of adding them.
    /// </summary>
    public int Roll(DiceExpression dice)
    {
        var faces = new int[dice.Count];
        for (var i = 0; i < faces.Length; i++)
            faces[i] = _random.Next(1, dice.Sides + 1);
        return dice.ResultFromFaces(faces);
    }

    public Task<int> RollAsync(DiceExpression expression, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested ? Task.FromCanceled<int>(cancellationToken) : Task.FromResult(Roll(expression));
}
