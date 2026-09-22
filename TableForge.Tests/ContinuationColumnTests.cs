using TableForge.Domain;
using TableForge.Import;

namespace TableForge.Tests;

/// <summary>Parts B and D: one result set printed in two page columns, with the visual gap lost and the heading repeated.</summary>
public class ContinuationColumnTests
{
    private static readonly string[] LeftNames =
        ["Ael", "Stan", "Cia", "Cor", "Maer", "Dun", "Fen", "Gar", "Hal", "Ivo", "Jor", "Kai", "Lir",
         "Mor", "Nen", "Oth", "Pel", "Quin", "Rin", "Sav", "Tor", "Ulf", "Vex", "Wyn", "Lynn"];

    private static readonly string[] RightNames =
        ["Wulf", "Mal", "Dred", "Ryk", "Val", "Bram", "Cael", "Dorn", "Eld", "Fyr", "Gorm", "Hest", "Iss",
         "Jax", "Krag", "Lorn", "Mirk", "Norr", "Orm", "Pike", "Quor", "Rusk", "Sten", "Thal", "Shel"];

    /// <summary>The full 01–100 syllable table as extraction delivers it: two ranges per line, single spaces only.</summary>
    public static string Syllables(string separator = " ", string heading = "D100 SYLLABLE D100 SYLLABLE")
    {
        var lines = Enumerable.Range(0, 25).Select(i =>
        {
            var left = $"{i * 2 + 1:00}-{i * 2 + 2:00}";
            var right = i == 24 ? "99-100" : $"{51 + i * 2:00}-{52 + i * 2:00}";
            return $"{left} {LeftNames[i]}{separator}{right} {RightNames[i]}";
        });
        return heading + "\n\n" + string.Join("\n", lines);
    }

    private static string[] Rows(TableImportDraft d) => d.ResultSets[0].Entries.Select(e => $"{e.RangeText} {e.Text}").ToArray();

    // ---- the full syllable table --------------------------------------------------------------

    [Fact]
    public void The_full_syllable_table_becomes_one_result_set_in_logical_order()
    {
        var draft = TableTextParser.Parse(Syllables());

        Assert.Equal("Syllable", draft.TableName);
        Assert.Equal("d100", draft.DiceText);
        var set = Assert.Single(draft.ResultSets);
        Assert.Equal(50, set.Entries.Count);
        Assert.Equal(["01-02 Ael", "03-04 Stan", "05-06 Cia", "07-08 Cor", "09-10 Maer"], Rows(draft).Take(5).ToArray());
        Assert.Equal(["47-48 Wyn", "49-50 Lynn", "51-52 Wulf", "53-54 Mal", "55-56 Dred"], Rows(draft).Skip(23).Take(5).ToArray());
        Assert.Equal(["97-98 Thal", "99-100 Shel"], Rows(draft).TakeLast(2).ToArray());
    }

    [Fact]
    public void Every_number_from_1_to_100_is_covered_exactly_once()
    {
        var table = Fixtures.ParseAndBuild(Syllables());
        var entries = table.ResultSets[0].Entries;

        var covered = entries.SelectMany(e => Enumerable.Range(e.Min, e.Max - e.Min + 1)).ToList();

        Assert.Equal(Enumerable.Range(1, 100), covered);                 // in order, none missing, none twice
        Assert.Empty(TableValidator.Validate(table));
        Assert.Equal((1, 2, "01–02"), (entries[0].Min, entries[0].Max, entries[0].DisplayRange));
        Assert.Equal((99, 100, (string?)null), (entries[^1].Min, entries[^1].Max, entries[^1].DisplayRange));
    }

    [Fact]
    public void The_repeated_heading_is_read_once_and_the_two_recoveries_are_reported_as_information()
    {
        var draft = TableTextParser.Parse(Syllables());

        Assert.Equal(
            new[] { ParseIssueCode.HeadingRepeated, ParseIssueCode.SideBySideSplit }.OrderBy(c => c),
            draft.Issues.Select(i => i.Code).OrderBy(c => c));
        Assert.All(draft.Issues, i => Assert.Equal(ParseIssueSeverity.Info, i.Severity));
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.HeadingRepeated && i.Message.Contains("“Syllable”"));
    }

    [Fact]
    public void The_imported_table_rolls_from_both_halves()
    {
        using var temp = new TempDatabase();
        using var db = temp.Open();
        var saved = db.SaveTable(Fixtures.ParseAndBuild(Syllables(), db.CreateCollection("C").Id));
        var loaded = db.LoadTable(saved.Id)!;

        string At(int roll) => TableResolver.Resolve(loaded, roll).Results[0].Entry!.Text;

        Assert.Equal(["Ael", "Ael", "Stan", "Lynn", "Wulf", "Wulf", "Shel", "Shel"], new[] { 1, 2, 3, 50, 51, 52, 99, 100 }.Select(At).ToArray());
    }

    [Fact]
    public void Real_column_gaps_still_work_exactly_as_before()
    {
        var spaced = TableTextParser.Parse(Syllables(separator: "        "));

        Assert.Equal(50, spaced.ResultSets[0].Entries.Count);
        Assert.Equal("01-02 Ael", Rows(spaced)[0]);
        Assert.Equal("51-52 Wulf", Rows(spaced)[25]);
    }

    [Fact]
    public void An_odd_number_of_rows_puts_the_extra_left_row_in_the_left_column()
    {
        // 1-26 on the left (13 rows), 27-50 on the right (12 rows): the last printed line holds only the left column's 13th row.
        var lines = Enumerable.Range(0, 12).Select(i => $"{i * 2 + 1}-{i * 2 + 2} L{i} {27 + i * 2}-{28 + i * 2} R{i}").Append("25-26 L12");
        var draft = TableTextParser.Parse("d50 Odd\n" + string.Join("\n", lines));

        var covered = Fixtures.ParseAndBuild("d50 Odd\n" + string.Join("\n", lines)).ResultSets[0].Entries
            .SelectMany(e => Enumerable.Range(e.Min, e.Max - e.Min + 1)).ToList();
        Assert.Equal(Enumerable.Range(1, 50), covered);
        Assert.Equal(25, draft.ResultSets[0].Entries.Count);
        Assert.Equal(["L11", "L12", "R0"], draft.ResultSets[0].Entries.Skip(11).Take(3).Select(e => e.Text).ToArray());
    }

    [Fact]
    public void Extra_headings_around_the_block_do_not_prevent_the_split()
    {
        var draft = TableTextParser.Parse("D100 SYLLABLE D100 SYLLABLE\n\nRoll once for each syllable of the name.\n\n" + Syllables().Split("\n\n")[1]);

        Assert.Equal(50, draft.ResultSets[0].Entries.Count);
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.UnrecognizedLine);   // the instruction line stays reviewable
    }

    // ---- what must not split ------------------------------------------------------------------

    [Fact]
    public void Price_like_lists_do_not_split_however_many_lines_there_are()
    {
        var lines = Enumerable.Range(1, 12).Select(i => $"{i} Item{i} {i + 20} gp");
        var draft = TableTextParser.Parse("d20 Shop\n" + string.Join("\n", lines));

        Assert.Equal(12, draft.ResultSets[0].Entries.Count);
        Assert.Equal("1 Item1 21 gp", Rows(draft)[0]);
    }

    [Fact]
    public void A_short_block_of_flattened_lines_is_not_enough_evidence()
    {
        var draft = TableTextParser.Parse("d100 Names\n01-25 Ash 51-75 Kel\n26-50 Bar 76-00 Lor\n");   // only two lines

        Assert.Equal(["01-25 Ash 51-75 Kel", "26-50 Bar 76-00 Lor"], Rows(draft));
        Assert.Equal(2, draft.Issues.Count(i => i.Code == ParseIssueCode.MultipleRangesOnLine));
    }

    [Theory]
    [InlineData("left column skips a range", "01-02 A 51-52 W\n03-04 B 53-54 X\n07-08 C 55-56 Y\n09-10 D 57-58 Z")]
    [InlineData("right column skips a range", "01-02 A 51-52 W\n03-04 B 53-54 X\n05-06 C 57-58 Y\n07-08 D 59-60 Z")]
    [InlineData("right does not continue where the left ends", "01-02 A 61-62 W\n03-04 B 63-64 X\n05-06 C 65-66 Y\n07-08 D 67-68 Z")]
    [InlineData("right starts inside the left", "01-02 A 03-04 W\n05-06 B 07-08 X\n09-10 C 11-12 Y\n13-14 D 15-16 Z")]
    public void Blocks_whose_sequences_are_not_coherent_are_left_alone_and_flagged(string why, string block)
    {
        var draft = TableTextParser.Parse("d100 Names\n" + block);

        Assert.Equal(4, draft.ResultSets[0].Entries.Count);   // one row per line, nothing split
        Assert.Contains(draft.Issues, i => i.Code == ParseIssueCode.MultipleRangesOnLine);
        Assert.DoesNotContain(draft.Issues, i => i.Code == ParseIssueCode.SideBySideSplit);
        _ = why;
    }

    [Fact]
    public void A_block_that_does_not_fit_the_dice_is_left_alone()
    {
        var draft = TableTextParser.Parse("d6 Small\n1-2 A 7-8 W\n3-4 B 9-10 X\n5-6 C 11-12 Y\n7-8 D 13-14 Z");

        Assert.Equal(4, draft.ResultSets[0].Entries.Count);
        Assert.DoesNotContain(draft.Issues, i => i.Code == ParseIssueCode.SideBySideSplit);
    }

    [Fact]
    public void Single_values_are_never_split_without_a_column_gap()
    {
        var draft = TableTextParser.Parse("d6 Names\n1 Ash 4 Kel\n2 Bar 5 Lor\n3 Cor 6 Mor\n4 Dun 7 Nim");

        Assert.Equal(4, draft.ResultSets[0].Entries.Count);
        Assert.Equal("1 Ash 4 Kel", Rows(draft)[0]);
    }

    [Fact]
    public void Text_that_contains_a_span_of_its_own_is_not_mistaken_for_a_second_column()
    {
        var draft = TableTextParser.Parse("d100 Encounters\n01-10 Wolves 2-4 in a pack 11-20 x\n21-30 Bears 1-2 grizzled\n31-40 Bats 3-6 hanging\n41-50 Owls 1-3 watching");

        Assert.Equal(4, draft.ResultSets[0].Entries.Count);
    }

    [Fact]
    public void Pasting_rows_recovers_flattened_columns_the_same_way()
    {
        var block = Syllables().Split("\n\n")[1];
        var result = TableTextParser.ParseRows(block, DiceExpression.Parse("d100"));

        Assert.Equal(50, result.Entries.Count);
        Assert.Equal("Wulf", result.Entries[25].Text);
    }

    // ---- headings (Part D) ----------------------------------------------------------------------

    [Theory]
    [InlineData("D100 SYLLABLE D100 SYLLABLE", "Syllable")]
    [InlineData("d100 Syllable d100 Syllable", "Syllable")]
    [InlineData("D6 GIVEN NAME D6 GIVEN NAME", "Given Name")]
    [InlineData("D100 SYLLABLE D100 SYLLABLE D100 SYLLABLE", "Syllable")]
    public void A_heading_repeated_once_per_column_is_read_as_one_heading(string heading, string name)
    {
        var draft = TableTextParser.Parse(heading + "\n1-50 Ael\n51-100 Wulf");

        Assert.Equal(name, draft.TableName);
        Assert.Equal(heading.StartsWith("D6") ? "d6" : "d100", draft.DiceText);
        Assert.Equal(ParseIssueCode.HeadingRepeated, Assert.Single(draft.Issues, i => i.Target == ParseIssueTarget.TableName).Code);
    }

    [Theory]
    [InlineData("D100 GIVEN NAME D100 SURNAME")]
    [InlineData("D100 SYLLABLE D20 SYLLABLE")]
    [InlineData("D100 ROLL D100")]
    public void A_second_dice_expression_that_does_not_simply_repeat_is_left_alone_and_flagged(string heading)
    {
        var draft = TableTextParser.Parse(heading + "\n1-50 Ael\n51-100 Wulf");

        var issue = Assert.Single(draft.Issues, i => i.Target == ParseIssueTarget.TableName);
        Assert.Equal((ParseIssueCode.AmbiguousHeading, ParseIssueSeverity.Warning), (issue.Code, issue.Severity));
        Assert.Equal("d100", draft.DiceText);
        Assert.Contains("D", draft.TableName);                        // nothing was cut away
    }

    [Fact]
    public void Ordinary_headings_are_unchanged()
    {
        foreach (var (heading, name, dice) in new[] { ("D10 RANDOM STARTING GEAR", "Random Starting Gear", "d10"), ("d20 Room Features", "Room Features", "d20"), ("2d6 Reaction", "Reaction", "2d6") })
        {
            var draft = TableTextParser.Parse(heading + "\n2-12 x");
            Assert.Equal((name, dice), (draft.TableName, draft.DiceText));
            Assert.DoesNotContain(draft.Issues, i => i.Target == ParseIssueTarget.TableName && i.Code is ParseIssueCode.HeadingRepeated or ParseIssueCode.AmbiguousHeading);
        }
    }
}
