namespace TableForge.Tests;

/// <summary>Runs every case in <see cref="ParserCorpus"/> (RC1 and RC2) and compares it with its frozen interpretation.</summary>
public class ParserCorpusTests
{
    private static IEnumerable<ParserCorpus.Case> AllCases => ParserCorpus.Cases.Concat(ParserCorpus.Rc2Cases);

    public static IEnumerable<object[]> CaseNames => AllCases.Select(c => new object[] { c.Name });

    private static string Normalize(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Corpus_case_still_produces_its_frozen_interpretation(string name)
    {
        var c = AllCases.Single(x => x.Name == name);

        Assert.Equal(Normalize(ParserCorpusExpected.All[name]), Normalize(ParserCorpus.Render(c)));
    }

    [Fact]
    public void Every_corpus_case_has_a_frozen_snapshot_and_nothing_is_orphaned()
    {
        Assert.Equal(
            AllCases.Select(c => c.Name).OrderBy(n => n),
            ParserCorpusExpected.All.Keys.OrderBy(n => n));
        Assert.Equal(AllCases.Count(), AllCases.Select(c => c.Name).Distinct().Count());
    }

    [Fact]
    public void Corpus_covers_the_representative_situations_it_promises()
    {
        var names = AllCases.Select(c => c.Name).ToHashSet();
        foreach (var required in new[]
        {
            "random-starting-gear", "room-features-multi-set", "wrapped-continuation", "side-by-side-ranged-block",
            "ambiguous-single-space-columns", "price-like-lone-line", "price-like-two-lines",
            "single-value-columns-two-lines", "single-value-columns-three-lines", "row-only-paste",
            "heading-title-case-unseparated", "heading-uppercase-no-restart", "heading-with-no-rows",
            // RC2: the three real Broken Shores failures and their guard rails
            "rc2-standalone-number-paragraphs", "rc2-continuation-columns-single-space", "rc2-parallel-outputs-difficulty",
            "rc2-price-like-block-not-split", "rc2-parallel-not-applied-to-ordinary-title", "rc2-heading-repeated-columns",
        })
            Assert.Contains(required, names);
    }
}
