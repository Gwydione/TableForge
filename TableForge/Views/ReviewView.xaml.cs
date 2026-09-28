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
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => NameBox.Focus(); // the table name is the first thing worth checking
    }

    // ---- bold / italic --------------------------------------------------------------------------------------------

    /// <summary>
    /// Ctrl+B, Ctrl+I and Ctrl+Space in a row's result text. Handled here, on the way down, rather than as KeyBindings: they
    /// need the real selection of the text box being typed in, which is a TextBox concern the view model does not have
    /// (Ctrl+J on the Paste screen works the same way). Anywhere else on the screen these keys are left alone.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (FormattingKey(e.Key) is not { } action || !IsResultTextBox(Keyboard.FocusedElement)) return;
        e.Handled = true;
        ApplyFormatting(action);
    }

    /// <summary>What a Ctrl+key does to the selected result text; null for every other key.</summary>
    public static FormattingAction? FormattingKey(Key key) => key switch
    {
        Key.B => FormattingAction.Bold,
        Key.I => FormattingAction.Italic,
        Key.Space => FormattingAction.Clear,
        _ => null,
    };

    private void OnBoldClick(object sender, RoutedEventArgs e) => ApplyFormatting(FormattingAction.Bold);
    private void OnItalicClick(object sender, RoutedEventArgs e) => ApplyFormatting(FormattingAction.Italic);
    private void OnClearFormattingClick(object sender, RoutedEventArgs e) => ApplyFormatting(FormattingAction.Clear);

    /// <summary>
    /// Formats the selection in the result text box that has keyboard focus. The buttons cannot take focus, so after a click
    /// that is still the box the person was selecting in, with its selection untouched.
    /// </summary>
    public void ApplyFormatting(FormattingAction action)
    {
        if (DataContext is not ReviewViewModel vm) return;
        var box = Keyboard.FocusedElement as TextBox;
        if (!IsResultTextBox(box)) box = null;
        vm.ApplyFormatting(box?.DataContext as EntryRowViewModel, box?.SelectionStart ?? 0, box?.SelectionLength ?? 0, action);
    }

    private static bool IsResultTextBox(object? element) => element is TextBox { Name: "ResultTextBox", DataContext: EntryRowViewModel };

    /// <summary>
    /// Tells the row exactly where the person's edit happened, so formatting next to it lands on the right side of a
    /// boundary (see <see cref="EntryRowViewModel.RefineLastEdit"/>). Only the focused box reports: the same row can also be
    /// shown, hidden, in the other layout, and that copy just receives the new text as a whole.
    /// </summary>
    private void OnResultTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox { IsKeyboardFocused: true, DataContext: EntryRowViewModel row } || e.Changes.Count != 1) return;
        var change = e.Changes.First();
        row.RefineLastEdit(change.Offset, change.RemovedLength, change.AddedLength);
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
