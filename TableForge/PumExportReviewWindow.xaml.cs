using System.Windows;

namespace TableForge;

/// <summary>The review before a PUM Companion bundle is saved (see <see cref="ViewModels.PumExportReviewViewModel"/>): true means Save Bundle…</summary>
public partial class PumExportReviewWindow : Window
{
    public PumExportReviewWindow() => InitializeComponent();

    private void OnSave(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
