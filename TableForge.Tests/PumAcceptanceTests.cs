using System.IO;
using TableForge.Domain;
using TableForge.Export.Pum;
using TableForge.Portable;

namespace TableForge.Tests;

/// <summary>
/// The Slice 1 acceptance bundle: one synthetic table per export rule (every name starts "ZZTF S1"), plus four that must be
/// skipped, checked here on every run. To import it into PUM Companion, run this test with TABLEFORGE_PUM_ACCEPTANCE_DIR set to
/// a folder: the bundle and its report are written there. Setting TABLEFORGE_PUM_SMOKE_COLLECTION to a .tfcollection file as
/// well also exports that whole Collection beside it (the Of Blood and Clay smoke test, kept out of the repository).
/// </summary>
public class PumAcceptanceTests
{
    public const string CollectionName = "ZZ TF Slice 1 Acceptance";
    public static readonly DateTime Stamp = new(2026, 10, 24, 12, 0, 0);
    private const string NR = PumText.NoResult;

    private static TableEntry E(int min, int max, string text, string? display = null) => new() { Min = min, Max = max, Text = text, DisplayRange = display };

    private static RollableTable T(string name, string dice, params TableEntry[] entries) => new()
    {
        Name = $"ZZTF S1 {name}", Dice = DiceExpression.Parse(dice), ResultSets = [new ResultSet { Entries = [.. entries] }],
    };

    private static TableEntry Styled(int min, int max, string text, params StyleRun[] runs) =>
        E(min, max, text).Also(e => e.Styles = TextStyles.FromRuns(runs, text.Length));

    /// <summary>The request: keys 1-18 are exported tables, 90-93 must be skipped, and 99 is a link target left out of the export.</summary>
    public static PumExportRequest Request()
    {
        const string Folder = "Folder A";
        var multi = T("A10 Two Result Sets", "d6");
        multi.ResultSets =
        [
            new ResultSet { Name = "Weather", Entries = [E(1, 3, "A10 Rain 1-3"), E(4, 6, "A10 Sun 4-6")] },
            new ResultSet { Name = "Wind", Entries = [E(1, 2, "A10 Calm 1-2"), E(3, 6, "A10 Gale 3-6")] },
        ];
        var clamped = T("A09 Clamp To Range", "d10", E(3, 5, "A09 LOW 3-5 (1-2 clamp here)"), E(6, 7, "A09 HIGH 6-7 (8-10 clamp here)"));
        clamped.ClampResultsToRange = true;
        var formatting = "A11 Bold lead: italic sentence. plain";

        PumSourceTable[] tables =
        [
            new(1, T("A01 Plain d6", "d6", E(1, 2, "A01 LOW 1-2"), E(3, 6, "A01 HIGH 3-6")), Folder),
            new(2, T("A02 Gaps d8", "d8", E(3, 4, "A02 ROW A 3-4"), E(7, 8, "A02 ROW B 7-8")), Folder),
            new(3, T("A03 Modifier 2d6+1", "2d6+1", E(3, 7, "A03 LOW 3-7"), E(8, 13, "A03 HIGH 8-13"))),
            new(4, T("A04 Below One d6-2", "d6-2", E(2, 4, "A04 ROW 2-4"))),
            new(5, T("A05 d66 Convention", "d66", E(11, 16, "A05 11-16"), E(21, 36, "A05 21-36"), E(51, 66, "A05 51-66"))),
            new(6, T("A06 Real 66-Sided Die", "1d66", E(1, 33, "A06 LOW 1-33"), E(34, 66, "A06 HIGH 34-66"))),
            new(7, T("A07 d100 With 00", "d100", E(1, 50, "A07 LOW 01-50", "01–50"), E(51, 100, "A07 HIGH 51-00", "51–00"))),
            new(8, T("A08 Open-Ended", "d6+4", E(5, 7, "A08 5-7"), E(8, RangeBounds.OpenAbove, "A08 8+", "8+"))),
            new(9, clamped),
            new(10, multi),
            new(11, T("A11 Formatting", "d2",
                Styled(1, 1, formatting, new StyleRun(4, 10, TextStyle.Bold), new StyleRun(15, 16, TextStyle.Italic)),
                Styled(2, 2, "A11 bold italic and mid-word italic", new StyleRun(4, 11, TextStyle.Bold | TextStyle.Italic), new StyleRun(29, 4, TextStyle.Italic)))),
            new(12, T("A12 Links", "d4",
                E(1, 1, "A12 FOLLOW").Also(e => e.LinkedTableId = 1),
                E(2, 2, "A12 OUTSIDE").Also(e => e.LinkedTableId = 99),
                E(3, 3, "A12 UNRESOLVED").Also(e => e.UnresolvedLinkName = "Somewhere Else"),
                E(4, 4, "A12 PLAIN"))),
            new(13, T("A13 Empty Row", "d2", E(1, 1, ""), E(2, 2, "A13 NOT EMPTY"))),
            new(14, T("A14 Yes/No – King’s Road", "d2", E(1, 2, "A14 SPECIAL NAME"))),
            new(15, T("A15 Duplicate", "d2", E(1, 2, "A15 FIRST"))),
            new(16, T("a15 duplicate", "d2", E(1, 2, "A15 SECOND (renamed)"))),
            new(17, T("A16 Literal Syntax", "d2", E(1, 2, "A16 50%% and ;;not dice;; and 2 * 3 and Roll 1d6 later"))),
            new(18, T("A17 Extended Rows", "d6", E(1, 6, "A17 NORMAL 1-6"), E(7, 10, "A17 EXTENDED 7-10"))),

            new(90, T("X1 Overlap (must be skipped)", "d6", E(1, 4, "X1 A"), E(3, 6, "X1 B"))),
            new(91, T("X2 Below One On Row 1 (must be skipped)", "d6-2", E(1, 4, "X2 A"))),
            new(92, T("X3 PUM Command (must be skipped)", "d6", E(1, 6, "X3 roll {{Other Table}}"))),
            new(93, T("X4 All Blank (must be skipped)", "d6", E(1, 3, ""), E(4, 6, " "))),
        ];
        return new(CollectionName, tables, new Dictionary<long, string> { [99] = "Outside Table" });
    }

    private static PumTable Table(PumExportResult result, string nameStart) => result.Tables.Single(t => t.Name.StartsWith($"ZZTF S1 {nameStart}"));

    private static List<(int, string)> Rows(PumTable table) => table.Rows.Select(r => (r.Key, r.Text)).ToList();

    [Fact]
    public void The_acceptance_bundle_has_the_expected_tables_rows_and_skips_and_is_written_when_requested()
    {
        var result = PumExporter.Export(Request(), Stamp);
        var report = result.Report;

        Assert.Equal(18, report.ExportedCount);
        Assert.Equal(["ZZTF S1 X1 Overlap (must be skipped)", "ZZTF S1 X2 Below One On Row 1 (must be skipped)", "ZZTF S1 X3 PUM Command (must be skipped)",
            "ZZTF S1 X4 All Blank (must be skipped)"],
            report.Tables.Where(t => !t.Exported).Select(t => t.SourceName));

        Assert.Equal([(2, "A01 LOW 1-2"), (6, "A01 HIGH 3-6"), (7, NR)], Rows(Table(result, "A01")));
        Assert.Equal($"{CollectionName} - Folder A", Table(result, "A01").Category);
        Assert.Equal([(2, NR), (4, "A02 ROW A 3-4"), (6, NR), (8, "A02 ROW B 7-8"), (9, NR)], Rows(Table(result, "A02")));
        Assert.Equal([(2, NR), (7, "A03 LOW 3-7"), (13, "A03 HIGH 8-13"), (14, NR)], Rows(Table(result, "A03")));
        Assert.Equal([(1, NR), (4, "A04 ROW 2-4"), (5, NR)], Rows(Table(result, "A04")));
        Assert.Equal(("1D66", "1d66"), (Table(result, "A05").Formula, Table(result, "A06").Formula));
        Assert.Equal([(16, "A05 11-16"), (36, "A05 21-36"), (50, NR), (66, "A05 51-66"), (67, NR)], Rows(Table(result, "A05")));
        Assert.Equal([(50, "A07 LOW 01-50"), (100, "A07 HIGH 51-00"), (101, NR)], Rows(Table(result, "A07")));
        Assert.Equal([(4, NR), (7, "A08 5-7"), (10, "A08 8+")], Rows(Table(result, "A08")));
        Assert.Equal([(5, "A09 LOW 3-5 (1-2 clamp here)"), (7, "A09 HIGH 6-7 (8-10 clamp here)")], Rows(Table(result, "A09")));
        Assert.Equal("Weather: A10 Rain 1-3\nWind: A10 Gale 3-6", PumEquivalence.PumLookup(Table(result, "A10").Rows, 3).Text);
        Assert.Equal(["A11 **Bold lead:** *italic sentence.* plain", "A11 ***bold italic*** and mid-word *ital*ic"], Table(result, "A11").Rows.Take(2).Select(r => r.Text));
        Assert.Equal(["A12 FOLLOW\n→ ZZTF S1 A01 Plain d6", "A12 OUTSIDE\n→ Outside Table (not included in this export)",
            "A12 UNRESOLVED\n→ Somewhere Else (unresolved link)", "A12 PLAIN"], Table(result, "A12").Rows.Take(4).Select(r => r.Text));
        Assert.Equal("", Table(result, "A13").Rows[0].Text);
        Assert.Equal("ZZTF S1 A14 Yes - No – King’s Road", Table(result, "A14").Name);
        Assert.Equal(["ZZTF S1 A15 Duplicate", "ZZTF S1 a15 duplicate (2)"], result.Tables.Where(t => t.Name.Contains("15")).Select(t => t.Name));
        Assert.Equal("A16 50%% and ;;not dice;; and 2 * 3 and Roll 1d6 later", Table(result, "A16").Rows[0].Text);
        Assert.Equal([(6, "A17 NORMAL 1-6"), (10, "A17 EXTENDED 7-10"), (11, NR)], Rows(Table(result, "A17")));

        Assert.Equal(result.Bundle, PumExporter.Export(Request(), Stamp).Bundle);   // deterministic

        if (Environment.GetEnvironmentVariable("TABLEFORGE_PUM_ACCEPTANCE_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "ZZTF_S1_Acceptance.pumb"), result.Bundle!);
            File.WriteAllText(Path.Combine(dir, "ZZTF_S1_Acceptance_report.txt"), report.ToText());

            if (Environment.GetEnvironmentVariable("TABLEFORGE_PUM_SMOKE_COLLECTION") is { Length: > 0 } collectionPath)
            {
                var read = PortableCollectionReader.ReadFile(collectionPath);
                Assert.True(read.Succeeded, read.Summary);
                var before = PortableCollectionExporter.Write(read.Collection!);
                var smoke = PumExporter.Export(PumExportRequest.FromPortable(read.Collection!), Stamp);
                Assert.Equal(before, PortableCollectionExporter.Write(read.Collection!));
                var stem = Path.GetFileNameWithoutExtension(collectionPath);
                if (smoke.Bundle is not null) File.WriteAllBytes(Path.Combine(dir, $"{stem}_PUM_smoke.pumb"), smoke.Bundle);
                File.WriteAllText(Path.Combine(dir, $"{stem}_PUM_smoke_report.txt"), smoke.Report.ToText());
            }
        }
    }
}
