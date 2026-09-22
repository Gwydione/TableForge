using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TableForge.Import;
using TableForge.ViewModels;

namespace TableForge.Views;

public partial class PasteView : UserControl
{
    public PasteView()
    {
        InitializeComponent();
        Loaded += (_, _) => PasteSourceBox.Focus(); // the only thing to do here is paste
        PasteSourceBox.PreviewKeyDown += OnPasteSourceKeyDown;
    }

    private PasteViewModel? ViewModel => DataContext as PasteViewModel;

    // Ctrl+J is handled here rather than as a KeyBinding: it needs the real caret position, which is a TextBox
    // concern, not something the view model has. (Ctrl+Enter stays a KeyBinding in XAML — it needs no caret.)
    private void OnPasteSourceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.J || Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        JoinCurrentLineWithPrevious();
    }

    private void JoinCurrentLineWithPrevious()
    {
        var box = PasteSourceBox;
        var joined = TextCleanup.TryJoinLineWithPrevious(box.Text, box.CaretIndex, out var result, out var caret, out var message);
        if (joined) ReplaceAll(box, result, caret);
        SetMessage(message);
    }

    private void OnNormalizeTextClick(object sender, RoutedEventArgs e)
    {
        var box = PasteSourceBox;
        var before = box.Text;
        var after = TextCleanup.NormalizePastedText(before);
        if (after == before) { SetMessage("Nothing needed normalizing."); return; }
        ReplaceAll(box, after, box.CaretIndex);
        SetMessage("Normalized the pasted text.");
    }

    private void OnDehyphenateClick(object sender, RoutedEventArgs e)
    {
        var box = PasteSourceBox;
        if (!TextCleanup.TryDehyphenate(box.Text, out var after))
        {
            SetMessage("No line-wrap hyphenation was found in the pasted text.");
            return;
        }
        ReplaceAll(box, after, box.CaretIndex);
        SetMessage("Removed line-wrap hyphenation from the pasted text.");
    }

    private void OnFindNextDamagedClick(object sender, RoutedEventArgs e)
    {
        var box = PasteSourceBox;
        var text = box.Text;
        var from = Math.Clamp(box.SelectionStart + box.SelectionLength, 0, text.Length);
        var index = text.IndexOf('�', from);
        if (index < 0) index = text.IndexOf('�'); // wrap around to the start
        if (index < 0) { SetMessage("No damaged characters were found."); return; }

        box.Focus();
        box.Select(index, 1);
        SetMessage("Found a damaged character.");
    }

    /// <summary>
    /// Replaces the whole editor's text by selecting everything and assigning <see cref="TextBox.SelectedText"/>,
    /// the same path a paste or a typed replacement goes through, so the change becomes one ordinary entry on the
    /// TextBox's own undo stack instead of a binding update that Ctrl+Z would not see.
    /// </summary>
    private static void ReplaceAll(TextBox box, string newText, int caretIndex)
    {
        box.Focus();
        box.Select(0, box.Text.Length);
        box.SelectedText = newText;
        var caret = Math.Clamp(caretIndex, 0, box.Text.Length);
        box.Select(caret, 0);
    }

    private void SetMessage(string message)
    {
        if (ViewModel is { } vm) vm.CleanupMessage = message;
    }
}
