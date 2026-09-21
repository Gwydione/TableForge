namespace TableForge.Tests;

/// <summary>Runs every case in <see cref="ParserCorpus"/> and compares it with its frozen interpretation.</summary>
public class ParserCorpusTests
{
    public static IEnumerable<object[]> CaseNames => ParserCorpus.Cases.Select(c => new object[] { c.Name });

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Corpus_case_still_produces_its_frozen_interpretation(string name)
    {
        var c = ParserCorpus.Cases.Single(x => x.Name == name);

        Assert.Equal(Normalize(ParserCorpusExpected.Snapshots[name]), Normalize(ParserCorpus.Render(c)));
    }

    [Fact]
    public void Every_corpus_case_has_a_frozen_snapshot_and_nothing_is_orphaned()
    {
        Assert.Equal(
            ParserCorpus.Cases.Select(c => c.Name).OrderBy(n => n),
            ParserCorpusExpected.Snapshots.Keys.OrderBy(n => n));
    }

    [Fact]
    public void Corpus_covers_the_representative_situations_it_promises()
    {
        var names = ParserCorpus.Cases.Select(c => c.Name).ToHashSet();
        foreach (var required in new[]
        {
            "random-starting-gear", "room-features-multi-set", "wrapped-continuation", "side-by-side-ranged-block",
            "ambiguous-single-space-columns", "price-like-lone-line", "price-like-two-lines",
            "single-value-columns-two-lines", "single-value-columns-three-lines", "row-only-paste",
            "heading-title-case-unseparated", "heading-uppercase-no-restart", "heading-with-no-rows",
        })
            Assert.Contains(required, names);
    }
}
