using System.Windows.Input;
using TableForge.Domain;
using TableForge.Import;

namespace TableForge.ViewModels;

public sealed class PasteViewModel : ObservableObject
{
    private string _sourceText = "";

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
        set => Set(ref _sourceText, value);
    }

    public ICommand InterpretCommand { get; }
    public ICommand CancelCommand { get; }
}
