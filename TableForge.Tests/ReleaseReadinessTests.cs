using System.IO;
using Microsoft.Data.Sqlite;
using TableForge.Data;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>The safety copy made before an older database is updated, and what happens around it.</summary>
public class MigrationBackupTests
{
    private static SqliteConnection Raw(string path) => new($"Data Source={path};Pooling=False;Foreign Keys=True");

    private static void Exec(string path, string sql)
    {
        using var c = Raw(path);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static int Version(string path)
    {
        using var c = Raw(path);
        c.Open();
        return DatabaseMigrations.GetVersion(c);
    }

    private static long Count(string path, string table)
    {
        using var c = Raw(path);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>A database left at an older schema by an earlier TableForge, holding one collection and one table.</summary>
    private static string OlderDatabase(TempFolder folder, int version = 6)
    {
        var path = folder.DatabasePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var c = Raw(path))
        {
            c.Open();
            DatabaseMigrations.Apply(c, upToVersion: version);
        }
        Exec(path, """
            INSERT INTO Collections (Name, CreatedUtc) VALUES ('Solo', '2026-09-01T12:00:00.0000000Z');
            INSERT INTO Tables (CollectionId, Name, DiceCount, DiceSides, CreatedUtc, UpdatedUtc)
            VALUES (1, 'Omens', 1, 6, '2026-09-01T12:00:00.0000000Z', '2026-09-01T12:00:00.0000000Z');
            """);
        return path;
    }

    private static string Current => $"v{DatabaseMigrations.CurrentVersion}";

    [Fact]
    public void Updating_an_older_database_first_saves_a_copy_of_it_as_it_was()
    {
        using var folder = new TempFolder();
        var path = OlderDatabase(folder);

        using (new AppDatabase(path)) { }

        var backup = Assert.Single(DatabaseBackup.Existing(path));
        Assert.Equal(Path.Combine(folder.Path, $"tableforge.pre-{Current}-from-v6.backup.db"), backup);
        Assert.Equal(6, Version(backup));                                  // the copy is the database before the update
        Assert.Equal(1, Count(backup, "Tables"));
        Assert.Equal(DatabaseMigrations.CurrentVersion, Version(path));    // and the database itself was updated
        Assert.Equal(1, Count(path, "Tables"));
        Assert.False(File.Exists(backup + ".tmp"));
    }

    [Fact]
    public void An_ordinary_start_or_a_brand_new_database_makes_no_copy()
    {
        using var folder = new TempFolder();
        var path = folder.DatabasePath();

        using (new AppDatabase(path)) { }                                   // first run: created, nothing to protect
        using (new AppDatabase(path)) { }                                   // already current

        Assert.Empty(DatabaseBackup.Existing(path));
        Assert.Equal(["tableforge.db"], Directory.GetFiles(folder.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void If_the_copy_cannot_be_made_nothing_is_updated()
    {
        using var folder = new TempFolder();
        var path = OlderDatabase(folder);
        Directory.CreateDirectory(DatabaseBackup.PathFor(path, 6, DatabaseMigrations.CurrentVersion)); // something is in the copy's way

        var error = Assert.Throws<DatabaseBackupException>(() => new AppDatabase(path));

        Assert.Equal(6, Version(path));                                     // untouched
        Assert.Equal(1, Count(path, "Tables"));
        var message = DatabaseOpenError.Describe(path, error);
        Assert.StartsWith("TableForge needs to update your data for this version, but could not make a safety copy of it first, so nothing was changed.", message);
        Assert.Contains("Details:", message);
        Assert.DoesNotContain("Exception", message);
        Assert.False(File.Exists(DatabaseBackup.PathFor(path, 6, DatabaseMigrations.CurrentVersion) + ".tmp"));
    }

    [Fact]
    public void A_failed_update_keeps_the_copy_and_says_where_it_is()
    {
        using var folder = new TempFolder();
        var path = OlderDatabase(folder);
        Exec(path, "ALTER TABLE Tables ADD COLUMN ClampResultsToRange INTEGER NULL"); // makes the v7 update step fail

        var error = Assert.Throws<DatabaseMigrationException>(() => new AppDatabase(path));

        Assert.True(File.Exists(error.BackupPath));
        Assert.Equal(6, Version(error.BackupPath));
        Assert.Equal(1, Count(error.BackupPath, "Tables"));
        Assert.Equal(6, Version(path));                                     // the failing step was rolled back
        var message = DatabaseOpenError.Describe(path, error);
        Assert.StartsWith("TableForge could not update your data safely. Your original database backup has been preserved.", message);
        Assert.Contains(error.BackupPath, message);
        Assert.Contains("Details: SQLite error", message);
        Assert.DoesNotContain("SqliteException", message);
    }

    [Fact]
    public void A_later_attempt_after_a_failure_still_keeps_a_copy_and_the_update_then_succeeds()
    {
        using var folder = new TempFolder();
        var path = OlderDatabase(folder);
        Exec(path, "ALTER TABLE Tables ADD COLUMN ClampResultsToRange INTEGER NULL");
        Assert.Throws<DatabaseMigrationException>(() => new AppDatabase(path));

        Exec(path, "ALTER TABLE Tables DROP COLUMN ClampResultsToRange");   // the cause is fixed
        using (new AppDatabase(path)) { }

        Assert.Equal(DatabaseMigrations.CurrentVersion, Version(path));
        Assert.Single(DatabaseBackup.Existing(path));                        // the same copy name, still there after success
    }

    [Fact]
    public void Only_the_newest_three_copies_are_kept_after_a_successful_update()
    {
        using var folder = new TempFolder();
        var path = OlderDatabase(folder);
        var old = new[] { "pre-v4-from-v3", "pre-v5-from-v4", "pre-v6-from-v5" }
            .Select((name, i) => Path.Combine(folder.Path, $"tableforge.{name}.backup.db")).ToList();
        for (var i = 0; i < old.Count; i++)
        {
            File.WriteAllText(old[i], "older copy");
            File.SetLastWriteTimeUtc(old[i], new DateTime(2026, 1, 1 + i, 0, 0, 0, DateTimeKind.Utc));
        }
        var unrelated = Path.Combine(folder.Path, "notes.backup.db");
        File.WriteAllText(unrelated, "not ours");

        using (new AppDatabase(path)) { }

        Assert.Equal(
            [$"tableforge.pre-{Current}-from-v6.backup.db", "tableforge.pre-v6-from-v5.backup.db", "tableforge.pre-v5-from-v4.backup.db"],
            DatabaseBackup.Existing(path).Select(Path.GetFileName).ToArray());
        Assert.False(File.Exists(old[0]));                                   // the oldest went
        Assert.True(File.Exists(unrelated));                                 // nothing else is touched
    }

    [Fact]
    public void Copies_live_beside_the_database_in_whatever_data_folder_is_in_use()
    {
        using var folder = new TempFolder();
        var dataFolder = AppDatabase.ResolveDataFolder(Path.Combine(folder.Path, "custom data"));   // as TABLEFORGE_DATA_DIR would give
        var path = Path.Combine(dataFolder, "tableforge.db");
        Directory.CreateDirectory(dataFolder);
        using (var c = Raw(path)) { c.Open(); DatabaseMigrations.Apply(c, upToVersion: 5); }

        using (new AppDatabase(path)) { }

        Assert.Equal(dataFolder, Path.GetDirectoryName(Assert.Single(DatabaseBackup.Existing(path))));
    }

    [Fact]
    public void Other_database_problems_are_explained_plainly_with_details_last()
    {
        var message = DatabaseOpenError.Describe(@"C:\x\tableforge.db", new SqliteException("disk I/O error", 10));

        Assert.StartsWith("TableForge could not open its database.", message);
        Assert.Contains("The database could not be read.", message);
        Assert.EndsWith("Details: SQLite error 10: disk I/O error", message);
        Assert.DoesNotContain("SqliteException", message);
    }
}

/// <summary>Open Data Folder: the folder in use, handed to File Explorer.</summary>
public class OpenDataFolderTests
{
    [Fact]
    public void It_opens_the_data_folder_TableForge_is_using()
    {
        using var temp = new TempDatabase();
        var opened = new List<string>();
        var folder = AppDatabase.ResolveDataFolder(@"D:\Games\TableForge Data");
        var main = new MainViewModel(temp.Open(), new FixedDice(1), dataFolder: folder, openFolder: opened.Add);

        Assert.True(main.HasDataFolder);
        main.OpenDataFolderCommand.Execute(null);

        Assert.Equal([@"D:\Games\TableForge Data"], opened);
    }

    [Fact]
    public void The_default_folder_is_under_local_application_data_and_an_override_wins()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TableForge"), AppDatabase.ResolveDataFolder(null));
        Assert.Equal(Path.GetFullPath(@"C:\Elsewhere\TF"), AppDatabase.ResolveDataFolder(@"  C:\Elsewhere\TF  "));
    }

    [Fact]
    public void Without_a_folder_the_command_is_not_offered_and_a_failure_is_only_a_status_message()
    {
        using var temp = new TempDatabase();
        var none = new MainViewModel(temp.Open(), new FixedDice(1));
        Assert.False(none.HasDataFolder);
        Assert.False(none.OpenDataFolderCommand.CanExecute(null));

        var failing = new MainViewModel(temp.Open(), new FixedDice(1), dataFolder: @"C:\TF", openFolder: _ => throw new IOException("Explorer is not available"));
        failing.OpenDataFolderCommand.Execute(null);
        Assert.Equal("Could not open the data folder: Explorer is not available", failing.Status);
    }
}

/// <summary>What ships: the publish profile, the installer script, publish.ps1 and the legal files.</summary>
public class PackagingTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TableForge.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    [Fact]
    public void The_public_publish_has_no_debug_symbols_and_publish_ps1_refuses_any()
    {
        var profile = Read("TableForge", "Properties", "PublishProfiles", "win-x64-folder.pubxml");
        Assert.Contains("<DebugType>none</DebugType>", profile);
        Assert.Contains("<DebugSymbols>false</DebugSymbols>", profile);
        Assert.Contains("<CopyOutputSymbolsToPublishDirectory>false</CopyOutputSymbolsToPublishDirectory>", profile);
        Assert.Contains("<PublishReferencesDocumentationFiles>false</PublishReferencesDocumentationFiles>", profile);
        Assert.DoesNotContain("DebugType", Read("TableForge", "TableForge.csproj"));   // development builds keep their symbols

        var script = Read("publish.ps1");
        Assert.Contains("-Filter *.pdb", script);
        Assert.Contains("Debug symbols were published", script);
    }

    [Fact]
    public void publish_ps1_builds_the_installer_only_when_asked_and_checks_it_exists()
    {
        var script = Read("publish.ps1");
        Assert.Contains("[switch]$Installer", script);
        Assert.Contains("if ($Installer)", script);
        Assert.Contains("\"/DAppVersion=$csprojVersion\"", script);
        Assert.Contains("TableForge-$csprojVersion-Setup.exe", script);
        Assert.Contains("winget install JRSoftware.InnoSetup", script);
    }

    [Fact]
    public void The_installer_is_per_user_stable_and_never_touches_the_data_folder()
    {
        var iss = Read("installer", "TableForge.iss");
        var lines = iss.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith(';')).ToList();

        Assert.Contains("AppId={{89A2A71F-CF3F-4C91-BDCB-125354F776EB}", lines);         // never change this
        Assert.Contains("PrivilegesRequired=lowest", lines);
        Assert.Contains("PrivilegesRequiredOverridesAllowed=", lines);
        Assert.Contains(@"DefaultDirName={localappdata}\Programs\TableForge", lines);
        Assert.Contains("DisableDirPage=yes", lines);
        Assert.Contains("ArchitecturesAllowed=x64compatible", lines);
        Assert.Contains("OutputBaseFilename=TableForge-{#AppVersion}-Setup", lines);
        Assert.Contains(lines, l => l.StartsWith("Name: \"{autoprograms}\\TableForge\"; Filename: \"{app}\\TableForge.exe\""));
        Assert.Contains(lines, l => l.StartsWith("Source: \"publish\\win-x64\\*\"") && l.Contains("Excludes: \"*.pdb\""));

        Assert.DoesNotContain("[UninstallDelete]", lines);
        Assert.DoesNotContain("[InstallDelete]", lines);
        Assert.DoesNotContain(lines, l => l.Contains(@"{localappdata}\TableForge", StringComparison.OrdinalIgnoreCase)); // the data folder
        Assert.DoesNotContain(lines, l => l.Contains("tableforge.db", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains(@"C:\Projects", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains("WebView2", StringComparison.OrdinalIgnoreCase));    // not a prerequisite
    }

    [Fact]
    public void The_legal_and_privacy_files_exist_and_say_what_they_should()
    {
        var license = Read("LICENSE.txt");
        Assert.Contains("All rights reserved.", license);
        Assert.DoesNotContain("MIT License", license);

        var notices = Read("THIRD-PARTY-NOTICES.txt");
        foreach (var component in new[] { "Microsoft.Data.Sqlite", "SQLitePCLRaw", "Microsoft.Web.WebView2", ".NET runtime", "Apache License", "The MIT License" })
            Assert.Contains(component, notices);

        var privacy = Read("PRIVACY.txt");
        foreach (var fact in new[] { "No telemetry", "no cloud sync", "dddice", "never asks for or stores your dddice password", "DPAPI", "clipboard", "%LOCALAPPDATA%\\TableForge" })
            Assert.Contains(fact, privacy);

        var project = Read("TableForge", "TableForge.csproj");                        // shipped beside the exe (installer and zip)
        foreach (var file in new[] { "LICENSE.txt", "THIRD-PARTY-NOTICES.txt", "PRIVACY.txt" })
            Assert.Contains($"<Content Include=\"..\\{file}\" Link=\"{file}\" CopyToOutputDirectory=\"PreserveNewest\" />", project);
    }

    [Fact]
    public void No_source_file_assumes_the_developers_machine_or_working_directory()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "TableForge"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".xaml") || f.EndsWith(".html") || f.EndsWith(".csproj") || f.EndsWith(".pubxml"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f =>
            {
                var text = File.ReadAllText(f);
                // OverlayServer.cs validates the HTTP Host header and must accept "localhost:<port>" as well as "127.0.0.1:<port>":
                // that is the Streaming Overlay's own loopback contract, not a dependency on the developer's machine.
                var hostValidation = Path.GetFileName(f) == "OverlayServer.cs" && Path.GetFileName(Path.GetDirectoryName(f)) == "Streaming";
                return text.Contains(@"C:\Projects", StringComparison.OrdinalIgnoreCase) || text.Contains("CurrentDirectory")
                    || (text.Contains("localhost") && !hostValidation);
            })
            .Select(Path.GetFileName)
            .ToList();
        Assert.Empty(offenders);
    }
}
