using Microsoft.Win32;

namespace TableForge;

/// <summary>What a Save dialog asks: its title ("Save Foundry JSON") and the file name it starts from.</summary>
public sealed record SaveFileRequest(string Title, string SuggestedName);

/// <summary>Asking where to save a file. Screens take this as a plain delegate, so tests hand them a path instead.</summary>
public static class SaveFileChooser
{
    /// <summary>The Windows Save dialog for a .json file, titled and starting from the request. Null when cancelled.</summary>
    public static string? ChooseJson(SaveFileRequest request)
    {
        var dialog = new SaveFileDialog
        {
            Title = request.Title,
            FileName = request.SuggestedName,
            DefaultExt = ".json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(System.Windows.Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }
}
