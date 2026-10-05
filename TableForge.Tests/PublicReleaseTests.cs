using System.IO;
using System.Reflection;
using System.Windows.Controls;
using TableForge.ViewModels;

namespace TableForge.Tests;

/// <summary>RC18: identity, icon, About, release artifacts and the public documents.</summary>
public class PublicReleaseTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TableForge.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    private static string PathOf(params string[] parts) => Path.Combine([RepoRoot(), .. parts]);
    private static string Read(params string[] parts) => File.ReadAllText(PathOf(parts));

    [Fact]
    public void The_exe_says_TableForge_by_RPG_Frequencies()
    {
        var assembly = typeof(AppInfo).Assembly;
        Assert.Equal("TableForge", assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product);
        Assert.Equal("RPG Frequencies", assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company);
        Assert.Equal("Copyright (c) 2026 RPG Frequencies", assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright);
        Assert.Equal("1.0.0-rc25", AppInfo.Version);
    }

    [Fact]
    public void The_icon_is_a_real_windows_icon_made_from_the_untouched_source_artwork()
    {
        var ico = File.ReadAllBytes(PathOf("TableForge", "Assets", "TableForge.ico"));
        Assert.Equal((0, 1), (BitConverter.ToUInt16(ico, 0), BitConverter.ToUInt16(ico, 2)));   // ICO header, not a renamed PNG
        var count = BitConverter.ToUInt16(ico, 4);
        var sizes = Enumerable.Range(0, count).Select(i => ico[6 + 16 * i] == 0 ? 256 : ico[6 + 16 * i]).ToList();
        foreach (var size in new[] { 16, 24, 32, 48, 256 }) Assert.Contains(size, sizes);

        var source = File.ReadAllBytes(PathOf("branding", "TableForge-icon-source.png"));
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], source[..4]);
        Assert.Equal(6, source[25]);                                                             // RGBA: the transparent artwork

        var project = Read("TableForge", "TableForge.csproj");
        Assert.Contains(@"<ApplicationIcon>Assets\TableForge.ico</ApplicationIcon>", project);
        Assert.Contains(@"<Resource Include=""Assets\TableForge.ico"" />", project);
        Assert.Contains(@"Icon=""Assets/TableForge.ico""", Read("TableForge", "MainWindow.xaml"));
        var iss = Read("installer", "TableForge.iss");
        Assert.Contains(@"SetupIconFile=TableForge\Assets\TableForge.ico", iss);
        Assert.Contains(@"IconFilename: ""{app}\TableForge.exe""", iss);
    }

    [Fact]
    public void The_installer_keeps_its_identity_and_names_its_artifacts_by_version()
    {
        var iss = Read("installer", "TableForge.iss");
        Assert.Contains("AppId={{89A2A71F-CF3F-4C91-BDCB-125354F776EB}", iss);                 // never changes
        Assert.Contains("AppPublisher=RPG Frequencies", iss);
        Assert.Contains("AppPublisherURL=https://github.com/Gwydione/TableForge", iss);
        Assert.Contains("AppSupportURL=https://github.com/Gwydione/TableForge/issues", iss);
        Assert.Contains("AppUpdatesURL=https://github.com/Gwydione/TableForge/releases", iss);
        Assert.Contains("VersionInfoCompany=RPG Frequencies", iss);
        Assert.Contains("VersionInfoCopyright=Copyright (c) 2026 RPG Frequencies", iss);
        Assert.Contains(@"OutputDir=publish\release", iss);
        Assert.Contains("OutputBaseFilename=TableForge-{#AppVersion}-Setup", iss);
        Assert.Contains("PrivilegesRequired=lowest", iss);
        Assert.Contains(@"DefaultDirName={localappdata}\Programs\TableForge", iss);
        var sections = iss.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('[')).ToList();   // not comments
        Assert.DoesNotContain("[UninstallDelete]", sections);
        Assert.DoesNotContain("[InstallDelete]", sections);
    }

    [Fact]
    public void publish_ps1_makes_the_installer_zip_and_checksums_and_refuses_developer_files()
    {
        var script = Read("publish.ps1");
        Assert.Contains(@"""publish\release""", script);
        Assert.Contains("TableForge-$csprojVersion-Setup.exe", script);
        Assert.Contains("TableForge-$csprojVersion-win-x64.zip", script);
        Assert.DoesNotContain("]::CreateFromDirectory(", script);                               // it writes "\" entry names on .NET Framework
        Assert.Contains(".Replace('\\', '/')", script);
        Assert.Contains("entries using '\\'", script);
        Assert.Contains("SHA256SUMS.txt", script);
        Assert.Contains("Get-FileHash $artifact -Algorithm SHA256", script);
        Assert.Contains("'.xml', '.db'", script);
        Assert.Contains("'*Tests*'", script);
        Assert.Contains("Development-only files were published", script);
    }

    [Fact]
    public void The_license_names_RPG_Frequencies()
    {
        Assert.Contains("Copyright (c) 2026 RPG Frequencies. All rights reserved.", Read("LICENSE.txt"));
    }

    [Fact]
    public void The_readme_answers_a_newcomers_questions_first()
    {
        var readme = Read("README.md").ReplaceLineEndings("\n");                               // a Windows checkout may have CRLF
        var sections = new[] { "# TableForge", "## Install", "## First use", "## Your data and backups", "## Privacy",
            "## Known limitations", "## Feedback and bug reports", "# Using TableForge in detail", "# For developers" };
        var positions = sections.Select(s => readme.IndexOf(s + "\n", StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.OrderBy(p => p), positions);                                     // in this order

        foreach (var fact in new[] { "Paste → Review/Correct → Save → Find → Roll → Read", "TableForge-<version>-Setup.exe",
                     "not code-signed", "More info", @"%LOCALAPPDATA%\TableForge", "does not delete this folder",
                     "no telemetry", "PRIVACY.txt", "GitHub Issues", "Never post dddice tokens", "OCR", "Windows x64 only" })
            Assert.Contains(fact, readme);
        Assert.DoesNotContain("disable SmartScreen", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(https://github.com/Gwydione/TableForge/releases)", readme);
        Assert.Contains("(https://github.com/Gwydione/TableForge/issues)", readme);
    }

    [Fact]
    public void The_release_notes_and_testing_checklist_exist()
    {
        var notes = Read("docs", "release-notes", "1.0.0-rc25.md");
        Assert.StartsWith("# TableForge 1.0.0-rc25", notes);
        foreach (var heading in new[] { "## Highlights", "## Installation", "## What's Included", "## Known Issues", "## Data and Upgrades", "## Feedback" })
            Assert.Contains(heading, notes);
        Assert.Contains("TableForge-1.0.0-rc25-Setup.exe", notes);
        Assert.Contains("**Portable Collections**", notes);                                    // the RC25 change, stated precisely:
        Assert.Contains("never merges into, replaces or", notes);                               // an import is always a new collection
        Assert.Contains("the import is all or nothing", notes);
        Assert.Contains("**Delete Collection…**", notes);
        Assert.Contains("**Existing exports are unchanged**", notes);
        Assert.Contains("**The data format does not change** (still version 9)", notes);         // rc24 and rc25 share the data
        Assert.Contains("rc24 can still open data that rc25 has used", notes);
        Assert.Contains("rc23 and earlier cannot open the data", notes);                         // the RC24 upgrade status is kept
        Assert.Contains("**Bold and italic result text**", notes);
        Assert.Contains("**Open-ended ranges**", notes);                                        // the RC23 status is kept
        Assert.Contains("**Questline import has not been verified.**", notes);                   // the RC22 status is kept
        Assert.Contains("**Checked in Tables+:**", notes);                                      // the RC21 Tables+ status is kept:
        Assert.Contains("Still untested there:", notes);                                         // what was checked live and what was not
        Assert.Contains("this is not full", notes);

        var checklist = Read("docs", "RELEASE_TESTING.md");
        foreach (var heading in new[] { "## Fresh install", "## Bold and italic result text", "## Portable Collections", "## dddice", "## Foundry VTT export", "## Sojour", "## Owlbear Rodeo (Tables+)", "## Uninstall and reinstall", "## Upgrade" })
            Assert.Contains(heading, checklist);
    }
}

/// <summary>About TableForge.</summary>
public class AboutTests
{
    [Fact]
    public void About_shows_the_name_version_publisher_and_purpose_and_opens_the_shipped_files()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"tableforge-about-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "LICENSE.txt"), "x");
            File.WriteAllText(Path.Combine(folder, "PRIVACY.txt"), "x");
            var opened = new List<string>();
            var about = new AboutViewModel("1.0.0-rc18", folder, @"C:\Data\TableForge", opened.Add);

            Assert.Equal(("TableForge", "Version 1.0.0-rc18", "© 2026 RPG Frequencies"), (about.Name, about.VersionLine, about.PublisherLine));
            Assert.Contains("rollable digital tables", about.PurposeLine);

            about.OpenLicenseCommand.Execute(null);
            about.OpenPrivacyCommand.Execute(null);
            about.OpenDataFolderCommand.Execute(null);
            Assert.Equal([Path.Combine(folder, "LICENSE.txt"), Path.Combine(folder, "PRIVACY.txt"), @"C:\Data\TableForge"], opened);
            Assert.False(about.OpenNoticesCommand.CanExecute(null));                           // not there: not offered

            Assert.Equal("https://github.com/Gwydione/TableForge", about.ProjectUrlText);
            about.OpenProjectPageCommand.Execute(null);                                         // only when clicked
            Assert.Equal("https://github.com/Gwydione/TableForge", opened[^1]);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void The_shipped_text_files_are_beside_the_built_exe()
    {
        foreach (var file in new[] { "LICENSE.txt", "PRIVACY.txt", "THIRD-PARTY-NOTICES.txt" })
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(typeof(AppInfo).Assembly.Location)!, file)), file);
    }

    [Fact]
    public void About_is_offered_from_the_main_window_only_when_wired()
    {
        using var temp = new TempDatabase();
        var shown = 0;
        var main = new MainViewModel(temp.Open(), new FixedDice(1), showAbout: () => shown++);
        Assert.True(main.HasAbout);
        main.AboutCommand.Execute(null);
        Assert.Equal(1, shown);
        Assert.False(new MainViewModel(temp.Open(), new FixedDice(1)).HasAbout);
    }

    [Fact]
    public void The_About_window_renders_its_details()
    {
        Sta.Run(() =>
        {
            var window = new AboutWindow
            {
                DataContext = new AboutViewModel("1.0.0-rc18", AppContext.BaseDirectory, null, _ => { }),
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual,
                Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false,
            };
            window.Show();
            try
            {
                window.Dispatcher.Invoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => { });
                Assert.Equal("Version 1.0.0-rc18", ((TextBlock)window.FindName("AboutVersion")).Text);
                Assert.Equal("© 2026 RPG Frequencies", ((TextBlock)window.FindName("AboutPublisher")).Text);
                Assert.NotNull(window.Icon);
            }
            finally { window.Close(); }
        });
    }
}
