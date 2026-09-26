using System.Windows;

namespace TableForge;

/// <summary>Writing text to the Windows clipboard. Screens take this as a plain delegate, so tests hand them a list instead.</summary>
public static class ClipboardText
{
    /// <summary>Throws <see cref="System.Runtime.InteropServices.ExternalException"/> if another program is holding the clipboard.</summary>
    public static void Set(string text) => Clipboard.SetText(text);
}
