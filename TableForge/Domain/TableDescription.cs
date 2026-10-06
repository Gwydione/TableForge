namespace TableForge.Domain;

/// <summary>
/// A table's optional Description (<see cref="RollableTable.Description"/>): plain text that says when or how the table is
/// used. Informational only — it never affects parsing, rolling or resolution, and it is never part of an external export.
/// </summary>
public static class TableDescription
{
    /// <summary>The longest description, in UTF-16 characters (what a WPF TextBox's MaxLength counts).</summary>
    public const int MaxLength = 2000;

    public static readonly string TooLongMessage = $"The description is longer than {MaxLength:N0} characters.";

    /// <summary>
    /// How Review/Edit saves a description: line breaks become "\n", the whole value is trimmed (so whitespace-only is ""),
    /// and nothing inside it is changed. Never used on import, which keeps a valid file's description exactly as written.
    /// </summary>
    public static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
}
