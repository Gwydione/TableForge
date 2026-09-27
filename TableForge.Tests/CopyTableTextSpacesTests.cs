using System.Windows.Controls;
using TableForge.Domain;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>
/// Copy Table Text (Spaces): exactly the text Copy Table Text writes, with one ordinary space instead of the tab between each
/// row's range and result. Copy Table Text itself still writes the tab.
/// </summary>
public class TableTextSpacesExporterTests
{
    private static string Lines(params string[] lines) => string.Join("\r\n", lines);

    private static TableEntry E(int min, int max, string text, string? display = null) => new() { Min = min, Max = max, Text = text, DisplayRange = display };

    private static RollableTable Table(string name, string dice, params TableEntry[] entries) => new()
    {
        Name = name, Dice = DiceExpression.Parse(dice), ResultSets = [new ResultSet { Entries = [.. entries] }],
    };

    private static string Tabs(RollableTable table, int set = 0) => TableTextExporter.Export(table, table.ResultSets[set]);
    private static string Spaces(RollableTable table, int set = 0) => TableTextExporter.Export(table, table.ResultSets[set], TableTextSeparator.Space);

    /// <summary>The only intended difference: the first tab of every row (after the name, dice and blank line) becomes one space.</summary>
    private static void AssertOnlyTheSeparatorDiffers(string tabs, string spaces)
    {
        var tabLines = tabs.Split("\r\n");
        var spaceLines = spaces.Split("\r\n");
        Assert.Equal(tabLines.Length, spaceLines.Length);
        Assert.Equal(tabLines.Take(3), spaceLines.Take(3));
        foreach (var (tab, space) in tabLines.Skip(3).Zip(spaceLines.Skip(3)))
        {
            var at = tab.IndexOf('\t');
            Assert.Equal(tab[..at] + " " + tab[(at + 1)..], space);
        }
        Assert.DoesNotContain('\t', spaces);
    }

    [Fact]
    public void Ordinary_ranges_get_one_space_and_Copy_Table_Text_keeps_its_tab()
    {
        var table = Table("WEATHER", "d6", E(1, 1, "Clear"), E(2, 3, "Cloudy"), E(4, 5, "Rain"), E(6, 6, "Storm"));

        Assert.Equal(Lines("WEATHER", "d6", "", "1\tClear", "2-3\tCloudy", "4-5\tRain", "6\tStorm"), Tabs(table));
        Assert.Equal(Lines("WEATHER", "d6", "", "1 Clear", "2-3 Cloudy", "4-5 Rain", "6 Storm"), Spaces(table));
        AssertOnlyTheSeparatorDiffers(Tabs(table), Spaces(table));
    }

    [Fact]
    public void The_regression_table_differs_only_by_the_separator()
    {
        var gear = Fixtures.ParseAndBuild(Fixtures.RandomStartingGear);

        Assert.Equal(Lines("Random Starting Gear", "d10", "",
            "1-2 Backpack", "3 Knife", "4 1x Torch (UD6)", "5 Fishing rod", "6 Rope (15 m)", "7 Tinderbox", "8 D4 Bandages",
            "9-10 D20 Construction Supplies"), Spaces(gear));
        AssertOnlyTheSeparatorDiffers(Tabs(gear), Spaces(gear));
    }

    [Fact]
    public void d100_keeps_its_displayed_ranges()
    {
        var written = Table("OMENS", "d100", E(1, 30, "Cold", "01–30"), E(31, 98, "Mild"), E(99, 99, "Odd"), E(100, 100, "Doom", "00"));
        Assert.Equal(Lines("OMENS", "d100", "", "01-30\tCold", "31-98\tMild", "99\tOdd", "00\tDoom"), Tabs(written));
        Assert.Equal(Lines("OMENS", "d100", "", "01-30 Cold", "31-98 Mild", "99 Odd", "00 Doom"), Spaces(written));

        var plain = Table("OMENS", "d100", E(1, 99, "Anything"), E(100, 100, "Doom"), E(91, 100, "Late"), E(96, 100, "End", "96–00"));
        Assert.Equal(Lines("OMENS", "d100", "", "1-99 Anything", "00 Doom", "91-00 Late", "96-00 End"), Spaces(plain));
        AssertOnlyTheSeparatorDiffers(Tabs(plain), Spaces(plain));
    }

    [Fact]
    public void d66_keeps_its_tens_and_ones_ranges()
    {
        var table = Table("NAMES", "d66", E(11, 16, "Ash"), E(21, 21, "Birch"), E(22, 66, "Cedar"));
        Assert.Equal(Lines("NAMES", "d66", "", "11-16\tAsh", "21\tBirch", "22-66\tCedar"), Tabs(table));
        Assert.Equal(Lines("NAMES", "d66", "", "11-16 Ash", "21 Birch", "22-66 Cedar"), Spaces(table));
    }

    [Fact]
    public void Result_text_is_treated_exactly_as_Copy_Table_Text_treats_it()
    {
        var table = Table("  SPACED  ", "d6",
            E(1, 1, "  padded  "), E(2, 2, "A ruined tower\r\ncovered in runes."), E(3, 3, "tab\there"), E(4, 4, "two  spaces stay"),
            new TableEntry { Min = 5, Max = 6, Text = "Ruins", LinkedTableId = 42 });

        Assert.Equal(Lines("SPACED", "d6", "",
            "1 padded", "2 A ruined tower covered in runes.", "3 tab here", "4 two  spaces stay", "5-6 Ruins"), Spaces(table));
        AssertOnlyTheSeparatorDiffers(Tabs(table), Spaces(table));
    }

    [Fact]
    public void Only_the_chosen_result_set_is_copied()
    {
        var rooms = Fixtures.RoomFeatures();

        Assert.Equal(Lines("Room Features", "d100", "",
            "01-25 Silence", "26-50 Distant scratching", "51-66 Dripping water", "67-71 Hissing", "72-00 Low chanting"), Spaces(rooms, 1));
        for (var set = 0; set < rooms.ResultSets.Count; set++) AssertOnlyTheSeparatorDiffers(Tabs(rooms, set), Spaces(rooms, set));
    }

    [Fact]
    public void The_default_is_still_the_tab_and_Sojour_rows_are_unchanged()
    {
        var rooms = Fixtures.RoomFeatures();
        Assert.Equal(TableTextExporter.Export(rooms, rooms.ResultSets[0], TableTextSeparator.Tab), Tabs(rooms));
        Assert.StartsWith("01-30\tCold stale air\r\n", TableTextExporter.ExportRows(rooms, rooms.ResultSets[0]));
    }
}

/// <summary>Copy Table Text (Spaces) on the Roll screen: the same result set as Copy Table Text, and nothing else changes.</summary>
public class CopyTableTextSpacesRollTests
{
    [Fact]
    public void Both_commands_copy_the_same_text_apart_from_the_separator_and_leave_everything_alone()
    {
        var copied = new List<string>();
        var history = new List<RollSnapshot>();
        var table = Fixtures.Table(DiceExpression.Parse("d6"), (1, 3, "You gain +1d4 Armor"), (4, 6, "Nothing"));
        var session = new RollViewModel(table, new SequenceDice(2, 5, 3), rolled: history.Add, copyText: copied.Add);
        session.ModifierText = "+1";
        session.RollCommand.Execute(null);
        session.LatestRolls[0].Lines[0].InlineActions[0].RollCommand!.Execute(null);
        var rollsBefore = session.LatestRolls.ToList();
        var before = (session.RollDisplay, session.RollCount, session.ModifierText, history.Count, session.Steps.Count,
            session.LatestRolls[0].Lines[0].ResolvedText, session.CopyResultSetIndex);

        Assert.True(session.CopyTableTextSpacesCommand.CanExecute(null));
        session.CopyTableTextCommand.Execute(null);
        Assert.Equal("Test\r\nd6\r\n\r\n1-3\tYou gain +1d4 Armor\r\n4-6\tNothing", copied[^1]);
        Assert.Equal("Table text copied.", session.CopyMessage);

        session.CopyTableTextSpacesCommand.Execute(null);
        Assert.Equal("Test\r\nd6\r\n\r\n1-3 You gain +1d4 Armor\r\n4-6 Nothing", copied[^1]);      // source text, not "+3 Armor"
        Assert.Equal("Table text copied (spaces).", session.CopyMessage);
        Assert.False(session.HasExportWarning);

        Assert.Equal(2, copied.Count);
        Assert.Equal(rollsBefore, session.LatestRolls);
        Assert.Equal(before, (session.RollDisplay, session.RollCount, session.ModifierText, history.Count, session.Steps.Count,
            session.LatestRolls[0].Lines[0].ResolvedText, session.CopyResultSetIndex));
    }

    [Fact]
    public void The_chosen_result_set_is_the_one_copied_and_stays_chosen()
    {
        var copied = new List<string>();
        var session = new RollViewModel(Fixtures.RoomFeatures(), new SequenceDice(10), copyText: copied.Add);

        session.CopyResultSetIndex = 2;
        session.CopyTableTextCommand.Execute(null);
        session.CopyTableTextSpacesCommand.Execute(null);

        Assert.Equal(TableTextExporter.Export(Fixtures.RoomFeatures(), Fixtures.RoomFeatures().ResultSets[2]), copied[0]);
        Assert.Equal(TableTextExporter.Export(Fixtures.RoomFeatures(), Fixtures.RoomFeatures().ResultSets[2], TableTextSeparator.Space), copied[1]);
        Assert.StartsWith("Room Features\r\nd100\r\n\r\n01-40 Cracked stone walls\r\n", copied[1]);
        Assert.Equal(2, session.CopyResultSetIndex);
    }

    [Fact]
    public void A_busy_clipboard_is_reported_not_thrown()
    {
        var session = new RollViewModel(Fixtures.Table(DiceExpression.Parse("d6"), (1, 6, "x")), new SequenceDice(1),
            copyText: _ => throw new System.Runtime.InteropServices.COMException("OpenClipboard failed"));

        session.CopyTableTextSpacesCommand.Execute(null);

        Assert.Equal("The table text could not be copied: OpenClipboard failed", session.CopyMessage);
    }
}

/// <summary>Copy Table Text (Spaces) through the real window: beside Copy Table Text under Export…, for the chosen result set.</summary>
[Collection("UI")]
public class CopyTableTextSpacesViewTests
{
    [Fact]
    public void Export_offers_both_copies_side_by_side_and_they_differ_only_by_the_separator()
    {
        Sta.Run(() =>
        {
            long id = 0;
            using var ui = new UiHarness(3, (db, c) => id = db.SaveTable(Fixtures.RoomFeatures(c.Id)).Id);
            ui.SelectTable("Room Features");
            ui.Click("Roll");
            var headers = ui.One<Button>(b => b.Name == "ExportButton").ContextMenu.Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
            Assert.Equal(headers.IndexOf("Copy Table Text") + 1, headers.IndexOf("Copy Table Text (Spaces)"));
            Assert.DoesNotContain(headers, h => h!.Contains("Questline"));

            var savedBefore = Enumerable.Range(0, 3).Select(i => TableTextExporter.Export(ui.Db.LoadTable(id)!, ui.Db.LoadTable(id)!.ResultSets[i])).ToList();
            var chooser = ui.One<ComboBox>(b => b.Name == "CopyResultSetBox");
            Assert.True(chooser.IsVisible);
            chooser.SelectedIndex = 1;                                                              // Noise

            ui.ChooseExport("Copy Table Text");
            ui.ChooseExport("Copy Table Text (Spaces)");

            Assert.Equal([
                "Room Features\r\nd100\r\n\r\n01-25\tSilence\r\n26-50\tDistant scratching\r\n51-66\tDripping water\r\n67-71\tHissing\r\n72-00\tLow chanting",
                "Room Features\r\nd100\r\n\r\n01-25 Silence\r\n26-50 Distant scratching\r\n51-66 Dripping water\r\n67-71 Hissing\r\n72-00 Low chanting",
            ], ui.Copied);
            Assert.Contains(ui.Texts(), t => t.Name == "CopyMessageText" && t.Text == "Table text copied (spaces).");
            Assert.Equal(1, chooser.SelectedIndex);                                                 // the choice is kept
            Assert.Contains(ui.Texts(), t => t.Text == "Rolled 3");                                 // the roll is still there
            Assert.Single(ui.Db.GetRollHistory());                                                  // and nothing was rolled
            Assert.Equal(savedBefore, Enumerable.Range(0, 3).Select(i => TableTextExporter.Export(ui.Db.LoadTable(id)!, ui.Db.LoadTable(id)!.ResultSets[i])));
        });
    }
}
