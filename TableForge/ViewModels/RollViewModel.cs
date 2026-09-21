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

public enum LinkState { None, Resolved, Unresolved, Missing }

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

    /// <summary>Only the latest roll on the current table offers its links; once the trail moves on they become plain notes.</summary>
    public bool IsActive
    {
        get => _isActive;
        internal set
        {
            if (!Set(ref _isActive, value)) return;
            Raise(nameof(ShowFollow));
            Raise(nameof(ShowLinkedNote));
        }
    }

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
    public string DiceInfo => $"{Table.Dice} · legal rolls {Table.Dice.FormatValue(Table.Dice.Min)}–{Table.Dice.FormatValue(Table.Dice.Max)}";
    public IReadOnlyList<ResultSetViewModel> ResultSets { get; } = table.ResultSets.Select(s => new ResultSetViewModel(s)).ToList();
    public ObservableCollection<RollOutcomeViewModel> Outcomes { get; } = [];
    public bool HasOutcomes => Outcomes.Count > 0;

    internal void Add(RollOutcomeViewModel outcome, int roll)
    {
        Outcomes.Add(outcome);
        Raise(nameof(HasOutcomes));
        foreach (var set in ResultSets)
            foreach (var entry in set.Entries)
                entry.IsMatched = entry.Entry.Covers(roll);
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
    private string _manualRollText = "";
    private string _message = "";

    /// <param name="tableUsed">Called when a table becomes the current rollable table by being followed to (recent tables).</param>
    /// <param name="rolled">Called once for every actual resolved roll, with a snapshot of what was shown (recent rolls).
    /// Never called for a followed link or an invalid manual entry.</param>
    public RollViewModel(RollableTable table, IDiceProvider dice, Func<long, RollableTable?>? loadTable = null,
        Action<long>? tableUsed = null, Action<RollSnapshot>? rolled = null)
    {
        _dice = dice;
        _loadTable = loadTable;
        _tableUsed = tableUsed;
        _rolled = rolled;
        Steps.Add(new RollStepViewModel(table, isFirst: true));
        RollCommand = new RelayCommand(() => Apply(_dice.Roll(Current.Table.Dice)));
        ResolveManualCommand = new RelayCommand(ResolveManual);
    }

    public ObservableCollection<RollStepViewModel> Steps { get; } = [];
    public RollStepViewModel Current => Steps[^1];

    public string Title => Current.Title;
    public string DiceInfo => Current.DiceInfo;
    public IReadOnlyList<ResultSetViewModel> ResultSets => Current.ResultSets;

    /// <summary>The latest roll's per-result-set outputs on the current table.</summary>
    public IReadOnlyList<ResultLineViewModel> Results => Current.Outcomes.LastOrDefault()?.Lines ?? [];

    /// <summary>The latest numeric roll on the current table; empty before its first roll.</summary>
    public string RollDisplay => Current.Outcomes.LastOrDefault()?.Display ?? "";

    public string ManualRollText { get => _manualRollText; set => Set(ref _manualRollText, value); }

    /// <summary>Feedback for a manual value that cannot be used.</summary>
    public string Message { get => _message; private set => Set(ref _message, value); }

    public ICommand RollCommand { get; }
    public ICommand ResolveManualCommand { get; }

    private void ResolveManual()
    {
        var dice = Current.Table.Dice;
        if (RangeText.TryParseValue(ManualRollText, dice, out var value) && dice.IsLegal(value))
            Apply(value);
        else
            Message = $"Enter a whole number from {dice.FormatValue(dice.Min)} to {dice.FormatValue(dice.Max)}.";
    }

    private void Apply(int roll)
    {
        Message = "";
        var step = Current;
        var dice = step.Table.Dice;
        var display = dice.FormatValue(roll) == roll.ToString() ? $"Rolled {roll}" : $"Rolled {dice.FormatValue(roll)} (numeric {roll})";

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

        var line = new ResultLineViewModel(heading, entry.RangeLabel, entry.Text, false)
        {
            Link = link, LinkName = linkName, LinkTarget = target,
        };
        if (link == LinkState.Resolved) line.FollowCommand = new RelayCommand(() => Follow(line), () => line.IsActive);
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
