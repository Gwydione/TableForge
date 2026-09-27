using System.Diagnostics;
using System.Windows.Controls;
using TableForge.Domain;
using TableForge.Import;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>The exact text <see cref="TableTextExporter"/> produces: name, dice, blank line, then range TAB result per line (CRLF).</summary>
public class TableTextExporterTests
{
    private static string Lines(params string[] lines) => string.Join("\r\n", lines);

    private static TableEntry E(int min, int max, string text, string? display = null) => new() { Min = min, Max = max, Text = text, DisplayRange = display };

    private static RollableTable Table(string name, string dice, params TableEntry[] entries) => new()
    {
        Name = name, Dice = DiceExpression.Parse(dice), ResultSets = [new ResultSet { Entries = [.. entries] }],
    };

    private static string Export(RollableTable table, int set = 0) => TableTextExporter.Export(table, table.ResultSets[set]);

    [Fact]
    public void The_regression_table_copies_exactly()
    {
        var gear = Fixtures.ParseAndBuild(Fixtures.RandomStartingGear);

        Assert.Equal(Lines(
            "Random Starting Gear",                                               // the name as the parser stored it
            "d10",
            "",
            "1-2\tBackpack",
            "3\tKnife",
            "4\t1x Torch (UD6)",
            "5\tFishing rod",
            "6\tRope (15 m)",
            "7\tTinderbox",
            "8\tD4 Bandages",
            "9-10\tD20 Construction Supplies"), Export(gear));
    }

    [Fact]
    public void A_simple_d6_table_with_single_values_and_spans()
    {
        var table = Table("WEATHER", "d6", E(1, 1, "Clear"), E(2, 3, "Cloudy"), E(4, 5, "Rain"), E(6, 6, "Storm"));
        Assert.Equal(Lines("WEATHER", "d6", "", "1\tClear", "2-3\tCloudy", "4-5\tRain", "6\tStorm"), Export(table));
    }

    [Fact]
    public void d100_keeps_its_written_00_and_leading_zeros()
    {
        var table = Table("OMENS", "d100", E(1, 30, "Cold", "01–30"), E(31, 98, "Mild"), E(99, 99, "Odd"), E(100, 100, "Doom", "00"));
        Assert.Equal(Lines("OMENS", "d100", "", "01-30\tCold", "31-98\tMild", "99\tOdd", "00\tDoom"), Export(table));

        var spans = Table("OMENS", "d100", E(96, 100, "Doom", "96–00"));
        Assert.Equal(Lines("OMENS", "d100", "", "96-00\tDoom"), Export(spans));
    }

    [Fact]
    public void d100_without_written_notation_shows_100_as_the_dice_read_it()
    {
        var table = Table("OMENS", "d100", E(1, 99, "Anything"), E(100, 100, "Doom"), E(91, 100, "Late"));
        Assert.Equal(Lines("OMENS", "d100", "", "1-99\tAnything", "00\tDoom", "91-00\tLate"), Export(table));
    }

    [Fact]
    public void d66_keeps_tens_and_ones_numbers()
    {
        var table = Table("NAMES", "d66", E(11, 16, "Ash"), E(21, 21, "Birch"), E(22, 66, "Cedar"));
        Assert.Equal(Lines("NAMES", "d66", "", "11-16\tAsh", "21\tBirch", "22-66\tCedar"), Export(table));
    }

    [Fact]
    public void Negative_ranges_and_modifiers_are_kept_as_TableForge_shows_them()
    {
        var table = Table("DRIFT", "d20-2", E(-1, 0, "Backwards"), E(1, 18, "Onwards"));
        Assert.Equal(Lines("DRIFT", "d20-2", "", "-1-0\tBackwards", "1-18\tOnwards"), Export(table));
    }

    [Fact]
    public void Only_the_chosen_result_set_is_copied_even_when_sets_are_misaligned()
    {
        var rooms = Fixtures.RoomFeatures();

        Assert.Equal(Lines("Room Features", "d100", "",
            "01-25\tSilence", "26-50\tDistant scratching", "51-66\tDripping water", "67-71\tHissing", "72-00\tLow chanting"), Export(rooms, 1));
        Assert.Equal(Lines("Room Features", "d100", "",
            "01-30\tCold stale air", "31-65\tDamp stone", "66-70\tSmell of burning flesh", "71-00\tHeavy incense"), Export(rooms, 0));
        Assert.DoesNotContain("Noise", Export(rooms, 1));                                        // no set names, nothing side by side
    }

    [Fact]
    public void Whitespace_is_trimmed_and_line_breaks_and_tabs_become_one_space_each()
    {
        var table = Table("  SPACED  ", "d6",
            E(1, 1, "  padded  "),
            E(2, 2, "A ruined tower\r\ncovered in strange runes."),
            E(3, 3, "line\nfeed"),
            E(4, 4, "carriage\rreturn"),
            E(5, 5, "tab\there"),
            E(6, 6, "two  spaces stay"),
            E(7, 7, "gap \nkept", " 07 "));

        Assert.Equal(Lines("SPACED", "d6", "",
            "1\tpadded",
            "2\tA ruined tower covered in strange runes.",
            "3\tline feed",
            "4\tcarriage return",
            "5\ttab here",
            "6\ttwo  spaces stay",
            "07\tgap  kept"), Export(table));
    }

    [Fact]
    public void Unicode_punctuation_commas_and_pipes_are_kept()
    {
        var table = Table("Épées & “Quotes”", "d4",
            E(1, 1, "Épée — fine, sharp; 50% off!"), E(2, 2, "A | B | C"), E(3, 3, "“Hello,” she said… ✓"), E(4, 4, "日本刀"));
        Assert.Equal(Lines("Épées & “Quotes”", "d4", "",
            "1\tÉpée — fine, sharp; 50% off!", "2\tA | B | C", "3\t“Hello,” she said… ✓", "4\t日本刀"), Export(table));
    }

    [Fact]
    public void Links_contribute_only_their_visible_text()
    {
        var table = Table("SITES", "d4",
            new TableEntry { Min = 1, Max = 2, Text = "A strange settlement", LinkedTableId = 42 },
            new TableEntry { Min = 3, Max = 4, Text = "Ruins", UnresolvedLinkName = "Ruin Type" });

        var text = Export(table);
        Assert.Equal(Lines("SITES", "d4", "", "1-2\tA strange settlement", "3-4\tRuins"), text);
        Assert.DoesNotContain("42", text);
        Assert.DoesNotContain("Ruin Type", text);
    }

    [Fact]
    public void Inline_dice_are_copied_as_written()
    {
        var table = Table("ENCOUNTERS", "d6", E(1, 6, "Encounter 2d6 Skeletons"));
        Assert.Equal(Lines("ENCOUNTERS", "d6", "", "1-6\tEncounter 2d6 Skeletons"), Export(table));
    }

    [Fact]
    public void Only_the_name_and_dice_come_before_the_rows()
    {
        var table = Table("LOOT", "2d6+1", E(3, 13, "Coins"));
        table.CollectionId = 7; table.FolderId = 9; table.Id = 123; table.ClampResultsToRange = true;

        Assert.Equal(Lines("LOOT", "2d6+1", "", "3-13\tCoins"), Export(table));
    }

    [Fact]
    public void The_same_table_always_gives_the_same_text()
    {
        var rooms = Fixtures.RoomFeatures();
        var first = Export(rooms, 2);
        for (var i = 0; i < 20; i++) Assert.Equal(first, Export(rooms, 2));
        Assert.Equal(first, TableTextExporter.Export(Fixtures.RoomFeatures(), Fixtures.RoomFeatures().ResultSets[2])); // a fresh copy too
    }

    [Fact]
    public void A_full_d100_table_is_copied_completely_and_quickly()
    {
        var table = Table("BIG", "d100", Enumerable.Range(1, 100).Select(i => E(i, i, $"Result {i}", i == 100 ? "00" : i < 10 ? $"0{i}" : null)).ToArray());

        var watch = Stopwatch.StartNew();
        var text = Export(table);
        watch.Stop();

        var lines = text.Split("\r\n");
        Assert.Equal(103, lines.Length);
        Assert.Equal("01\tResult 1", lines[3]);
        Assert.Equal("00\tResult 100", lines[^1]);
        Assert.True(watch.ElapsedMilliseconds < 500, $"took {watch.ElapsedMilliseconds} ms");
    }
}

/// <summary>Copy Table Text on the Roll screen: the chosen result set of the current table, and nothing else changes.</summary>
public class CopyTableTextRollTests
{
    [Fact]
    public void Copying_puts_the_exact_text_on_the_clipboard_and_leaves_rolls_links_and_history_alone()
    {
        var copied = new List<string>();
        var history = new List<RollSnapshot>();
        var table = Fixtures.Table(DiceExpression.Parse("d6"), (1, 3, "You gain +1d4 Armor"), (4, 6, "Nothing"));
        var session = new RollViewModel(table, new SequenceDice(2, 5, 3), rolled: history.Add, copyText: copied.Add);
        session.ModifierText = "+1";
        session.RollCount = 2;
        session.RollCommand.Execute(null);
        session.LatestRolls[0].Lines[0].InlineActions[0].RollCommand!.Execute(null);
        var rollsBefore = session.LatestRolls.ToList();
        var before = (session.RollDisplay, session.RollCount, session.ModifierText, history.Count, session.Steps.Count,
            session.LatestRolls[0].Lines[0].ResolvedText);

        Assert.False(session.ShowCopyResultSetChoice);
        session.CopyTableTextCommand.Execute(null);

        Assert.Equal("Test\r\nd6\r\n\r\n1-3\tYou gain +1d4 Armor\r\n4-6\tNothing", Assert.Single(copied)); // source text, not "+3 Armor"
        Assert.Equal("Table text copied.", session.CopyMessage);
        Assert.Equal(rollsBefore, session.LatestRolls);
        Assert.Equal(before, (session.RollDisplay, session.RollCount, session.ModifierText, history.Count, session.Steps.Count,
            session.LatestRolls[0].Lines[0].ResolvedText));
        Assert.True(session.LatestRolls[0].Lines[0].ShowResolved);
    }

    [Fact]
    public void With_several_result_sets_the_chosen_one_is_copied_and_a_followed_table_starts_at_its_first()
    {
        var copied = new List<string>();
        var rooms = Fixtures.RoomFeatures();
        rooms.ResultSets[0].Entries[0].LinkedTableId = 5;
        var linked = Fixtures.Table(DiceExpression.Parse("d4"), (1, 4, "Anything")).Also(t => { t.Id = 5; t.Name = "Cellar"; });
        var session = new RollViewModel(rooms, new SequenceDice(10), _ => linked, copyText: copied.Add);

        Assert.True(session.ShowCopyResultSetChoice);
        Assert.Equal(["Ambient", "Noise", "General Feature"], session.CopyResultSetNames);
        session.CopyResultSetIndex = 1;
        session.CopyTableTextCommand.Execute(null);
        Assert.StartsWith("Room Features\r\nd100\r\n\r\n01-25\tSilence", copied[^1]);

        session.RollCommand.Execute(null);
        session.Results[0].FollowCommand!.Execute(null);                                         // the Cellar is now the current table
        Assert.Equal((0, false, ""), (session.CopyResultSetIndex, session.ShowCopyResultSetChoice, session.CopyMessage));
        session.CopyTableTextCommand.Execute(null);
        Assert.Equal("Cellar\r\nd4\r\n\r\n1-4\tAnything", copied[^1]);
    }

    [Fact]
    public void An_unnamed_result_set_is_offered_by_its_position()
    {
        var table = new RollableTable
        {
            Name = "Two", Dice = DiceExpression.Parse("d4"),
            ResultSets = [new ResultSet { Entries = [new() { Min = 1, Max = 4, Text = "a" }] }, new ResultSet { Name = "  Second ", Entries = [new() { Min = 1, Max = 4, Text = "b" }] }],
        };
        Assert.Equal(["Result set 1", "Second"], new RollViewModel(table, new SequenceDice(1)).CopyResultSetNames);
    }

    [Fact]
    public void A_busy_clipboard_is_reported_not_thrown()
    {
        var session = new RollViewModel(Fixtures.Table(DiceExpression.Parse("d6"), (1, 6, "x")), new SequenceDice(1),
            copyText: _ => throw new System.Runtime.InteropServices.COMException("OpenClipboard failed"));

        session.CopyTableTextCommand.Execute(null);

        Assert.Equal("The table text could not be copied: OpenClipboard failed", session.CopyMessage);
    }
}

/// <summary>Copy Table Text on Review: the same table Save would build, copied without saving, and only when Save is possible.</summary>
public class CopyTableTextReviewTests
{
    private static (ReviewViewModel Review, List<string> Copied, TempDatabase Temp, TableForge.Data.AppDatabase Db, Collection Collection) Open(TableImportDraft draft)
    {
        var temp = new TempDatabase();
        var db = temp.Open();
        var collection = db.CreateCollection("C");
        var copied = new List<string>();
        return (new ReviewViewModel(draft, collection, db, _ => { }, () => { }, copied.Add), copied, temp, db, collection);
    }

    [Fact]
    public void A_pasted_table_can_be_copied_without_saving_it()
    {
        var (review, copied, temp, db, collection) = Open(TableTextParser.Parse(Fixtures.RandomStartingGear));
        using (temp)
        {
            Assert.True(review.CopyTableTextCommand.CanExecute(null));
            review.CopyTableTextCommand.Execute(null);

            Assert.Equal(TableTextExporter.Export(Fixtures.ParseAndBuild(Fixtures.RandomStartingGear), Fixtures.ParseAndBuild(Fixtures.RandomStartingGear).ResultSets[0]),
                Assert.Single(copied));
            Assert.StartsWith("Random Starting Gear\r\nd10\r\n\r\n1-2\tBackpack", copied[0]);
            Assert.Equal("Table text copied.", review.CopyMessage);
            Assert.Empty(db.GetTableSummaries(collection.Id));                                    // nothing was saved
        }
    }

    [Fact]
    public void Edits_are_copied_as_they_now_stand_and_clear_the_confirmation()
    {
        var (review, copied, temp, _, _) = Open(TableTextParser.Parse(Fixtures.RandomStartingGear));
        using (temp)
        {
            review.CopyTableTextCommand.Execute(null);
            review.TableName = "Starting Gear";
            Assert.Equal("", review.CopyMessage);
            review.Rows[1].Text = "Knife\nand fork";

            review.CopyTableTextCommand.Execute(null);

            Assert.StartsWith("Starting Gear\r\nd10\r\n\r\n1-2\tBackpack\r\n3\tKnife and fork\r\n", copied[^1]);
        }
    }

    [Fact]
    public void A_table_that_cannot_be_saved_cannot_be_copied()
    {
        var (review, copied, temp, _, _) = Open(TableTextParser.Parse(Fixtures.RandomStartingGear));
        using (temp)
        {
            review.TableName = "";
            Assert.False(review.CanSave);
            Assert.False(review.CopyTableTextCommand.CanExecute(null));
            review.CopyTableTextCommand.Execute(null);                                            // even if asked directly
            Assert.Empty(copied);

            review.TableName = "Back";
            review.DiceText = "d7x";
            Assert.False(review.CopyTableTextCommand.CanExecute(null));
        }
    }

    [Fact]
    public void The_selected_result_set_is_the_one_copied()
    {
        var (review, copied, temp, _, _) = Open(TableImportDraft.FromTable(Fixtures.RoomFeatures()));
        using (temp)
        {
            Assert.True(review.ShowCopyResultSetChoice);
            review.SelectedResultSet = review.ResultSets[2];
            review.CopyTableTextCommand.Execute(null);

            Assert.Equal(TableTextExporter.Export(Fixtures.RoomFeatures(), Fixtures.RoomFeatures().ResultSets[2]), Assert.Single(copied));
        }
    }
}

/// <summary>Copy Table Text through the real window: under Export… on the Roll screen, its own button on Review.</summary>
[Collection("UI")]
public class CopyTableTextViewTests
{
    [Fact]
    public void Roll_and_Edit_screens_copy_the_table_text_and_confirm()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(3, (db, c) =>
                db.SaveTable(Fixtures.Table(DiceExpression.Parse("d6"), (1, 3, "Low, | odd"), (4, 6, "High")).Also(t => { t.Name = "Weather"; t.CollectionId = c.Id; })));
            ui.SelectTable("Weather");
            ui.Click("Roll");
            Assert.False(ui.One<ComboBox>(b => b.Name == "CopyResultSetBox").IsVisible);          // one result set: no chooser
            Assert.False(ui.HasVisibleButton("Copy Table Text"));                                   // RC19: it lives under Export… here

            ui.ChooseExport("Copy Table Text");

            const string expected = "Weather\r\nd6\r\n\r\n1-3\tLow, | odd\r\n4-6\tHigh";
            Assert.Equal([expected], ui.Copied);
            Assert.Contains(ui.Texts(), t => t.Name == "CopyMessageText" && t.Text == "Table text copied.");
            Assert.Contains(ui.Texts(), t => t.Text == "Rolled 3");                                 // the roll is still there
            Assert.Single(ui.Db.GetRollHistory());

            ui.Click("Edit table");
            ui.Click("Copy Table Text");
            Assert.Equal([expected, expected], ui.Copied);
            Assert.Contains(ui.Texts(), t => t.Name == "ReviewCopyMessageText" && t.Text == "Table text copied.");
        });
    }
}
