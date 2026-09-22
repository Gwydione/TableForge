using System.Windows;
using System.Windows.Threading;
using TableForge.Data;
using TableForge.Dice;
using TableForge.ViewModels;

namespace TableForge;

public partial class App : Application
{
    private AppDatabase? _db;
    private WebViewDddiceRoller? _dddice;

    // The dice choice is one word in a small file next to the database (no schema change for a single setting).
    private static string DataFolder => System.IO.Path.GetDirectoryName(AppDatabase.DefaultPath)!;
    private static string PreferencePath => System.IO.Path.Combine(DataFolder, "dice-provider.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Developer-only: TableForge.exe --clipboard-diagnostic [--out <file>]. Reports what is on the clipboard and touches
        // no database. With --out it writes the report to that file and exits; without it, it shows a window.
        if (e.Args.Contains("--clipboard-diagnostic", StringComparer.OrdinalIgnoreCase))
        {
            RunClipboardDiagnostic(e.Args);
            return;
        }

        // Last resort only: commands already report their own failures in the status bar. This keeps the app open, and
        // says something a person can read, if something unexpected slips through. Saved data is never touched here.
        DispatcherUnhandledException += OnUnhandled;

        var path = AppDatabase.DefaultPath;
        try
        {
            _db = new AppDatabase(path); // creates the folder and database on first run, and upgrades older ones
            var window = new MainWindow();
            // Built-in is the default and does no network work. dddice is only prepared once it has been chosen (or was chosen last time).
            _dddice = new WebViewDddiceRoller(window.AttachDiceView, System.IO.Path.Combine(DataFolder, "WebView2"));
            var dice = new DiceProviderViewModel(new BuiltInDiceProvider(), new DddiceDiceProvider(_dddice), LoadDicePreference, SaveDicePreference);
            var main = new MainViewModel(_db, dice,
                confirm: message => MessageBox.Show(message, "Delete table", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
            window.DataContext = main;
            window.Show();
            dice.RestorePreference();
        }
        catch (Exception ex)
        {
            _db?.Dispose(); // don't hold the file open after a failed start
            _db = null;
            MessageBox.Show(DatabaseOpenError.Describe(path, ex), AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string? LoadDicePreference() =>
        System.IO.File.Exists(PreferencePath) ? System.IO.File.ReadAllText(PreferencePath).Trim() : null;

    private static void SaveDicePreference(string value)
    {
        System.IO.Directory.CreateDirectory(DataFolder);
        System.IO.File.WriteAllText(PreferencePath, value);
    }

    private void RunClipboardDiagnostic(string[] args)
    {
        var outIndex = Array.FindIndex(args, a => a.Equals("--out", StringComparison.OrdinalIgnoreCase));
        if (outIndex >= 0 && outIndex + 1 < args.Length)
        {
            string report;
            try { report = ClipboardReader.Report(); }
            catch (Exception ex) { report = $"The clipboard could not be read: {ex.Message}"; }
            System.IO.File.WriteAllText(args[outIndex + 1], report, new System.Text.UTF8Encoding(false));
            Shutdown(0);
            return;
        }
        new ClipboardDiagnosticWindow().Show();
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Something unexpected went wrong:\n\n{e.Exception.Message}\n\nYour saved tables are safe. You can carry on; if it keeps happening, close and reopen TableForge.",
            AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _dddice?.Dispose();
        _db?.Dispose();
        base.OnExit(e);
    }
}
