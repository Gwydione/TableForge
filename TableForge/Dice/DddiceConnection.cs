namespace TableForge.Dice;

/// <summary>What the dice page is started with: whose token, which room, which theme. Guest and account alike.</summary>
public sealed record DddiceSession(string Token, string Room, string Theme, bool IsAccount)
{
    public override string ToString() => $"dddice session (room {Room}, theme {Theme}, {(IsAccount ? "account" : "guest")})"; // never the token
}

public enum DddiceConnectionState
{
    /// <summary>No account: dddice uses a temporary guest, exactly as before accounts existed.</summary>
    Guest,
    Connected,
    /// <summary>An account was connected but can't be used as it is (refused by dddice, or its saved file unreadable). Reconnect or Disconnect.</summary>
    Expired,
}

/// <summary>Which kind of change: <see cref="Identity"/> changes who or what the dice page must be started with; <see cref="Status"/> only what is shown.</summary>
public enum DddiceChange { Identity, Status }

/// <summary>
/// The one owner of the optional dddice account. It reads the saved account at start-up (a local file, no network), and the
/// account is checked with dddice only when dddice is actually prepared (<see cref="PrepareSessionAsync"/>). A refused account is
/// marked <see cref="DddiceConnectionState.Expired"/> and kept until the person reconnects or disconnects: it never becomes a guest by itself.
/// </summary>
public sealed class DddiceConnection
{
    private readonly IDddiceAccountStore _store;

    public DddiceConnection(IDddiceAccountStore store)
    {
        _store = store;
        var loaded = store.Load();
        (Account, State) = loaded.State switch
        {
            DddiceAccountFileState.Loaded => (loaded.Account, DddiceConnectionState.Connected),
            DddiceAccountFileState.Unreadable => (null, DddiceConnectionState.Expired),
            _ => ((DddiceAccount?)null, DddiceConnectionState.Guest),
        };
    }

    public event EventHandler<DddiceChange>? Changed;

    public DddiceConnectionState State { get; private set; }
    public DddiceAccount? Account { get; private set; }

    /// <summary>True whenever an account (usable or not) is in charge of dddice, so the guest path is not used.</summary>
    public bool IsAccountMode => State != DddiceConnectionState.Guest;

    /// <summary>The account's name when dddice gave one; otherwise "Connected".</summary>
    public string DisplayName => Account?.DisplayName is { Length: > 0 } name ? name : "Connected";
    public string? ThemeName => Account?.ThemeName;
    public string? ThemeId => Account?.ThemeId;

    /// <summary>
    /// dddice's own page for the account's saved room (where its Streaming tools are), or null for a guest or before the account
    /// has a room. TableForge only opens it in the person's browser; it never sees or handles the room's streaming key.
    /// </summary>
    public string? RoomUrl => State == DddiceConnectionState.Connected && Account?.RoomSlug is { Length: > 0 } slug
        ? $"https://dddice.com/room/{Uri.EscapeDataString(slug)}"
        : null;

    /// <summary>True once, this run, dddice refused the account's saved room and TableForge had to create a new one (see <see cref="DddiceMessages.RoomChanged"/>).</summary>
    public bool RoomChanged { get; private set; }

    /// <summary>
    /// A newly approved activation. A previous choice of theme and room is kept (a reconnect should not lose it); both are checked
    /// against dddice again before use, so a different account simply gets asked for a theme and given its own room.
    /// Saving happens first: if it fails, nothing changes.
    /// </summary>
    public void Connect(string token, string? displayName)
    {
        var account = new DddiceAccount(token, displayName, Account?.RoomSlug, Account?.ThemeId, Account?.ThemeName);
        _store.Save(account);
        Account = account;
        State = DddiceConnectionState.Connected;
        Changed?.Invoke(this, DddiceChange.Identity);
    }

    public void SelectTheme(DddiceTheme theme)
    {
        if (Account is null || !theme.IsCompatible) return;
        if (Account.ThemeId == theme.Id && Account.ThemeName == theme.Name) return;
        var account = Account with { ThemeId = theme.Id, ThemeName = theme.Name };
        _store.Save(account);
        Account = account;
        Changed?.Invoke(this, DddiceChange.Identity);
    }

    /// <summary>Remembers the account's room for next time. Failing to save only means a new room next run.</summary>
    public void RememberRoom(string slug)
    {
        if (Account is null || Account.RoomSlug == slug) return;
        Account = Account with { RoomSlug = slug };
        try { _store.Save(Account); } catch (Exception) { /* the room still works for this run */ }
    }

    /// <summary>dddice refused the account. Kept as it is; the person decides (Reconnect or Disconnect).</summary>
    public void MarkExpired()
    {
        if (State == DddiceConnectionState.Expired) return;
        State = DddiceConnectionState.Expired;
        Changed?.Invoke(this, DddiceChange.Status);
    }

    /// <summary>Local only: forgets the token, theme and room, and deletes the file. The dddice account itself is not touched.</summary>
    public void Disconnect()
    {
        _store.Delete();
        Account = null;
        RoomChanged = false;
        State = DddiceConnectionState.Guest;
        Changed?.Invoke(this, DddiceChange.Identity);
    }

    /// <summary>
    /// Everything the dice page needs for the account: the Dice Box is loaded (which also checks the token), the saved theme must
    /// still be there and compatible, and the saved room is reused if dddice still lets this account use it, or replaced.
    /// Never falls back to a guest or another theme: each problem is reported with what to do.
    /// </summary>
    public async Task<DddiceSession> PrepareSessionAsync(DddiceRest rest, CancellationToken ct)
    {
        if (State == DddiceConnectionState.Expired || Account is not { } account)
            throw new DddiceException(DddiceMessages.Expired, isAuthProblem: true) { IsAccountProblem = true };

        try
        {
            var themes = await rest.LoadDiceBoxAsync(account.Token, ct);
            if (account.ThemeId is null)
                throw new DddiceException(DddiceMessages.ChooseTheme) { IsAccountProblem = true };
            var theme = themes.FirstOrDefault(t => t.Id == account.ThemeId);
            if (theme is null)
                throw new DddiceException(DddiceMessages.ThemeMissing(account.ThemeName ?? account.ThemeId)) { IsAccountProblem = true };
            if (!theme.IsCompatible)
                throw new DddiceException(DddiceMessages.ThemeIncompatible(theme.Name, theme.Reason)) { IsAccountProblem = true };

            var previous = account.RoomSlug;
            var room = previous is { } saved && await rest.RoomIsAccessibleAsync(account.Token, saved, ct)
                ? saved
                : await rest.CreateAccountRoomAsync(account.Token, ct);
            RememberRoom(room);
            // A saved room dddice no longer lets this account use was replaced: anything pointing at the old room (an OBS dice
            // source made from its Streaming tools) no longer receives rolls. The first room an account ever gets is not a change.
            if (previous is not null && room != previous && !RoomChanged)
            {
                RoomChanged = true;
                Changed?.Invoke(this, DddiceChange.Status);
            }
            return new DddiceSession(account.Token, room, theme.Id, IsAccount: true);
        }
        catch (DddiceException ex) when (ex.IsAuthProblem)
        {
            MarkExpired();
            throw new DddiceException(DddiceMessages.Expired, isAuthProblem: true, inner: ex) { IsAccountProblem = true };
        }
    }
}

/// <summary>
/// Hands the dice page its session. With no account this is the unchanged guest flow: one guest login and room for the whole
/// run, created on first need, thrown away (and created afresh next time) only if dddice refuses them. With an account, the
/// <see cref="DddiceConnection"/> supplies it — and a refusal there never turns into a guest.
/// </summary>
public sealed class DddiceSessionSource(DddiceRest rest, DddiceConnection? connection = null)
{
    private string? _guestToken;
    private string? _guestRoom;

    public bool IsAccountMode => connection?.IsAccountMode ?? false;

    public async Task<DddiceSession> GetAsync(CancellationToken ct)
    {
        if (connection is { IsAccountMode: true }) return await connection.PrepareSessionAsync(rest, ct);
        try
        {
            _guestToken ??= await rest.CreateGuestTokenAsync(ct);
            _guestRoom ??= await rest.CreateRoomAsync(_guestToken, ct);
            return new DddiceSession(_guestToken, _guestRoom, DddiceDiceMapping.GuestTheme, IsAccount: false);
        }
        catch (DddiceException ex) when (ex.IsAuthProblem)
        {
            ForgetGuest();
            throw;
        }
    }

    /// <summary>The dice page itself was refused with this session: a guest is replaced next time; an account is marked expired.</summary>
    public void AuthFailed(DddiceSession? session)
    {
        if (session is { IsAccount: true }) connection?.MarkExpired();
        else ForgetGuest();
    }

    private void ForgetGuest() { _guestToken = null; _guestRoom = null; }
}

public sealed class DddiceActivationTimeoutException() : DddiceException(DddiceMessages.ActivationTimedOut);

/// <summary>
/// Waits for the person to approve an activation code on dddice.com: one poll straight away, then one every
/// <see cref="Interval"/>, until a token arrives, the code's lifetime (the sooner of its expires_at and <see cref="MaxLifetime"/>)
/// runs out, or it is cancelled. "Busy" and network hiccups just mean another poll later. Nothing is kept anywhere by this class.
/// </summary>
public sealed class DddiceActivationPoller(
    Func<DddiceActivationCode, CancellationToken, Task<DddiceActivationPoll>> poll,
    Func<DateTimeOffset>? now = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public async Task<DddiceActivationPoll> WaitForApprovalAsync(DddiceActivationCode activation, CancellationToken ct)
    {
        var start = _now();
        var deadline = activation.ExpiresAt < start + MaxLifetime ? activation.ExpiresAt : start + MaxLifetime;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await poll(activation, ct);
                if (result.Token is not null) return result;
            }
            catch (DddiceException ex) when (ex.IsTemporary) { /* busy or briefly offline: try again at the next poll */ }

            if (_now() + Interval >= deadline) throw new DddiceActivationTimeoutException();
            await _delay(Interval, ct);
        }
    }
}
