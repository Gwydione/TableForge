using System.Windows;
using System.Windows.Controls;

namespace TableForge.Views;

public partial class PasteView : UserControl
{
    public PasteView()
    {
        InitializeComponent();
        Loaded += (_, _) => PasteSourceBox.Focus(); // the only thing to do here is paste
    }
}
