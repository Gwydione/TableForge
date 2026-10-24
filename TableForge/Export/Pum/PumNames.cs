using System.Text;
using System.Text.RegularExpressions;

namespace TableForge.Export.Pum;

/// <summary>
/// PUM table names and categories. PUM stores each table in a folder named after it, so a name must be a usable Windows folder
/// name, and names must stay distinct after PUM's own storage-name changes (it stores "Yes/No" as "Yes_No"), ignoring case.
/// </summary>
public static partial class PumNames
{
    public const int MaxLength = 100;
    public const string FallbackName = "TableForge table";
    public const string FallbackCategory = "TableForge";

    /// <summary>
    /// A name PUM can store: "/" becomes " - " (so "Yes/No" reads "Yes - No"), the other characters Windows forbids and control
    /// characters become "_", runs of whitespace (line breaks included) become one space, trailing dots and spaces are dropped,
    /// at most <see cref="MaxLength"/> characters, and a reserved device name (CON, NUL, COM1...) gets a "_".
    /// </summary>
    public static string Normalize(string? name)
    {
        var spaced = Whitespace().Replace((name ?? "").Replace("/", " - "), " ");
        var safe = new StringBuilder(spaced.Length);
        foreach (var c in spaced) safe.Append("<>:\"\\|?*".Contains(c) || char.IsControl(c) ? '_' : c);

        var result = Fit(Whitespace().Replace(safe.ToString(), " ").Trim(), MaxLength);
        if (result.Length == 0) return FallbackName;
        if (ReservedNames.Contains(result.Split('.')[0].TrimEnd(' '))) result += "_";
        return result;
    }

    /// <summary>
    /// Makes already-normalized names unique, ignoring case, in order: the first keeps its name, later ones get " (2)", " (3)"...
    /// (shortened to keep within <see cref="MaxLength"/>). The same input always gives the same names.
    /// </summary>
    public static IReadOnlyList<string> MakeUnique(IReadOnlyList<string> names)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(names.Count);
        foreach (var name in names)
        {
            var unique = name;
            for (var n = 2; !used.Add(unique); n++)
            {
                var suffix = $" ({n})";
                unique = Fit(name, MaxLength - suffix.Length) + suffix;
            }
            result.Add(unique);
        }
        return result;
    }

    /// <summary>"Collection" for an Unfiled table, "Collection - Folder" for a filed one.</summary>
    public static string Category(string? collectionName, string? folderName)
    {
        var collection = Whitespace().Replace(collectionName ?? "", " ").Trim();
        if (collection.Length == 0) collection = FallbackCategory;
        var folder = Whitespace().Replace(folderName ?? "", " ").Trim();
        return folder.Length == 0 ? collection : $"{collection} - {folder}";
    }

    /// <summary>At most <paramref name="length"/> characters, never ending in a dot, a space or half of a surrogate pair.</summary>
    private static string Fit(string text, int length)
    {
        if (text.Length > length) text = text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];
        return text.TrimEnd('.', ' ');
    }

    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })],
        StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
