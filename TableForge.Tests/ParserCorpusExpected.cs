namespace TableForge.Tests;

internal static partial class ParserCorpusExpected
{
    /// <summary>Frozen interpretations for <see cref="ParserCorpus.Cases"/>, reviewed by eye when the corpus was created (V1 RC1).</summary>
    public static readonly IReadOnlyDictionary<string, string> Snapshots = new Dictionary<string, string>
    {
        ["random-starting-gear"] = """
TABLE Random Starting Gear | d10
SET (unnamed)
  1-2 | Backpack
  3 | Knife
  4 | 1x Torch (UD6)
  5 | Fishing rod
  6 | Rope (15 m)
  7 | Tinderbox
  8 | D4 Bandages
  9-10 | D20 Construction Supplies
ISSUE Warning ContinuationJoined set0/row7 line10
""",
        ["room-features-multi-set"] = """
TABLE Room Features | d100
SET Ambient
  01-30 | Cold stale air
  31-65 | Damp stone
  66-70 | Smell of burning flesh
  71-00 | Heavy incense
SET Noise
  01-25 | Silence
  26-50 | Distant scratching
  51-66 | Dripping water
  67-71 | Hissing
  72-00 | Low chanting
SET General Feature
  01-40 | Cracked stone walls
  41-67 | Broken furniture
  68-69 | Grated floors reveal dozens of people below
  70-00 | Carved pillars
""",
        ["wrapped-continuation"] = """
TABLE Loot | d6
SET (unnamed)
  1 | A very long wrapped row
  2 | Gem
  3-6 | Rope (15 m)
ISSUE Warning ContinuationJoined set0/row0 line3
ISSUE Warning ContinuationJoined set0/row0 line4
""",
        ["side-by-side-ranged-block"] = """
TABLE Names | d100
SET (unnamed)
  01-05 | Ash
  06-10 | Bar
  11-15 | Cor
  51-55 | Kel
  56-60 | Lor
  61-65 | Mor
ISSUE Info SideBySideSplit set0/row0 line2
""",
        ["side-by-side-ranged-lone-line"] = """
TABLE Names | d100
SET (unnamed)
  01-50 | Ash
  51-00 | Kel
ISSUE Info SideBySideSplit set0/row0 line2
""",
        ["ambiguous-single-space-columns"] = """
TABLE Names | d100
SET (unnamed)
  01-05 | Ash 51-55 Kel
ISSUE Warning MultipleRangesOnLine set0/row0 line2
""",
        ["ambiguous-parallel-columns"] = """
TABLE Both | d100
SET (unnamed)
  01-30 | Cold air 01-25 Silence
ISSUE Warning MultipleRangesOnLine set0/row0 line2
""",
        ["price-like-lone-line"] = """
TABLE Shop | d20
SET (unnamed)
  4 | Rope 10 gp
ISSUE Warning MultipleRangesOnLine set0/row0 line2
""",
        ["price-like-two-lines"] = """
TABLE Shop | d20
SET (unnamed)
  4 | Rope 10 gp
  5 | Lamp 11 gp
ISSUE Warning MultipleRangesOnLine set0/row0 line2
ISSUE Warning MultipleRangesOnLine set0/row1 line3
""",
        ["single-value-columns-two-lines"] = """
TABLE Names | d6
SET (unnamed)
  1 | Ash 4 Kel
  2 | Bar 5 Lor
ISSUE Warning MultipleRangesOnLine set0/row0 line2
ISSUE Warning MultipleRangesOnLine set0/row1 line3
""",
        ["single-value-columns-three-lines"] = """
TABLE Names | d6
SET (unnamed)
  1 | Ash
  2 | Bar
  3 | Cor
  4 | Kel
  5 | Lor
  6 | Mor
ISSUE Info SideBySideSplit set0/row0 line2
""",
        ["heading-title-case-unseparated"] = """
TABLE Weather | d6
SET Day
  1-3 | Sun
  4-6 | Cloud
SET Night
  1-6 | Moon
ISSUE Warning ProbableResultSetHeading set1 line5
""",
        ["heading-uppercase-no-restart"] = """
TABLE Loot | d6
SET (unnamed)
  1-3 | Coin
  4-6 | Gem
ISSUE Warning AmbiguousSectionBreak line4
""",
        ["heading-with-no-rows"] = """
TABLE Loot | d6
SET (unnamed)
  1-3 | Coin
  4-6 | Gem
ISSUE Warning UnrecognizedLine line5
""",
        ["rows-before-first-heading"] = """
TABLE Loot | d6
SET (unnamed)
  1-3 | Coin
  4-6 | Gem
SET Noise
  1-6 | Hiss
ISSUE Warning UnnamedResultSet set0
""",
        ["no-heading-no-dice"] = """
TABLE  | 
SET (unnamed)
  1-2 | Coin
  3-4 | Gem
ISSUE Warning NoTableName
ISSUE Error NoDiceExpression
""",
        ["row-only-paste"] = """
ROWS
  01-30 | Cold air
  31-65 | Damp stone
  66-70 | A
  71-00 | B
ISSUE Warning ContinuationJoined set0/row1 line3
ISSUE Info SideBySideSplit set0/row2 line4
""",
        ["row-only-paste-malformed"] = """
ROWS
  1-3 | Hiss stray words
ISSUE Warning UnrecognizedLine line1
ISSUE Warning UnrecognizedLine line2
ISSUE Warning ContinuationJoined set0/row0 line4
""",
    };
}
