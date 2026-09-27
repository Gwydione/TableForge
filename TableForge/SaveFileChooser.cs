using Microsoft.Win32;

namespace TableForge;

/// <summary>Asking where to save a file. Screens take this as a plain delegate, so tests hand them a path instead.</summary>
public static class SaveFileChooser
{
    /// <summary>The Windows Save dialog for a .json file, starting from <paramref name="suggestedName"/>. Null when cancelled.</summary>
    public static string? ChooseJson(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Foundry JSON",
            FileName = suggestedName,
            DefaultExt = ".json",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(System.Windows.Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }
}
