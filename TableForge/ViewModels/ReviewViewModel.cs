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
    private bool _isPasteRowsOpen;
    private string _pasteRowsText = "";
    private string _pasteRowsMessage = "";

    public ReviewViewModel(TableImportDraft draft, Collection collection, AppDatabase db, Action<RollableTable> saved, Action cancelled)
    {
        _draft = draft;
        _collection = collection;
        _db = db;
        _saved = saved;
        if (draft.ResultSets.Count == 0) draft.ResultSets.Add(new ResultSetDraft());

        _linkChoices = BuildLinkChoices(draft, db.GetTableSummaries(collection.Id));

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
        Refresh();
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

    public ObservableCollection<ResultSetEditorViewModel> ResultSets { get; } = [];

    public ResultSetEditorViewModel SelectedResultSet
    {
        get => _selectedResultSet;
        set
        {
            if (value is null || !Set(ref _selectedResultSet, value)) return;
            Raise(nameof(Rows));
        }
    }

    /// <summary>The selected result set's rows.</summary>
    public ObservableCollection<EntryRowViewModel> Rows => _selectedResultSet.Rows;

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

    /// <summary>Row the user is working in; the view uses it to show the matching lines of the original text.</summary>
    public EntryRowViewModel? SelectedRow { get => _selectedRow; set => Set(ref _selectedRow, value); }

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
        var full = DiceExpression.TryParse(DiceText, out var dice) ? $"{dice.Min}-{dice.Max}" : "1";
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
        var highest = set.Rows.Select(r => r.Draft.TryParseRange(dice, out var range, out _) ? range.Max : 0).DefaultIfEmpty(0).Max();
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
                notes.Add(list);
            }
            rowNotes.Add(notes);
            parsedRows.Add(parsedInSet);
        }

        // Validation runs once over the whole table but reports per result set, so sets never affect one another.
        ValidationNotes.Clear();
        var setHasFinding = new bool[ResultSets.Count];
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

            foreach (var f in TableValidator.Validate(table))
            {
                var s = f.ResultSetIndex;
                var rows = f.EntryIndexes.Select(e => parsedRows[s][e].RowIndex).ToList();
                var span = f.Start == f.End ? d.FormatValue(f.Start) : $"{d.FormatValue(f.Start)}–{d.FormatValue(f.End)}";
                var message = f.Kind switch
                {
                    ValidationKind.Gap => $"No row covers {span}.",
                    ValidationKind.Overlap => $"Rows {rows[0] + 1} and {rows[1] + 1} both cover {span}.",
                    ValidationKind.BelowMinimum => $"Row {rows[0] + 1} includes {span}, below the lowest roll ({d.FormatValue(d.Min)}).",
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

        Blockers.Clear();
        CanSave = _draft.TryBuildTable(_collection.Id, out _, out var errors);
        foreach (var e in errors) Blockers.Add(e);
        SaveError = "";
        CommandManager.InvalidateRequerySuggested(); // e.g. whether the last result set can still be deleted
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
