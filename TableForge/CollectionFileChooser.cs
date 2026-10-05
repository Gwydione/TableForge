using Microsoft.Win32;
using TableForge.Portable;

namespace TableForge;

/// <summary>Asking where to save a Collection file, or which one to import. Screens take these as plain delegates, so tests hand them a path instead.</summary>
public static class CollectionFileChooser
{
    private const string Filter = "TableForge Collection (*.tfcollection)|*.tfcollection|All files (*.*)|*.*";

    /// <summary>The Windows Save dialog for a .tfcollection file, titled and starting from the request. Null when cancelled.</summary>
    public static string? ChooseSave(SaveFileRequest request)
    {
        var dialog = new SaveFileDialog
        {
            Title = request.Title,
            FileName = request.SuggestedName,
            DefaultExt = PortableFormat.Extension,
            Filter = Filter,
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(System.Windows.Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    /// <summary>The Windows Open dialog for one .tfcollection file. Null when cancelled.</summary>
    public static string? ChooseOpen()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import Collection",
            DefaultExt = PortableFormat.Extension,
            Filter = Filter,
            CheckFileExists = true,
            Multiselect = false,
        };
        return dialog.ShowDialog(System.Windows.Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }
}
