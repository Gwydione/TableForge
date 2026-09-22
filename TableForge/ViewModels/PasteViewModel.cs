using System.Windows.Input;
using TableForge.Domain;
using TableForge.Import;

namespace TableForge.ViewModels;

public sealed class PasteViewModel : ObservableObject
{
    private string _sourceText = "";
    private string _cleanupMessage = "";

    public PasteViewModel(Collection collection, Action<TableImportDraft> interpreted, Action cancelled)
    {
        CollectionName = collection.Name;
        InterpretCommand = new RelayCommand(
            () => interpreted(TableTextParser.Parse(SourceText)),
            () => !string.IsNullOrWhiteSpace(SourceText));
        CancelCommand = new RelayCommand(cancelled);
    }

    public string CollectionName { get; }

    public string SourceText
    {
        get => _sourceText;
        set
        {
            if (!Set(ref _sourceText, value)) return;
            Raise(nameof(HasDamagedCharacters));
            Raise(nameof(DamagedCharacterCount));
            Raise(nameof(DamagedCharacterWarning));
        }
    }

    public ICommand InterpretCommand { get; }
    public ICommand CancelCommand { get; }

    // ---- raw-text cleanup, before parsing ----------------------------------------------------
    // Ctrl+J, Normalize Text and Dehyphenate all need the real caret position and the WPF TextBox's own undo
    // stack, so they run from PasteView's code-behind rather than as commands here; this view model only holds
    // what the view shows about them.

    /// <summary>What the last raw-text cleanup action (Ctrl+J, Normalize Text, Dehyphenate, Find Next Damaged Character) did, or why it did nothing.</summary>
    public string CleanupMessage { get => _cleanupMessage; internal set => Set(ref _cleanupMessage, value); }

    /// <summary>True while the pasted text still contains a Unicode replacement character from a broken PDF copy.</summary>
    public bool HasDamagedCharacters => TextCleanup.ContainsReplacementCharacter(SourceText);

    /// <summary>How many replacement characters are in the pasted text right now.</summary>
    public int DamagedCharacterCount => TextCleanup.CountReplacementCharacters(SourceText);

    /// <summary>The warning shown above the editor while <see cref="HasDamagedCharacters"/> is true; empty otherwise.</summary>
    public string DamagedCharacterWarning => DamagedCharacterCount == 0 ? "" :
        $"{DamagedCharacterCount} damaged character{(DamagedCharacterCount == 1 ? "" : "s")} detected. " +
        "This pasted text contains one or more characters that could not be copied correctly from the source PDF. Review them before continuing.";
}
