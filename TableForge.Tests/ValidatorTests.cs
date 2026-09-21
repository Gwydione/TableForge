using TableForge.Domain;

namespace TableForge.Tests;

public class ValidatorTests
{
    private static IReadOnlyList<ValidationFinding> Validate(string dice, params (int Min, int Max, string Text)[] entries) =>
        TableValidator.Validate(Fixtures.Table(DiceExpression.Parse(dice), entries));

    [Fact]
    public void Full_coverage_has_no_findings()
    {
        Assert.Empty(TableValidator.Validate(Fixtures.ParseAndBuild(Fixtures.RandomStartingGear)));
        Assert.Empty(Validate("d6", (1, 2, "a"), (3, 6, "b")));
    }

    [Fact]
    public void Entries_given_out_of_order_are_still_full_coverage()
    {
        Assert.Empty(Validate("d6", (4, 6, "b"), (1, 3, "a")));
    }

    [Fact]
    public void Gap_in_the_middle_is_reported_with_its_bounds()
    {
        var f = Assert.Single(Validate("d10", (1, 3, "a"), (7, 10, "b")));
        Assert.Equal((ValidationKind.Gap, 4, 6), (f.Kind, f.Start, f.End));
        Assert.Empty(f.EntryIndexes);
    }

    [Fact]
    public void Uncovered_start_and_end_are_gaps()
    {
        var findings = Validate("d10", (3, 8, "a"));
        Assert.Equal(
            [(ValidationKind.Gap, 1, 2), (ValidationKind.Gap, 9, 10)],
            findings.Select(f => (f.Kind, f.Start, f.End)).ToArray());
    }

    [Fact]
    public void Empty_result_set_is_one_gap_over_the_whole_range()
    {
        var f = Assert.Single(Validate("d6"));
        Assert.Equal((ValidationKind.Gap, 1, 6), (f.Kind, f.Start, f.End));
    }

    [Fact]
    public void Overlap_is_reported_with_both_entries()
    {
        var f = Assert.Single(Validate("d10", (1, 5, "a"), (5, 10, "b")));
        Assert.Equal((ValidationKind.Overlap, 5, 5), (f.Kind, f.Start, f.End));
        Assert.Equal([0, 1], f.EntryIndexes);
    }

    [Fact]
    public void Overlap_across_a_wider_span_reports_the_shared_values()
    {
        var f = Assert.Single(Validate("d10", (1, 6, "a"), (4, 10, "b")));
        Assert.Equal((ValidationKind.Overlap, 4, 6), (f.Kind, f.Start, f.End));
    }

    [Fact]
    public void Lower_out_of_range_is_reported()
    {
        var findings = Validate("d10", (0, 5, "a"), (6, 10, "b"));
        var f = Assert.Single(findings);
        Assert.Equal((ValidationKind.BelowMinimum, 0, 0), (f.Kind, f.Start, f.End));
        Assert.Equal([0], f.EntryIndexes);
    }

    [Fact]
    public void Upper_out_of_range_is_reported()
    {
        var findings = Validate("d10", (1, 5, "a"), (6, 12, "b"));
        var f = Assert.Single(findings);
        Assert.Equal((ValidationKind.AboveMaximum, 11, 12), (f.Kind, f.Start, f.End));
        Assert.Equal([1], f.EntryIndexes);
    }

    [Fact]
    public void Entry_entirely_out_of_range_is_reported_and_leaves_its_gap()
    {
        var findings = Validate("d6", (1, 3, "a"), (10, 12, "b"));
        Assert.Contains(findings, f => f.Kind == ValidationKind.AboveMaximum && (f.Start, f.End) == (10, 12));
        Assert.Contains(findings, f => f.Kind == ValidationKind.Gap && (f.Start, f.End) == (4, 6));
    }

    [Fact]
    public void Two_d6_validates_against_2_to_12_not_1_to_12()
    {
        // Correct 2d6 coverage: no findings, in particular no gap at 1.
        Assert.Empty(Validate("2d6", (2, 6, "low"), (7, 12, "high")));

        // Starting at 1 is below the legal minimum...
        var findings = Validate("2d6", (1, 6, "low"), (7, 12, "high"));
        var f = Assert.Single(findings);
        Assert.Equal((ValidationKind.BelowMinimum, 1, 1), (f.Kind, f.Start, f.End));

        // ...and stopping at 11 leaves 12 uncovered.
        var short12 = Assert.Single(Validate("2d6", (2, 11, "all")));
        Assert.Equal((ValidationKind.Gap, 12, 12), (short12.Kind, short12.Start, short12.End));
    }

    [Fact]
    public void Each_result_set_is_validated_independently()
    {
        var table = new RollableTable
        {
            Dice = DiceExpression.Parse("d6"),
            ResultSets =
            [
                new ResultSet { Entries = [new() { Min = 1, Max = 6, Text = "ok" }] },
                new ResultSet { Entries = [new() { Min = 1, Max = 3, Text = "half" }] },
            ],
        };

        var f = Assert.Single(TableValidator.Validate(table));
        Assert.Equal((1, ValidationKind.Gap, 4, 6), (f.ResultSetIndex, f.Kind, f.Start, f.End));
    }

    [Fact]
    public void D100_table_with_00_as_100_is_fully_covered()
    {
        Assert.Empty(TableValidator.Validate(Fixtures.ParseAndBuild("d100 Treasure\n01-50 Copper\n51-95 Silver\n96-00 Gold")));
    }
}
