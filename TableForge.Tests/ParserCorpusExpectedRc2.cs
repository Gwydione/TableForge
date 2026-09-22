namespace TableForge.Tests;

internal static partial class ParserCorpusExpected
{
    /// <summary>Frozen interpretations for <see cref="ParserCorpus.Rc2Cases"/>, reviewed by eye when they were added (RC2).</summary>
    public static readonly IReadOnlyDictionary<string, string> Rc2Snapshots = new Dictionary<string, string>
    {
        ["rc2-standalone-number-paragraphs"] = """
TABLE Why Do You Go On? | d12
SET (unnamed)
  1 | You are terrified of reality tearing itself apart as the result of someone casting the wrong spell. You seek for a permanent solution to the danger of sorcery, to safely cast magic without risking madness or demonic mutation.
  2 | In a brutal world where fresh water is worth far more than blood, your goal is to find an unpolluted, newly emerged island with a natural spring where you can settle down and never worry about dehydration again.
ISSUE Info ParagraphsAttached set0/row0 line5
""",
        ["rc2-standalone-range-second-paragraph"] = """
TABLE Weather | d8
SET (unnamed)
  1-3 | Fair skies.

Broken Shores 12
  4-8 | Grey.
ISSUE Info ParagraphsAttached set0/row0 line5
ISSUE Warning MultipleParagraphs set0/row0 line7
""",
        ["rc2-standalone-number-line-kept-as-text"] = """
TABLE Reasons | d12
SET (unnamed)
  1 | You will wait 10 days for the rain to stop.
  2 | The next reason.
ISSUE Info ParagraphsAttached set0/row0 line4
ISSUE Warning NumberedLineKeptAsText set0/row0 line5
""",
        ["rc2-standalone-stray-prose-first"] = """
TABLE Loot | d6
SET (unnamed)
  1 | First.
  2 | Second.
ISSUE Warning UnrecognizedLine line3
ISSUE Info ParagraphsAttached set0/row0 line7
""",
        ["rc2-continuation-columns-single-space"] = """
TABLE Syllable | d100
SET (unnamed)
  01-02 | Ael
  03-04 | Stan
  05-06 | Cia
  07-08 | Cor
  09-10 | Maer
  11-12 | Dun
  13-14 | Fen
  15-16 | Gar
  17-18 | Hal
  19-20 | Ivo
  21-22 | Jor
  23-24 | Kai
  25-26 | Lir
  27-28 | Mor
  29-30 | Nen
  31-32 | Oth
  33-34 | Pel
  35-36 | Quin
  37-38 | Rin
  39-40 | Sav
  41-42 | Tor
  43-44 | Ulf
  45-46 | Vex
  47-48 | Wyn
  49-50 | Lynn
  51-52 | Wulf
  53-54 | Mal
  55-56 | Dred
  57-58 | Ryk
  59-60 | Val
  61-62 | Bram
  63-64 | Cael
  65-66 | Dorn
  67-68 | Eld
  69-70 | Fyr
  71-72 | Gorm
  73-74 | Hest
  75-76 | Iss
  77-78 | Jax
  79-80 | Krag
  81-82 | Lorn
  83-84 | Mirk
  85-86 | Norr
  87-88 | Orm
  89-90 | Pike
  91-92 | Quor
  93-94 | Rusk
  95-96 | Sten
  97-98 | Thal
  99-100 | Shel
ISSUE Info HeadingRepeated line1
ISSUE Info SideBySideSplit set0/row0 line3
""",
        ["rc2-continuation-columns-with-gaps"] = """
TABLE Syllable | d100
SET (unnamed)
  01-02 | Ael
  03-04 | Stan
  05-06 | Cia
  07-08 | Cor
  09-10 | Maer
  11-12 | Dun
  13-14 | Fen
  15-16 | Gar
  17-18 | Hal
  19-20 | Ivo
  21-22 | Jor
  23-24 | Kai
  25-26 | Lir
  27-28 | Mor
  29-30 | Nen
  31-32 | Oth
  33-34 | Pel
  35-36 | Quin
  37-38 | Rin
  39-40 | Sav
  41-42 | Tor
  43-44 | Ulf
  45-46 | Vex
  47-48 | Wyn
  49-50 | Lynn
  51-52 | Wulf
  53-54 | Mal
  55-56 | Dred
  57-58 | Ryk
  59-60 | Val
  61-62 | Bram
  63-64 | Cael
  65-66 | Dorn
  67-68 | Eld
  69-70 | Fyr
  71-72 | Gorm
  73-74 | Hest
  75-76 | Iss
  77-78 | Jax
  79-80 | Krag
  81-82 | Lorn
  83-84 | Mirk
  85-86 | Norr
  87-88 | Orm
  89-90 | Pike
  91-92 | Quor
  93-94 | Rusk
  95-96 | Sten
  97-98 | Thal
  99-100 | Shel
ISSUE Info HeadingRepeated line1
ISSUE Info SideBySideSplit set0/row0 line3
""",
        ["rc2-continuation-two-lines-not-split"] = """
TABLE Names | d100
SET (unnamed)
  01-25 | Ash 51-75 Kel
  26-50 | Bar 76-00 Lor
ISSUE Warning MultipleRangesOnLine set0/row0 line2
ISSUE Warning MultipleRangesOnLine set0/row1 line3
""",
        ["rc2-continuation-incoherent-not-split"] = """
TABLE Names | d100
SET (unnamed)
  01-02 | A 51-52 W
  03-04 | B 53-54 X
  07-08 | C 55-56 Y
  09-10 | D 57-58 Z
ISSUE Warning MultipleRangesOnLine set0/row0 line2
ISSUE Warning MultipleRangesOnLine set0/row1 line3
ISSUE Warning MultipleRangesOnLine set0/row2 line4
ISSUE Warning MultipleRangesOnLine set0/row3 line5
""",
        ["rc2-price-like-block-not-split"] = """
TABLE Shop | d20
SET (unnamed)
  1 | Item1 21 gp
  2 | Item2 22 gp
  3 | Item3 23 gp
  4 | Item4 24 gp
  5 | Item5 25 gp
""",
        ["rc2-parallel-outputs-difficulty"] = """
TABLE Difficulty Modifier | d8
SET Difficulty
  1 | Child's play
  2 | Effortless
  3 | Easy
  4-5 | Normal
  6 | Demanding
  7 | Hard
  8 | Impossible
SET Modifier
  1 | +30
  2 | +20
  3 | +10
  4-5 | +0
  6 | -10
  7 | -20
  8 | -30
ISSUE Info ParallelOutputsSplit
""",
        ["rc2-parallel-outputs-column-gaps"] = """
TABLE Name Type | d6
SET Name
  1 | Ash
  2 | Bar
  3-4 | Cor
  5-6 | Dun
SET Type
  1 | Elf
  2 | Orc
  3-4 | Elf
  5-6 | Dwarf
ISSUE Info ParallelOutputsSplit
""",
        ["rc2-parallel-not-applied-to-ordinary-title"] = """
TABLE Random Encounter | d6
SET (unnamed)
  1 | Goblin ambush
  2 | Wolves in the woods
  3-6 | A quiet road
""",
        ["rc2-heading-repeated-columns"] = """
TABLE Syllable | d100
SET (unnamed)
  1-50 | Ael
  51-100 | Wulf
ISSUE Info HeadingRepeated line1
""",
        ["rc2-heading-second-dice-ambiguous"] = """
TABLE Given Name D100 Surname | d100
SET (unnamed)
  1-50 | Ael
  51-100 | Wulf
ISSUE Warning AmbiguousHeading line1
""",
        ["rc2-row-only-standalone"] = """
ROWS
  1 | First paragraph wrapped.
  2 | Second.
ISSUE Info ParagraphsAttached set0/row0 line3
""",
        ["rc2-row-only-flattened-columns"] = """
ROWS
  01-02 | Ael
  03-04 | Stan
  05-06 | Cia
  07-08 | Cor
  09-10 | Maer
  11-12 | Dun
  13-14 | Fen
  15-16 | Gar
  17-18 | Hal
  19-20 | Ivo
  21-22 | Jor
  23-24 | Kai
  25-26 | Lir
  27-28 | Mor
  29-30 | Nen
  31-32 | Oth
  33-34 | Pel
  35-36 | Quin
  37-38 | Rin
  39-40 | Sav
  41-42 | Tor
  43-44 | Ulf
  45-46 | Vex
  47-48 | Wyn
  49-50 | Lynn
  51-52 | Wulf
  53-54 | Mal
  55-56 | Dred
  57-58 | Ryk
  59-60 | Val
  61-62 | Bram
  63-64 | Cael
  65-66 | Dorn
  67-68 | Eld
  69-70 | Fyr
  71-72 | Gorm
  73-74 | Hest
  75-76 | Iss
  77-78 | Jax
  79-80 | Krag
  81-82 | Lorn
  83-84 | Mirk
  85-86 | Norr
  87-88 | Orm
  89-90 | Pike
  91-92 | Quor
  93-94 | Rusk
  95-96 | Sten
  97-98 | Thal
  99-100 | Shel
ISSUE Info SideBySideSplit set0/row0 line1
""",
    };

    /// <summary>Every frozen snapshot, RC1 and RC2.</summary>
    public static IReadOnlyDictionary<string, string> All { get; } = Snapshots.Concat(Rc2Snapshots).ToDictionary(p => p.Key, p => p.Value);
}
