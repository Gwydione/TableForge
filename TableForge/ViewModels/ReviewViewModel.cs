using System.Collections.ObjectModel;
using System.Windows.Input;
using TableForge.Data;
using TableForge.Domain;
using TableForge.Import;

namespace TableForge.ViewModels;

public enum LinkChoiceKind { None, Unresolved, Table }

/// <summary>How much a note asks of the user: Info is provenance to glance at, Warning wants a check, Error must be fixed to save.</summary>
public enum NoteLevel { Info, Warning, Error }

/// <summary>One option in an entry's link chooser: no link, an unresolved name, or a table in the same collection.</summary>
public sealed record LinkChoice(LinkChoiceKind Kind, long? TableId, string Label)
{
    /// <summary>What a screen reader or UI Automation reads for this option.</summary>
    public override string ToString() => Label;
}

/// <summary>One option in the table's folder chooser: Unfiled (null id) or a folder in the same collection.</summary>
public sealed record FolderPickerOption(long? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One editable row of the draft, with the notes attached to it.</summary>
public sealed class EntryRowViewModel : ObservableObject
{
    private readonly EntryDraft _draft;
    private readonly Action _edited;
    private LinkChoice _selectedLink;
    private string _unresolvedName;
    private string _notes = "";
    private bool _hasError;
    private bool _hasWarning;
    private bool _hasInfo;
    private int _number;

    public EntryRowViewModel(EntryDraft draft, int number, IReadOnlyList<ParseIssue> issues,
        IReadOnlyList<LinkChoice> linkChoices, Action edited, Action<EntryRowViewModel> delete)
    {
        _draft = draft;
        _number = number;
        _edited = edited;
        Issues = issues;
        LinkChoices = linkChoices;
        _unresolvedName = draft.UnresolvedLinkName ?? "";
        _selectedLink = draft.LinkedTableId is long id
            ? linkChoices.First(c => c.Kind == LinkChoiceKind.Table && c.TableId == id)
            : draft.UnresolvedLinkName is not null
                ? linkChoices.First(c => c.Kind == LinkChoiceKind.Unresolved)
                : linkChoices.First(c => c.Kind == LinkChoiceKind.None);
        DeleteCommand = new RelayCommand(() => delete(this));
    }

    /// <summary>Parser issues that were raised for this entry when it was interpreted.</summary>
    internal IReadOnlyList<ParseIssue> Issues { get; }
    internal EntryDraft Draft => _draft;

    public int Number { get => _number; internal set => Set(ref _number, value); }
    public int SourceLineStart => _draft.SourceLineStart;
    public int SourceLineEnd => _draft.SourceLineEnd;
    public ICommand DeleteCommand { get; }

    public string RangeText
    {
        get => _draft.RangeText;
        set { if (_draft.RangeText != value) { _draft.RangeText = value; Raise(); _edited(); } }
    }

    public string Text
    {
        get => _draft.Text;
        set { if (_draft.Text != value) { _draft.Text = value; Raise(); _edited(); } }
    }

    public IReadOnlyList<LinkChoice> LinkChoices { get; }

    public LinkChoice SelectedLink
    {
        get => _selectedLink;
        set
        {
            if (value is null || value == _selectedLink) return;
            _selectedLink = value;
            _draft.LinkedTableId = value.Kind == LinkChoiceKind.Table ? value.TableId : null;
            _draft.UnresolvedLinkName = value.Kind == LinkChoiceKind.Unresolved ? _unresolvedName : null;
            Raise();
            Raise(nameof(IsUnresolvedLink));
            _edited();
        }
    }

    public bool IsUnresolvedLink => _selectedLink.Kind == LinkChoiceKind.Unresolved;

    public string UnresolvedName
    {
        get => _unresolvedName;
        set
        {
            if (_unresolvedName == value) return;
            _unresolvedName = value;
            if (IsUnresolvedLink) _draft.UnresolvedLinkName = value;
            Raise();
            _edited();
        }
    }

    /// <summary>All notes on this row, one per line, each starting with a symbol for its level (ℹ, ⚠, ✖).</summary>
    public string Notes { get => _notes; private set { if (Set(ref _notes, value)) Raise(nameof(HasNotes)); } }
    public bool HasNotes => _notes.Length > 0;

    // The row is styled by its most serious note: an error beats a warning, which beats information.
    public bool HasError { get => _hasError; private set => Set(ref _hasError, value); }
    public bool HasWarning { get => _hasWarning; private set => Set(ref _hasWarning, value); }
    public bool HasInfo { get => _hasInfo; private set => Set(ref _hasInfo, value); }

    internal void SetNotes(List<(NoteLevel Level, string Message)> notes)
    {
        Notes = string.Join(Environment.NewLine, notes.Select(n => $"{Symbol(n.Level)} {n.Message}"));
        HasError = notes.Any(n => n.Level == NoteLevel.Error);
        HasWarning = !HasError && notes.Any(n => n.Level == NoteLevel.Warning);
        HasInfo = !HasError && !HasWarning && notes.Count > 0;
    }

    private static string Symbol(NoteLevel level) => level switch { NoteLevel.Info => "ℹ", NoteLevel.Warning => "⚠", _ => "✖" };
}

/// <summary>One result set as edited in Review: its own name and its own independently ranged rows.</summary>
public sealed class ResultSetEditorViewModel : ObservableObject
{
    private readonly Action _edited;
    private int _position;
    private bool _hasNotes;

    public ResultSetEditorViewModel(ResultSetDraft draft, int position, Action edited, Action<ResultSetEditorViewModel> addRow)
    {
        Draft = draft;
        _position = position;
        _edited = edited;
        AddRowCommand = new RelayCommand(() => addRow(this));
    }

    internal ResultSetDraft Draft { get; }
    public ObservableCollection<EntryRowViewModel> Rows { get; } = [];
    public ICommand AddRowCommand { get; }

    /// <summary>One-based position among the table's result sets.</summary>
    public int Position { get => _position; internal set { if (Set(ref _position, value)) Raise(nameof(DisplayName)); } }

    public string Name
    {
        get => Draft.Name;
        set { if (Draft.Name != value) { Draft.Name = value; Raise(); Raise(nameof(DisplayName)); _edited(); } }
    }

    /// <summary>Shown on the selector; an unnamed set is identified by its position.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Draft.Name) ? $"Result set {Position}" : Draft.Name.Trim();

    /// <summary>What a screen reader or UI Automation reads for this tab.</summary>
    public override string ToString() => DisplayName;

    /// <summary>True when this set has warnings or errors, so the selector can point at it.</summary>
    public bool HasNotes { get => _hasNotes; internal set => Set(ref _hasNotes, value); }
}

/// <summary>
/// One row of the aligned multi-column view: the range shared by every result set at this row (they are parallel
/// outputs of one roll, so their ranges are kept in step), plus each result set's own cell for that row, in result-set
/// order. Purely a presentation grouping — each cell is still the same <see cref="EntryRowViewModel"/> that the
/// per-set editor would show, wrapping its own independent <see cref="EntryDraft"/>, so nothing about the underlying
/// result sets is merged.
/// </summary>
public sealed class AlignedRowViewModel : ObservableObject
{
    public AlignedRowViewModel(IReadOnlyList<EntryRowViewModel> cells, Action<AlignedRowViewModel> delete)
    {
        Cells = cells;
        DeleteCommand = new RelayCommand(() => delete(this));
    }

    /// <summary>This row's cell in each result set, in the same order as <see cref="ReviewViewModel.ResultSets"/>.</summary>
    public IReadOnlyList<EntryRowViewModel> Cells { get; }

    public int Number => Cells[0].Number;

    /// <summary>Shared across every column: setting it writes the same range to every result set's row, keeping them aligned.</summary>
    public string RangeText
    {
        get => Cells[0].RangeText;
        set { foreach (var cell in Cells) cell.RangeText = value; }
    }

    public ICommand DeleteCommand { get; }
}

/// <summary>
/// The Review screen: original text beside the editable interpretation, with parser issues and
/// validation facts attached to the content they concern. Also used to edit an already saved table.
/// </summary>
public sealed class ReviewViewModel : ObservableObject
{
    private readonly TableImportDraft _draft;
    private readonly Collection _collection;
    private readonly AppDatabase _db;
    private readonly Action<RollableTable> _saved;
    private readonly List<LinkChoice> _linkChoices;
    private bool _canSave;
    private string _saveError = "";
    private EntryRowViewModel? _selectedRow;
    private ResultSetEditorViewModel _selectedResultSet;
    private FolderPickerOption _selectedFolder;
    private bool _isPasteRowsOpen;
    private string _pasteRowsText = "";
    private string _pasteRowsMessage = "";
    private string _cleanupMessage = "";
    private Action? _undoCleanup;
    private bool _isAligned;
    private bool _isClampAvailable;
    private string _clampUnavailableNote = "";
    private string _copyMessage = "";
    private readonly Action<string> _copyText;

    /// <param name="copyText">Puts text on the clipboard (Copy Table Text). Null means the Windows clipboard.</param>
    public ReviewViewModel(TableImportDraft draft, Collection collection, AppDatabase db, Action<RollableTable> saved, Action cancelled,
        Action<string>? copyText = null)
    {
        _draft = draft;
        _copyText = copyText ?? ClipboardText.Set;
        _collection = collection;
        _db = db;
        _saved = saved;
        if (draft.ResultSets.Count == 0) draft.ResultSets.Add(new ResultSetDraft());

        _linkChoices = BuildLinkChoices(draft, db.GetTableSummaries(collection.Id));
        FolderOptions = BuildFolderOptions(db.GetFolders(collection.Id));
        _selectedFolder = FolderOptions.FirstOrDefault(o => o.Id == draft.FolderId) ?? FolderOptions[0];

        // Parser issues are attached to the entry they were raised for, so they stay with it as rows are added or removed.
        var issuesByEntry = new Dictionary<EntryDraft, List<ParseIssue>>();
        foreach (var issue in draft.Issues.Where(i => i.Target == ParseIssueTarget.Entry))
        {
            if (issue.ResultSetIndex is int s && issue.EntryIndex is int e && s < draft.ResultSets.Count && e < draft.ResultSets[s].Entries.Count)
                (issuesByEntry.TryGetValue(draft.ResultSets[s].Entries[e], out var list) ? list : issuesByEntry[draft.ResultSets[s].Entries[e]] = []).Add(issue);
        }

        for (var s = 0; s < draft.ResultSets.Count; s++)
        {
            var editor = NewEditor(draft.ResultSets[s], s + 1);
            for (var i = 0; i < draft.ResultSets[s].Entries.Count; i++)
            {
                var entry = draft.ResultSets[s].Entries[i];
                editor.Rows.Add(NewRow(entry, i + 1, issuesByEntry.GetValueOrDefault(entry) ?? []));
            }
            ResultSets.Add(editor);
        }
        _selectedResultSet = ResultSets[0];

        SaveCommand = new RelayCommand(Save, () => CanSave);
        CancelCommand = new RelayCommand(cancelled);
        AddResultSetCommand = new RelayCommand(AddResultSet);
        DeleteResultSetCommand = new RelayCommand(DeleteSelectedResultSet, () => ResultSets.Count > 1);
        TogglePasteRowsCommand = new RelayCommand(() => { IsPasteRowsOpen = !IsPasteRowsOpen; PasteRowsMessage = ""; });
        AppendPastedRowsCommand = new RelayCommand(() => ApplyPastedRows(replace: false), () => !string.IsNullOrWhiteSpace(PasteRowsText));
        ReplacePastedRowsCommand = new RelayCommand(() => ApplyPastedRows(replace: true), () => !string.IsNullOrWhiteSpace(PasteRowsText));
        JoinWithPreviousRowCommand = new RelayCommand(JoinWithPreviousRow, () => SelectedRow is not null);
        DehyphenateSelectedCommand = new RelayCommand(DehyphenateSelected, () => SelectedRow is not null);
        RemoveEmptyRowsCommand = new RelayCommand(RemoveEmptyRows);
        NormalizeTextCommand = new RelayCommand(NormalizeText);
        UndoCleanupCommand = new RelayCommand(UndoCleanup, () => _undoCleanup is not null);
        AddAlignedRowCommand = new RelayCommand(AddAlignedRow);
        CopyTableTextCommand = new RelayCommand(CopyTableText, () => CanSave);
        Refresh();
    }

    // ---- Copy Table Text -----------------------------------------------------------------------------------------------

    public ICommand CopyTableTextCommand { get; }

    /// <summary>With several result sets a chooser shows which one is copied (the result-set tabs are hidden in the aligned view).</summary>
    public bool ShowCopyResultSetChoice => ResultSets.Count > 1;

    /// <summary>"Table text copied." after a copy; empty otherwise (and again after any edit).</summary>
    public string CopyMessage { get => _copyMessage; private set { if (Set(ref _copyMessage, value)) Raise(nameof(HasCopyMessage)); } }
    public bool HasCopyMessage => CopyMessage.Length > 0;

    /// <summary>
    /// Copies the selected result set as plain text, without saving anything: the table is built exactly as Save would build it
    /// (same rules, so it is only offered when Save is), and that structured table is what gets written out.
    /// </summary>
    private void CopyTableText()
    {
        if (!_draft.TryBuildTable(_collection.Id, out var table, out _)) return;
        var index = Math.Max(0, ResultSets.IndexOf(SelectedResultSet));
        try
        {
            _copyText(TableTextExporter.Export(table!, table!.ResultSets[index]));
            CopyMessage = "Table text copied.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            CopyMessage = $"The table text could not be copied: {ex.Message}";
        }
    }

    public string HeadingTitle => _draft.TableId == 0 ? "Review" : "Edit table";
    public string HeadingDetail => _draft.TableId == 0 ? $" — saving to {_collection.Name}" : $" — in {_collection.Name}";
    public string CollectionName => _collection.Name;

    public string SourceText => _draft.SourceText.Length > 0
        ? _draft.SourceText
        : "(No original text: this table is being edited from its saved copy.)";

    public string TableName
    {
        get => _draft.TableName;
        set { if (_draft.TableName != value) { _draft.TableName = value; Raise(); Refresh(); } }
    }

    public string DiceText
    {
        get => _draft.DiceText;
        set { if (_draft.DiceText != value) { _draft.DiceText = value; Raise(); Refresh(); } }
    }

    /// <summary>Unfiled plus every folder in the table's collection. Only these can ever be chosen: a table can never reference another collection's folder.</summary>
    public IReadOnlyList<FolderPickerOption> FolderOptions { get; }

    public FolderPickerOption SelectedFolder
    {
        get => _selectedFolder;
        set
        {
            if (value is null || !Set(ref _selectedFolder, value)) return;
            _draft.FolderId = value.Id;
        }
    }

    /// <summary>
    /// Clamp out-of-range rolls to table range (see <see cref="TableClamp"/>). Always shows what Save will store: while the table
    /// cannot be clamped (d66, result sets with different ranges) it reads off and cannot be changed. The user's choice is kept
    /// underneath meanwhile, so an edit that briefly breaks the ranges and then restores them does not lose it.
    /// </summary>
    public bool ClampResultsToRange
    {
        get => _draft.ClampResultsToRange && IsClampAvailable;
        set
        {
            if (!IsClampAvailable || _draft.ClampResultsToRange == value) return;
            _draft.ClampResultsToRange = value;
            Raise();
        }
    }

    /// <summary>Whether this table, as currently edited, has one common range to clamp to.</summary>
    public bool IsClampAvailable
    {
        get => _isClampAvailable;
        private set
        {
            if (!Set(ref _isClampAvailable, value)) return;
            Raise(nameof(ClampResultsToRange));
            Raise(nameof(HasClampUnavailableNote));
        }
    }

    /// <summary>Why Clamp cannot be turned on right now, and, if it was on, that saving will turn it off. Empty while it is available.</summary>
    public string ClampUnavailableNote { get => _clampUnavailableNote; private set => Set(ref _clampUnavailableNote, value); }
    public bool HasClampUnavailableNote => !IsClampAvailable;

    public ObservableCollection<ResultSetEditorViewModel> ResultSets { get; } = [];

    public ResultSetEditorViewModel SelectedResultSet
    {
        get => _selectedResultSet;
        set
        {
            if (value is null || !Set(ref _selectedResultSet, value)) return;
            Raise(nameof(Rows));
            CopyMessage = "";
        }
    }

    /// <summary>The selected result set's rows.</summary>
    public ObservableCollection<EntryRowViewModel> Rows => _selectedResultSet.Rows;

    /// <summary>
    /// True when every result set has at least one row, the same number of rows as every other, and, row for row, the
    /// same range — evidence that they are parallel outputs of one shared roll (Difficulty/Modifier, not independent
    /// lists like Ambient/Noise) rather than a coincidence. Recomputed on every <see cref="Refresh"/>, so a table that
    /// starts aligned and is edited out of alignment (or the reverse) switches views automatically and safely: nothing
    /// about the result sets themselves is merged or lost either way.
    /// </summary>
    public bool IsAligned { get => _isAligned; private set { if (Set(ref _isAligned, value)) Raise(nameof(IsNotAligned)); } }

    public bool IsNotAligned => !IsAligned;

    /// <summary>The aligned view of the rows, one entry per row position across every result set. Empty unless <see cref="IsAligned"/>.</summary>
    public ObservableCollection<AlignedRowViewModel> AlignedRows { get; } = [];

    public ICommand AddAlignedRowCommand { get; }

    /// <summary>Parser issues about the table as a whole (heading, dice, unrecognized lines).</summary>
    public ObservableCollection<string> TableNotes { get; } = [];

    /// <summary>Information about how the text was interpreted that needs no correction (table-level; row-level notes sit on their rows).</summary>
    public ObservableCollection<string> InfoNotes { get; } = [];

    /// <summary>Facts from the validator, per result set: gaps, overlaps, out-of-range entries. They do not block saving.</summary>
    public ObservableCollection<string> ValidationNotes { get; } = [];

    /// <summary>Reasons the draft cannot yet form a table.</summary>
    public ObservableCollection<string> Blockers { get; } = [];

    public bool CanSave { get => _canSave; private set => Set(ref _canSave, value); }
    public string SaveError { get => _saveError; private set => Set(ref _saveError, value); }

    /// <summary>
    /// Row the user is working in; the view uses it to show the matching lines of the original text. Selecting a row
    /// also selects the result set that owns it, so the per-set tools (Join, Dehyphenate, Remove Empty Rows, Paste
    /// rows into this set) act on the right column even when a row was reached through the aligned multi-column view
    /// rather than the result-set tabs.
    /// </summary>
    public EntryRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!Set(ref _selectedRow, value)) return;
            if (value is not null && ResultSets.FirstOrDefault(s => s.Rows.Contains(value)) is { } owner)
                SelectedResultSet = owner;
        }
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand AddResultSetCommand { get; }
    public ICommand DeleteResultSetCommand { get; }

    // ---- pasting rows into the selected result set --------------------------------------------

    /// <summary>Whether the inline "paste rows" area is showing. It sits on the Review screen; nothing about it is modal.</summary>
    public bool IsPasteRowsOpen { get => _isPasteRowsOpen; set => Set(ref _isPasteRowsOpen, value); }

    /// <summary>Copied rows waiting to be added to the selected result set.</summary>
    public string PasteRowsText { get => _pasteRowsText; set => Set(ref _pasteRowsText, value); }

    /// <summary>What the last paste did, or why it did nothing.</summary>
    public string PasteRowsMessage { get => _pasteRowsMessage; private set => Set(ref _pasteRowsMessage, value); }

    public ICommand TogglePasteRowsCommand { get; }
    public ICommand AppendPastedRowsCommand { get; }
    public ICommand ReplacePastedRowsCommand { get; }

    // ---- PDF copy/paste cleanup --------------------------------------------------------------

    /// <summary>What the last cleanup action did, or why it did nothing. Shared by every cleanup command below.</summary>
    public string CleanupMessage { get => _cleanupMessage; private set => Set(ref _cleanupMessage, value); }

    /// <summary>True once a cleanup action has something to undo. Only the single most recent one can be undone.</summary>
    public bool CanUndoCleanup => _undoCleanup is not null;

    public ICommand JoinWithPreviousRowCommand { get; }
    public ICommand DehyphenateSelectedCommand { get; }
    public ICommand RemoveEmptyRowsCommand { get; }
    public ICommand NormalizeTextCommand { get; }
    public ICommand UndoCleanupCommand { get; }

    /// <summary>
    /// Ctrl+J. Joins <see cref="SelectedRow"/> into the row above it in the same result set: the usual shape of a PDF
    /// line-wrap that came through as its own row. Refuses (with a message, nothing changed) when there is no previous
    /// row to join into, or when the selected row carries its own range — that is very likely a real row, not a
    /// continuation, and joining it would destroy it.
    /// </summary>
    private void JoinWithPreviousRow()
    {
        var set = SelectedResultSet;
        var row = SelectedRow;
        if (row is null) { CleanupMessage = "Select a continuation row first."; return; }
        if (!set.Rows.Contains(row)) { CleanupMessage = "Select a row in the current result set."; return; }

        var index = set.Rows.IndexOf(row);
        if (index == 0) { CleanupMessage = "This is the first row of the result set: there is no previous row to join into."; return; }
        if (row.RangeText.Trim().Length > 0)
        {
            CleanupMessage = "This row has its own range, so it was not joined. Clear the range first if it is really a continuation.";
            return;
        }

        var previous = set.Rows[index - 1];
        var previousNumber = previous.Number;
        var rowNumber = row.Number;
        var oldPreviousText = previous.Text;
        var draftIndex = set.Draft.Entries.IndexOf(row.Draft);

        previous.Text = TextCleanup.JoinContinuationText(previous.Text, row.Text);
        set.Draft.Entries.RemoveAt(draftIndex);
        set.Rows.RemoveAt(index);
        for (var i = 0; i < set.Rows.Count; i++) set.Rows[i].Number = i + 1;
        SelectedRow = previous;

        SetUndoCleanup(() =>
        {
            previous.Text = oldPreviousText;
            set.Draft.Entries.Insert(draftIndex, row.Draft);
            set.Rows.Insert(index, row);
            for (var i = 0; i < set.Rows.Count; i++) set.Rows[i].Number = i + 1;
            SelectedRow = row;
        });

        CleanupMessage = $"Joined row {rowNumber} into row {previousNumber}.";
        Refresh();
    }

    /// <summary>
    /// Removes a PDF line-wrap hyphen from the selected row's result text ("magnifi- cent" → "magnificent"). Applies
    /// only to the selected row, never globally, so legitimate hyphenated words elsewhere are never at risk.
    /// </summary>
    private void DehyphenateSelected()
    {
        var row = SelectedRow;
        if (row is null) { CleanupMessage = "Select a row to dehyphenate."; return; }
        if (!TextCleanup.TryDehyphenate(row.Text, out var result))
        {
            CleanupMessage = "No line-wrap hyphenation was found in the selected row.";
            return;
        }

        var old = row.Text;
        row.Text = result;
        SetUndoCleanup(() => row.Text = old);
        CleanupMessage = "Removed line-wrap hyphenation from the selected row.";
    }

    /// <summary>Removes rows in the selected result set with no range and no result text (whitespace-only counts as empty).</summary>
    private void RemoveEmptyRows()
    {
        var set = SelectedResultSet;
        var removed = new List<(int Index, EntryDraft Draft, EntryRowViewModel Row)>();
        for (var i = 0; i < set.Rows.Count; i++)
        {
            var row = set.Rows[i];
            if (row.RangeText.Trim().Length == 0 && row.Text.Trim().Length == 0)
                removed.Add((i, row.Draft, row));
        }

        if (removed.Count == 0) { CleanupMessage = "No empty rows were found in this result set."; return; }

        foreach (var (_, draft, row) in removed)
        {
            set.Draft.Entries.Remove(draft);
            set.Rows.Remove(row);
        }
        for (var i = 0; i < set.Rows.Count; i++) set.Rows[i].Number = i + 1;
        if (SelectedRow is not null && removed.Any(r => r.Row == SelectedRow)) SelectedRow = null;

        SetUndoCleanup(() =>
        {
            foreach (var (index, draft, row) in removed)
            {
                set.Draft.Entries.Insert(Math.Min(index, set.Draft.Entries.Count), draft);
                set.Rows.Insert(Math.Min(index, set.Rows.Count), row);
            }
            for (var i = 0; i < set.Rows.Count; i++) set.Rows[i].Number = i + 1;
        });

        CleanupMessage = $"Removed {removed.Count} empty {(removed.Count == 1 ? "row" : "rows")} from {set.DisplayName}.";
        Refresh();
    }

    /// <summary>
    /// Fixes non-breaking spaces, repeated whitespace and the fi/fl ligatures across the table name, every result set's
    /// name, and every row's range and result text — plus, for range fields only, rewriting the span separator to a
    /// plain hyphen. Purely character-level and conservative: never touches punctuation or wording in result prose,
    /// and never alters dice text embedded in a row ("4D6+5" stays "4D6+5").
    /// </summary>
    private void NormalizeText()
    {
        var undoSteps = new List<Action>();

        void Apply(Func<string> get, Action<string> set, Func<string?, string> normalize)
        {
            var before = get();
            var after = normalize(before);
            if (after == before) return;
            set(after);
            undoSteps.Add(() => set(before));
        }

        Apply(() => TableName, v => TableName = v, TextCleanup.NormalizeGeneralText);
        foreach (var set in ResultSets)
        {
            Apply(() => set.Name, v => set.Name = v, TextCleanup.NormalizeGeneralText);
            foreach (var row in set.Rows)
            {
                Apply(() => row.RangeText, v => row.RangeText = v, TextCleanup.NormalizeRangeText);
                Apply(() => row.Text, v => row.Text = v, TextCleanup.NormalizeGeneralText);
            }
        }

        if (undoSteps.Count == 0) { CleanupMessage = "Nothing needed normalizing."; return; }

        SetUndoCleanup(() => { for (var i = undoSteps.Count - 1; i >= 0; i--) undoSteps[i](); });
        CleanupMessage = $"Normalized {undoSteps.Count} {(undoSteps.Count == 1 ? "field" : "fields")}.";
    }

    private void SetUndoCleanup(Action undo)
    {
        _undoCleanup = undo;
        Raise(nameof(CanUndoCleanup));
    }

    private void UndoCleanup()
    {
        var undo = _undoCleanup;
        if (undo is null) return;
        _undoCleanup = null;
        undo();
        Raise(nameof(CanUndoCleanup));
        CleanupMessage = "Undone.";
        Refresh();
    }

    /// <summary>
    /// Interprets <see cref="PasteRowsText"/> as entry rows only and adds them to the selected result set, after its
    /// existing rows or in place of them. It never changes the table name, the dice, or any other result set.
    /// Text with no usable rows changes nothing; lines that were not understood are reported like import issues.
    /// </summary>
    private void ApplyPastedRows(bool replace)
    {
        DiceExpression? dice = DiceExpression.TryParse(DiceText, out var parsed) ? parsed : null;
        var result = TableTextParser.ParseRows(PasteRowsText, dice);
        var set = SelectedResultSet;

        // Issues that are not about a particular row (unrecognized lines) join the table-level notes.
        _draft.Issues.AddRange(result.Issues.Where(i => i.Target != ParseIssueTarget.Entry && i.Code != ParseIssueCode.NoEntries));

        if (result.Entries.Count == 0)
        {
            PasteRowsMessage = "No numbered rows were found in the pasted text, so nothing was changed.";
            Refresh();
            return;
        }

        if (replace)
        {
            set.Draft.Entries.Clear();
            set.Rows.Clear();
            SelectedRow = null;
        }

        for (var i = 0; i < result.Entries.Count; i++)
        {
            var entry = result.Entries[i];
            var issues = result.Issues.Where(x => x.Target == ParseIssueTarget.Entry && x.EntryIndex == i).ToList();
            set.Draft.Entries.Add(entry);
            set.Rows.Add(NewRow(entry, set.Rows.Count + 1, issues));
        }

        var count = result.Entries.Count;
        PasteRowsMessage = replace
            ? $"Replaced the rows of {set.DisplayName} with {count} pasted {(count == 1 ? "row" : "rows")}."
            : $"Added {count} pasted {(count == 1 ? "row" : "rows")} to {set.DisplayName}.";
        PasteRowsText = "";
        Refresh();
    }

    // ---- editing structure ------------------------------------------------------------------

    private ResultSetEditorViewModel NewEditor(ResultSetDraft draft, int position) =>
        new(draft, position, Refresh, AddRow);

    private EntryRowViewModel NewRow(EntryDraft entry, int number, IReadOnlyList<ParseIssue> issues) =>
        new(entry, number, issues, _linkChoices, Refresh, DeleteRow);

    private void AddResultSet()
    {
        // A new set starts with one row spanning the whole legal range, so it is immediately valid and easy to split.
        // (A d66 has no single row that covers everything without also covering impossible numbers, so it starts with its first tens row.)
        var full = !DiceExpression.TryParse(DiceText, out var dice) ? "1" : dice.IsD66 ? "11-16" : $"{dice.Min}-{dice.Max}";
        var draft = new ResultSetDraft { Entries = [new EntryDraft { RangeText = full }] };
        _draft.ResultSets.Add(draft);

        var editor = NewEditor(draft, ResultSets.Count + 1);
        editor.Rows.Add(NewRow(draft.Entries[0], 1, []));
        ResultSets.Add(editor);
        SelectedResultSet = editor;
        Refresh();
    }

    private void DeleteSelectedResultSet()
    {
        if (ResultSets.Count <= 1) return; // a table always keeps at least one result set

        var index = ResultSets.IndexOf(_selectedResultSet);
        _draft.ResultSets.Remove(_selectedResultSet.Draft);
        ResultSets.RemoveAt(index);
        for (var i = 0; i < ResultSets.Count; i++) ResultSets[i].Position = i + 1;
        SelectedRow = null;
        SelectedResultSet = ResultSets[Math.Min(index, ResultSets.Count - 1)];
        Refresh();
    }

    private void AddRow(ResultSetEditorViewModel set)
    {
        // Suggest the number after the highest one already used.
        DiceExpression? dice = DiceExpression.TryParse(DiceText, out var parsed) ? parsed : null;
        // Only finite bounds count ("26+" suggests 27), so an open-ended row never turns into a sentinel number here.
        var highest = set.Rows
            .Select(r => r.Draft.TryParseRange(dice, out var range, out _) ? (range.IsOpenAbove ? range.Min : range.Max) : 0)
            .DefaultIfEmpty(0).Max();
        var next = highest == 0 && dice is { } d ? d.Min : highest + 1;
        var entry = new EntryDraft { RangeText = next.ToString() };
        set.Draft.Entries.Add(entry);
        set.Rows.Add(NewRow(entry, set.Rows.Count + 1, []));
        Refresh();
    }

    private void DeleteRow(EntryRowViewModel row)
    {
        var set = ResultSets.First(s => s.Rows.Contains(row));
        set.Draft.Entries.Remove(row.Draft);
        set.Rows.Remove(row);
        for (var i = 0; i < set.Rows.Count; i++) set.Rows[i].Number = i + 1;
        if (SelectedRow == row) SelectedRow = null;
        Refresh();
    }

    /// <summary>Adds one row to every result set at once, keeping them aligned. Only offered while <see cref="IsAligned"/>.</summary>
    private void AddAlignedRow()
    {
        foreach (var set in ResultSets) AddRow(set);
    }

    /// <summary>Removes this row's cell from every result set: deleting a row of the aligned view deletes that whole roll.</summary>
    private void DeleteAlignedRow(AlignedRowViewModel row)
    {
        foreach (var cell in row.Cells) DeleteRow(cell);
    }

    /// <summary>None, an unresolved name, every table in the collection, plus a stand-in for any linked table not in that list.</summary>
    private static List<LinkChoice> BuildLinkChoices(TableImportDraft draft, IReadOnlyList<TableSummary> tables)
    {
        var choices = new List<LinkChoice>
        {
            new(LinkChoiceKind.None, null, "(no link)"),
            new(LinkChoiceKind.Unresolved, null, "Unresolved name…"),
        };
        choices.AddRange(tables.Select(t => new LinkChoice(LinkChoiceKind.Table, t.Id, t.Name)));

        foreach (var id in draft.ResultSets.SelectMany(s => s.Entries).Select(e => e.LinkedTableId).OfType<long>().Distinct())
            if (!choices.Any(c => c.Kind == LinkChoiceKind.Table && c.TableId == id))
                choices.Add(new LinkChoice(LinkChoiceKind.Table, id, $"(table #{id})"));
        return choices;
    }

    /// <summary>Unfiled first, then every folder in the collection, alphabetical (as <paramref name="folders"/> already is).</summary>
    private static List<FolderPickerOption> BuildFolderOptions(IReadOnlyList<Folder> folders)
    {
        var options = new List<FolderPickerOption> { new(null, "Unfiled") };
        options.AddRange(folders.Select(f => new FolderPickerOption(f.Id, f.Name)));
        return options;
    }

    // ---- validation and notes ---------------------------------------------------------------

    private void Refresh()
    {
        DiceExpression? dice = DiceExpression.TryParse(DiceText, out var parsed) ? parsed : null;

        // Parser issues about the table as a whole stay visible, but drop ones the user has since fixed.
        TableNotes.Clear();
        InfoNotes.Clear();
        foreach (var issue in _draft.Issues.Where(i => i.Target != ParseIssueTarget.Entry))
        {
            if (issue.Code == ParseIssueCode.NoTableName && !string.IsNullOrWhiteSpace(TableName)) continue;
            if (issue.Code is ParseIssueCode.NoDiceExpression or ParseIssueCode.UnsupportedDice && dice is not null) continue;
            if (issue.Code == ParseIssueCode.NoEntries && ResultSets.Any(s => s.Rows.Count > 0)) continue;
            (issue.Severity == ParseIssueSeverity.Info ? InfoNotes : TableNotes).Add(issue.Message);
        }
        if (TextCleanup.ContainsReplacementCharacter(TableName))
            TableNotes.Add("The table name contains a character that could not be copied correctly from the source PDF. Review it before saving.");

        // Per row: the parser's issues for it, plus any range error found now.
        var rowNotes = new List<List<List<(NoteLevel Level, string Message)>>>();
        var parsedRows = new List<List<(int RowIndex, ParsedRange Range)>>();
        foreach (var set in ResultSets)
        {
            var notes = new List<List<(NoteLevel Level, string Message)>>();
            var parsedInSet = new List<(int, ParsedRange)>();
            for (var i = 0; i < set.Rows.Count; i++)
            {
                var row = set.Rows[i];
                var list = new List<(NoteLevel Level, string Message)>();
                foreach (var issue in row.Issues)
                {
                    if (issue.Code == ParseIssueCode.EmptyEntryText && row.Text.Trim().Length > 0) continue;
                    list.Add((issue.Severity switch
                    {
                        ParseIssueSeverity.Info => NoteLevel.Info,
                        ParseIssueSeverity.Warning => NoteLevel.Warning,
                        _ => NoteLevel.Error,
                    }, issue.Message));
                }
                if (row.Draft.TryParseRange(dice, out var range, out var error))
                    parsedInSet.Add((i, range));
                else
                    list.Add((NoteLevel.Error, error!));

                // The source PDF's font mapping was already broken before TableForge saw the clipboard; guessing the
                // missing character would just be a different wrong answer, so this only ever flags it for review.
                if (TextCleanup.ContainsReplacementCharacter(row.Text) || TextCleanup.ContainsReplacementCharacter(row.RangeText))
                    list.Add((NoteLevel.Warning, "This text contains a character that could not be copied correctly from the source PDF. Review it before saving."));

                notes.Add(list);
            }
            rowNotes.Add(notes);
            parsedRows.Add(parsedInSet);
        }

        // Validation runs once over the whole table but reports per result set, so sets never affect one another.
        ValidationNotes.Clear();
        var setHasFinding = new bool[ResultSets.Count];
        string? clampReason = "Clamp needs a supported dice expression.";
        if (dice is { } d)
        {
            var table = new RollableTable
            {
                Dice = d,
                ResultSets = parsedRows.Select(rows => new ResultSet
                {
                    Entries = rows.Select(p => new TableEntry { Min = p.Range.Min, Max = p.Range.Max }).ToList(),
                }).ToList(),
            };
            TableClamp.TryGetRange(table, out _, out _, out clampReason);

            foreach (var f in TableValidator.Validate(table))
            {
                var s = f.ResultSetIndex;
                var rows = f.EntryIndexes.Select(e => parsedRows[s][e].RowIndex).ToList();
                var span = RangeBounds.Label(f.Start, f.End, d);
                if (f.IsAuthoredExtension)
                {
                    // Rows beyond the dice in a set that covers every roll of them: authored for modified rolls, so information only.
                    var natural = $"{d.FormatValue(d.Min)}–{d.FormatValue(d.Max)}";
                    var info = $"Row {rows[0] + 1} includes {span}, outside the natural {d} range ({natural}); only a modified roll reaches it.";
                    InfoNotes.Add(ResultSets.Count > 1 ? $"{ResultSets[s].DisplayName}: {info}" : info);
                    foreach (var r in rows) rowNotes[s][r].Add((NoteLevel.Info, info));
                    continue;
                }
                var message = f.Kind switch
                {
                    ValidationKind.Gap => $"No row covers {span}.",
                    ValidationKind.Overlap => $"Rows {rows[0] + 1} and {rows[1] + 1} both cover {span}.",
                    ValidationKind.BelowMinimum => $"Row {rows[0] + 1} includes {span}, below the lowest roll ({d.FormatValue(d.Min)}).",
                    ValidationKind.ImpossibleValue => f.Start == f.End
                        ? $"Row {rows[0] + 1}: {span} is not a possible d66 result (each digit must be 1 to 6)."
                        : $"Row {rows[0] + 1}: {span} are not possible d66 results (each digit must be 1 to 6).",
                    _ => $"Row {rows[0] + 1} includes {span}, above the highest roll ({d.FormatValue(d.Max)}).",
                };
                ValidationNotes.Add(ResultSets.Count > 1 ? $"{ResultSets[s].DisplayName}: {message}" : message);
                foreach (var r in rows) rowNotes[s][r].Add((NoteLevel.Warning, message));
                setHasFinding[s] = true;
            }
        }

        for (var s = 0; s < ResultSets.Count; s++)
        {
            for (var i = 0; i < ResultSets[s].Rows.Count; i++) ResultSets[s].Rows[i].SetNotes(rowNotes[s][i]);
            // The tab's ⚠ means "look here"; information alone does not earn it.
            ResultSets[s].HasNotes = setHasFinding[s] || rowNotes[s].Any(n => n.Any(x => x.Level != NoteLevel.Info));
        }

        // Gaps and overlaps never matter here: clamp only needs one common outer range. The note is set before availability
        // flips, so the checkbox and its explanation change together.
        ClampUnavailableNote = clampReason is null ? ""
            : _draft.ClampResultsToRange ? $"{clampReason} Clamp will be off when this table is saved." : clampReason;
        IsClampAvailable = clampReason is null;

        CopyMessage = ""; // a copy describes the table as it was; after an edit it no longer does
        Raise(nameof(ShowCopyResultSetChoice));

        Blockers.Clear();
        CanSave = _draft.TryBuildTable(_collection.Id, out _, out var errors);
        foreach (var e in errors) Blockers.Add(e);
        SaveError = "";

        IsAligned = ComputeIsAligned();
        AlignedRows.Clear();
        if (IsAligned)
        {
            var count = ResultSets[0].Rows.Count;
            for (var i = 0; i < count; i++)
                AlignedRows.Add(new AlignedRowViewModel(ResultSets.Select(s => s.Rows[i]).ToList(), DeleteAlignedRow));
        }

        CommandManager.InvalidateRequerySuggested(); // e.g. whether the last result set can still be deleted
    }

    /// <summary>See <see cref="ReviewViewModel.IsAligned"/>: two or more result sets, all with the same non-zero row
    /// count, and the same range text at every row position.</summary>
    private bool ComputeIsAligned()
    {
        if (ResultSets.Count < 2) return false;
        var count = ResultSets[0].Rows.Count;
        if (count == 0) return false;
        if (ResultSets.Any(s => s.Rows.Count != count)) return false;
        for (var i = 0; i < count; i++)
        {
            var range = ResultSets[0].Rows[i].RangeText;
            if (ResultSets.Any(s => s.Rows[i].RangeText != range)) return false;
        }
        return true;
    }

    private void Save()
    {
        if (!_draft.TryBuildTable(_collection.Id, out var table, out _)) return;

        RollableTable stored;
        try
        {
            _db.SaveTable(table!); // one transaction: it either saves completely or not at all
            // Reload so what the user rolls next is exactly what was stored.
            stored = _db.LoadTable(table!.Id)!;
        }
        catch (Exception ex)
        {
            // Nothing was lost: the draft is intact on this screen, so the user can fix the cause and save again.
            SaveError = $"Could not save: {ex.Message} Your edits are still here.";
            return;
        }

        // Outside the try: from here the table is saved, so a problem afterwards must not be reported as a failed save.
        _saved(stored);
    }
}
