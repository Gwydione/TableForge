using System.Windows.Input;
using TableForge.Export.Pum;

namespace TableForge.ViewModels;

/// <summary>
/// The review shown before a PUM Companion bundle is saved, for one table or a whole Collection: what will be exported, what
/// needs a look (skipped tables and warnings), the re-import warning, and below them the full report, which Copy Report copies.
/// A Collection export also offers native nested tables (off each time the review opens): turning it on or off builds the export
/// again in memory, and the review then shows that one. It only shows; whether to save is the window's answer.
/// </summary>
public sealed class PumExportReviewViewModel : ObservableObject
{
    /// <summary>How many skipped tables and warnings are listed above the report before "…and N more".</summary>
    public const int ImportantShown = 8;

    private readonly Action<string> _copyText;
    private readonly Func<bool, PumExportResult>? _export;
    private string _copyMessage = "";
    private bool _rollLinkedTables;
    private PumExportReport _report = null!;

    /// <param name="what">What is being exported, for the title line: "Of Blood and Clay" or "Weather".</param>
    /// <param name="copyText">Puts text on the clipboard (Copy Report).</param>
    /// <param name="export">
    /// Builds the export again with native nested tables on (true) or off (false), for the checkbox; null hides it (one table).
    /// </param>
    public PumExportReviewViewModel(string what, PumExportReport report, Action<string> copyText, Func<bool, PumExportResult>? export = null)
    {
        _copyText = copyText;
        _export = export;
        Heading = $"Export \"{what}\" to PUM Companion";
        Show(report);
        CopyReportCommand = new RelayCommand(CopyReport);
    }

    public PumExportReport Report => _report;

    public string Heading { get; }

    /// <summary>"38 tables will be exported. 2 will be skipped." or why nothing will be saved.</summary>
    public string Summary { get; private set; } = "";

    /// <summary>Skipped tables with their reasons, then warnings, at most <see cref="ImportantShown"/> (all of them are in the report).</summary>
    public IReadOnlyList<string> Important { get; private set; } = [];
    public bool HasImportant => Important.Count > 0;

    /// <summary>Always shown when a bundle can be saved (Slice 1 acceptance: PUM offers to overwrite tables it already has).</summary>
    public string ReimportWarning => PumExporter.ReimportWarning;

    /// <summary>The full report, exactly what Copy Report copies.</summary>
    public string ReportText { get; private set; } = "";

    /// <summary>Whether there is a bundle to save: otherwise the window only offers Close.</summary>
    public bool CanSave { get; private set; }

    /// <summary>"Cancel" beside Save Bundle…, or "Close" when there is nothing to save.</summary>
    public string CancelLabel => CanSave ? "Cancel" : "Close";

    /// <summary>Whether the native nested tables checkbox is offered: Collection exports only.</summary>
    public bool CanRollLinkedTables => _export is not null;

    /// <summary>
    /// The checkbox "Roll linked tables automatically in PUM": links to tables in the bundle become nested rolls, unless they must
    /// stay text. Never remembered: every review starts with it off.
    /// </summary>
    public bool RollLinkedTables
    {
        get => _rollLinkedTables;
        set
        {
            if (_export is null || !Set(ref _rollLinkedTables, value)) return;
            Result = _export(value);
            Show(Result.Report);
        }
    }

    /// <summary>The export as the checkbox last built it; null while it hasn't been touched (the caller's own export stands).</summary>
    public PumExportResult? Result { get; private set; }

    /// <summary>With the checkbox on: "12 of 15 links will roll their table in PUM. 3 stay text (see the report)."</summary>
    public string LinkSummary { get; private set; } = "";
    public bool HasLinkSummary => LinkSummary.Length > 0;

    /// <summary>With the checkbox on: PUM finds linked tables by name alone, so same-named tables replace each other.</summary>
    public string NameClashWarning => PumExporter.NameClashWarning;
    public bool ShowNameClashWarning => RollLinkedTables && CanSave;

    public ICommand CopyReportCommand { get; }

    /// <summary>"Report copied." after Copy Report, or why it could not be copied.</summary>
    public string CopyMessage { get => _copyMessage; private set { if (Set(ref _copyMessage, value)) Raise(nameof(HasCopyMessage)); } }
    public bool HasCopyMessage => CopyMessage.Length > 0;

    private void Show(PumExportReport report)
    {
        _report = report;
        ReportText = report.ToText();
        CanSave = report.ExportedCount > 0;

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

        var links = report.LinkCount;
        var native = report.NativeLinkCount;
        LinkSummary = !RollLinkedTables || !CanSave ? ""
            : links == 0 ? "No exported table links to another, so nothing changes."
            : native == links ? (links == 1 ? "The 1 link will roll its table in PUM." : $"All {links} links will roll their table in PUM.")
            : native == 0 ? (links == 1 ? "The 1 link stays text (see the report)." : $"None of the {links} links can roll in PUM: they stay text (see the report).")
            : $"{native} of {links} links will roll their table in PUM. {links - native} stay{(links - native == 1 ? "s" : "")} text (see the report).";

        foreach (var name in (string[])[nameof(Report), nameof(ReportText), nameof(CanSave), nameof(CancelLabel), nameof(Summary), nameof(Important),
                     nameof(HasImportant), nameof(LinkSummary), nameof(HasLinkSummary), nameof(ShowNameClashWarning)])
            Raise(name);
    }

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
