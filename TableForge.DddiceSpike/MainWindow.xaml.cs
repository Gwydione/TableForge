using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using TableForge.Dice;
using TableForge.Domain;

namespace TableForge.DddiceSpike;

/// <summary>
/// EXPERIMENTAL dddice spike window. Interactive buttons for a human, and "--auto --out DIR [--fail MODE]" for a scripted run that
/// captures screenshots and a log, then exits. Nothing here is used by TableForge itself.
/// </summary>
public partial class MainWindow : Window
{
    private readonly string[] _args = Environment.GetCommandLineArgs();
    private readonly StringBuilder _logText = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DddiceRest? _rest;
    private DddiceVisualRoller? _roller;
    private IAsyncDiceProvider? _dddiceProvider;
    private string? _room;
    private bool _busy;
    private string _userDataFolder = "";
    private string? _snapPrefix;   // when set, the roller events "roll:started"/"roll:finished" trigger screenshots

    private string? Arg(string name) { var i = Array.IndexOf(_args, name); return i >= 0 && i + 1 < _args.Length ? _args[i + 1] : null; }
    private bool Flag(string name) => _args.Contains(name);
    private string Fail => Arg("--fail") ?? "";
    private string? OutDir => Arg("--out");

    public MainWindow()
    {
        InitializeComponent();
        foreach (var (label, dice) in new[] { ("d4", "d4"), ("d6", "d6"), ("d8", "d8"), ("d10", "d10"), ("d12", "d12"), ("d20", "d20"), ("d100", "d100"), ("2d6", "2d6") })
        {
            var b = new Button { Content = label, Width = 52, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(4), IsEnabled = false, Tag = dice };
            b.Click += async (_, _) => await RunDiceAsync(DiceExpression.Parse(dice));
            DiceButtons.Children.Add(b);
        }
        Loaded += async (_, _) => await StartAsync();
    }

    private void Log(string line)
    {
        var text = $"[{_clock.ElapsedMilliseconds,6} ms] {line}";
        _logText.AppendLine(text);
        LogBox.AppendText(text + Environment.NewLine);
        LogBox.ScrollToEnd();
        if (OutDir is { } d) try { File.AppendAllText(Path.Combine(d, "spike.log"), text + Environment.NewLine); } catch { }
    }

    private void SetEnabled(bool on)
    {
        TestD20Button.IsEnabled = D20TableButton.IsEnabled = TwoD6Button.IsEnabled = on;
        BuiltInButton.IsEnabled = _room is not null || Fail != "" || true; // built-in fallback must always work, even when dddice failed
        foreach (Button b in DiceButtons.Children) b.IsEnabled = on;
    }

    // ---------- start-up: guest token, room, WebView2 + dddice-js ----------
    private async Task StartAsync()
    {
        try
        {
            if (OutDir is { } d) Directory.CreateDirectory(d);
            Log($"start. fail-mode='{Fail}'");
            HttpMessageHandler? handler = null;
            if (Fail == "offline") handler = new SocketsHttpHandler { Proxy = new WebProxy("http://127.0.0.1:9"), UseProxy = true };
            _rest = new DddiceRest(handler);

            var sw = Stopwatch.StartNew();
            string token;
            if (Fail == "badtoken") { token = "not-a-real-token"; _rest.Token = token; }
            else { token = await _rest.CreateGuestTokenAsync(); }
            Log($"guest token created in {sw.ElapsedMilliseconds} ms (no account, no login)  token={token[..4]}...({token.Length} chars)");

            sw.Restart();
            _room = Fail == "badroom" ? "zzzzzzz" : Fail == "badtoken" ? "zzzzzzz" : await _rest.CreateRoomAsync("TableForge spike");
            Log($"room '{_room}' ready in {sw.ElapsedMilliseconds} ms");

            _roller = new DddiceVisualRoller(Dice, Log) { BlockDddiceRequests = Fail == "nosdk" };
            if (Fail == "timeout") _roller.RollTimeout = TimeSpan.FromMilliseconds(300);
            _userDataFolder = Path.Combine(Path.GetTempPath(), "tf-dddice-spike-wv2-" + Environment.ProcessId);
            sw.Restart();
            await _roller.InitializeAsync(token, _room, _userDataFolder, TimeSpan.FromSeconds(20));
            Log($"WebView2 + dddice-js initialised in {sw.ElapsedMilliseconds} ms");
            _dddiceProvider = new DddiceDiceProvider(_roller);
            _roller.RollEvent = (kind, ms) =>
            {
                if (_snapPrefix is not { } prefix) return;
                if (kind == "roll:started") _ = SnapAfterAsync($"{prefix}-1-tumbling", 600);
                else if (kind == "roll:finished") _ = SnapAfterAsync($"{prefix}-2-settled", 250);
            };

            StatusText.Text = $"dddice ready. Guest room {_room}. Theme: {DddiceRest.FreeGuestTheme} (the only theme a guest may use).";
            SetEnabled(true);
        }
        catch (Exception ex)
        {
            StatusText.Text = "dddice is not available: " + ex.Message + "   -> Use Built-in Dice.";
            Log("START FAILED: " + ex.GetType().Name + ": " + ex.Message);
            BuiltInButton.IsEnabled = true;
        }
        if (Flag("--auto")) await RunAutoAsync();
    }

    // ---------- the three interactive experiments ----------
    private async void TestD20_Click(object s, RoutedEventArgs e) => await TestD20Async();
    private async void D20Table_Click(object s, RoutedEventArgs e) => await TableRollAsync(SpikeTables.D20Test(), _dddiceProvider!);
    private async void TwoD6Table_Click(object s, RoutedEventArgs e) => await TableRollAsync(SpikeTables.TwoD6PlusOne(), _dddiceProvider!);
    private async void BuiltIn_Click(object s, RoutedEventArgs e) => await TableRollAsync(SpikeTables.D20Test(), new BuiltInAsyncDiceProvider(new BuiltInDiceProvider()));

    private async Task<string> TestD20Async()
    {
        return await Guarded("d20 test", async () =>
        {
            ResultText.Text = "Rolling...";   // nothing else is shown until dddice says the dice have settled
            var r = await _roller!.RollAsync(["d20"], null, CancellationToken.None);
            ResultText.Text = $"Result from dddice: {r.Values[0].Value}";
            Log($"d20 -> {r.Values[0].Value}; numbers known +{r.KnownAfter.TotalMilliseconds:0} ms, roll:started +{r.StartedAfter.TotalMilliseconds:0} ms, roll:finished +{r.FinishedAfter.TotalMilliseconds:0} ms");
            return ResultText.Text;
        });
    }

    private async Task<string> RunDiceAsync(DiceExpression dice)
    {
        return await Guarded(dice.ToString(), async () =>
        {
            ResultText.Text = "Rolling...";
            var r = await _roller!.RollAsync(DiceMapping.ToDddice(dice), null, CancellationToken.None);
            var subtotal = DiceMapping.Subtotal(dice, r.Values);
            ResultText.Text = $"Result from dddice: {subtotal}   ({string.Join(" + ", r.Values.Select(v => $"{v.Type}:{v.Value}"))})";
            if (dice is { Count: 2, Sides: 6 }) Log($"  d66 experiment: faces in returned order [{r.Values[0].Value},{r.Values[1].Value}] -> d66 {r.Values[0].Value}{r.Values[1].Value}");
            Log($"{dice}: faces [{string.Join(", ", r.Values.Select(v => $"{v.Type}:{v.Value}"))}] dddice-total={r.Total} TableForge-subtotal={subtotal}; known +{r.KnownAfter.TotalMilliseconds:0} ms, finished +{r.FinishedAfter.TotalMilliseconds:0} ms");
            return $"{dice}|{string.Join(",", r.Values.Select(v => $"{v.Type}:{v.Value}"))}|{r.Total}|{subtotal}|{r.KnownAfter.TotalMilliseconds:0}|{r.FinishedAfter.TotalMilliseconds:0}";
        });
    }

    private async Task<string> TableRollAsync(RollableTable table, IAsyncDiceProvider provider)
    {
        return await Guarded(table.Name, async () =>
        {
            ResultText.Text = $"{table.Name} ({table.Dice}): rolling...";   // table result must not appear before the dice settle
            var sw = Stopwatch.StartNew();
            var value = await provider.RollAsync(table.Dice, CancellationToken.None);
            var resolution = TableResolver.Resolve(table, value);
            var text = string.Join(" / ", resolution.Results.Select(x => x.Entry?.Text ?? "(no entry)"));
            ResultText.Text = $"{table.Name} ({table.Dice}) -> {table.Dice.FormatValue(value)}\n{text}";
            Log($"{table.Name} {table.Dice}: final {value} -> \"{text}\" (provider {provider.GetType().Name}, {sw.ElapsedMilliseconds} ms end to end)");
            return $"{table.Name}|{table.Dice}|{value}|{text}";
        });
    }

    private async Task<string> Guarded(string what, Func<Task<string>> body)
    {
        if (_busy) return "busy";
        _busy = true; SetEnabled(false);
        try { return await body(); }
        catch (Exception ex)
        {
            ResultText.Text = $"dddice roll failed: {ex.Message}\n(Use Built-in Dice instead.)";
            Log($"{what} FAILED: {ex.GetType().Name}: {ex.Message}");
            return "FAILED: " + ex.Message;
        }
        finally { _busy = false; if (_roller is not null) SetEnabled(true); }
    }

    // ---------- scripted run ----------
    private async Task RunAutoAsync()
    {
        var results = new List<string>();
        try
        {
            await Task.Delay(1500);
            if (_roller is null || _dddiceProvider is null)
            {
                // dddice never came up: prove the built-in fallback still works from the same screen.
                results.Add("fallback: " + await TableRollAsync(SpikeTables.D20Test(), new BuiltInAsyncDiceProvider(new BuiltInDiceProvider())));
                await Snap("failed-start");
            }
            else if (Fail != "")
            {
                results.Add("failure-mode d20: " + await TestD20Async());
                await Snap("failure-" + Fail);
                results.Add("fallback: " + await TableRollAsync(SpikeTables.D20Test(), new BuiltInAsyncDiceProvider(new BuiltInDiceProvider())));
            }
            else
            {
                // 1) [Test dddice d20], with a mid-roll capture
                _snapPrefix = "d20test";
                var shownDuring = new List<string>();
                var t = TestD20Async();
                for (var i = 0; i < 12 && !t.IsCompleted; i++) { await Task.Delay(400); shownDuring.Add(ResultText.Text); }
                results.Add("d20 test: " + await t);
                results.Add("text on screen while waiting (sampled every 400 ms): " + string.Join(" | ", shownDuring.Distinct()));
                await Task.Delay(600);

                // 2) every dice shape
                foreach (var d in new[] { "d4", "d6", "d8", "d10", "d12", "d20", "d100", "2d6" })
                {
                    _snapPrefix = d is "d100" or "2d6" or "d12" ? "dice-" + d : null;
                    results.Add("dice: " + await RunDiceAsync(DiceExpression.Parse(d)));
                    await Task.Delay(800);
                }

                // 2b) is the order of a visual 2d6 stable (future d66)? five more rolls
                for (var i = 0; i < 5; i++) { results.Add("2d6 order run: " + await RunDiceAsync(DiceExpression.Parse("2d6"))); await Task.Delay(500); }

                // 3) real table through the async provider
                _snapPrefix = "table-d20";
                results.Add("table: " + await TableRollAsync(SpikeTables.D20Test(), _dddiceProvider));
                await Task.Delay(600); await Snap("table-d20-result-shown");
                _snapPrefix = "table-2d6plus1";
                for (var i = 0; i < 3; i++) { results.Add("table 2d6+1: " + await TableRollAsync(SpikeTables.TwoD6PlusOne(), _dddiceProvider)); if (i == 0) { await Task.Delay(600); await Snap("table-2d6plus1-result-shown"); _snapPrefix = null; } await Task.Delay(800); }
                results.Add("builtin: " + await TableRollAsync(SpikeTables.D20Test(), new BuiltInAsyncDiceProvider(new BuiltInDiceProvider())));
            }
        }
        catch (Exception ex) { results.Add("AUTO RUN ERROR: " + ex); }
        if (OutDir is { } d2) File.WriteAllLines(Path.Combine(d2, "results.txt"), results);
        Log("auto run complete");
        Close();
    }

    private async Task SnapAfterAsync(string name, int delayMs) { await Task.Delay(delayMs); await Snap(name); }

    /// <summary>Screenshot of THIS window only: PrintWindow for the WPF chrome, WebView2's own CapturePreview for the dice surface, composited.</summary>
    private async Task Snap(string name)
    {
        if (OutDir is not { } dir) return;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            GetWindowRect(hwnd, out var rc);
            using var bmp = new System.Drawing.Bitmap(rc.Right - rc.Left, rc.Bottom - rc.Top);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            var dc = g.GetHdc();
            PrintWindow(hwnd, dc, 2 /* PW_RENDERFULLCONTENT: this window only, never the desktop */);
            g.ReleaseHdc(dc);
            if (Dice.CoreWebView2 is { } core)
            {
                using var ms = new MemoryStream();
                await core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, ms);
                ms.Position = 0;
                using var preview = new System.Drawing.Bitmap(ms);
                var origin = Dice.PointToScreen(new System.Windows.Point(0, 0));
                g.DrawImageUnscaled(preview, (int)origin.X - rc.Left, (int)origin.Y - rc.Top);
            }
            bmp.Save(Path.Combine(dir, $"{name}.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { Log("snapshot failed: " + ex.Message); }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _rest?.Dispose();
        try { Dice.Dispose(); } catch { }
        try { if (_userDataFolder.Length > 0 && Directory.Exists(_userDataFolder)) Directory.Delete(_userDataFolder, true); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
}

/// <summary>The two experimental tables, built in code. They are never saved to the TableForge database.</summary>
public static class SpikeTables
{
    public static RollableTable D20Test() => Build("D20 TEST TABLE", new DiceExpression(1, 20), [
        (1, 5, "Nothing of interest"), (6, 10, "A handful of rusty nails"), (11, 15, "A dented brass lantern"), (16, 19, "Fifty feet of good rope"), (20, 20, "A sealed letter with a wax dragon")]);

    public static RollableTable TwoD6PlusOne() => Build("2D6+1 TEST TABLE", new DiceExpression(2, 6, 1), [
        (3, 4, "Quiet"), (5, 7, "Footsteps in the dark"), (8, 10, "A wandering patrol"), (11, 12, "Two patrols"), (13, 13, "An ambush")]);

    private static RollableTable Build(string name, DiceExpression dice, (int Min, int Max, string Text)[] rows)
    {
        var set = new ResultSet { Name = "", Entries = rows.Select((r, i) => new TableEntry { Min = r.Min, Max = r.Max, Text = r.Text, SortOrder = i }).ToList() };
        return new RollableTable { Name = name, Dice = dice, ResultSets = [set] };
    }
}
