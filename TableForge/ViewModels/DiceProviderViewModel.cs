using System.Windows.Input;
using TableForge.Dice;
using TableForge.Domain;

namespace TableForge.ViewModels;

public enum DiceProviderKind { BuiltIn, Dddice }

public enum DiceProviderState { Ready, Preparing, Failed }

/// <summary>
/// Which dice provider is in use, and whether it can roll right now. There are exactly two: Built-in (the default, always ready,
/// no network) and dddice (chosen by the person, prepared when chosen, never at start-up otherwise). This object is itself the
/// <see cref="IDiceProvider"/> the roll screen uses, so choosing a provider never has to touch an open table.
/// A dddice failure is reported here with "Use Built-in Dice"; TableForge never rerolls with Built-in by itself, because that
/// would quietly produce a different random result.
/// dddice rolls as a temporary guest unless a dddice account is connected (<see cref="DddiceConnection"/>, managed in the
/// Account… dialog); then it uses that account's chosen theme. An account problem is reported with Account…, never by quietly
/// going back to the guest.
/// </summary>
public sealed class DiceProviderViewModel : ObservableObject, IDiceProvider
{
    private const string PreferenceBuiltIn = "builtin";
    private const string PreferenceDddice = "dddice";

    private readonly IDiceProvider _builtIn;
    private readonly IPreparableDiceProvider? _dddice;
    private readonly Func<string?>? _loadPreference;
    private readonly Action<string>? _savePreference;
    private readonly DddiceConnection? _connection;
    private readonly Action? _openAccount;
    private DiceProviderKind _selected = DiceProviderKind.BuiltIn;
    private DiceProviderState _state = DiceProviderState.Ready;
    private string _message = "";
    private CancellationTokenSource? _cancel;

    /// <param name="dddice">Null when dddice is not offered at all (the option is then disabled).</param>
    /// <param name="loadPreference">Reads the saved choice ("builtin"/"dddice"), or null. Optional.</param>
    /// <param name="savePreference">Saves the choice. Optional; a failure to save never affects rolling.</param>
    /// <param name="connection">The optional dddice account. Null means dddice is always a guest (no Account… at all).</param>
    /// <param name="openAccount">Shows the Account… dialog.</param>
    public DiceProviderViewModel(IDiceProvider builtIn, IPreparableDiceProvider? dddice = null,
        Func<string?>? loadPreference = null, Action<string>? savePreference = null,
        DddiceConnection? connection = null, Action? openAccount = null)
    {
        _builtIn = builtIn;
        _dddice = dddice;
        _loadPreference = loadPreference;
        _savePreference = savePreference;
        _connection = connection;
        _openAccount = openAccount;
        UseBuiltInCommand = new RelayCommand(() => Select(DiceProviderKind.BuiltIn));
        RetryCommand = new RelayCommand(() => _ = PrepareDddiceAsync(), () => Selected == DiceProviderKind.Dddice && State == DiceProviderState.Failed);
        AccountCommand = new RelayCommand(() => _openAccount?.Invoke(), () => _openAccount is not null);
        if (_connection is not null) _connection.Changed += (_, change) => OnConnectionChanged(change);
    }

    // ---- the dddice account ----------------------------------------------------------------------------------------

    /// <summary>Whether the sidebar shows who dddice rolls as, with Account… (only when accounts are offered).</summary>
    public bool ShowDddiceIdentity => IsDddiceAvailable && _connection is not null;

    /// <summary>"dddice: Guest", "dddice: Allen" (or "dddice: Connected" with no name), or "dddice: Connection expired".</summary>
    public string DddiceIdentityText => _connection?.State switch
    {
        DddiceConnectionState.Connected => $"dddice: {_connection.DisplayName}",
        DddiceConnectionState.Expired => "dddice: Connection expired",
        _ => "dddice: Guest",
    };

    /// <summary>"Theme: My Blue Dice" for a connected account that has chosen one; empty otherwise (a guest always uses Bees).</summary>
    public string ThemeText => _connection is { IsAccountMode: true, ThemeName: { Length: > 0 } name } ? $"Theme: {name}" : "";
    public bool HasThemeText => ThemeText.Length > 0;

    /// <summary>A failure caused by the account (expired, theme gone or unusable) offers Account… beside Try again and Built-in.</summary>
    public bool ShowAccountOnFailure => ShowFailure && (_connection?.IsAccountMode ?? false);

    public ICommand AccountCommand { get; }

    private void OnConnectionChanged(DddiceChange change)
    {
        Raise(nameof(DddiceIdentityText));
        Raise(nameof(ThemeText));
        Raise(nameof(HasThemeText));
        Raise(nameof(ShowAccountOnFailure));
        if (change != DddiceChange.Identity || _dddice is null) return;

        // A different account, theme or a disconnect: the prepared dice page belongs to the old identity. Start again with the new one.
        CancelPending();
        _dddice.Reset();
        if (Selected == DiceProviderKind.Dddice) _ = PrepareDddiceAsync();
    }

    public bool IsDddiceAvailable => _dddice is not null;

    public DiceProviderKind Selected
    {
        get => _selected;
        private set
        {
            if (!Set(ref _selected, value)) return;
            Raise(nameof(IsBuiltInSelected));
            Raise(nameof(IsDddiceSelected));
            RaiseState();
        }
    }

    /// <summary>Radio button bindings. Choosing sets the provider; a radio button un-choosing itself is ignored.</summary>
    public bool IsBuiltInSelected { get => Selected == DiceProviderKind.BuiltIn; set { if (value) Select(DiceProviderKind.BuiltIn); } }
    public bool IsDddiceSelected { get => Selected == DiceProviderKind.Dddice; set { if (value) Select(DiceProviderKind.Dddice); } }

    public DiceProviderState State { get => _state; private set { if (Set(ref _state, value)) RaiseState(); } }

    /// <summary>What to tell the person: "Preparing dddice…", "dddice is ready", or why it failed.</summary>
    public string Message { get => _message; private set => Set(ref _message, value); }

    /// <summary>Whether Roll may use the selected provider right now (dddice only once its dice are ready).</summary>
    public bool CanRoll => State == DiceProviderState.Ready;

    /// <summary>The dice panel (dddice's 3D dice) is shown only while dddice is the chosen provider.</summary>
    public bool ShowDicePanel => Selected == DiceProviderKind.Dddice;
    public bool ShowFailure => Selected == DiceProviderKind.Dddice && State == DiceProviderState.Failed;

    public ICommand UseBuiltInCommand { get; }
    public ICommand RetryCommand { get; }

    private void RaiseState()
    {
        Raise(nameof(CanRoll));
        Raise(nameof(ShowDicePanel));
        Raise(nameof(ShowFailure));
        Raise(nameof(ShowAccountOnFailure));
    }

    /// <summary>Re-applies the saved choice at start-up. Built-in (or nothing saved) does nothing at all; dddice starts preparing.</summary>
    public void RestorePreference()
    {
        string? saved;
        try { saved = _loadPreference?.Invoke(); }
        catch (Exception) { return; } // an unreadable setting just means the default
        if (saved == PreferenceDddice && _dddice is not null) Select(DiceProviderKind.Dddice);
    }

    public void Select(DiceProviderKind kind)
    {
        if (kind == Selected) return;
        if (kind == DiceProviderKind.Dddice && _dddice is null) return;

        CancelPending(); // a roll or preparation still under way for the provider being left is abandoned
        Selected = kind;
        Save(kind == DiceProviderKind.Dddice ? PreferenceDddice : PreferenceBuiltIn);

        if (kind == DiceProviderKind.BuiltIn)
        {
            Message = "";
            State = DiceProviderState.Ready;
        }
        else
        {
            _ = PrepareDddiceAsync();
        }
    }

    private void Save(string value)
    {
        try { _savePreference?.Invoke(value); }
        catch (Exception) { /* not being able to remember the choice must never get in the way */ }
    }

    private void CancelPending()
    {
        _cancel?.Cancel();
        _cancel = null;
    }

    /// <summary>Guest login, room, renderer, theme, realtime connection. Roll stays disabled until this succeeds.</summary>
    public async Task PrepareDddiceAsync()
    {
        if (_dddice is null) return;

        CancelPending();
        var cancel = _cancel = new CancellationTokenSource();
        State = DiceProviderState.Preparing;
        Message = "Preparing dddice…";
        try
        {
            await _dddice.PrepareAsync(cancel.Token);
            if (cancel.IsCancellationRequested) return;
            State = DiceProviderState.Ready;
            Message = _connection is { IsAccountMode: true } ? $"dddice is ready (theme: {_connection.ThemeName})." : "dddice is ready (guest mode: no account needed).";
        }
        catch (OperationCanceledException) { /* the person switched away */ }
        catch (Exception ex)
        {
            if (cancel.IsCancellationRequested) return;
            State = DiceProviderState.Failed;
            Message = Describe(ex, "dddice could not start");
        }
        finally
        {
            if (ReferenceEquals(_cancel, cancel)) _cancel = null;
        }
    }

    /// <summary>
    /// Rolls with whichever provider is selected. A dddice failure (other than "this table's dice cannot be shown") puts dddice
    /// in the failed state: its message and "Use Built-in Dice" appear, and Roll stays off until the person retries or switches.
    /// </summary>
    public async Task<int> RollAsync(DiceExpression expression, CancellationToken cancellationToken)
    {
        if (Selected == DiceProviderKind.BuiltIn) return await _builtIn.RollAsync(expression, cancellationToken);
        if (_dddice is null || State != DiceProviderState.Ready) throw new DddiceException("dddice is not ready yet.");

        CancelPending();
        var cancel = _cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            return await _dddice.RollAsync(expression, cancel.Token);
        }
        catch (DddiceUnsupportedDiceException) { throw; } // the connection is fine; only this roll cannot be shown
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            if (!cancel.IsCancellationRequested)
            {
                State = DiceProviderState.Failed;
                Message = Describe(ex, "The dddice roll failed");
            }
            throw;
        }
        finally
        {
            if (ReferenceEquals(_cancel, cancel)) _cancel = null;
        }
    }

    private static string Describe(Exception ex, string prefix) => ex is DddiceException ? ex.Message : $"{prefix}: {ex.Message}";
}
