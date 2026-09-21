using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using TableForge.ViewModels;

namespace TableForge.Views;

public partial class ReviewView : UserControl
{
    public ReviewView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Focus moving into any control of a row shows where that row came from in the original text.
        GotKeyboardFocus += OnFocused;
        Loaded += (_, _) => NameBox.Focus(); // the table name is the first thing worth checking
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnViewModelChanged;
        if (e.NewValue is INotifyPropertyChanged now) now.PropertyChanged += OnViewModelChanged;
    }

    private void OnFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (DataContext is ReviewViewModel vm && (e.OriginalSource as FrameworkElement)?.DataContext is EntryRowViewModel row)
            vm.SelectedRow = row;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ReviewViewModel vm) return;

        if (e.PropertyName == nameof(ReviewViewModel.SelectedRow) && vm.SelectedRow is { } row)
            HighlightSourceLines(row.SourceLineStart, row.SourceLineEnd);

        // Opening the paste area puts the cursor in it; closing it (button or Escape) puts focus back where it came from.
        if (e.PropertyName == nameof(ReviewViewModel.IsPasteRowsOpen))
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => (vm.IsPasteRowsOpen ? (Control)PasteRowsBox : PasteRowsToggle).Focus());
    }

    /// <summary>Selects whole lines (one-based, inclusive) in the read-only original text.</summary>
    private void HighlightSourceLines(int firstLine, int lastLine)
    {
        var first = firstLine - 1;
        var last = lastLine - 1;
        if (first < 0 || last >= SourceBox.LineCount) return;

        var start = SourceBox.GetCharacterIndexFromLineIndex(first);
        var end = SourceBox.GetCharacterIndexFromLineIndex(last) + SourceBox.GetLineLength(last);
        SourceBox.Select(start, Math.Max(0, end - start));
        SourceBox.ScrollToLine(first);
    }
}
