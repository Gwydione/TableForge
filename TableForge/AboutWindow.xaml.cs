using System.Windows;

namespace TableForge;

/// <summary>About TableForge (see <see cref="ViewModels.AboutViewModel"/>).</summary>
public partial class AboutWindow : Window
{
    public AboutWindow() => InitializeComponent();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
