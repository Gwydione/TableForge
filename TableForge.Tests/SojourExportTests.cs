using System.Windows.Controls;
using TableForge.Domain;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>
/// The exact text <see cref="TableTextExporter.ExportRows"/> produces for Copy for Sojour: only range TAB result per line (CRLF),
/// the same rows Copy Table Text writes, with no name, dice or blank line above them.
/// </summary>
public class SojourRowsExporterTests
{
    private static string Lines(params string[] lines) => string.Join("\r\n", lines);

    private static TableEntry E(int min, int max, string text, string? display = null) => new() { Min = min, Max = max, Text = text, DisplayRange = display };

    private static RollableTable Table(string name, string dice, params TableEntry[] entries) => new()
    {
        Name = name, Dice = DiceExpression.Parse(dice), ResultSets = [new ResultSet { Entries = [.. entries] }],
    };

    private static string Rows(RollableTable table, int set = 0) => TableTextExporter.ExportRows(table, table.ResultSets[set]);

    [Fact]
    public void The_proven_Sojour_payload_is_produced_exactly()
    {
        var gods = Table("GODS OF THE REALM", "d3", E(1, 1, "Axor, God of the Earth"), E(2, 2, "Dhylesia, Goddess of Dreams"), E(3, 3, "Irus, God of the Sun"));

        Assert.Equal("1\tAxor, God of the Earth\r\n2\tDhylesia, Goddess of Dreams\r\n3\tIrus, God of the Sun", Rows(gods));
    }

    [Fact]
    public void Exact_values_and_numeric_ranges()
    {
        var table = Table("WEATHER", "2d6", E(2, 2, "Blizzard"), E(3, 6, "Rain"), E(7, 11, "Clear"), E(12, 12, "Heatwave"));
        Assert.Equal(Lines("2\tBlizzard", "3-6\tRain", "7-11\tClear", "12\tHeatwave"), Rows(table));
    }

    [Fact]
    public void d100_keeps_its_written_notation_such_as_96_00()
    {
        var table = Table("OMENS", "d100", E(1, 30, "Cold", "01–30"), E(31, 95, "Mild"), E(96, 100, "Doom", "96–00"));
        Assert.Equal(Lines("01-30\tCold", "31-95\tMild", "96-00\tDoom"), Rows(table));

        var unwritten = Table("OMENS", "d100", E(1, 99, "Anything"), E(100, 100, "Doom"));   // no written notation: as the dice read it
        Assert.Equal(Lines("1-99\tAnything", "00\tDoom"), Rows(unwritten));
    }

    [Fact]
    public void d66_ranges_stay_tens_and_ones()
    {
        var table = Table("NAMES", "d66", E(11, 16, "Ash"), E(21, 66, "Birch"));
        Assert.Equal(Lines("11-16\tAsh", "21-66\tBirch"), Rows(table));
    }

    [Fact]
    public void Unicode_and_punctuation_are_kept()
    {
        var table = Table("Épées", "d5",
            E(1, 1, "Épée — fine, sharp; 50% off!"), E(2, 2, "A | B | C"), E(3, 3, "“Hello,” she said… ✓"), E(4, 4, "日本刀"),
            E(5, 5, "Sword & Shield <Value> \"quoted\" 'single' = 1/2"));
        Assert.Equal(Lines("1\tÉpée — fine, sharp; 50% off!", "2\tA | B | C", "3\t“Hello,” she said… ✓", "4\t日本刀",
            "5\tSword & Shield <Value> \"quoted\" 'single' = 1/2"), Rows(table));
    }

    [Fact]
    public void Inline_dice_stay_as_written_source_text()
    {
        var table = Table("ENCOUNTERS", "d6", E(1, 3, "Encounter 2d6 Skeletons"), E(4, 6, "You gain +1d4 Armor"));
        Assert.Equal(Lines("1-3\tEncounter 2d6 Skeletons", "4-6\tYou gain +1d4 Armor"), Rows(table));
    }

    [Fact]
    public void Links_add_nothing_but_their_visible_text()
    {
        var table = Table("SITES", "d4",
            new TableEntry { Min = 1, Max = 2, Text = "A strange settlement", LinkedTableId = 42 },
            new TableEntry { Min = 3, Max = 4, Text = "Ruins", UnresolvedLinkName = "Ruin Type" });

        var text = Rows(table);
        Assert.Equal(Lines("1-2\tA strange settlement", "3-4\tRuins"), text);
        Assert.DoesNotContain("42", text);
        Assert.DoesNotContain("Ruin Type", text);
        Assert.DoesNotContain("→", text);
    }

    [Fact]
    public void No_name_dice_blank_line_result_set_name_or_table_settings_come_before_the_rows()
    {
        var rooms = Fixtures.RoomFeatures().Also(t => { t.Id = 123; t.FolderId = 9; t.ClampResultsToRange = true; });

        var text = Rows(rooms, 1);
        Assert.StartsWith("01-25\tSilence\r\n", text);
        Assert.DoesNotContain("Room Features", text);
        Assert.DoesNotContain("d100", text);
        Assert.DoesNotContain("Noise", text);
        Assert.DoesNotContain("\r\n\r\n", text);
        Assert.False(text.EndsWith("\r\n"));                                                   // no trailing empty row either
        Assert.All(text.Split("\r\n"), line => Assert.Single(line.Split('\t').Skip(1)));      // every line is exactly two cells
    }

    [Fact]
    public void Only_the_chosen_result_set_is_copied()
    {
        var rooms = Fixtures.RoomFeatures();
        Assert.Equal(Lines("01-25\tSilence", "26-50\tDistant scratching", "51-66\tDripping water", "67-71\tHissing", "72-00\tLow chanting"), Rows(rooms, 1));
        Assert.Equal(Lines("01-30\tCold stale air", "31-65\tDamp stone", "66-70\tSmell of burning flesh", "71-00\tHeavy incense"), Rows(rooms, 0));
    }

    [Fact]
    public void Line_breaks_and_tabs_inside_a_result_become_one_space_so_each_entry_stays_one_row_of_two_cells()
    {
        var table = Table("SPACED", "d6",
            E(1, 1, "  padded  "),
            E(2, 2, "A ruined tower\r\ncovered in strange runes."),
            E(3, 3, "line\nfeed"),
            E(4, 4, "carriage\rreturn"),
            E(5, 5, "tab\there"),
            E(6, 6, "two  spaces stay"));

        var text = Rows(table);
        Assert.Equal(Lines("1\tpadded", "2\tA ruined tower covered in strange runes.", "3\tline feed", "4\tcarriage return", "5\ttab here",
            "6\ttwo  spaces stay"), text);
        Assert.Equal(6, text.Split("\r\n").Length);
        Assert.All(text.Split("\r\n"), line => Assert.Equal(1, line.Count(c => c == '\t')));
    }

    [Fact]
    public void The_rows_are_exactly_Copy_Table_Text_without_its_three_header_lines()
    {
        var tables = new[] { Fixtures.ParseAndBuild(Fixtures.RandomStartingGear), Fixtures.RoomFeatures(), Fixtures.ScavengedItems() };
        foreach (var table in tables)
            foreach (var set in table.ResultSets)
            {
                var full = TableTextExporter.Export(table, set);
                Assert.Equal(string.Join("\r\n", full.Split("\r\n").Skip(3)), TableTextExporter.ExportRows(table, set));
            }
    }

    [Fact]
    public void Copy_Table_Text_is_unchanged()
    {
        var gear = Fixtures.ParseAndBuild(Fixtures.RandomStartingGear);
        Assert.Equal(Lines("Random Starting Gear", "d10", "", "1-2\tBackpack", "3\tKnife", "4\t1x Torch (UD6)", "5\tFishing rod",
            "6\tRope (15 m)", "7\tTinderbox", "8\tD4 Bandages", "9-10\tD20 Construction Supplies"), TableTextExporter.Export(gear, gear.ResultSets[0]));

        var empty = Table("EMPTY", "d6");
        Assert.Equal(Lines("EMPTY", "d6", ""), TableTextExporter.Export(empty, empty.ResultSets[0]));
        Assert.Equal("", Rows(empty));
    }
}

/// <summary>Copy for Sojour on the Roll screen: the chosen result set's rows, and nothing else changes.</summary>
public class SojourExportRollTests
{
    [Fact]
    public void Copying_puts_only_the_rows_on_the_clipboard_and_leaves_rolls_modifier_clamp_and_history_alone()
    {
        var copied = new List<string>();
        var history = new List<RollSnapshot>();
        var table = Fixtures.Table(DiceExpression.Parse("d6"), (1, 3, "You gain +1d4 Armor"), (4, 6, "Nothing")).Also(t => t.ClampResultsToRange = true);
        var session = new RollViewModel(table, new SequenceDice(2, 5, 3), rolled: history.Add, copyText: copied.Add);
        session.ModifierText = "+1";
        session.RollCommand.Execute(null);
        session.LatestRolls[0].Lines[0].InlineActions[0].RollCommand!.Execute(null);
        var before = (session.RollDisplay, session.ModifierText, history.Count, session.LatestRolls[0].Lines[0].ResolvedText);

        Assert.True(session.CopyForSojourCommand.CanExecute(null));
        session.CopyForSojourCommand.Execute(null);

        Assert.Equal("1-3\tYou gain +1d4 Armor\r\n4-6\tNothing", Assert.Single(copied));    // source text, not the resolved "+3 Armor"
        Assert.Equal("Copied for Sojour.", session.CopyMessage);
        Assert.False(session.HasExportWarning);
        Assert.Equal(before, (session.RollDisplay, session.ModifierText, history.Count, session.LatestRolls[0].Lines[0].ResolvedText));
    }

    [Fact]
    public void The_chosen_result_set_is_the_one_copied()
    {
        var copied = new List<string>();
        var session = new RollViewModel(Fixtures.RoomFeatures(), new SequenceDice(10), copyText: copied.Add);

        session.CopyResultSetIndex = 2;
        session.CopyForSojourCommand.Execute(null);

        Assert.Equal(TableTextExporter.ExportRows(Fixtures.RoomFeatures(), Fixtures.RoomFeatures().ResultSets[2]), Assert.Single(copied));
        Assert.StartsWith("01-40\tCracked stone walls\r\n", copied[0]);
    }

    [Fact]
    public void A_result_set_with_no_rows_copies_nothing_and_says_why()
    {
        var copied = new List<string>();
        var table = new RollableTable { Name = "Empty", Dice = DiceExpression.Parse("d6"), ResultSets = [new ResultSet()] };
        var session = new RollViewModel(table, new SequenceDice(1), copyText: copied.Add);

        session.CopyForSojourCommand.Execute(null);

        Assert.Empty(copied);
        Assert.Equal("This result set has no rows to copy.", session.CopyMessage);
    }

    [Fact]
    public void A_busy_clipboard_is_reported_not_thrown()
    {
        var session = new RollViewModel(Fixtures.Table(DiceExpression.Parse("d6"), (1, 6, "x")), new SequenceDice(1),
            copyText: _ => throw new System.Runtime.InteropServices.COMException("OpenClipboard failed"));

        session.CopyForSojourCommand.Execute(null);

        Assert.Equal("The rows could not be copied: OpenClipboard failed", session.CopyMessage);
    }

    [Fact]
    public void A_Foundry_clamp_warning_clears_when_copying_for_Sojour()
    {
        var table = Fixtures.Table(DiceExpression.Parse("d20+2"), (1, 20, "x")).Also(t => t.ClampResultsToRange = true);
        var session = new RollViewModel(table, new SequenceDice(1), copyText: _ => { });

        session.CopyFoundryJsonCommand.Execute(null);
        Assert.True(session.HasExportWarning);
        session.CopyForSojourCommand.Execute(null);
        Assert.False(session.HasExportWarning);
    }
}

/// <summary>Copy for Sojour through the real window: under Export… on the Roll screen only.</summary>
[Collection("UI")]
public class SojourExportViewTests
{
    [Fact]
    public void Export_offers_Copy_for_Sojour_which_copies_only_the_rows()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(3, (db, c) =>
                db.SaveTable(Fixtures.Table(DiceExpression.Parse("d100"), (1, 95, "Axor, God of the Earth"), (96, 100, "Irus, God of the Sun"))
                    .Also(t => { t.Name = "Gods"; t.CollectionId = c.Id; t.ResultSets[0].Entries[1].DisplayRange = "96–00"; })));
            ui.SelectTable("Gods");
            ui.Click("Roll");
            Assert.Contains("Copy for Sojour",
                ui.One<Button>(b => b.Name == "ExportButton").ContextMenu.Items.OfType<MenuItem>().Select(i => i.Header as string));

            ui.ChooseExport("Copy for Sojour");

            Assert.Equal(["1-95\tAxor, God of the Earth\r\n96-00\tIrus, God of the Sun"], ui.Copied);
            Assert.Contains(ui.Texts(), t => t.Name == "CopyMessageText" && t.Text == "Copied for Sojour.");
            Assert.Single(ui.Db.GetRollHistory());                                                  // the roll is still the only one

            ui.Click("Edit table");                                                                 // Review is unchanged: no Sojour copy there
            Assert.False(ui.HasVisibleButton("Copy for Sojour"));
            Assert.False(ui.HasVisibleButton("Export…"));
        });
    }
}
