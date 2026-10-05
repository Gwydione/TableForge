using System.IO;
using System.Windows.Controls;
using TableForge.Domain;
using static TableForge.Tests.PortableFixtures;

namespace TableForge.Tests;

/// <summary>Export, Import and Delete Collection through the real window.</summary>
[Collection("UI")]
public class PortableCollectionViewTests
{
    [Fact]
    public void Export_import_and_delete_a_collection_through_the_real_window()
    {
        Sta.Run(() =>
        {
            using var file = new TempFile();
            using var ui = new UiHarness(6, (db, c) => Fixtures.SeedScavenging(db, c.Id));

            ui.SavePath = file.Path;
            ui.Click("Export Collection…");
            Assert.Equal("Dungeon.tfcollection", ui.SuggestedFileNames[^1]);
            Assert.Equal("Export Collection", ui.SaveTitles[^1]);
            Assert.True(File.Exists(file.Path));

            ui.ImportPath = file.Path;
            ui.Click("Import Collection…");
            Assert.Equal("Dungeon (2)", ui.Main.SelectedCollection!.Name);
            Assert.Equal("Dungeon (2)", ((Collection)ui.One<ComboBox>(b => b.Name == "CollectionBox").SelectedItem).Name);
            Assert.Equal(["Scavenged Items", "Scavenging"], ui.Main.Tables.Select(t => t.Name).ToArray());

            // The imported link works: it follows to the imported copy of its destination, not the original.
            ui.SelectTable("Scavenging");
            ui.Click("Roll");
            Assert.Contains(ui.Texts(), t => t.Text == "1x Scavenged Item" && t.FontSize == 26);
            ui.Click("Open Scavenged Items");
            var followed = ((ViewModels.RollViewModel)ui.Main.Current!).Steps[^1].Table;
            Assert.Equal("Scavenged Items", followed.Name);
            Assert.Equal(ui.Main.SelectedCollection!.Id, followed.CollectionId);

            ui.Click("Delete Collection…");
            Assert.Equal(["Dungeon"], ui.Main.Collections.Select(c => c.Name).ToArray());
            Assert.Equal("Dungeon", ui.Main.SelectedCollection!.Name);
            Assert.Null(ui.Main.Current);
        });
    }

    [Fact]
    public void A_new_installation_can_import_but_has_nothing_to_export_or_delete()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1);
            Assert.True(ui.One<Button>(b => b.Name == "ImportCollectionButton").IsEnabled);
            Assert.False(ui.One<Button>(b => b.Name == "ExportCollectionButton").IsEnabled);
            Assert.False(ui.One<Button>(b => b.Name == "DeleteCollectionButton").IsEnabled);
        });
    }
}
