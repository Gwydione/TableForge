using System.Windows;
using System.Windows.Threading;
using TableForge.Data;
using TableForge.Dice;
using TableForge.ViewModels;

namespace TableForge;

public partial class App : Application
{
    private AppDatabase? _db;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Last resort only: commands already report their own failures in the status bar. This keeps the app open, and
        // says something a person can read, if something unexpected slips through. Saved data is never touched here.
        DispatcherUnhandledException += OnUnhandled;

        var path = AppDatabase.DefaultPath;
        try
        {
            _db = new AppDatabase(path); // creates the folder and database on first run, and upgrades older ones
            var main = new MainViewModel(_db, new BuiltInDiceProvider(),
                confirm: message => MessageBox.Show(message, "Delete table", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
            new MainWindow { DataContext = main }.Show();
        }
        catch (Exception ex)
        {
            _db?.Dispose(); // don't hold the file open after a failed start
            _db = null;
            MessageBox.Show(DatabaseOpenError.Describe(path, ex), AppInfo.Title, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
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
        _db?.Dispose();
        base.OnExit(e);
    }
}
