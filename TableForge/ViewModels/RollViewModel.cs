using System.Collections.ObjectModel;
using System.Windows.Input;
using TableForge.Dice;
using TableForge.Domain;
using TableForge.Import;

namespace TableForge.ViewModels;

public sealed class EntryViewModel(TableEntry entry) : ObservableObject
{
    private bool _isMatched;

    public TableEntry Entry { get; } = entry;
    public string RangeLabel => Entry.RangeLabel;
    public string Text => Entry.Text;
    public bool IsMatched { get => _isMatched; set => Set(ref _isMatched, value); }
}

public sealed class ResultSetViewModel(ResultSet set)
{
    public string Name { get; } = set.Name;
    public bool HasName => set.Name.Length > 0;
    public IReadOnlyList<EntryViewModel> Entries { get; } = set.Entries.Select(e => new EntryViewModel(e)).ToList();
}

/// <summary>
/// One row of the aligned multi-column Roll view: the range shared by every result set at this row (they are parallel
/// outputs of one roll, so their ranges are kept in step — see <see cref="RollStepViewModel.IsAligned"/>), plus each
/// result set's own entry for that row, in result-set order. Purely a presentation grouping, mirroring
/// <see cref="AlignedRowViewModel"/> on the Review screen: each cell is still the same <see cref="EntryViewModel"/>
/// the stacked, one-set-at-a-time view would show, so nothing about the underlying result sets is merged.
/// </summary>
public sealed class AlignedEntryRowViewModel : ObservableObject
{
    private bool _isMatched;

    public AlignedEntryRowViewModel(IReadOnlyList<EntryViewModel> cells) => Cells = cells;

    /// <summary>This row's entry in each result set, in the same order as <see cref="RollStepViewModel.ResultSets"/>.</summary>
    public IReadOnlyList<EntryViewModel> Cells { get; }

    public string RangeLabel => Cells[0].RangeLabel;

    /// <summary>True when the latest roll landed on this row. Every cell shares the same range, so they always agree.</summary>
    public bool IsMatched { get => _isMatched; internal set => Set(ref _isMatched, value); }
}

public enum LinkState { None, Resolved, Unresolved, Missing }

/// <summary>
/// One supported dice expression found inside a displayed result's text (see <see cref="InlineDiceDetector"/>), offered as a
/// separate, user-triggered roll. Purely auxiliary: rolling it never touches the result's own text, and it uses whichever
/// <see cref="IDiceProvider"/> the roll screen is already using. Its state is transient — it lives only as long as the
/// <see cref="ResultLineViewModel"/> that found it stays the current, active one (see <see cref="ResultLineViewModel.IsActive"/>).
/// </summary>
public sealed class InlineDiceAction : ObservableObject
{
    private bool _isRolling;

    public InlineDiceAction(DiceExpression expression)
    {
        Expression = expression;
        DisplayExpression = expression.ToString();
    }

    public DiceExpression Expression { get; }

    /// <summary>The canonical form ("d20", "2d6+1", "d66") — never the casing or spelling found in the source text.</summary>
    public string DisplayExpression { get; }

    /// <summary>Every roll made with this action so far, formatted, oldest first. Pressing the button again appends; nothing is replaced.</summary>
    public ObservableCollection<string> Results { get; } = [];
    public bool HasResults => Results.Count > 0;
    public string ResultsText => string.Join(", ", Results);

    /// <summary>True from the moment this specific action's roll is asked for until it has a result (or fails/cancels).</summary>
    public bool IsRolling { get => _isRolling; internal set => Set(ref _isRolling, value); }

    public string RollLabel => HasResults ? "Roll Again" : $"Roll {DisplayExpression}";

    public ICommand? RollCommand { get; internal set; }

    internal void AddResult(int value)
    {
        Results.Add(Expression.FormatValue(value));
        Raise(nameof(HasResults));
        Raise(nameof(ResultsText));
        Raise(nameof(RollLabel));
    }
}

/// <summary>One result set's outcome for a roll, plus what (if anything) its entry links to.</summary>
public sealed class ResultLineViewModel(string heading, string range, string text, bool isProblem) : ObservableObject
{
    private bool _isActive = true;

    public string Heading { get; } = heading;
    public string Range { get; } = range;
    public string Text { get; } = text;
    public bool IsProblem { get; } = isProblem;
    public bool HasHeading => Heading.Length > 0;

    public LinkState Link { get; init; }

    /// <summary>The destination's current name for a resolved link; the intended name for an unresolved one.</summary>
    public string LinkName { get; init; } = "";
    public RollableTable? LinkTarget { get; init; }
    public ICommand? FollowCommand { get; internal set; }

    /// <summary>Supported dice expressions found in <see cref="Text"/> (see <see cref="InlineDiceDetector"/>); empty for a problem line.</summary>
    public IReadOnlyList<InlineDiceAction> InlineActions { get; init; } = [];
    public bool HasInlineActions => InlineActions.Count > 0;

    /// <summary>Only the latest roll on the current table offers its links; once the trail moves on they become plain notes.</summary>
    public bool IsActive
    {
        get => _isActive;
        internal set
        {
            if (!Set(ref _isActive, value)) return;
            Raise(nameof(ShowFollow));
            Raise(nameof(ShowLinkedNote));
            Raise(nameof(ShowInlineActions));
        }
    }

    /// <summary>Inline roll buttons are offered only for the currently active (latest) result, exactly like <see cref="ShowFollow"/>.</summary>
    public bool ShowInlineActions => HasInlineActions && IsActive;

    public bool ShowFollow => Link == LinkState.Resolved && IsActive;
    public bool ShowLinkedNote => Link == LinkState.Resolved && !IsActive;
    public bool ShowUnresolved => Link == LinkState.Unresolved;
    public bool ShowMissing => Link == LinkState.Missing;
    public string FollowLabel => $"Roll {LinkName}";
    public string LinkedNote => $"→ {LinkName}";
    public string UnresolvedNote => LinkName.Length > 0
        ? $"⚠ Unresolved link to “{LinkName}”: no table is linked."
        : "⚠ Unresolved link: no table is linked.";
}

/// <summary>One numeric roll on a step's table, shown once, with every result set's output.</summary>
public sealed record RollOutcomeViewModel(string Display, IReadOnlyList<ResultLineViewModel> Lines);

/// <summary>A table in the linked-roll trail and every roll made on it. Repeat rolls stay in the same step.</summary>
public sealed class RollStepViewModel(RollableTable table, bool isFirst) : ObservableObject
{
    public RollableTable Table { get; } = table;
    public bool IsFirst { get; } = isFirst;
    public string Title => Table.Name;
    public string DiceInfo => Table.Dice.IsD66
        ? "d66 · two d6 read as tens and ones · legal rolls 11–66"
        : $"{Table.Dice} · legal rolls {Table.Dice.FormatValue(Table.Dice.Min)}–{Table.Dice.FormatValue(Table.Dice.Max)}";
    public IReadOnlyList<ResultSetViewModel> ResultSets { get; } = table.ResultSets.Select(s => new ResultSetViewModel(s)).ToList();
    public ObservableCollection<RollOutcomeViewModel> Outcomes { get; } = [];
    public bool HasOutcomes => Outcomes.Count > 0;

    /// <summary>
    /// True when this table's result sets are parallel outputs of one shared roll — two or more result sets, all with
    /// the same non-zero entry count, and the same numeric range at every row position — the same evidence
    /// <see cref="ReviewViewModel.IsAligned"/> uses on the Review screen. Computed once: a saved table's structure
    /// does not change while rolling it.
    /// </summary>
    public bool IsAligned { get; } = ComputeIsAligned(table.ResultSets);

    /// <summary>The aligned view of the entries, one row per position across every result set. Empty unless <see cref="IsAligned"/>.</summary>
    public IReadOnlyList<AlignedEntryRowViewModel> AlignedRows { get; } = ComputeIsAligned(table.ResultSets)
        ? Enumerable.Range(0, table.ResultSets[0].Entries.Count)
            .Select(i => new AlignedEntryRowViewModel(table.ResultSets.Select(s => new EntryViewModel(s.Entries[i])).ToList()))
            .ToList()
        : [];

    internal void Add(RollOutcomeViewModel outcome, int roll)
    {
        Outcomes.Add(outcome);
        Raise(nameof(HasOutcomes));
        foreach (var set in ResultSets)
            foreach (var entry in set.Entries)
                entry.IsMatched = entry.Entry.Covers(roll);
        foreach (var row in AlignedRows)
            row.IsMatched = row.Cells[0].Entry.Covers(roll); // every cell in a row shares the same range
    }

    /// <summary>See <see cref="IsAligned"/>.</summary>
    private static bool ComputeIsAligned(IReadOnlyList<ResultSet> sets)
    {
        if (sets.Count < 2) return false;
        var count = sets[0].Entries.Count;
        if (count == 0) return false;
        if (sets.Any(s => s.Entries.Count != count)) return false;
        for (var i = 0; i < count; i++)
        {
            var (min, max) = (sets[0].Entries[i].Min, sets[0].Entries[i].Max);
            if (sets.Any(s => s.Entries[i].Min != min || s.Entries[i].Max != max)) return false;
        }
        return true;
    }
}

/// <summary>
/// The rolling screen for one opened table. Following a link appends a new step for the destination
/// without rolling it; earlier steps stay visible. The trail lives only in memory.
/// The properties below without "Step" describe the current (latest) step.
/// </summary>
public sealed class RollViewModel : ObservableObject
{
    private readonly IDiceProvider _dice;
    private readonly Func<long, RollableTable?>? _loadTable;
    private readonly Action<long>? _tableUsed;
    private readonly Action<RollSnapshot>? _rolled;
    private readonly Func<bool>? _diceReady;
    private string _manualRollText = "";
    private string _message = "";
    private bool _isRolling;
    private CancellationTokenSource? _rollCancel;

    /// <param name="tableUsed">Called when a table becomes the current rollable table by being followed to (recent tables).</param>
    /// <param name="rolled">Called once for every actual resolved roll, with a snapshot of what was shown (recent rolls).
    /// Never called for a followed link or an invalid manual entry.</param>
    /// <param name="diceReady">Whether the chosen dice provider can roll right now (dddice is still preparing, for example). Null means always.</param>
    public RollViewModel(RollableTable table, IDiceProvider dice, Func<long, RollableTable?>? loadTable = null,
        Action<long>? tableUsed = null, Action<RollSnapshot>? rolled = null, Func<bool>? diceReady = null)
    {
        _dice = dice;
        _diceReady = diceReady;
        _loadTable = loadTable;
        _tableUsed = tableUsed;
        _rolled = rolled;
        Steps.Add(new RollStepViewModel(table, isFirst: true));
        RollCommand = new RelayCommand(() => _ = RollAsync(), () => !IsRolling && (_diceReady?.Invoke() ?? true));
        ResolveManualCommand = new RelayCommand(ResolveManual, () => !IsRolling);
    }

    public ObservableCollection<RollStepViewModel> Steps { get; } = [];
    public RollStepViewModel Current => Steps[^1];

    public string Title => Current.Title;
    public string DiceInfo => Current.DiceInfo;
    public IReadOnlyList<ResultSetViewModel> ResultSets => Current.ResultSets;

    /// <summary>See <see cref="RollStepViewModel.IsAligned"/>, for the current (latest) step's table.</summary>
    public bool IsAligned => Current.IsAligned;
    public bool IsNotAligned => !IsAligned;

    /// <summary>See <see cref="RollStepViewModel.AlignedRows"/>, for the current (latest) step's table.</summary>
    public IReadOnlyList<AlignedEntryRowViewModel> AlignedRows => Current.AlignedRows;

    /// <summary>The latest roll's per-result-set outputs on the current table.</summary>
    public IReadOnlyList<ResultLineViewModel> Results => Current.Outcomes.LastOrDefault()?.Lines ?? [];

    /// <summary>The latest numeric roll on the current table; empty before its first roll.</summary>
    public string RollDisplay => Current.Outcomes.LastOrDefault()?.Display ?? "";

    public string ManualRollText { get => _manualRollText; set => Set(ref _manualRollText, value); }

    /// <summary>Feedback for a manual value that cannot be used.</summary>
    public string Message { get => _message; private set => Set(ref _message, value); }

    /// <summary>True from pressing Roll until the provider has a final number (for dddice: until the dice settle). Roll cannot be pressed again meanwhile.</summary>
    public bool IsRolling
    {
        get => _isRolling;
        private set { if (Set(ref _isRolling, value)) Raise(nameof(RollStatus)); }
    }

    /// <summary>"Rolling…" while a roll is in progress. The table result is not shown until it ends.</summary>
    public string RollStatus => IsRolling ? "Rolling…" : "";

    /// <summary>The roll under way, or the last one that finished (for callers that need to wait for it).</summary>
    public Task RollTask { get; private set; } = Task.CompletedTask;

    public ICommand RollCommand { get; }
    public ICommand ResolveManualCommand { get; }

    /// <summary>
    /// Asks the chosen provider for a final number and, only when it has one, resolves and shows it. A failed or cancelled roll
    /// changes nothing on screen except a message; it never falls back to another provider by itself.
    /// </summary>
    public Task RollAsync()
    {
        if (IsRolling || !(_diceReady?.Invoke() ?? true)) return RollTask; // a second press while rolling joins the roll already under way
        return RollTask = RollCoreAsync();
    }

    private async Task RollCoreAsync()
    {
        Message = "";
        var cancel = _rollCancel = new CancellationTokenSource();
        IsRolling = true;
        int roll;
        try
        {
            roll = await _dice.RollAsync(Current.Table.Dice, cancel.Token);
        }
        catch (OperationCanceledException) { Message = "The roll was cancelled."; return; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Message = ex is DddiceException ? ex.Message : $"The roll could not be made: {ex.Message}";
            return;
        }
        finally
        {
            IsRolling = false;
            if (ReferenceEquals(_rollCancel, cancel)) _rollCancel = null;
            cancel.Dispose();
        }
        Apply(roll);
    }

    /// <summary>
    /// Rolls one auxiliary <see cref="InlineDiceAction"/> found inside a displayed result (see <see cref="InlineDiceDetector"/>),
    /// through the same provider and the same single-flight machinery as the table's own Roll: while any roll (this one, another
    /// inline action, or the parent table) is under way, a second press is simply ignored rather than raced against it, and
    /// leaving the table (<see cref="CancelRoll"/>) abandons it exactly like a parent roll in the air. Never recorded to history
    /// and never changes the result's own text.
    /// </summary>
    private Task RollInlineAsync(InlineDiceAction action)
    {
        if (IsRolling) return RollTask; // a roll is already under way somewhere on this screen; this press joins it
        return RollTask = RollInlineCoreAsync(action);
    }

    private async Task RollInlineCoreAsync(InlineDiceAction action)
    {
        Message = "";
        var cancel = _rollCancel = new CancellationTokenSource();
        IsRolling = true;
        action.IsRolling = true;
        int roll;
        try
        {
            roll = await _dice.RollAsync(action.Expression, cancel.Token);
        }
        catch (OperationCanceledException) { Message = "The roll was cancelled."; return; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Message = ex is DddiceException ? ex.Message : $"The roll could not be made: {ex.Message}";
            return;
        }
        finally
        {
            IsRolling = false;
            action.IsRolling = false;
            if (ReferenceEquals(_rollCancel, cancel)) _rollCancel = null;
            cancel.Dispose();
        }
        action.AddResult(roll);
    }

    /// <summary>Abandons a roll still in progress (the person left this table, or switched provider) — the parent table's own
    /// roll or an <see cref="InlineDiceAction"/>'s, whichever is currently under way.</summary>
    public void CancelRoll() { try { _rollCancel?.Cancel(); } catch (ObjectDisposedException) { } }

    private void ResolveManual()
    {
        var dice = Current.Table.Dice;
        // A typed roll is the FINAL result (dice plus any modifier, worked out by hand), so it is checked as it is and never modified again.
        var parsed = RangeText.TryParseRollValue(ManualRollText, dice, out var value);
        if (parsed && dice.IsLegal(value))
            Apply(value);
        else if (dice.IsD66)
            // The final d66 value is typed (35), never two separate dice; say why an impossible one is refused.
            Message = parsed ? $"{value} is not a possible d66 result. Enter two digits from 1 to 6, such as 35." : "Enter a d66 result: two digits from 1 to 6, such as 35.";
        else
            Message = $"Enter a whole number from {dice.FormatValue(dice.Min)} to {dice.FormatValue(dice.Max)}.";
    }

    private void Apply(int roll)
    {
        Message = "";
        var step = Current;
        var dice = step.Table.Dice;
        var formatted = dice.FormatValue(roll);
        var display = formatted != roll.ToString() ? $"Rolled {formatted} (numeric {roll})"
            : dice.IsD66 ? $"Rolled {roll} (d66)"   // never shown as a sum: 3 then 5 is 35
            : dice.Modifier != 0 ? $"Rolled {roll} ({dice})"   // keep the expression visible when a modifier was involved: "Rolled 7 (2d6+1)"
            : $"Rolled {roll}";

        var lines = new List<ResultLineViewModel>();
        foreach (var r in TableResolver.Resolve(step.Table, roll).Results)
        {
            var heading = r.ResultSet.Name;
            lines.Add(r.Status switch
            {
                ResolutionStatus.Matched => MatchedLine(heading, r.Entry!),
                ResolutionStatus.NoMatch => new(heading, "", $"No entry covers {dice.FormatValue(roll)}.", true),
                _ => new(heading, "", $"Ambiguous: {string.Join(" and ", r.Matches.Select(m => $"\"{m.Text}\" ({m.RangeLabel})"))} both cover {dice.FormatValue(roll)}.", true),
            });
        }

        DeactivateLinks(); // links offered by earlier rolls are superseded by this one
        step.Add(new RollOutcomeViewModel(display, lines), roll);
        Raise(nameof(Results));
        Raise(nameof(RollDisplay));

        // What the user saw, as text: each set's output, headed by the set's name when it has one.
        var shown = string.Join("\n", lines.Select(l => l.HasHeading ? $"{l.Heading}: {l.Text}" : l.Text));
        _rolled?.Invoke(new RollSnapshot(step.Table.Id, step.Table.Name, dice.ToString(), roll, shown));
    }

    private ResultLineViewModel MatchedLine(string heading, TableEntry entry)
    {
        LinkState link = LinkState.None;
        string linkName = "";
        RollableTable? target = null;

        if (entry.LinkedTableId is long id)
        {
            try { target = _loadTable?.Invoke(id); }
            catch (Exception) { target = null; } // an unreadable destination is shown as unavailable; it must not stop the roll
            link = target is null ? LinkState.Missing : LinkState.Resolved;
            linkName = target?.Name ?? "";
        }
        else if (entry.UnresolvedLinkName is not null)
        {
            link = LinkState.Unresolved;
            linkName = entry.UnresolvedLinkName;
        }

        var inlineActions = InlineDiceDetector.Detect(entry.Text).Select(e => new InlineDiceAction(e)).ToList();
        var line = new ResultLineViewModel(heading, entry.RangeLabel, entry.Text, false)
        {
            Link = link, LinkName = linkName, LinkTarget = target, InlineActions = inlineActions,
        };
        foreach (var action in inlineActions)
            action.RollCommand = new RelayCommand(() => _ = RollInlineAsync(action), () => !IsRolling && (_diceReady?.Invoke() ?? true));
        if (link == LinkState.Resolved) line.FollowCommand = new RelayCommand(() => Follow(line), () => line.IsActive && !IsRolling);
        return line;
    }

    /// <summary>User-triggered: makes the destination the current table, keeps the trail, and does not roll it.</summary>
    private void Follow(ResultLineViewModel line)
    {
        if (!line.IsActive || line.LinkTarget is null) return;

        DeactivateLinks();
        Steps.Add(new RollStepViewModel(line.LinkTarget, isFirst: false));
        ManualRollText = "";
        Message = "";
        Raise(nameof(Title));
        Raise(nameof(DiceInfo));
        Raise(nameof(ResultSets));
        Raise(nameof(IsAligned));
        Raise(nameof(IsNotAligned));
        Raise(nameof(AlignedRows));
        Raise(nameof(Results));
        Raise(nameof(RollDisplay));
        _tableUsed?.Invoke(line.LinkTarget.Id);
    }

    private void DeactivateLinks()
    {
        foreach (var step in Steps)
            foreach (var outcome in step.Outcomes)
                foreach (var line in outcome.Lines)
                    line.IsActive = false;
    }
}
