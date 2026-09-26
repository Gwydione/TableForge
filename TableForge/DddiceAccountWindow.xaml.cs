using System.Windows;
using TableForge.ViewModels;

namespace TableForge;

/// <summary>The Account… dialog (see <see cref="DddiceAccountViewModel"/>). Closing it stops anything still under way.</summary>
public partial class DddiceAccountWindow : Window
{
    public DddiceAccountWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (DataContext is DddiceAccountViewModel vm) _ = vm.OpenedAsync(); };
        Closed += (_, _) => (DataContext as DddiceAccountViewModel)?.Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
