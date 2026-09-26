using TableForge.Domain;

namespace TableForge.Dice;

/// <summary>A dddice problem with a message written for the person using TableForge.</summary>
public class DddiceException(string message, bool isAuthProblem = false, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The guest token or room was refused, so the saved session is no use and must be created afresh.</summary>
    public bool IsAuthProblem { get; } = isAuthProblem;

    /// <summary>A passing problem (network, timeout, busy, dddice server error): nothing saved needs to change.</summary>
    public bool IsTemporary { get; init; }

    /// <summary>About the connected account or its theme: the way forward is in Account… (reconnect, or choose a theme).</summary>
    public bool IsAccountProblem { get; init; }
}

/// <summary>This table's dice have no dddice equivalent (a d3, d7, d66...). The connection is fine; only this roll cannot be shown.</summary>
public sealed class DddiceUnsupportedDiceException(string message) : DddiceException(message);

/// <summary>One die face as dddice reports it: its dddice die type ("d6", "d10x") and the raw face value.</summary>
public sealed record DddiceFace(string Type, int Value);

/// <summary>
/// The network and rendering boundary. The real one is <see cref="WebViewDddiceRoller"/>; tests use a fake, so nothing in the
/// automated suite depends on the live dddice service.
/// </summary>
public interface IDddiceRoomRoller
{
    /// <summary>Guest access, room, renderer, theme, realtime connection. Completes when a roll can be made.</summary>
    Task PrepareAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Rolls these dice visibly. Completes ONLY when dddice reports the dice have settled (its "roll:finished" event),
    /// with the faces in the order requested. Never completes early with a result that is already known.
    /// </summary>
    Task<IReadOnlyList<DddiceFace>> RollAsync(IReadOnlyList<string> diceTypes, CancellationToken cancellationToken);

    /// <summary>Forgets the prepared page so the next <see cref="PrepareAsync"/> starts afresh (the account or theme changed).</summary>
    void Reset() { }
}

/// <summary>A dddice roll worked out from its faces, before and after TableForge's modifier.</summary>
/// <param name="Faces">Every die face in request order. Kept so that a future d66 can read [3,5] as 35 instead of 3 + 5.</param>
/// <param name="BaseSubtotal">The dice alone (a d100 counts as one 1-100 value per tens/ones pair). For a d66, the tens-and-ones reading.</param>
/// <param name="Final">The base subtotal with TableForge's own fixed modifier applied once.</param>
public sealed record DddiceRollResult(IReadOnlyList<DddiceFace> Faces, int BaseSubtotal, int Final);

/// <summary>
/// TableForge NdM to dddice dice and back (see the spike report). d4/d6/d8/d10/d12/d20 are single dice with faces 1..N.
/// d100 is not a dddice die: it is a "d10x" (the tens die, faces 1..10 where 10 means 00) with a "d10" (faces 1..10, 10 counts as ten),
/// which together cover 1-100 evenly. Only the base dice are ever sent; the modifier is TableForge's own.
/// </summary>
public static class DddiceDiceMapping
{
    /// <summary>The free guest theme: the only one a guest may use.</summary>
    public const string GuestTheme = "dddice-bees";

    public static bool IsSupported(DiceExpression dice) => dice.Sides is 4 or 6 or 8 or 10 or 12 or 20 or 100;

    /// <summary>The dddice dice for the BASE dice of an expression (the modifier is not part of this).</summary>
    public static IReadOnlyList<string> ToDddice(DiceExpression dice)
    {
        if (!IsSupported(dice)) throw new DddiceUnsupportedDiceException($"dddice cannot show a d{dice.Sides}. Choose Built-in Dice to roll this table.");
        var list = new List<string>();
        for (var i = 0; i < dice.Count; i++)
        {
            if (dice.Sides == 100) { list.Add("d10x"); list.Add("d10"); }
            else list.Add($"d{dice.Sides}");
        }
        return list;
    }

    /// <summary>Totals the faces of the base dice (from the raw faces, not from any total dddice supplies) and applies the modifier once.</summary>
    public static DddiceRollResult Interpret(DiceExpression dice, IReadOnlyList<DddiceFace> faces)
    {
        var expected = ToDddice(dice.Base);
        if (faces.Count != expected.Count || !faces.Select(f => f.Type).SequenceEqual(expected))
            throw new DddiceException("dddice returned dice that do not match the roll that was asked for.");

        var subtotal = 0;
        if (dice.IsD66)
        {
            // Two ordinary d6, read in the order dddice returns them (stable: request order): first = tens, second = ones.
            foreach (var f in faces)
                if (f.Value < 1 || f.Value > 6) throw new DddiceException("dddice returned a die face that is out of range.");
            subtotal = dice.ResultFromFaces(faces.Select(f => f.Value).ToList());
        }
        else if (dice.Sides == 100)
        {
            if (faces.Any(f => f.Value < 1 || f.Value > 10)) throw new DddiceException("dddice returned a die face that is out of range.");
            for (var i = 0; i < faces.Count; i += 2)
                subtotal += faces[i].Value % 10 * 10 + faces[i + 1].Value; // tens die 10 = "00"; the ones die keeps 1..10, so each pair is 1..100
        }
        else
        {
            foreach (var f in faces)
            {
                if (f.Value < 1 || f.Value > dice.Sides) throw new DddiceException("dddice returned a die face that is out of range.");
                subtotal += f.Value;
            }
        }

        var final = dice.IsD66 ? subtotal : dice.Apply(subtotal); // for d66 the faces are already the final tens-and-ones value
        if (!dice.IsLegal(final)) throw new DddiceException("dddice returned a result outside the legal range for this roll.");
        return new DddiceRollResult(faces, subtotal, final);
    }
}

/// <summary>
/// Rolls with dddice's visible 3D dice. Only the base dice go to dddice; TableForge totals the returned faces and applies
/// <see cref="DiceExpression.Modifier"/> itself, so a "2d6+1" shows a 2d6 and TableForge adds the 1.
/// </summary>
public sealed class DddiceDiceProvider(IDddiceRoomRoller roller) : IPreparableDiceProvider
{
    public Task PrepareAsync(CancellationToken cancellationToken) => roller.PrepareAsync(cancellationToken);

    public void Reset() => roller.Reset();

    public async Task<int> RollAsync(DiceExpression expression, CancellationToken cancellationToken) =>
        (await RollDetailedAsync(expression, cancellationToken)).Final;

    /// <summary>The same roll with the individual faces kept, for anything that needs more than the total.</summary>
    public async Task<DddiceRollResult> RollDetailedAsync(DiceExpression expression, CancellationToken cancellationToken)
    {
        var dice = DddiceDiceMapping.ToDddice(expression.Base);
        var faces = await roller.RollAsync(dice, cancellationToken); // completes only after the dice have settled
        return DddiceDiceMapping.Interpret(expression, faces);
    }
}
