using System.Windows;
using TableForge.ViewModels;

namespace TableForge;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Never open larger than the screen's usable area (the default suits a 1080p display, not a 768-pixel laptop).
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width - 20));
        Height = Math.Min(Height, Math.Max(MinHeight, area.Height - 20));

        Loaded += (_, _) => FocusStartingPoint();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.CollectionCreated -= OnCollectionCreated;
            if (e.NewValue is MainViewModel now) now.CollectionCreated += OnCollectionCreated;
        };
    }

    /// <summary>Where dddice's WebView2 lives: a dedicated panel above the roll screen, shown only while dddice is chosen.</summary>
    public void AttachDiceView(FrameworkElement view) => DiceHost.Child = view;

    /// <summary>A brand-new user starts in the collection name box; otherwise the search box, ready to find a table.</summary>
    private void FocusStartingPoint()
    {
        if (DataContext is MainViewModel { Collections.Count: 0 }) NewCollectionBox.Focus();
        else TableFilterBox.Focus();
    }

    /// <summary>Clicking a table opens it, even if it is already highlighted (selection itself only highlights).</summary>
    private void TableItem_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is System.Windows.Controls.ListBoxItem item
            && ReferenceEquals(item.DataContext, vm.HighlightedTable) && vm.OpenTableCommand.CanExecute(null))
            vm.OpenTableCommand.Execute(null);
    }

    /// <summary>
    /// After naming a first collection the natural next step is pasting a table. Paste Table only becomes enabled once WPF
    /// re-evaluates its command (a moment after the collection exists), and a disabled button cannot take focus, so wait for that.
    /// </summary>
    private void OnCollectionCreated(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () => PasteTableButton.Focus());
}
