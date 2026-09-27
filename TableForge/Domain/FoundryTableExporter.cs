using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TableForge.Domain;

/// <summary>What one Foundry export produced: the table name inside the JSON, the JSON itself, and any warnings to show beside it.</summary>
public sealed record FoundryExport(string Name, string Json, IReadOnlyList<string> Warnings);

/// <summary>
/// Foundry VTT export: one result set of a saved table as the JSON the Roll Table Importer module accepts
/// (https://github.com/jendave/roll-table-importer), never Foundry's own RollTable document format:
/// <code>
/// { "name": "Goods", "formula": "1d12", "results": [ { "range": [1, 4], "text": "Backpacks or sacks" } ] }
/// </code>
/// The formula is built from the dice's structured fields, never their display text: the d66 convention is
/// "1d6 * 10 + 1d6" (tens and ones, exactly), while a genuine 66-sided die stays "1d66". Ranges are the numbers TableForge
/// resolves with (a written "96–00" is [96, 100]). Text is the authored result text, only trimmed; line breaks, Unicode and
/// characters like &amp; and &lt; are kept as they are, and inline dice stay plain text. Links, ids, clamp settings and every
/// kind of runtime state (rolls, modifiers, resolved inline values) are never exported, and there is no description.
/// Gaps and overlaps are exported as they are; only a table that cannot become a Foundry table at all is refused.
/// </summary>
public static class FoundryTableExporter
{
    public const string D66Formula = "1d6 * 10 + 1d6";

    public const string ClampWarning =
        "This table uses Clamp to Range in TableForge. Foundry does not clamp, so some rolls there may find no result.";

    /// <summary>Readable output: "king’s sword" and "Sword & Shield" stay as written instead of ’ and & (still valid JSON).</summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The JSON for one result set, or false with a reason when this table cannot be a Foundry table.</summary>
    public static bool TryExport(RollableTable table, ResultSet resultSet, out FoundryExport? export, out string? error)
    {
        export = null;
        error = null;

        var index = table.ResultSets.IndexOf(resultSet);
        if (index < 0)
        {
            error = "Choose a result set to export.";
            return false;
        }
        if (table.Name.Trim().Length == 0)
        {
            error = "The table needs a name.";
            return false;
        }
        if (resultSet.Entries.Count == 0)
        {
            error = "This result set has no rows.";
            return false;
        }
        if (resultSet.Entries.FirstOrDefault(e => e.Min > e.Max) is { } backwards)
        {
            error = $"The row \"{backwards.RangeLabel}\" has a range that runs backwards.";
            return false;
        }

        var name = Name(table, index);
        var dto = new FoundryTableDto(name, Formula(table.Dice),
            resultSet.Entries.Select(e => new FoundryResultDto([e.Min, e.Max], e.Text.Trim())).ToList());
        export = new FoundryExport(name, JsonSerializer.Serialize(dto, Options), Warnings(table, resultSet));
        return true;
    }

    /// <summary>A Foundry roll formula with the same outcomes: "1d20", "2d6+1", "1d20-2", and the d66 convention as tens plus ones.</summary>
    public static string Formula(DiceExpression dice)
    {
        if (dice.IsD66) return D66Formula;
        var formula = $"{dice.Count}d{dice.Sides}";
        return dice.Modifier == 0 ? formula : dice.Modifier > 0 ? $"{formula}+{dice.Modifier}" : $"{formula}-{-dice.Modifier}";
    }

    /// <summary>The table's name, or "Table — Result Set" when it has several result sets (an unnamed one by its position).</summary>
    public static string Name(RollableTable table, int resultSetIndex)
    {
        if (table.ResultSets.Count <= 1) return table.Name.Trim();
        var set = table.ResultSets[resultSetIndex].Name.Trim();
        return $"{table.Name.Trim()} — {(set.Length > 0 ? set : $"Result set {resultSetIndex + 1}")}";
    }

    /// <summary>
    /// Clamp is the one thing Foundry cannot reproduce, and it only matters when the dice can land outside the rows: then
    /// TableForge looks those rolls up at the nearest end, and Foundry finds nothing. Clamp that never applies is not mentioned.
    /// </summary>
    private static IReadOnlyList<string> Warnings(RollableTable table, ResultSet resultSet)
    {
        if (!table.ClampResultsToRange || !TableClamp.TryGetRange(table, out _, out _)) return [];
        var min = resultSet.Entries.Min(e => e.Min);
        var max = resultSet.Entries.Max(e => e.Max);
        return table.Dice.Min < min || table.Dice.Max > max ? [ClampWarning] : [];
    }

    /// <summary>
    /// A file name Windows accepts, from the exported table name (the name inside the JSON is never changed): characters
    /// Windows forbids become "_", trailing spaces and dots are dropped, and a reserved device name (CON, NUL, COM1...) gets a "_".
    /// </summary>
    public static string SuggestedFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat("<>:\"/\\|?*").ToHashSet();
        var safe = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.', ' ');
        if (safe.Length > 100) safe = safe[..100].TrimEnd('.', ' ');
        if (safe.Length == 0) safe = "Foundry table";
        if (ReservedNames.Contains(safe.Split('.')[0].TrimEnd(' '))) safe += "_";
        return safe + ".json";
    }

    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })],
        StringComparer.OrdinalIgnoreCase);

    // The exact Roll Table Importer shape, and nothing more.
    private sealed record FoundryTableDto(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("formula")] string Formula,
        [property: JsonPropertyName("results")] IReadOnlyList<FoundryResultDto> Results);

    private sealed record FoundryResultDto(
        [property: JsonPropertyName("range")] int[] Range,
        [property: JsonPropertyName("text")] string Text);
}
