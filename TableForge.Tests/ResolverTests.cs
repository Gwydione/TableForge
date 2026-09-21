using TableForge.Domain;

namespace TableForge.Tests;

public class ResolverTests
{
    private static readonly RollableTable Gear = Fixtures.ParseAndBuild(Fixtures.RandomStartingGear);

    [Theory]
    [InlineData(1, "Backpack")]
    [InlineData(2, "Backpack")]
    [InlineData(3, "Knife")]
    [InlineData(4, "1x Torch (UD6)")]
    [InlineData(8, "D4 Bandages")]
    [InlineData(9, "D20 Construction Supplies")]
    [InlineData(10, "D20 Construction Supplies")]
    public void Boundary_values_resolve_to_the_expected_entry(int roll, string expected)
    {
        var resolution = TableResolver.Resolve(Gear, roll);

        Assert.Equal(roll, resolution.Roll);
        var result = Assert.Single(resolution.Results);
        Assert.Equal(ResolutionStatus.Matched, result.Status);
        Assert.Equal(expected, result.Entry!.Text);
    }

    [Fact]
    public void Uncovered_value_returns_explicit_no_match()
    {
        var incomplete = Fixtures.Table(DiceExpression.Parse("d10"), (1, 3, "Low"), (7, 10, "High"));

        var result = Assert.Single(TableResolver.Resolve(incomplete, 5).Results);

        Assert.Equal(ResolutionStatus.NoMatch, result.Status);
        Assert.Null(result.Entry);
        Assert.Empty(result.Matches);
    }

    [Fact]
    public void Value_outside_every_range_does_not_throw()
    {
        Assert.Equal(ResolutionStatus.NoMatch, TableResolver.Resolve(Gear, 0).Results[0].Status);
        Assert.Equal(ResolutionStatus.NoMatch, TableResolver.Resolve(Gear, 11).Results[0].Status);
        Assert.Equal(ResolutionStatus.NoMatch, TableResolver.Resolve(Gear, -5).Results[0].Status);
    }

    [Fact]
    public void Overlapping_entries_are_ambiguous_not_guessed()
    {
        var overlapping = Fixtures.Table(DiceExpression.Parse("d6"), (1, 4, "A"), (3, 6, "B"));

        var result = Assert.Single(TableResolver.Resolve(overlapping, 3).Results);

        Assert.Equal(ResolutionStatus.Ambiguous, result.Status);
        Assert.Null(result.Entry);
        Assert.Equal(["A", "B"], result.Matches.Select(m => m.Text).ToArray());
    }

    [Fact]
    public void One_roll_resolves_independently_against_every_result_set()
    {
        var table = new RollableTable
        {
            Dice = DiceExpression.Parse("d6"),
            ResultSets =
            [
                new ResultSet { Name = "Day", Entries = [new() { Min = 1, Max = 3, Text = "Sun" }, new() { Min = 4, Max = 6, Text = "Cloud" }] },
                new ResultSet { Name = "Night", Entries = [new() { Min = 1, Max = 5, Text = "Moon" }] },
            ],
        };

        var results = TableResolver.Resolve(table, 6).Results;

        Assert.Equal("Cloud", results[0].Entry!.Text);
        Assert.Equal(ResolutionStatus.NoMatch, results[1].Status);
        Assert.Equal("Day", results[0].ResultSet.Name);
    }

    [Fact]
    public void Resolution_uses_numbers_not_display_text()
    {
        var table = Fixtures.ParseAndBuild("d100 Treasure\n01-50 Copper\n51-95 Silver\n96-00 Gold");

        Assert.Equal("Gold", TableResolver.Resolve(table, 100).Results[0].Entry!.Text);
        Assert.Equal("Gold", TableResolver.Resolve(table, 96).Results[0].Entry!.Text);
        Assert.Equal("Copper", TableResolver.Resolve(table, 1).Results[0].Entry!.Text);
    }
}
