namespace TableForge.Domain;

/// <summary>
/// Copy Table Text: one result set of a table as plain text, from TableForge's structured data (never raw pasted text):
/// <code>
/// TABLE NAME
/// d10
///
/// 1-2&lt;TAB&gt;Backpack
/// 3&lt;TAB&gt;Knife
/// </code>
/// Lines end in CRLF; there is no line break after the last row. The same table and result set always give exactly the
/// same text. Ranges keep their written notation ("00", "08", "96-00"; d66 as 11-66) with a plain hyphen between the two
/// numbers; a range written as plain numbers is shown as TableForge displays the dice (a d100's 100 is "00"). Text is
/// only trimmed, and its line breaks and tabs each become one space (a tab separates the columns); nothing else is
/// changed. Links, inline-roll results and every other runtime state are not table text and are never included.
/// <see cref="ExportRows"/> is the same text without the name, dice and blank line (Copy for Sojour).
/// </summary>
public static class TableTextExporter
{
    public const string NewLine = "\r\n";

    public static string Export(RollableTable table, ResultSet resultSet) =>
        string.Join(NewLine, [Flatten(table.Name), table.Dice.ToString(), "", .. Rows(table, resultSet)]);

    /// <summary>
    /// Copy for Sojour: exactly the rows <see cref="Export"/> writes, with no name, dice or blank line before them, because a
    /// Sojour Lookup Table pastes (Ctrl+V) every line as one row of two cells. An empty result set gives "".
    /// </summary>
    public static string ExportRows(RollableTable table, ResultSet resultSet) => string.Join(NewLine, Rows(table, resultSet));

    private static IEnumerable<string> Rows(RollableTable table, ResultSet resultSet) =>
        resultSet.Entries.Select(entry => $"{Range(entry, table.Dice)}\t{Flatten(entry.Text)}");

    /// <summary>The written notation when there is one (TableForge stores its separator as an en dash; it is copied as "-").</summary>
    private static string Range(TableEntry entry, DiceExpression dice)
    {
        if (entry.DisplayRange is { } written && written.Trim().Length > 0) return written.Trim().Replace('–', '-');
        return entry.Min == entry.Max ? dice.FormatValue(entry.Min) : $"{dice.FormatValue(entry.Min)}-{dice.FormatValue(entry.Max)}";
    }

    /// <summary>Trims, then turns each line break (CRLF, CR or LF) and each tab into exactly one space. Nothing else changes.</summary>
    private static string Flatten(string text) =>
        text.Trim().Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
}
