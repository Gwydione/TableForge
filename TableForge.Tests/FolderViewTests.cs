using System.Windows;
using System.Windows.Controls;
using TableForge.Domain;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>Folder navigation, creation, rename, delete, Paste defaults and search, driven through the real MainWindow and its views.</summary>
[Collection("UI")]
public class FolderViewTests
{
    private static void Select(ListBox list, string name)
    {
        list.SelectedItem = list.Items.Cast<object>().Single(i => i.ToString() == name);
    }

    [Fact]
    public void Folder_navigation_lists_all_tables_and_unfiled_and_filters_the_table_list()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) =>
            {
                var combat = db.CreateFolder(c.Id, "Combat");
                db.CreateFolder(c.Id, "Exploration");
                db.SaveTable(Fixtures.ScavengedItems(c.Id).Also(t => { t.Name = "Weather"; t.FolderId = null; }));
                db.SaveTable(Fixtures.ScavengedItems(c.Id).Also(t => { t.Name = "Critical Injuries"; t.FolderId = combat.Id; }));
            });

            var folderNav = ui.One<ListBox>(l => l.Name == "FolderNavList");
            Assert.Equal(["All Tables", "Unfiled", "Combat", "Exploration"], folderNav.Items.Cast<FolderNavItem>().Select(f => f.Name).ToArray());

            Select(folderNav, "Combat");
            ui.Layout();
            var tables = ui.One<ListBox>(l => l.Name == "TablesList");
            Assert.Equal(["Critical Injuries"], tables.Items.Cast<TableSummary>().Select(t => t.Name).ToArray());

            Select(folderNav, "Unfiled");
            ui.Layout();
            Assert.Equal(["Weather"], tables.Items.Cast<TableSummary>().Select(t => t.Name).ToArray());

            Select(folderNav, "All Tables");
            ui.Layout();
            Assert.Equal(["Critical Injuries", "Weather"], tables.Items.Cast<TableSummary>().Select(t => t.Name).OrderBy(n => n).ToArray());
        });
    }

    [Fact]
    public void Table_list_heading_shows_the_selected_scope_or_search_results_and_recent_is_labelled_recent_tables()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) =>
            {
                var creation = db.CreateFolder(c.Id, "Character Creation");
                db.CreateFolder(c.Id, "Combat");
                db.SaveTable(Fixtures.ScavengedItems(c.Id).Also(t => { t.Name = "Backgrounds"; t.FolderId = creation.Id; }));
            });
            ui.SelectTable("Backgrounds"); // opening a table puts it in Recent, which makes the headings visible
            ui.Layout();

            Assert.Equal("Recent tables", ui.One<TextBlock>(t => t.Name == "RecentTablesHeading").Text);
            var heading = ui.One<TextBlock>(t => t.Name == "TableListHeading");
            Assert.Equal("All tables", heading.Text);

            var folderNav = ui.One<ListBox>(l => l.Name == "FolderNavList");
            Select(folderNav, "Character Creation");
            ui.Layout();
            Assert.Equal("Character Creation tables", heading.Text);

            Select(folderNav, "Combat");
            ui.Layout();
            Assert.Equal("Combat tables", heading.Text);

            Select(folderNav, "Unfiled");
            ui.Layout();
            Assert.Equal("Unfiled tables", heading.Text);

            ui.One<TextBox>(t => t.Name == "TableFilterBox").Text = "back";
            ui.Layout();
            Assert.Equal("Search results", heading.Text);
            Assert.True(ui.One<TextBlock>(t => t.Name == "RecentTablesHeading").IsVisible); // Recent stays collection-wide
        });
    }

    [Fact]
    public void New_folder_is_immediately_available_in_navigation()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (_, _) => { });

            ui.One<TextBox>(t => t.Name == "NewFolderBox").Text = "Combat";
            ui.Click("New folder");

            var folderNav = ui.One<ListBox>(l => l.Name == "FolderNavList");
            Assert.Contains(folderNav.Items.Cast<FolderNavItem>(), f => f.Name == "Combat");
            Assert.Equal("Combat", ((FolderNavItem)folderNav.SelectedItem).Name); // usable straight away
        });
    }

    [Fact]
    public void Renaming_the_selected_folder_updates_the_navigation_list()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) => db.CreateFolder(c.Id, "Combat"));

            var folderNav = ui.One<ListBox>(l => l.Name == "FolderNavList");
            Select(folderNav, "Combat");
            ui.Layout();
            Assert.Equal("Combat", ui.One<TextBox>(t => t.Name == "RenameFolderBox").Text); // pre-filled

            ui.One<TextBox>(t => t.Name == "RenameFolderBox").Text = "Skirmishes";
            ui.Click("Rename");

            Assert.Contains(folderNav.Items.Cast<FolderNavItem>(), f => f.Name == "Skirmishes");
            Assert.DoesNotContain(folderNav.Items.Cast<FolderNavItem>(), f => f.Name == "Combat");
        });
    }

    [Fact]
    public void Deleting_a_nonempty_folder_moves_its_tables_to_unfiled_without_a_confirmation_prompt_here()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) =>
            {
                var combat = db.CreateFolder(c.Id, "Combat");
                db.SaveTable(Fixtures.ScavengedItems(c.Id).Also(t => { t.Name = "Critical Injuries"; t.FolderId = combat.Id; }));
            });

            var folderNav = ui.One<ListBox>(l => l.Name == "FolderNavList");
            Select(folderNav, "Combat");
            ui.Layout();

            ui.Click("Delete"); // UiHarness's confirm callback always answers yes

            Assert.DoesNotContain(folderNav.Items.Cast<FolderNavItem>(), f => f.Name == "Combat");
            Assert.True(((FolderNavItem)folderNav.SelectedItem).IsAllTables);
            Select(folderNav, "Unfiled");
            ui.Layout();
            Assert.Equal(["Critical Injuries"], ui.One<ListBox>(l => l.Name == "TablesList").Items.Cast<TableSummary>().Select(t => t.Name).ToArray());
        });
    }

    [Fact]
    public void Pasting_a_table_from_within_a_folder_defaults_it_into_that_folder()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) => db.CreateFolder(c.Id, "Combat"));

            var folderNav = ui.One<ListBox>(l => l.Name == "FolderNavList");
            Select(folderNav, "Combat");
            ui.Layout();

            ui.Click("Paste Table…");
            ui.One<TextBox>(t => t.AcceptsReturn).Text = "d6 Reactions\n1-6 Flee";
            ui.Click("Interpret");
            ui.Layout();

            var folderBox = ui.One<ComboBox>(c => c.Name == "FolderBox");
            Assert.Equal("Combat", ((FolderPickerOption)folderBox.SelectedItem).Name);

            ui.Click("Save Table");
            ui.Layout();

            Select(folderNav, "Combat");
            ui.Layout();
            Assert.Contains("Reactions", ui.One<ListBox>(l => l.Name == "TablesList").Items.Cast<TableSummary>().Select(t => t.Name));
        });
    }

    [Fact]
    public void Pasting_a_table_from_all_tables_defaults_it_to_unfiled_but_it_can_be_changed_before_saving()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) => db.CreateFolder(c.Id, "Exploration"));

            ui.Click("Paste Table…");
            ui.One<TextBox>(t => t.AcceptsReturn).Text = "d6 Weather\n1-6 x";
            ui.Click("Interpret");
            ui.Layout();

            var folderBox = ui.One<ComboBox>(c => c.Name == "FolderBox");
            Assert.Equal("Unfiled", ((FolderPickerOption)folderBox.SelectedItem).Name);

            folderBox.SelectedItem = folderBox.Items.Cast<FolderPickerOption>().Single(o => o.Name == "Exploration");
            ui.Layout();
            ui.Click("Save Table");
            ui.Layout();

            var folderNav = ui.One<ListBox>(l => l.Name == "FolderNavList");
            Select(folderNav, "Exploration");
            ui.Layout();
            Assert.Contains("Weather", ui.One<ListBox>(l => l.Name == "TablesList").Items.Cast<TableSummary>().Select(t => t.Name));
        });
    }

    [Fact]
    public void Search_from_within_a_folder_finds_a_table_in_a_different_folder()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) =>
            {
                var combat = db.CreateFolder(c.Id, "Combat");
                var exploration = db.CreateFolder(c.Id, "Exploration");
                db.SaveTable(Fixtures.ScavengedItems(c.Id).Also(t => { t.Name = "Weather"; t.FolderId = exploration.Id; }));
                db.SaveTable(Fixtures.ScavengedItems(c.Id).Also(t => { t.Name = "Critical Injuries"; t.FolderId = combat.Id; }));
            });

            var folderNav = ui.One<ListBox>(l => l.Name == "FolderNavList");
            Select(folderNav, "Combat");
            ui.Layout();

            ui.One<TextBox>(t => t.Name == "TableFilterBox").Text = "weather";
            ui.Layout();

            Assert.Equal(["Weather"], ui.One<ListBox>(l => l.Name == "TablesList").Items.Cast<TableSummary>().Select(t => t.Name).ToArray());
        });
    }

    [Fact]
    public void Editing_a_tables_folder_offers_only_folders_from_the_current_collection()
    {
        Sta.Run(() =>
        {
            using var ui = new UiHarness(1, (db, c) =>
            {
                db.CreateFolder(c.Id, "Combat");
                db.SaveTable(Fixtures.ScavengedItems(c.Id).Also(t => t.Name = "Loot"));
            });
            var other = ui.Db.CreateCollection("Wilds");
            ui.Db.CreateFolder(other.Id, "Rituals");

            ui.SelectTable("Loot");
            ui.Click("Edit table");
            ui.Layout();

            var folderBox = ui.One<ComboBox>(c => c.Name == "FolderBox");
            Assert.Equal(["Unfiled", "Combat"], folderBox.Items.Cast<FolderPickerOption>().Select(o => o.Name).ToArray());
        });
    }
}
