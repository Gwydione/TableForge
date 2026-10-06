using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace TableForge.Streaming;

/// <summary>The Streaming Overlay's port is taken (another program, or another TableForge) or reserved by Windows.</summary>
public sealed class OverlayPortUnavailableException(int port, Exception inner)
    : Exception($"Port {port} is already in use or unavailable.", inner)
{
    public int Port { get; } = port;
}

/// <summary>
/// The Streaming Overlay's read-only, two-route HTTP adapter: <c>GET /</c> (the overlay page) and <c>GET /state</c> (the current
/// state from <see cref="OverlayPublisher.StateJson"/>), on 127.0.0.1 only. Deliberately not a web server: one strict request
/// shape, no bodies, no other methods, routes or hosts, one response per connection, then close.
/// <para>
/// Limits: listen backlog <see cref="Backlog"/>; at most <see cref="MaxConnections"/> connections handled at once (any other
/// accepted connection is closed straight away); request line at most <see cref="MaxRequestLineBytes"/> bytes and the whole
/// head at most <see cref="MaxHeadBytes"/>, with at most <see cref="MaxHeaderLines"/> header lines; the full head must arrive
/// within the request deadline and the response is written within the response deadline (2 s each).
/// </para>
/// It never touches the UI: it only reads the publisher's immutable snapshot.
/// </summary>
public sealed class OverlayServer : IDisposable
{
    public const int Backlog = 16;
    public const int MaxConnections = 8;
    public const int MaxRequestLineBytes = 1024;
    public const int MaxHeadBytes = 8 * 1024;
    public const int MaxHeaderLines = 32;
    public const string ObsLocalFileOrigin = "http://absolute";
    public static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ResponseDeadline = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(1);

    private static readonly byte[] BadRequest = Encoding.ASCII.GetBytes("Bad Request");
    private static readonly byte[] NotFound = Encoding.ASCII.GetBytes("Not Found");
    private static readonly byte[] MethodNotAllowed = Encoding.ASCII.GetBytes("Method Not Allowed");

    private readonly OverlayPublisher _publisher;
    private readonly object _gate = new();
    private readonly HashSet<TcpClient> _open = [];
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private SemaphoreSlim? _slots;
    private Task _acceptLoop = Task.CompletedTask;
    private readonly List<Task> _handlers = [];

    public OverlayServer(OverlayPublisher publisher, int port)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "The port must be from 1024 to 65535.");
        _publisher = publisher;
        Port = port;
    }

    public int Port { get; }
    public bool IsRunning { get { lock (_gate) return _listener is not null; } }

    /// <summary>Binds 127.0.0.1:<see cref="Port"/> (exclusively) and starts accepting. Throws <see cref="OverlayPortUnavailableException"/>.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_listener is not null) return;
            var listener = new TcpListener(IPAddress.Loopback, Port);
            listener.Server.ExclusiveAddressUse = true;               // nobody else may bind this port beside us while we run
            try { listener.Start(Backlog); }
            catch (SocketException ex)
            {
                listener.Server.Dispose();
                throw new OverlayPortUnavailableException(Port, ex);
            }
            _listener = listener;
            _stop = new CancellationTokenSource();
            _slots = new SemaphoreSlim(MaxConnections);
            _acceptLoop = Task.Run(() => AcceptLoopAsync(listener, _slots, _stop.Token));
        }
    }

    /// <summary>
    /// Stops listening, cancels every request in progress and closes every open connection, then waits at most
    /// <see cref="StopWait"/> for the work to finish. Safe to call more than once; <see cref="Start"/> may be called again after.
    /// </summary>
    public void Stop()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_listener is null) return;
            _stop!.Cancel();
            _listener.Stop();
            _listener = null;
            foreach (var client in _open) client.Dispose();
            _open.Clear();
            pending = [_acceptLoop, .. _handlers];
        }
        try { Task.WaitAll(pending, StopWait); } catch (AggregateException) { /* cancelled work ends with exceptions; none matter here */ }
    }

    public void Dispose() => Stop();

    /// <summary>How many connections are being handled right now (for tests).</summary>
    public int OpenConnections { get { lock (_gate) return _open.Count; } }

    private async Task AcceptLoopAsync(TcpListener listener, SemaphoreSlim slots, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false); }
            catch (Exception) { return; }                                // stopped (or the listener failed): no more connections

            if (!slots.Wait(0)) { client.Dispose(); continue; }        // already handling MaxConnections: close this one
            lock (_gate)
            {
                if (stop.IsCancellationRequested) { client.Dispose(); slots.Release(); return; }
                _open.Add(client);
                _handlers.RemoveAll(t => t.IsCompleted);
                _handlers.Add(Task.Run(() => HandleAsync(client, slots, stop)));
            }
        }
    }

    private async Task HandleAsync(TcpClient client, SemaphoreSlim slots, CancellationToken stop)
    {
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var (status, body, contentType, allowOrigin) = await ReadAndRouteAsync(stream, stop).ConfigureAwait(false);
            await RespondAsync(stream, status, body, contentType, allowOrigin, stop).ConfigureAwait(false);
        }
        catch (Exception) { /* a closed or reset connection, or shutdown: nothing to report to anyone */ }
        finally
        {
            lock (_gate) _open.Remove(client);
            client.Dispose();
            slots.Release();
        }
    }

    // ---- request ------------------------------------------------------------------------------------------------------------

    private async Task<(int Status, byte[] Body, string ContentType, bool AllowOrigin)> ReadAndRouteAsync(NetworkStream stream, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(RequestDeadline);
        string? head;
        try { head = await ReadHeadAsync(stream, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested) { head = null; }   // too slow: 400
        return head is null ? Error(400) : Route(head);
    }

    /// <summary>
    /// The request head (request line and headers, without the final blank line), or null if it breaks any rule: only CRLF line
    /// ends, printable ASCII (and tab), the size limits. Nothing after the blank line is ever read.
    /// </summary>
    internal static async Task<string?> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeadBytes];
        var count = 0;
        var lineStart = 0;
        var firstLine = true;
        while (true)
        {
            if (count == buffer.Length) return null;                    // the head is larger than MaxHeadBytes
            var read = await stream.ReadAsync(buffer.AsMemory(count, buffer.Length - count), ct).ConfigureAwait(false);
            if (read == 0) return null;                                  // closed before the head was complete
            for (var i = count; i < count + read; i++)
            {
                var b = buffer[i];
                if (b == '\n')
                {
                    if (i == 0 || buffer[i - 1] != '\r') return null;    // bare LF
                    if (i - 1 == lineStart)                              // an empty line: the end of the head
                        return Encoding.ASCII.GetString(buffer, 0, lineStart is 0 ? 0 : lineStart - 2);
                    firstLine = false;
                    lineStart = i + 1;
                    continue;
                }
                if (i > 0 && buffer[i - 1] == '\r') return null;          // a CR not followed by LF
                if (b == '\r') continue;
                if (b == '\t') continue;
                if (b < 0x20 || b > 0x7E) return null;                    // NUL, other controls, DEL and non-ASCII
                if (firstLine && i - lineStart >= MaxRequestLineBytes) return null;
            }
            count += read;
        }
    }

    private (int, byte[], string, bool) Route(string head)
    {
        var lines = head.Split("\r\n");
        if (lines.Length - 1 > MaxHeaderLines) return Error(400);

        var parts = lines[0].Split(' ');
        if (parts.Length != 3 || !IsToken(parts[0]) || parts[1].Length == 0 || parts[2] is not ("HTTP/1.0" or "HTTP/1.1")) return Error(400);
        var (method, target) = (parts[0], parts[1]);

        var hosts = new List<string>();
        var origins = new List<string>();
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || line[0] is ' ' or '\t') return Error(400);          // obsolete line folding
            var colon = line.IndexOf(':');
            if (colon <= 0 || !IsToken(line[..colon])) return Error(400);
            var name = line[..colon];
            var value = line[(colon + 1)..].Trim(' ', '\t');
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && value != "0") return Error(400);   // no bodies
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) return Error(400);
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) hosts.Add(value);
            if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) origins.Add(value);
        }
        if (hosts.Count != 1 || !IsOwnHost(hosts[0])) return Error(400);
        if (method != "GET") return Error(405);

        return target switch
        {
            "/" => (200, OverlayPage.Served, "text/html; charset=utf-8", false),
            "/state" => (200, _publisher.StateJson, "application/json; charset=utf-8", origins is [ObsLocalFileOrigin]),
            _ => Error(404),
        };
    }

    private bool IsOwnHost(string host) =>
        host == $"127.0.0.1:{Port}" || host.Equals($"localhost:{Port}", StringComparison.OrdinalIgnoreCase);

    /// <summary>An HTTP token (RFC 9110 tchar): method and header names.</summary>
    private static bool IsToken(string s) => s.Length > 0 && s.All(c => c < 0x7F && (char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c)));

    private static (int, byte[], string, bool) Error(int status) =>
        (status, status switch { 404 => NotFound, 405 => MethodNotAllowed, _ => BadRequest }, "text/plain; charset=utf-8", false);

    // ---- response -----------------------------------------------------------------------------------------------------------

    private static async Task RespondAsync(NetworkStream stream, int status, byte[] body, string contentType, bool allowOrigin, CancellationToken stop)
    {
        var reason = status switch { 200 => "OK", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Bad Request" };
        var head = new StringBuilder()
            .Append($"HTTP/1.1 {status} {reason}\r\n")
            .Append($"Content-Type: {contentType}\r\n")
            .Append($"Content-Length: {body.Length}\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n");
        if (status == 405) head.Append("Allow: GET\r\n");
        if (allowOrigin) head.Append($"Access-Control-Allow-Origin: {ObsLocalFileOrigin}\r\n");
        if (status == 200 && contentType.StartsWith("application/json", StringComparison.Ordinal)) head.Append("Vary: Origin\r\n");
        head.Append("Connection: close\r\n\r\n");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(ResponseDeadline);
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), deadline.Token).ConfigureAwait(false);
        await stream.WriteAsync(body, deadline.Token).ConfigureAwait(false);
        await stream.FlushAsync(deadline.Token).ConfigureAwait(false);
    }
}
