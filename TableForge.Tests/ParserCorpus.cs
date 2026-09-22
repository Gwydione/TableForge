using System.Text;
using TableForge.Domain;
using TableForge.Import;

namespace TableForge.Tests;

/// <summary>
/// The parser regression corpus: representative copied-text cases, each with the interpretation TableForge V1 RC1 produces,
/// frozen as a readable snapshot. If a parser change alters any of these, the diff shows exactly what moved.
/// The corpus exists to pin current behavior before any later import work (for example OCR) builds on it.
/// </summary>
internal static class ParserCorpus
{
    public sealed record Case(string Name, string Source, DiceExpression? RowsDice = null, bool RowsOnly = false);

    // ---- rendering --------------------------------------------------------------------------------

    private static string Issues(IEnumerable<ParseIssue> issues) =>
        string.Concat(issues.Select(i =>
            $"ISSUE {i.Severity} {i.Code}" +
            (i.ResultSetIndex is { } s ? $" set{s}" : "") +
            (i.EntryIndex is { } e ? $"/row{e}" : "") +
            (i.SourceLine is { } l ? $" line{l}" : "") + "\n"));

    public static string Snapshot(TableImportDraft d)
    {
        var sb = new StringBuilder();
        sb.Append($"TABLE {d.TableName} | {d.DiceText}\n");
        foreach (var set in d.ResultSets)
        {
            sb.Append($"SET {(set.Name.Length == 0 ? "(unnamed)" : set.Name)}\n");
            foreach (var e in set.Entries) sb.Append($"  {e.RangeText} | {e.Text}\n");
        }
        sb.Append(Issues(d.Issues));
        return sb.ToString();
    }

    public static string Snapshot(RowsParseResult r)
    {
        var sb = new StringBuilder("ROWS\n");
        foreach (var e in r.Entries) sb.Append($"  {e.RangeText} | {e.Text}\n");
        sb.Append(Issues(r.Issues));
        return sb.ToString();
    }

    public static string Render(Case c) =>
        c.RowsOnly ? Snapshot(TableTextParser.ParseRows(c.Source, c.RowsDice)) : Snapshot(TableTextParser.Parse(c.Source));

    // ---- the corpus -------------------------------------------------------------------------------

    public static readonly IReadOnlyList<Case> Cases =
    [
        new("random-starting-gear", Fixtures.RandomStartingGear),
        new("room-features-multi-set", MultiSetParserTests.RoomFeatures),
        new("wrapped-continuation", "d6 Loot\n1 A very\nlong\nwrapped row\n2 Gem\n3-6 Rope (15 m)"),
        new("side-by-side-ranged-block", "d100 Names\n01-05 Ash        51-55 Kel\n06-10 Bar        56-60 Lor\n11-15 Cor        61-65 Mor"),
        new("side-by-side-ranged-lone-line", "d100 Names\n01-50 Ash        51-00 Kel"),
        new("ambiguous-single-space-columns", "d100 Names\n01-05 Ash 51-55 Kel"),
        new("ambiguous-parallel-columns", "d100 Both\n01-30 Cold air        01-25 Silence"),
        new("price-like-lone-line", "d20 Shop\n4 Rope    10 gp"),
        new("price-like-two-lines", "d20 Shop\n4 Rope    10 gp\n5 Lamp    11 gp"),
        new("single-value-columns-two-lines", "d6 Names\n1 Ash    4 Kel\n2 Bar    5 Lor"),
        new("single-value-columns-three-lines", "d6 Names\n1 Ash    4 Kel\n2 Bar    5 Lor\n3 Cor    6 Mor"),
        new("heading-title-case-unseparated", "d6 Weather\nDay\n1-3 Sun\n4-6 Cloud\nNight\n1-6 Moon"),
        new("heading-uppercase-no-restart", "d6 Loot\n1-3 Coin\n\nSTRAY NOTE\n4-6 Gem"),
        new("heading-with-no-rows", "d6 Loot\n1-3 Coin\n4-6 Gem\n\nNOTES"),
        new("rows-before-first-heading", "d6 Loot\n1-3 Coin\n4-6 Gem\n\nNOISE\n1-6 Hiss"),
        new("no-heading-no-dice", "1-2 Coin\n3-4 Gem"),
        new("row-only-paste", "01-30 Cold air\n31-65 Damp\nstone\n66-70 A        71-00 B", DiceExpression.Parse("d100"), RowsOnly: true),
        new("row-only-paste-malformed", "D6 OTHER TABLE\nNOISE\n1-3 Hiss\nstray words", DiceExpression.Parse("d6"), RowsOnly: true),
    ];

    /// <summary>RC2 additions: the real Broken Shores failures and their neighbours.</summary>
    public static readonly IReadOnlyList<Case> Rc2Cases =
    [
        new("rc2-standalone-number-paragraphs", StandaloneParagraphTests.WhyDoYouGoOn),
        new("rc2-standalone-range-second-paragraph", "d8 Weather\n\n1-3\n\nFair skies.\n\nBroken Shores 12\n\n4-8\nGrey."),
        new("rc2-standalone-number-line-kept-as-text", "d12 Reasons\n1\n\nYou will wait\n10 days for the rain to stop.\n\n2\n\nThe next reason."),
        new("rc2-standalone-stray-prose-first", "d6 Loot\n\nChoose one reason.\n\n1\n\nFirst.\n\n2\n\nSecond."),
        new("rc2-continuation-columns-single-space", ContinuationColumnTests.Syllables()),
        new("rc2-continuation-columns-with-gaps", ContinuationColumnTests.Syllables(separator: "        ")),
        new("rc2-continuation-two-lines-not-split", "d100 Names\n01-25 Ash 51-75 Kel\n26-50 Bar 76-00 Lor"),
        new("rc2-continuation-incoherent-not-split", "d100 Names\n01-02 A 51-52 W\n03-04 B 53-54 X\n07-08 C 55-56 Y\n09-10 D 57-58 Z"),
        new("rc2-price-like-block-not-split", "d20 Shop\n1 Item1 21 gp\n2 Item2 22 gp\n3 Item3 23 gp\n4 Item4 24 gp\n5 Item5 25 gp"),
        new("rc2-parallel-outputs-difficulty", ParallelOutputTests.Difficulty),
        new("rc2-parallel-outputs-column-gaps", "D6 NAME TYPE\n1 Ash    Elf\n2 Bar    Orc\n3-4 Cor    Elf\n5-6 Dun    Dwarf"),
        new("rc2-parallel-not-applied-to-ordinary-title", "D6 RANDOM ENCOUNTER\n1 Goblin ambush\n2 Wolves in the woods\n3-6 A quiet road"),
        new("rc2-heading-repeated-columns", "D100 SYLLABLE D100 SYLLABLE D100 SYLLABLE\n1-50 Ael\n51-100 Wulf"),
        new("rc2-heading-second-dice-ambiguous", "D100 GIVEN NAME D100 SURNAME\n1-50 Ael\n51-100 Wulf"),
        new("rc2-row-only-standalone", "1\n\nFirst paragraph\nwrapped.\n\n2\n\nSecond.", DiceExpression.Parse("d12"), RowsOnly: true),
        new("rc2-row-only-flattened-columns", ContinuationColumnTests.Syllables().Split("\n\n")[1], DiceExpression.Parse("d100"), RowsOnly: true),
    ];
}
