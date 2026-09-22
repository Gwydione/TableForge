using System.Windows;

namespace TableForge;

/// <summary>
/// Developer-only: shows which formats a copy from another program put on the clipboard, and whether bold/italic survives.
/// Reached only with <c>TableForge.exe --clipboard-diagnostic</c>; it is not part of the normal interface and never opens the database.
/// </summary>
public partial class ClipboardDiagnosticWindow : Window
{
    public ClipboardDiagnosticWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        try { ReportBox.Text = ClipboardReader.Report(); }
        catch (Exception ex) { ReportBox.Text = $"The clipboard could not be read: {ex.Message}"; }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(ReportBox.Text); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Copy failed"); }
    }
}
