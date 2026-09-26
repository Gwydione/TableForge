using System.IO;
using System.Windows.Input;

namespace TableForge.ViewModels;

/// <summary>
/// About TableForge: name, version, publisher and purpose, plus the data folder and the text files that ship beside the
/// exe. Nothing here contacts the network: no update check, no telemetry.
/// </summary>
public sealed class AboutViewModel
{
    public const string Publisher = "RPG Frequencies";
    public const string Purpose = "Turns random tables from RPG books and PDFs into fast, searchable, rollable digital tables.";
    public const string ProjectUrl = "https://github.com/Gwydione/TableForge";

    private readonly string _appFolder;
    private readonly Action<string> _open;

    /// <param name="version">The running version (<see cref="AppInfo.Version"/>).</param>
    /// <param name="appFolder">Where TableForge.exe and its LICENSE/PRIVACY/THIRD-PARTY-NOTICES files are.</param>
    /// <param name="dataFolder">The data folder, or null when it is not known.</param>
    /// <param name="open">Opens a file or folder with Windows (Notepad for .txt, File Explorer for a folder).</param>
    public AboutViewModel(string version, string appFolder, string? dataFolder, Action<string> open)
    {
        Version = version;
        _appFolder = appFolder;
        DataFolder = dataFolder;
        _open = open;
        OpenDataFolderCommand = new RelayCommand(() => _open(DataFolder!), () => DataFolder is not null);
        OpenLicenseCommand = OpenFile("LICENSE.txt");
        OpenPrivacyCommand = OpenFile("PRIVACY.txt");
        OpenNoticesCommand = OpenFile("THIRD-PARTY-NOTICES.txt");
        OpenProjectPageCommand = new RelayCommand(() => _open(ProjectUrl));
    }

    public string Name => "TableForge";
    public string Version { get; }
    public string VersionLine => $"Version {Version}";
    public string PublisherLine => $"© 2026 {Publisher}";
    public string PurposeLine => Purpose;
    public string? DataFolder { get; }

    /// <summary>The project's home page (opened in the person's own browser only when they click it).</summary>
    public string ProjectUrlText => ProjectUrl;
    public ICommand OpenProjectPageCommand { get; }

    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenLicenseCommand { get; }
    public ICommand OpenPrivacyCommand { get; }
    public ICommand OpenNoticesCommand { get; }

    /// <summary>Offered only when the file is really there (it is in every installed or published build).</summary>
    private RelayCommand OpenFile(string name)
    {
        var path = Path.Combine(_appFolder, name);
        return new RelayCommand(() => _open(path), () => File.Exists(path));
    }
}
