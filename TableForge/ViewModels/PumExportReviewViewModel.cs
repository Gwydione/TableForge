using System.Windows.Input;
using TableForge.Export.Pum;

namespace TableForge.ViewModels;

/// <summary>
/// The review shown before a PUM Companion bundle is saved, for one table or a whole Collection: what will be exported, what
/// needs a look (skipped tables and warnings), the re-import warning, and below them the full report, which Copy Report copies.
/// It only shows; whether to save is the window's answer.
/// </summary>
public sealed class PumExportReviewViewModel : ObservableObject
{
    /// <summary>How many skipped tables and warnings are listed above the report before "…and N more".</summary>
    public const int ImportantShown = 8;

    private readonly Action<string> _copyText;
    private string _copyMessage = "";

    /// <param name="what">What is being exported, for the title line: "Of Blood and Clay" or "Weather".</param>
    /// <param name="copyText">Puts text on the clipboard (Copy Report).</param>
    public PumExportReviewViewModel(string what, PumExportReport report, Action<string> copyText)
    {
        Report = report;
        _copyText = copyText;
        ReportText = report.ToText();
        CanSave = report.ExportedCount > 0;
        Heading = $"Export \"{what}\" to PUM Companion";

        var exported = report.ExportedCount;
        var skipped = report.SkippedCount;
        Summary = !CanSave
            ? $"Nothing can be exported, so no file will be saved. {Tables(skipped)} {(skipped == 1 ? "was" : "were")} skipped."
            : $"{Tables(exported)} will be exported." + (skipped > 0 ? $" {Tables(skipped)} will be skipped." : "");

        var important = report.Tables.Where(t => !t.Exported).Select(t => $"Skipped: {t.SourceName}: {t.SkipReason}")
            .Concat(report.Tables.Where(t => t.Exported).SelectMany(t => t.Messages.Where(m => m.Kind == PumMessageKind.Warning)
                .Select(m => $"Warning: {t.SourceName}: {m.Text}")))
            .ToList();
        Important = important.Count <= ImportantShown
            ? important
            : [.. important.Take(ImportantShown), $"…and {important.Count - ImportantShown} more (see the full report below)."];

        CopyReportCommand = new RelayCommand(CopyReport);
    }

    public PumExportReport Report { get; }

    public string Heading { get; }

    /// <summary>"38 tables will be exported. 2 will be skipped." or why nothing will be saved.</summary>
    public string Summary { get; }

    /// <summary>Skipped tables with their reasons, then warnings, at most <see cref="ImportantShown"/> (all of them are in the report).</summary>
    public IReadOnlyList<string> Important { get; }
    public bool HasImportant => Important.Count > 0;

    /// <summary>Always shown when a bundle can be saved (Slice 1 acceptance: PUM offers to overwrite tables it already has).</summary>
    public string ReimportWarning => PumExporter.ReimportWarning;

    /// <summary>The full report, exactly what Copy Report copies.</summary>
    public string ReportText { get; }

    /// <summary>Whether there is a bundle to save: otherwise the window only offers Close.</summary>
    public bool CanSave { get; }

    /// <summary>"Cancel" beside Save Bundle…, or "Close" when there is nothing to save.</summary>
    public string CancelLabel => CanSave ? "Cancel" : "Close";

    public ICommand CopyReportCommand { get; }

    /// <summary>"Report copied." after Copy Report, or why it could not be copied.</summary>
    public string CopyMessage { get => _copyMessage; private set { if (Set(ref _copyMessage, value)) Raise(nameof(HasCopyMessage)); } }
    public bool HasCopyMessage => CopyMessage.Length > 0;

    private void CopyReport()
    {
        try
        {
            _copyText(ReportText);
            CopyMessage = "Report copied.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            CopyMessage = $"The report could not be copied: {ex.Message}";
        }
    }

    private static string Tables(int count) => count == 1 ? "1 table" : $"{count} tables";
}
