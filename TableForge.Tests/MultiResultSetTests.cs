using TableForge.Domain;

namespace TableForge.Tests;

/// <summary>One roll, resolved and validated independently against every result set.</summary>
public class MultiResultSetTests
{
    private static string[] Outputs(TableResolution r) => r.Results.Select(x => x.Entry?.Text ?? $"<{x.Status}>").ToArray();

    // ---- resolver ---------------------------------------------------------------------------

    [Fact]
    public void Roll_68_resolves_independently_in_all_three_result_sets()
    {
        var resolution = TableResolver.Resolve(Fixtures.RoomFeatures(), 68);

        Assert.Equal(68, resolution.Roll);
        Assert.Equal(["Ambient", "Noise", "General Feature"], resolution.Results.Select(r => r.ResultSet.Name).ToArray());
        Assert.Equal(
            ["Smell of burning flesh", "Hissing", "Grated floors reveal dozens of people below"],
            Outputs(resolution));
        Assert.All(resolution.Results, r => Assert.Equal(ResolutionStatus.Matched, r.Status));
    }

    [Theory]
    [InlineData(1, "Cold stale air", "Silence", "Cracked stone walls")]
    [InlineData(30, "Cold stale air", "Distant scratching", "Cracked stone walls")]
    [InlineData(31, "Damp stone", "Distant scratching", "Cracked stone walls")]
    [InlineData(66, "Smell of burning flesh", "Dripping water", "Broken furniture")]
    [InlineData(67, "Smell of burning flesh", "Hissing", "Broken furniture")]
    [InlineData(71, "Heavy incense", "Hissing", "Carved pillars")]
    [InlineData(72, "Heavy incense", "Low chanting", "Carved pillars")]
    [InlineData(100, "Heavy incense", "Low chanting", "Carved pillars")] // "00"
    public void Set_boundaries_do_not_line_up_and_each_set_uses_its_own(int roll, string ambient, string noise, string general)
    {
        Assert.Equal([ambient, noise, general], Outputs(TableResolver.Resolve(Fixtures.RoomFeatures(), roll)));
    }

    [Fact]
    public void No_match_in_one_set_does_not_stop_the_others()
    {
        var table = Fixtures.RoomFeatures();
        table.ResultSets[1].Entries.RemoveAll(e => e.Text == "Hissing"); // hole at 67-71 in Noise only

        var resolution = TableResolver.Resolve(table, 68);

        Assert.Equal(["Smell of burning flesh", "<NoMatch>", "Grated floors reveal dozens of people below"], Outputs(resolution));
        Assert.Empty(resolution.Results[1].Matches);
    }

    [Fact]
    public void Ambiguity_in_one_set_does_not_alter_the_others()
    {
        var table = Fixtures.RoomFeatures();
        table.ResultSets[0].Entries.Add(new TableEntry { Min = 60, Max = 68, Text = "Rotting damp" }); // overlaps 66-70 in Ambient only

        var resolution = TableResolver.Resolve(table, 68);

        var ambient = resolution.Results[0];
        Assert.Equal(ResolutionStatus.Ambiguous, ambient.Status);
        Assert.Null(ambient.Entry);
        Assert.Equal(["Smell of burning flesh", "Rotting damp"], ambient.Matches.Select(m => m.Text).ToArray());
        Assert.Equal("Hissing", resolution.Results[1].Entry!.Text);
        Assert.Equal("Grated floors reveal dozens of people below", resolution.Results[2].Entry!.Text);
    }

    [Fact]
    public void Entries_with_the_same_text_in_different_sets_are_separate_rows()
    {
        var table = Fixtures.RoomFeatures();
        Assert.Empty(table.ResultSets[0].Entries.Intersect(table.ResultSets[1].Entries));
        Assert.Empty(table.ResultSets[1].Entries.Intersect(table.ResultSets[2].Entries));
    }

    // ---- validation -------------------------------------------------------------------------

    [Fact]
    public void Representative_table_is_fully_covered_in_every_set()
    {
        Assert.Empty(TableValidator.Validate(Fixtures.RoomFeatures()));
    }

    [Fact]
    public void A_gap_in_one_set_is_reported_only_for_that_set()
    {
        var table = Fixtures.RoomFeatures();
        table.ResultSets[1].Entries.RemoveAll(e => e.Text == "Hissing"); // Noise: 67-71 uncovered

        var f = Assert.Single(TableValidator.Validate(table));

        Assert.Equal((1, ValidationKind.Gap, 67, 71), (f.ResultSetIndex, f.Kind, f.Start, f.End));
    }

    [Fact]
    public void An_overlap_in_one_set_is_reported_only_for_that_set()
    {
        var table = Fixtures.RoomFeatures();
        table.ResultSets[2].Entries[1].Max = 70; // General Feature: 41-70 overlaps 68-69 and 70-100

        var findings = TableValidator.Validate(table);

        Assert.All(findings, f => Assert.Equal(2, f.ResultSetIndex));
        Assert.All(findings, f => Assert.Equal(ValidationKind.Overlap, f.Kind));
        Assert.Equal([(68, 69), (70, 70)], findings.Select(f => (f.Start, f.End)).ToArray());
    }

    [Fact]
    public void Complete_coverage_in_one_set_does_not_mask_problems_in_others()
    {
        var table = Fixtures.RoomFeatures();
        table.ResultSets[0].Entries[3].Max = 120;       // Ambient: above maximum
        table.ResultSets[2].Entries[0].Min = 0;         // General Feature: below minimum

        var findings = TableValidator.Validate(table);

        Assert.Equal(
            [(0, ValidationKind.AboveMaximum), (2, ValidationKind.BelowMinimum)],
            findings.OrderBy(f => f.ResultSetIndex).Select(f => (f.ResultSetIndex, f.Kind)).ToArray());
        Assert.DoesNotContain(findings, f => f.ResultSetIndex == 1); // Noise is untouched and clean
    }

    [Fact]
    public void Each_set_can_have_its_own_different_problem_at_once()
    {
        var table = Fixtures.RoomFeatures();
        table.ResultSets[0].Entries.RemoveAt(0);                    // gap 1-30
        table.ResultSets[1].Entries[1].Min = 20;                    // overlap with 01-25
        table.ResultSets[2].Entries[3].Max = 99;                    // gap at 100

        var byKind = TableValidator.Validate(table).Select(f => (f.ResultSetIndex, f.Kind, f.Start, f.End)).ToArray();

        Assert.Equal(
            [(0, ValidationKind.Gap, 1, 30), (1, ValidationKind.Overlap, 20, 25), (2, ValidationKind.Gap, 100, 100)],
            byKind.OrderBy(x => x.ResultSetIndex).ToArray());
    }
}
