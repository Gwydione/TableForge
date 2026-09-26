using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TableForge.Dice;

/// <summary>
/// The real dddice boundary: a session (token, room, theme) from <see cref="DddiceSessionSource"/> — a guest login and room, or a
/// connected account's saved token, reused room and chosen theme — and dddice-js (from dddice's CDN) drawing the dice in a WebView2
/// that lives inside the TableForge window. The page reports back through <c>chrome.webview.postMessage</c>.
/// Nothing is created until <see cref="PrepareAsync"/> is called, so a person who stays on Built-in Dice costs nothing here.
/// A guest identity is created once and reused for the whole application session; it is never written to disk. The token is
/// handed to the page only as an argument of its init call: never in a URL, storage, or a log.
/// </summary>
public sealed class WebViewDddiceRoller : IDddiceRoomRoller, IDisposable
{
    private const string Host = "tableforge-app.local";

    private readonly Action<FrameworkElement> _attach;
    private readonly string _userDataFolder;
    private readonly DddiceRest _rest;
    private readonly DddiceSessionSource _sessions;
    private readonly DddiceRollTracker _tracker = new();
    private WebView2? _view;
    private DddiceSession? _session;
    private bool _isReady;
    private TaskCompletionSource? _pageReady;
    private TaskCompletionSource? _pageLoaded;

    public TimeSpan PrepareTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan RollTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <param name="attach">Puts the WebView2 into the visible window. WebView2 only starts once it is part of a shown window.</param>
    /// <param name="userDataFolder">Where WebView2 keeps its browser profile (cache, cookies). It holds no TableForge data.</param>
    /// <param name="connection">The optional connected account. Null (or no account connected) means the unchanged guest flow.</param>
    public WebViewDddiceRoller(Action<FrameworkElement> attach, string userDataFolder, DddiceRest? rest = null, DddiceConnection? connection = null)
    {
        _attach = attach;
        _userDataFolder = userDataFolder;
        _rest = rest ?? new DddiceRest();
        _sessions = new DddiceSessionSource(_rest, connection);
    }

    /// <summary>The account or theme changed (or was disconnected): the next preparation starts a fresh page with the new session.</summary>
    public void Reset()
    {
        _isReady = false;
        _session = null;
    }

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        if (_isReady) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PrepareTimeout);
            try
            {
                _session = null;
                _session = await _sessions.GetAsync(timeout.Token);
                await EnsureWebViewAsync(timeout.Token);
                await LoadPageAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DddiceException("dddice did not become ready in time. Check the internet connection.");
            }
            _isReady = true;
        }
        catch (DddiceException ex) when (ex.IsAuthProblem)
        {
            _sessions.AuthFailed(_session); // a refused guest or room is replaced by a new one on the next try; an account is marked expired
            _session = null;
            throw;
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            throw new DddiceException("dddice's dice need the Microsoft Edge WebView2 Runtime, which is not installed on this computer. " +
                                      "Install it from Microsoft (free), or keep using Built-in Dice.", inner: ex);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or DddiceException))
        {
            throw new DddiceException("The dddice dice could not be started: " + ex.Message, inner: ex);
        }
    }

    private async Task EnsureWebViewAsync(CancellationToken ct)
    {
        if (_view?.CoreWebView2 is not null) return;

        _view ??= new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Transparent };
        _attach(_view);
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: _userDataFolder);
        await _view.EnsureCoreWebView2Async(env).WaitAsync(ct);

        var core = _view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.SetVirtualHostNameToFolderMapping(Host, AppContext.BaseDirectory, CoreWebView2HostResourceAccessKind.Allow);
        core.NewWindowRequested += (_, e) => e.Handled = true;                                  // the page never opens windows
        core.NavigationStarting += (_, e) => e.Cancel = !e.Uri.StartsWith($"https://{Host}/", StringComparison.Ordinal); // and never leaves its own page
        core.WebMessageReceived += (_, e) => OnPageMessage(e.WebMessageAsJson);
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess) _pageLoaded?.TrySetException(new DddiceException("The dddice dice page could not be loaded."));
        };
    }

    private async Task LoadPageAsync(CancellationToken ct)
    {
        var core = _view!.CoreWebView2;
        _pageLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        core.Navigate($"https://{Host}/Assets/dddice-host.html"); // a fresh page (and so a fresh dddice-js) for every preparation
        await _pageLoaded.Task.WaitAsync(ct);
        var session = _session!;
        await core.ExecuteScriptAsync(
            $"window.tf.init({JsonSerializer.Serialize(session.Token)}, {JsonSerializer.Serialize(session.Room)}, {JsonSerializer.Serialize(session.Theme)})");
        await _pageReady.Task.WaitAsync(ct);
    }

    public async Task<IReadOnlyList<DddiceFace>> RollAsync(IReadOnlyList<string> diceTypes, CancellationToken cancellationToken)
    {
        if (!_isReady || _view?.CoreWebView2 is null || _session is not { } session) throw new DddiceException("dddice is not ready.");

        var externalId = Guid.NewGuid().ToString("N");
        var settled = _tracker.Begin(externalId);
        try
        {
            var dice = new JsonArray(diceTypes.Select(d => (JsonNode)new JsonObject { ["type"] = d, ["theme"] = session.Theme }).ToArray());
            await _view.CoreWebView2.ExecuteScriptAsync("window.tf.clear()"); // sweep the previous roll's dice off the tray
            await _view.CoreWebView2.ExecuteScriptAsync($"window.tf.roll({dice.ToJsonString()}, '{externalId}')");
            return await settled.WaitAsync(RollTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            _isReady = false;
            throw new DddiceException($"The dddice roll did not finish within {RollTimeout.TotalSeconds:0} seconds.");
        }
        catch (DddiceException)
        {
            _isReady = false; // whatever went wrong, the next attempt starts from a fresh page
            throw;
        }
        finally { _tracker.Abandon(); }
    }

    private void OnPageMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var kind = root.TryGetProperty("kind", out var k) ? k.GetString() : null;
        var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";

        switch (kind)
        {
            case "pageLoaded": _pageLoaded?.TrySetResult(); break;
            case "ready": _pageReady?.TrySetResult(); break;
            case "sdkLoadFailed":
                _pageReady?.TrySetException(new DddiceException("The dddice dice could not be downloaded from dddice.com. Check the internet connection."));
                break;
            case "initFailed":
                var auth = message.Contains("401") || message.Contains("403");
                var account = _session?.IsAccount ?? false;
                _pageReady?.TrySetException(new DddiceException(auth
                    ? account ? DddiceMessages.Expired : "dddice refused the guest login for its room."
                    : "TableForge could not connect to dddice's room. Check the internet connection.", auth) { IsAccountProblem = auth && account });
                break;
            case "connectionState" when root.TryGetProperty("state", out var s) && s.GetString() is "unavailable" or "failed":
                if (_tracker.IsRolling) _tracker.Fail("The connection to dddice was lost during the roll.");
                _isReady = false;
                break;
            default:
                _tracker.OnPageMessage(json);
                break;
        }
    }

    public void Dispose()
    {
        _rest.Dispose();
        try { _view?.Dispose(); } catch { /* shutting down */ }
    }
}
