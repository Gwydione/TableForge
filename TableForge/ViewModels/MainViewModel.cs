using System.Collections.ObjectModel;
using System.Windows.Input;
using TableForge.Data;
using TableForge.Dice;
using TableForge.Domain;
using TableForge.Import;

namespace TableForge.ViewModels;

/// <summary>One Recent Rolls entry: a stored snapshot, shown compactly. It can reopen its table, but never restores any state.</summary>
public sealed class RecentRollViewModel(RollHistoryItem item)
{
    public RollHistoryItem Item { get; } = item;
    public string TableName => Item.TableName;
    /// <summary>
    /// The final roll, with the situational modifier it used when there was one ("14 (+3)"), and the value it was looked up
    /// with when it was clamped to the table's range ("8 (+2) → 6 (clamped)").
    /// </summary>
    public string RollDisplay =>
        (Item.SituationalModifier == 0 ? Item.RollDisplay : $"{Item.RollDisplay} ({SituationalModifier.Signed(Item.SituationalModifier)})")
        + (Item.ClampedValue is { } clamped ? $" → {clamped} (clamped)" : "");

    /// <summary>The table was deleted after this roll: the snapshot stays readable but cannot be opened.</summary>
    public bool IsDeleted => Item.TableId is null;

    /// <summary>Time and a one-line result, e.g. "14:32 · Smell of burning flesh · Hissing".</summary>
    public string Detail => $"{Item.RolledUtc.ToLocalTime():HH:mm} · {Item.ResultText.Replace("\n", " · ")}";

    /// <summary>The full snapshot, for the tooltip.</summary>
    public string FullText => IsDeleted ? Item.FullText + "\n\n(This table has been deleted.)" : Item.FullText;
}

/// <summary>
/// One entry in the folder navigation list: "All Tables", "Unfiled", or a real folder. The two sentinels are
/// singletons so re-selecting them after a reload is a simple reference match.
/// </summary>
public sealed class FolderNavItem
{
    public static readonly FolderNavItem AllTables = new(null, "All Tables", isAllTables: true);
    public static readonly FolderNavItem Unfiled = new(null, "Unfiled", isAllTables: false);

    public FolderNavItem(long folderId, string name) : this((long?)folderId, name, false) { }

    private FolderNavItem(long? folderId, string name, bool isAllTables)
    {
        FolderId = folderId;
        Name = name;
        IsAllTables = isAllTables;
    }

    /// <summary>Null for both All Tables and Unfiled; a real folder's id otherwise.</summary>
    public long? FolderId { get; }
    public string Name { get; }
    public bool IsAllTables { get; }

    public override string ToString() => Name;
}

/// <summary>Shell: pick a collection, find or recall a table, paste/edit/delete tables, roll them, and glance at recent rolls.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly AppDatabase _db;
    private readonly IDiceProvider _dice;
    private readonly Func<string, bool> _confirm;
    private readonly Action<string>? _copyText;
    private readonly Func<SaveFileRequest, string?>? _chooseSaveFile;
    private readonly Action<string>? _openFolder;
    private readonly Action? _showAbout;
    private List<TableSummary> _allTables = [];
    private Collection? _selectedCollection;
    private TableSummary? _selectedTable;
    private TableSummary? _highlightedTable;
    private FolderNavItem? _selectedFolderNav;
    private string _newCollectionName = "";
    private string _newFolderName = "";
    private string _renameFolderName = "";
    private string _tableFilter = "";
    private string _status = "";
    private object? _current;
    private bool _suppressOpen;

    /// <param name="confirm">Asks the user to confirm a destructive action; declines by default.</param>
    /// <param name="copyText">Puts text on the clipboard (Copy Table Text). Null means the Windows clipboard.</param>
    /// <param name="dataFolder">The folder holding the database and settings, for Open Data Folder. Null hides the command.</param>
    /// <param name="openFolder">Shows a folder in File Explorer.</param>
    /// <param name="showAbout">Shows About TableForge. Null hides the command.</param>
    /// <param name="chooseSaveFile">Asks where to save an exported file (Save Foundry JSON…, Save Tables+ JSON…). Null means the Windows Save dialog.</param>
    public MainViewModel(AppDatabase db, IDiceProvider dice, Func<string, bool>? confirm = null, Action<string>? copyText = null,
        string? dataFolder = null, Action<string>? openFolder = null, Action? showAbout = null, Func<SaveFileRequest, string?>? chooseSaveFile = null)
    {
        _db = db;
        _dice = dice;
        _confirm = confirm ?? (_ => false);
        _copyText = copyText;
        _chooseSaveFile = chooseSaveFile;
        DataFolder = dataFolder;
        _openFolder = openFolder;
        _showAbout = showAbout;
        AboutCommand = new RelayCommand(() => _showAbout?.Invoke(), () => _showAbout is not null);
        OpenDataFolderCommand = new RelayCommand(() => Try("open the data folder", () => _openFolder!(DataFolder!)),
            () => DataFolder is not null && _openFolder is not null);

        // Every command that touches the database reports a failure in the status bar instead of throwing into WPF.
        CreateCollectionCommand = new RelayCommand(() => Try("create the collection", CreateCollection), () => !string.IsNullOrWhiteSpace(NewCollectionName));
        NewFolderCommand = new RelayCommand(() => Try("create the folder", CreateFolder), () => SelectedCollection is not null && !string.IsNullOrWhiteSpace(NewFolderName));
        RenameFolderCommand = new RelayCommand(() => Try("rename the folder", RenameFolder), () => IsRealFolderSelected && !string.IsNullOrWhiteSpace(RenameFolderName));
        DeleteFolderCommand = new RelayCommand(() => Try("delete the folder", DeleteFolder), () => IsRealFolderSelected);
        PasteTableCommand = new RelayCommand(StartPaste, () => SelectedCollection is not null);
        OpenTableCommand = new RelayCommand(() => Try("open the table", OpenSelectedTable), () => Target is not null);
        EditTableCommand = new RelayCommand(() => Try("open the table for editing", EditSelectedTable), () => Target is not null);
        DeleteTableCommand = new RelayCommand(() => Try("delete the table", DeleteSelectedTable), () => Target is not null);
        OpenRecentTableCommand = new RelayCommand<TableSummary>(t => Try("open the table", () => OpenTable(t.Id)));
        OpenRecentRollCommand = new RelayCommand<RecentRollViewModel>(r => Try("open the table", () => OpenTable(r.Item.TableId!.Value)), r => !r.IsDeleted);
        OpenFirstMatchCommand = new RelayCommand(() => Try("open the table", OpenFirstMatch));
        ClearFilterCommand = new RelayCommand(() => TableFilter = "");

        foreach (var c in db.GetCollections()) Collections.Add(c);
        SelectedCollection = Collections.FirstOrDefault();
        ReloadRecentRolls();
        Status = Collections.Count == 0 ? "Create a collection to begin." : "";
    }

    /// <summary>Raised after a collection is created, so the view can move focus on to the natural next step.</summary>
    public event EventHandler? CollectionCreated;

    /// <summary>
    /// What the empty right-hand area says. It adapts to how far along the person is: no collection yet, a collection with no
    /// tables, or tables to choose from. It is guidance, not an onboarding flow.
    /// </summary>
    public string EmptyStateText =>
        Collections.Count == 0
            ? "Welcome to TableForge.\n\nStart by naming a collection (for example a game or campaign) in the box at the top left, then press Enter. " +
              "After that, choose “Paste Table…” and paste a rollable table copied from a PDF or book."
            : _allTables.Count == 0
                ? "This collection has no tables yet.\n\nChoose “Paste Table…” and paste a rollable table copied from a PDF or book."
                : "Choose a table on the left to roll it, or “Paste Table…” to add another.";

    /// <summary>Runs work that may hit the database; a failure becomes a status message and the app carries on.</summary>
    private bool Try(string action, Action work)
    {
        try
        {
            work();
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Status = $"Could not {action}: {ex.Message}";
            return false;
        }
    }

    private void OpenFirstMatch()
    {
        var target = Target ?? Tables.FirstOrDefault();
        if (target is null) Status = TableFilter.Trim().Length > 0 ? "No table matches that search." : "There are no tables to open.";
        else OpenTable(target.Id);
    }

    public ObservableCollection<Collection> Collections { get; } = [];

    /// <summary>"All Tables", "Unfiled", then the selected collection's folders, alphabetical. Rebuilt whenever the collection or its folders change.</summary>
    public ObservableCollection<FolderNavItem> FolderNav { get; } = [];

    /// <summary>The selected collection's tables that match <see cref="TableFilter"/>, and (when no search is active) <see cref="SelectedFolderNav"/>.</summary>
    public ObservableCollection<TableSummary> Tables { get; } = [];

    /// <summary>The selected collection's most recently used tables, newest first. Not affected by the filter.</summary>
    public ObservableCollection<TableSummary> RecentTables { get; } = [];

    /// <summary>The last rolls made in any collection, newest first.</summary>
    public ObservableCollection<RecentRollViewModel> RecentRolls { get; } = [];

    public bool HasRecentTables => RecentTables.Count > 0;
    public bool HasRecentRolls => RecentRolls.Count > 0;

    public Collection? SelectedCollection
    {
        get => _selectedCollection;
        set { if (Set(ref _selectedCollection, value)) { ReloadFolders(); ReloadTables(); } }
    }

    /// <summary>
    /// The active scope in <see cref="FolderNav"/>: All Tables, Unfiled, or a specific folder. Ignored while
    /// <see cref="TableFilter"/> has text — search always looks across the whole collection.
    /// </summary>
    public FolderNavItem? SelectedFolderNav
    {
        get => _selectedFolderNav;
        set
        {
            if (!Set(ref _selectedFolderNav, value)) return;
            RenameFolderName = value is { IsAllTables: false, FolderId: not null } ? value.Name : "";
            Raise(nameof(IsRealFolderSelected));
            ApplyFilter();
        }
    }

    /// <summary>True when a real folder (not All Tables or Unfiled) is selected: what Rename and Delete act on.</summary>
    public bool IsRealFolderSelected => SelectedFolderNav is { IsAllTables: false, FolderId: not null };

    /// <summary>
    /// Whether each row in <see cref="Tables"/> should show its folder: while searching (results span every
    /// folder) or while viewing All Tables. Redundant while a specific folder or Unfiled is already the whole view.
    /// </summary>
    public bool ShowFolderInList => TableFilter.Trim().Length > 0 || (SelectedFolderNav?.IsAllTables ?? true);

    /// <summary>
    /// Heading over <see cref="Tables"/>, naming what the list actually holds: "Search results" while searching (search
    /// ignores the folder scope), otherwise "All tables", "Unfiled tables" or "&lt;folder&gt; tables".
    /// </summary>
    public string TableListHeading =>
        TableFilter.Trim().Length > 0 ? "Search results"
        : SelectedFolderNav is null or { IsAllTables: true } ? "All tables"
        : $"{SelectedFolderNav.Name} tables";

    /// <summary>The table shown for rolling. Setting it opens that table.</summary>
    public TableSummary? SelectedTable
    {
        get => _selectedTable;
        set
        {
            if (!Set(ref _selectedTable, value)) return;
            if (!ReferenceEquals(_highlightedTable, value))
            {
                _highlightedTable = value;
                Raise(nameof(HighlightedTable));
            }
            if (value is not null && !_suppressOpen) OpenSelectedTable();
        }
    }

    /// <summary>
    /// The row the list cursor is on. Arrowing through the list only highlights, so keyboard users can move freely;
    /// Enter or a click opens it (<see cref="OpenTableCommand"/>). Edit and Delete act on this row.
    /// </summary>
    public TableSummary? HighlightedTable
    {
        get => _highlightedTable;
        set => Set(ref _highlightedTable, value);
    }

    /// <summary>What Edit, Delete and Enter act on: the highlighted row, or else the table currently open.</summary>
    private TableSummary? Target => HighlightedTable ?? SelectedTable;

    public string NewCollectionName { get => _newCollectionName; set => Set(ref _newCollectionName, value); }

    /// <summary>Name typed for a new folder in the selected collection.</summary>
    public string NewFolderName { get => _newFolderName; set => Set(ref _newFolderName, value); }

    /// <summary>Name typed to rename <see cref="SelectedFolderNav"/>; refilled with its current name whenever a real folder becomes selected.</summary>
    public string RenameFolderName { get => _renameFolderName; set => Set(ref _renameFolderName, value); }

    public string TableFilter
    {
        get => _tableFilter;
        set { if (Set(ref _tableFilter, value)) ApplyFilter(); }
    }

    public string Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>The workflow screen currently shown (Paste, Review or Roll view model), or null.</summary>
    public object? Current
    {
        get => _current;
        private set
        {
            var old = _current;
            if (Set(ref _current, value) && old is RollViewModel leaving) leaving.CancelRoll(); // a dice roll still in the air belongs to the screen being left
        }
    }

    /// <summary>The Built-in / dddice choice, when the dice provider is a <see cref="DiceProviderViewModel"/> (it is in the app; tests may use a plain provider).</summary>
    public DiceProviderViewModel? DiceProviders => _dice as DiceProviderViewModel;

    public ICommand CreateCollectionCommand { get; }

    /// <summary>The folder TableForge keeps its data in (%LOCALAPPDATA%\TableForge, or TABLEFORGE_DATA_DIR); null when not known.</summary>
    public string? DataFolder { get; }
    public bool HasDataFolder => DataFolder is not null && _openFolder is not null;

    /// <summary>Opens <see cref="DataFolder"/> in File Explorer.</summary>
    public ICommand OpenDataFolderCommand { get; }

    /// <summary>Shows About TableForge.</summary>
    public ICommand AboutCommand { get; }
    public bool HasAbout => _showAbout is not null;
    public ICommand NewFolderCommand { get; }
    public ICommand RenameFolderCommand { get; }
    public ICommand DeleteFolderCommand { get; }
    public ICommand PasteTableCommand { get; }
    public ICommand OpenTableCommand { get; }
    public ICommand EditTableCommand { get; }
    public ICommand DeleteTableCommand { get; }
    public ICommand OpenRecentTableCommand { get; }
    public ICommand OpenRecentRollCommand { get; }

    /// <summary>Opens the selected table, or the first one matching the search: Enter in the search box.</summary>
    public ICommand OpenFirstMatchCommand { get; }

    /// <summary>Clears the search: Escape in the search box.</summary>
    public ICommand ClearFilterCommand { get; }

    private void CreateCollection()
    {
        var collection = _db.CreateCollection(NewCollectionName);
        Collections.Add(collection);
        NewCollectionName = "";
        SelectedCollection = collection;
        Raise(nameof(EmptyStateText));
        Status = $"Created collection \"{collection.Name}\".";
        CollectionCreated?.Invoke(this, EventArgs.Empty);
    }

    private void ReloadTables()
    {
        _allTables = SelectedCollection is null ? [] : _db.GetTableSummaries(SelectedCollection.Id).ToList();
        SelectWithoutOpening(null);
        ApplyFilter();
        ReloadRecentTables();
        Raise(nameof(EmptyStateText));
    }

    /// <summary>
    /// Rebuilds <see cref="Tables"/> from <see cref="_allTables"/>: scoped to <see cref="SelectedFolderNav"/> when
    /// there is no search text, or across the whole collection (ignoring the folder scope) once there is — search
    /// never becomes folder-local.
    /// </summary>
    private void ApplyFilter()
    {
        var keepId = SelectedTable?.Id;
        var filter = TableFilter.Trim();
        var scoped = filter.Length > 0 || SelectedFolderNav is null || SelectedFolderNav.IsAllTables
            ? _allTables
            : _allTables.Where(t => t.FolderId == SelectedFolderNav.FolderId);
        Tables.Clear();
        foreach (var t in scoped.Where(t => filter.Length == 0 || t.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)))
            Tables.Add(t);
        SelectWithoutOpening(Tables.FirstOrDefault(t => t.Id == keepId));
        Raise(nameof(ShowFolderInList));
        Raise(nameof(TableListHeading));
    }

    /// <summary>
    /// Rebuilds <see cref="FolderNav"/> for the selected collection. Tries to keep the same kind of selection
    /// (All Tables stays All Tables, Unfiled stays Unfiled, a folder that still exists stays selected); a folder
    /// that no longer exists (just deleted, or a different collection was selected) falls back to All Tables.
    /// </summary>
    private void ReloadFolders()
    {
        var previous = _selectedFolderNav;
        FolderNav.Clear();
        FolderNav.Add(FolderNavItem.AllTables);
        FolderNav.Add(FolderNavItem.Unfiled);
        if (SelectedCollection is not null)
            foreach (var f in _db.GetFolders(SelectedCollection.Id))
                FolderNav.Add(new FolderNavItem(f.Id, f.Name));

        SelectedFolderNav = previous switch
        {
            null or { IsAllTables: true } => FolderNav[0],
            { FolderId: null } => FolderNav[1], // Unfiled
            { FolderId: long id } => FolderNav.FirstOrDefault(f => !f.IsAllTables && f.FolderId == id) ?? FolderNav[0],
        };
    }

    /// <summary>After a folder is created, renamed or deleted: refresh the navigation list and the table list together.</summary>
    private void ReloadFoldersAndTables()
    {
        ReloadFolders();
        _allTables = SelectedCollection is null ? [] : _db.GetTableSummaries(SelectedCollection.Id).ToList();
        ApplyFilter();
    }

    private void CreateFolder()
    {
        var folder = _db.CreateFolder(SelectedCollection!.Id, NewFolderName);
        NewFolderName = "";
        ReloadFoldersAndTables();
        SelectedFolderNav = FolderNav.First(f => !f.IsAllTables && f.FolderId == folder.Id);
        Status = $"Created folder \"{folder.Name}\".";
    }

    private void RenameFolder()
    {
        var target = SelectedFolderNav!;
        _db.RenameFolder(target.FolderId!.Value, RenameFolderName);
        var renamedId = target.FolderId.Value;
        ReloadFoldersAndTables();
        SelectedFolderNav = FolderNav.First(f => !f.IsAllTables && f.FolderId == renamedId);
        Status = $"Renamed folder to \"{SelectedFolderNav.Name}\".";
    }

    private void DeleteFolder()
    {
        var target = SelectedFolderNav!;
        var count = _allTables.Count(t => t.FolderId == target.FolderId);
        var message = $"Delete \"{target.Name}\"?" +
            (count == 0 ? "\n\nIt has no tables." : $"\n\nIts {count} {(count == 1 ? "table" : "tables")} will move to Unfiled.");
        if (!_confirm(message)) return;

        _db.DeleteFolder(target.FolderId!.Value);
        ReloadFoldersAndTables(); // the deleted folder is gone from FolderNav, so this settles on All Tables
        Status = $"Deleted folder \"{target.Name}\".";
    }

    private void ReloadRecentTables()
    {
        RecentTables.Clear();
        if (SelectedCollection is not null)
            foreach (var t in _db.GetRecentTables(SelectedCollection.Id)) RecentTables.Add(t);
        Raise(nameof(HasRecentTables));
    }

    private void ReloadRecentRolls()
    {
        RecentRolls.Clear();
        foreach (var item in _db.GetRollHistory()) RecentRolls.Add(new RecentRollViewModel(item));
        Raise(nameof(HasRecentRolls));
    }

    private void SelectWithoutOpening(TableSummary? table)
    {
        _suppressOpen = true;
        try
        {
            SelectedTable = table;
            HighlightedTable = table;
        }
        finally { _suppressOpen = false; }
    }

    private void StartPaste()
    {
        var collection = SelectedCollection!;
        var defaultFolderId = PasteDefaultFolderId;
        SelectWithoutOpening(null);
        Current = new PasteViewModel(collection, StartReview, () => Current = null);
        Status = "";

        void StartReview(TableImportDraft draft)
        {
            draft.FolderId = defaultFolderId;
            Current = new ReviewViewModel(draft, collection, _db, OnSaved, () => Current = null, _copyText);
        }
    }

    /// <summary>
    /// Where a newly pasted table lands: the folder being viewed, if one specifically is (and no search is
    /// narrowing the list) — otherwise Unfiled. The user can still change it on the Review screen before saving.
    /// </summary>
    private long? PasteDefaultFolderId =>
        TableFilter.Trim().Length == 0 && SelectedFolderNav is { IsAllTables: false, FolderId: long id } ? id : null;

    private void EditSelectedTable()
    {
        var summary = Target!;
        var table = _db.LoadTable(summary.Id);
        if (table is null) { TableGone(); return; }

        Current = new ReviewViewModel(TableImportDraft.FromTable(table), SelectedCollection!, _db, OnSaved, OpenSelectedTable, _copyText);
        Status = "";
    }

    /// <summary>The table is already safely saved when this runs; a failure to refresh the lists must not look like a failed save.</summary>
    private void OnSaved(RollableTable table)
    {
        Current = NewRollSession(table);
        if (Try("refresh the table lists", () =>
            {
                _allTables = _db.GetTableSummaries(table.CollectionId).ToList();
                ApplyFilter();
                SelectWithoutOpening(Tables.FirstOrDefault(t => t.Id == table.Id));
                ReloadRecentTables(); // a rename shows here under its new name
                Raise(nameof(EmptyStateText));
            }))
            Status = $"Saved \"{table.Name}\".";
    }

    private void OpenSelectedTable()
    {
        if (Target is { } target) OpenTable(target.Id);
    }

    /// <summary>
    /// Opens a table for rolling from the list, Recent Tables or Recent Rolls. Opening (not merely listing) makes it recent.
    /// A table in another collection switches to that collection first. This starts a fresh trail: no old state is restored.
    /// </summary>
    private void OpenTable(long id)
    {
        var table = _db.LoadTable(id);
        if (table is null) { TableGone(); return; }

        if (SelectedCollection?.Id != table.CollectionId)
            SelectedCollection = Collections.First(c => c.Id == table.CollectionId);

        SelectWithoutOpening(Tables.FirstOrDefault(t => t.Id == id));
        Current = NewRollSession(table);
        Status = "";
    }

    private void DeleteSelectedTable()
    {
        var table = Target!;
        var links = _db.CountLinksTo(table.Id);
        var message = $"Delete the table \"{table.Name}\"?";
        if (links > 0)
            message += $"\n\n{links} {(links == 1 ? "entry" : "entries")} in other tables link to it. They will keep the name \"{table.Name}\" as an unresolved link.";
        message += "\n\nIts recent rolls stay in Recent Rolls as readable records.";
        if (!_confirm(message)) return;

        _db.DeleteTable(table.Id);
        Current = null;
        ReloadTables();
        ReloadRecentRolls(); // those entries can no longer be opened
        Status = $"Deleted \"{table.Name}\".";
    }

    private void TableGone()
    {
        Current = null;
        ReloadTables();
        ReloadRecentRolls();
        Status = "That table no longer exists.";
    }

    private RollViewModel NewRollSession(RollableTable table)
    {
        MarkUsed(table.Id);
        return new RollViewModel(table, _dice, _db.LoadTable, MarkUsed, RecordRoll,
            diceReady: DiceProviders is { } providers ? () => providers.CanRoll : null, copyText: _copyText, chooseSaveFile: _chooseSaveFile);
    }

    private void MarkUsed(long tableId)
    {
        try
        {
            _db.MarkTableUsed(tableId);
            ReloadRecentTables();
        }
        catch (Exception ex)
        {
            Status = $"Could not update recent tables: {ex.Message}";
        }
    }

    /// <summary>One history record per actual roll. A failure here must never get in the way of rolling.</summary>
    private void RecordRoll(RollSnapshot snapshot)
    {
        try
        {
            _db.AddRollHistory(snapshot);
            ReloadRecentRolls();
        }
        catch (Exception ex)
        {
            Status = $"Could not record the roll in history: {ex.Message}";
        }
    }
}
