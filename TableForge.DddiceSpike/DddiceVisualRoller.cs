using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TableForge.DddiceSpike;

public sealed record VisualRollResult(string Uuid, string Equation, int Total, IReadOnlyList<RestDie> Values,
    TimeSpan KnownAfter, TimeSpan StartedAfter, TimeSpan FinishedAfter);

/// <summary>Hosts dddice-js in a WebView2 and turns "roll:finished" into a Task. One visual roll at a time.</summary>
public sealed class DddiceVisualRoller
{
    private readonly WebView2 _view;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _one = new(1, 1);
    private TaskCompletionSource? _ready;
    private Pending? _pending;

    public TimeSpan RollTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Spike hook: called with (event name, milliseconds since the roll was requested) for every dddice event of the current roll.</summary>
    public Action<string, long>? RollEvent { get; set; }
    public bool BlockDddiceRequests { get; set; }   // failure testing: makes the page behave as if dddice were unreachable

    public DddiceVisualRoller(WebView2 view, Action<string> log) { _view = view; _log = log; }

    private sealed class Pending
    {
        public required string ExternalId { get; init; }
        public required Stopwatch Clock { get; init; }
        public TaskCompletionSource<VisualRollResult> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public JsonElement? Started; public TimeSpan StartedAt; public JsonElement? Finished; public TimeSpan FinishedAt;
    }

    public async Task InitializeAsync(string token, string room, string userDataFolder, TimeSpan timeout)
    {
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        await _view.EnsureCoreWebView2Async(env);
        var core = _view.CoreWebView2;
        _view.DefaultBackgroundColor = System.Drawing.Color.Transparent;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.SetVirtualHostNameToFolderMapping("tableforge-spike.local", AppContext.BaseDirectory, CoreWebView2HostResourceAccessKind.Allow);
        if (BlockDddiceRequests)
        {
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (new Uri(e.Request.Uri).Host.EndsWith("dddice.com", StringComparison.OrdinalIgnoreCase))
                    e.Response = core.Environment.CreateWebResourceResponse(null, 503, "Blocked (offline test)", "");
            };
        }
        core.WebMessageReceived += OnMessage;

        _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var pageLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Nav(object? s, CoreWebView2NavigationCompletedEventArgs e) { if (e.IsSuccess) pageLoaded.TrySetResult(); else pageLoaded.TrySetException(new DddiceException("The dice page did not load.")); }
        core.NavigationCompleted += Nav;
        core.Navigate("https://tableforge-spike.local/Assets/dddice-host.html");
        await pageLoaded.Task.WaitAsync(timeout);
        core.NavigationCompleted -= Nav;

        await core.ExecuteScriptAsync($"window.tf.init({JsonSerializer.Serialize(token)}, {JsonSerializer.Serialize(room)}, null)");
        await _ready.Task.WaitAsync(timeout);
    }

    public async Task<VisualRollResult> RollAsync(IReadOnlyList<string> dice, JsonObject? operatorObject, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            var pending = new Pending { ExternalId = Guid.NewGuid().ToString("N"), Clock = Stopwatch.StartNew() };
            _pending = pending;
            var diceJson = new JsonArray(dice.Select(d => (JsonNode)new JsonObject { ["type"] = d, ["theme"] = DddiceRest.FreeGuestTheme }).ToArray()).ToJsonString();
            var op = operatorObject?.ToJsonString() ?? "null";
            await _view.CoreWebView2.ExecuteScriptAsync("window.tf.clear()");   // sweep the previous roll's dice off the tray
            await _view.CoreWebView2.ExecuteScriptAsync($"window.tf.roll({diceJson}, {op}, '{pending.ExternalId}')");
            using var timeout = new CancellationTokenSource(RollTimeout);
            using var link = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try { return await pending.Done.Task.WaitAsync(link.Token); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new DddiceException($"The dddice roll did not finish within {RollTimeout.TotalSeconds:0.#} s (dice started: {(pending.Started is null ? "no" : "yes")}).");
            }
        }
        finally { _pending = null; _one.Release(); }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var doc = JsonDocument.Parse(e.WebMessageAsJson);
        var m = doc.RootElement;
        var kind = m.GetProperty("kind").GetString();
        var p = _pending;
        var hasRoll = m.TryGetProperty("roll", out var r) && r.ValueKind == JsonValueKind.Object;
        _log($"  js> {kind} {(hasRoll ? Short(r) : m.TryGetProperty("message", out var msg) ? msg.GetString() : "")}" + (p is null ? "" : $"  (+{p.Clock.ElapsedMilliseconds} ms)"));
        if (p is not null && kind is "roll:started" or "roll:finished" or "roll:fade:started" or "roll:fade:finished") RollEvent?.Invoke(kind!, p.Clock.ElapsedMilliseconds);
        switch (kind)
        {
            case "ready": _ready?.TrySetResult(); break;
            case "initFailed": _ready?.TrySetException(new DddiceException("dddice could not start: " + m.GetProperty("message").GetString())); break;
            case "sdkLoadFailed": _ready?.TrySetException(new DddiceException("The dddice viewer script could not be downloaded (offline or blocked).")); break;
            case "rollError": p?.Done.TrySetException(new DddiceException("dddice refused the roll: " + m.GetProperty("message").GetString())); break;
            case "roll:started" when p is not null && hasRoll && Ours(p, r):
                p.Started = r.Clone(); p.StartedAt = p.Clock.Elapsed; break;
            case "roll:finished" when p is not null && hasRoll && Ours(p, r):
                p.Finished = r.Clone(); p.FinishedAt = p.Clock.Elapsed; Complete(p); break;
        }
    }

    // dddice puts our external_id on the roll; if a roll has none (e.g. someone else in the room) it is not ours.
    private static bool Ours(Pending p, JsonElement r) => !r.TryGetProperty("externalId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() == p.ExternalId;

    // The visual roll is over only when dddice's own roll:finished fired for our roll. The numbers were already in the roll:started payload; we keep them but wait.
    private static void Complete(Pending p)
    {
        if (p.Finished is not { } finished) return;
        var source = p.Started ?? finished;
        var values = finished.GetProperty("values").EnumerateArray().Select(v => new RestDie(v.GetProperty("type").GetString()!, v.GetProperty("value").GetInt32())).ToList();
        p.Done.TrySetResult(new VisualRollResult(finished.GetProperty("uuid").GetString()!, finished.GetProperty("equation").GetString() ?? "",
            finished.GetProperty("total").GetInt32(), values, p.Started is null ? p.FinishedAt : p.StartedAt, p.Started is null ? default : p.StartedAt, p.FinishedAt));
    }

    private static string Short(JsonElement r) =>
        $"{r.GetProperty("equation").GetString()} total={r.GetProperty("total")} [{string.Join(" ", r.GetProperty("values").EnumerateArray().Select(v => v.GetProperty("type").GetString() + ":" + v.GetProperty("value")))}]";
}
