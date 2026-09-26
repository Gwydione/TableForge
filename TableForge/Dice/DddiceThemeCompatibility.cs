using System.Text.Json.Nodes;

namespace TableForge.Dice;

/// <summary>One theme from a Digital Dice Box, already judged. Only <see cref="Id"/> and <see cref="Name"/> are ever stored.</summary>
/// <param name="Reason">Why TableForge cannot use it ("Missing d12, d20"); empty when <see cref="IsCompatible"/>.</param>
public sealed record DddiceTheme(string Id, string Name, bool IsCompatible, string Reason);

/// <summary>
/// Whether a dddice theme can roll every die TableForge asks dddice for: d4, d6, d8, d10, d10x (the percentile tens die), d12 and
/// d20 (a d66 is two ordinary d6). A die counts only when <c>available_dice</c> has an entry whose id is exactly that die — never
/// dddice's looser match on the entry's mesh type or notation — and its faces are the standard numbers: <c>values[id]</c> absent,
/// or exactly 1..N (d10x: 10, 20, …, 90, 0). Letters, notes, images, Fudge faces, doubled faces or anything unexpected mean the
/// die is non-standard. Custom dice with other ids are ignored. A shape TableForge does not understand is incompatible, never a crash.
/// </summary>
public static class DddiceThemeCompatibility
{
    private static readonly (string Id, int[] Faces)[] Required =
    [
        ("d4", Faces(4)), ("d6", Faces(6)), ("d8", Faces(8)), ("d10", Faces(10)),
        ("d10x", [10, 20, 30, 40, 50, 60, 70, 80, 90, 0]),
        ("d12", Faces(12)), ("d20", Faces(20)),
    ];

    private static int[] Faces(int n) => Enumerable.Range(1, n).ToArray();

    /// <summary>Judges one theme record from dddice. Null only when it has no id at all (nothing could refer to it).</summary>
    public static DddiceTheme? Evaluate(JsonNode? theme)
    {
        if (theme is not JsonObject obj || Text(obj["id"]) is not { Length: > 0 } id) return null;
        var name = Text(obj["name"]) is { Length: > 0 } n ? n.Trim() : id;

        if (obj["available_dice"] is not JsonArray available) return new(id, name, false, "Dice list could not be read");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in available)
            if (EntryId(entry) is { } entryId) ids.Add(entryId);

        var values = obj["values"];
        if (values is not null and not JsonObject) return new(id, name, false, "Dice faces could not be read");

        var missing = new List<string>();
        var nonStandard = new List<string>();
        foreach (var (die, faces) in Required)
        {
            if (!ids.Contains(die)) { missing.Add(die); continue; }
            var dieValues = (values as JsonObject)?[die];
            var present = (values as JsonObject)?.ContainsKey(die) ?? false;
            if (present && !IsExactly(dieValues, faces)) nonStandard.Add(die);
        }

        var reasons = new List<string>();
        var missingOrdinary = missing.Where(d => d != "d10x").ToList();
        if (missingOrdinary.Count > 0) reasons.Add($"Missing {string.Join(", ", missingOrdinary)}");
        if (missing.Contains("d10x")) reasons.Add("No d10x (percentile)");
        if (nonStandard.Count > 0) reasons.Add($"Non-standard {string.Join(", ", nonStandard)} faces");
        return new(id, name, reasons.Count == 0, string.Join("; ", reasons));
    }

    /// <summary>An entry is a plain string ("d6") or an object whose "id" is what a roll asks for (its "type" only picks the mesh).</summary>
    private static string? EntryId(JsonNode? entry) => entry switch
    {
        JsonValue v => Text(v),
        JsonObject o => Text(o["id"]),
        _ => null,
    };

    private static bool IsExactly(JsonNode? values, int[] expected)
    {
        if (values is not JsonArray array || array.Count != expected.Length) return false;
        for (var i = 0; i < expected.Length; i++)
        {
            // Plain whole numbers only: 1 is a face; "1", 1.5, null, {"src": …} are not.
            if (array[i] is not JsonValue v || v.GetValueKind() != System.Text.Json.JsonValueKind.Number) return false;
            if (!v.TryGetValue<int>(out var face))
            {
                if (!v.TryGetValue<double>(out var d) || d != Math.Floor(d) || d != expected[i]) return false;
                continue;
            }
            if (face != expected[i]) return false;
        }
        return true;
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
