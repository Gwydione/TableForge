using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using TableForge.Import;

namespace TableForge;

/// <summary>Reads every format currently on the Windows clipboard, for the developer diagnostic. Read-only.</summary>
public static class ClipboardReader
{
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public sealed record Snapshot(string Owner, IReadOnlyList<ClipboardEntry> Entries);

    /// <summary>Must run on an STA thread. Retries briefly, since another program may be holding the clipboard open.</summary>
    public static Snapshot Read()
    {
        IDataObject? data = null;
        for (var attempt = 0; attempt < 10 && data is null; attempt++)
        {
            try { data = Clipboard.GetDataObject(); }
            catch (COMException) { Thread.Sleep(50); }
        }

        var entries = new List<ClipboardEntry>();
        if (data is not null)
        {
            foreach (var format in data.GetFormats(autoConvert: false))
            {
                object? value;
                try { value = Normalize(data.GetData(format, autoConvert: false)); }
                catch (Exception ex) { value = $"(unreadable: {ex.GetType().Name})"; }
                entries.Add(new ClipboardEntry(format, value));
            }
        }
        return new Snapshot(OwnerName(), entries);
    }

    /// <summary>Text stays text, streams become bytes, file lists become text; anything else is left for its type name to be reported.</summary>
    private static object? Normalize(object? value) => value switch
    {
        MemoryStream ms => ms.ToArray(),
        Stream s => ReadAll(s),
        string[] files => string.Join("; ", files),
        _ => value,
    };

    private static byte[] ReadAll(Stream s)
    {
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static string OwnerName()
    {
        try
        {
            var hwnd = GetClipboardOwner();
            if (hwnd == IntPtr.Zero) return "(none)";
            GetWindowThreadProcessId(hwnd, out var pid);
            return $"{Process.GetProcessById((int)pid).ProcessName} (pid {pid})";
        }
        catch (Exception) { return "(unknown)"; }
    }

    /// <summary>The whole report for whatever is on the clipboard right now.</summary>
    public static string Report()
    {
        var snapshot = Read();
        return ClipboardAnalyzer.Analyze(snapshot.Entries).ToReport(snapshot.Owner);
    }
}
