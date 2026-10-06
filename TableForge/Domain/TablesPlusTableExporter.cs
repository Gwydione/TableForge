using System.Text.Json;
using System.Text.Json.Serialization;

namespace TableForge.Domain;

/// <summary>What one Tables+ export produced: the table name inside the JSON, the JSON itself, and any warnings to show beside it.</summary>
public sealed record TablesPlusExport(string Name, string Json, IReadOnlyList<string> Warnings);

/// <summary>
/// Owlbear Rodeo Tables+ export: one result set of a saved table as one table in the Tables+ JSON import shape:
/// <code>
/// { "name": "Wilderness Encounters", "type": "weighted", "dice": "1d20", "entries": [ { "low": 1, "high": 3, "text": "Wolf pack" } ] }
/// </code>
/// Tables+ assigns ids and timestamps on import, so they are left out, as are description (TableForge's Description included), tags, folder and anything
/// TableForge-specific. The type and dice come from the dice's structured fields, never their display text:
///  - one die, with or without a fixed modifier: "weighted", "1d20", "1d100", "1d20+2", "1d20-2" (a genuine 66-sided die is "1d66");
///  - the d66 convention (tens and ones): "weighted", "T66";
///  - several summed dice with no modifier: "bell-curve", "2d6", "3d6";
///  - several dice with a modifier (2d6+1) are refused until Tables+ is known to handle them; the modifier is never dropped.
/// low/high are the numbers TableForge resolves with (a written "96–00" is 96 to 100), exported as they are: gaps, overlaps,
/// negative numbers and d66 ranges that cover unrollable values are never filled, resolved, shifted or clamped. A result set with
/// an open-ended row ("26+", "1 or less") is refused: low/high cannot say "no bound", and no number stands in for one. Text is the
/// authored result text, only trimmed, exactly as the Foundry export writes it: line breaks, Unicode, &amp;, &lt;, inline dice,
/// "{2d6}" and "#reroll" are all kept as written. Links, clamp settings and runtime state are never exported.
/// Checked live in Tables+ (2026-09-27) only for d20, d66 (T66) and 2d6 exports; see docs/RELEASE_TESTING.md for what is untested.
/// </summary>
public static class TablesPlusTableExporter
{
    public const string Weighted = "weighted";
    public const string BellCurve = "bell-curve";
    public const string D66Dice = "T66";

    public const string ClampWarning =
        "Tables+ JSON does not include TableForge’s Clamp to Range setting; rolls outside the exported ranges may have no matching entry.";

    public const string ModifiedDiceError =
        "Tables+ export does not support a fixed modifier on more than one die (such as 2d6+1) yet.";

    /// <summary>The JSON for one result set, or false with a reason when this table cannot be a Tables+ table.</summary>
    public static bool TryExport(RollableTable table, ResultSet resultSet, out TablesPlusExport? export, out string? error)
    {
        export = null;
        if ((error = FoundryTableExporter.Refusal(table, resultSet, out var index) ?? FoundryTableExporter.OpenRangeRefusal(resultSet, "Tables+")) is not null) return false;
        if (!TryMapDice(table.Dice, out var type, out var dice))
        {
            error = ModifiedDiceError;
            return false;
        }

        var name = FoundryTableExporter.Name(table, index);
        var dto = new TablesPlusTableDto(name, type, dice,
            resultSet.Entries.Select(e => new TablesPlusEntryDto(e.Min, e.Max, e.Text.Trim())).ToList());
        export = new TablesPlusExport(name, JsonSerializer.Serialize(dto, FoundryTableExporter.Options), FoundryTableExporter.ClampApplies(table, resultSet) ? [ClampWarning] : []);
        return true;
    }

    /// <summary>The Tables+ table type and dice for TableForge dice, or false for dice Tables+ export does not support (2d6+1).</summary>
    public static bool TryMapDice(DiceExpression dice, out string type, out string formula)
    {
        if (dice.IsD66)
        {
            (type, formula) = (Weighted, D66Dice);
            return true;
        }
        if (dice.Count > 1 && dice.Modifier != 0)
        {
            (type, formula) = ("", "");
            return false;
        }
        type = dice.Count == 1 ? Weighted : BellCurve;
        formula = $"{dice.Count}d{dice.Sides}";
        if (dice.Modifier != 0) formula += dice.Modifier > 0 ? $"+{dice.Modifier}" : $"-{-dice.Modifier}";
        return true;
    }

    /// <summary>A file name Windows accepts, made the same way as the Foundry one; "Tables+ table.json" when the name leaves nothing.</summary>
    public static string SuggestedFileName(string name) => FoundryTableExporter.SuggestedFileName(name, "Tables+ table");

    // The Tables+ import shape, and nothing more.
    private sealed record TablesPlusTableDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("dice")] string Dice,
        [property: JsonPropertyName("entries")] IReadOnlyList<TablesPlusEntryDto> Entries);

    private sealed record TablesPlusEntryDto(
        [property: JsonPropertyName("low")] int Low,
        [property: JsonPropertyName("high")] int High,
        [property: JsonPropertyName("text")] string Text);
}
