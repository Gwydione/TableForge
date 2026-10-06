using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TableForge.Streaming;

namespace TableForge.Tests;

/// <summary>A raw HTTP exchange with the overlay adapter: what came back, or that the connection was closed/reset without an answer.</summary>
internal sealed record RawResponse(int Status, IReadOnlyDictionary<string, List<string>> Headers, byte[] Body, bool ClosedWithoutAnswer)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? string.Join(",", v) : null;
    public string Text => Encoding.UTF8.GetString(Body);
}

internal static class RawHttp
{
    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public static string Get(int port, string target = "/state", string? extra = null) =>
        $"GET {target} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n{extra}\r\n";

    public static Task<RawResponse> Send(int port, string request, int waitMs = 4000) => Send(port, Encoding.ASCII.GetBytes(request), waitMs);

    public static async Task<RawResponse> Send(int port, byte[] request, int waitMs = 4000)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        try { await stream.WriteAsync(request); } catch (IOException) { }
        return await ReadAll(stream, waitMs);
    }

    public static async Task<RawResponse> ReadAll(NetworkStream stream, int waitMs = 4000)
    {
        var received = new MemoryStream();
        var buffer = new byte[8192];
        using var cts = new CancellationTokenSource(waitMs);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0) received.Write(buffer, 0, read);
        }
        catch (IOException) { }                                   // reset by the server
        catch (OperationCanceledException) { throw new TimeoutException("The server neither answered nor closed the connection."); }
        return Parse(received.ToArray());
    }

    private static RawResponse Parse(byte[] bytes)
    {
        var text = Encoding.ASCII.GetString(bytes);
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (bytes.Length == 0 || end < 0) return new(0, new Dictionary<string, List<string>>(), [], true);
        var lines = text[..end].Split("\r\n");
        var headers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            (headers.TryGetValue(line[..colon], out var list) ? list : headers[line[..colon]] = []).Add(line[(colon + 1)..].Trim());
        }
        return new(int.Parse(lines[0].Split(' ')[1]), headers, bytes[(end + 4)..], false);
    }
}

/// <summary>RC27 Streaming Overlay, phase 3: the two-route loopback adapter and its exact HTTP contract.</summary>
public class StreamingOverlayServerTests : IDisposable
{
    private readonly OverlayPublisher _publisher = new(instanceId: "inst");
    private readonly int _port = RawHttp.FreePort();
    private readonly OverlayServer _server;

    public StreamingOverlayServerTests()
    {
        _server = new OverlayServer(_publisher, _port);
        _server.Start();
    }

    public void Dispose() => _server.Dispose();

    private Task<RawResponse> Send(string request) => RawHttp.Send(_port, request);
    private Task<RawResponse> Get(string target = "/state", string? extra = null) => Send(RawHttp.Get(_port, target, extra));

    private static void AssertCommonHeaders(RawResponse r, string contentType)
    {
        Assert.Equal(contentType, r.Header("Content-Type"));
        Assert.Equal(r.Body.Length.ToString(), r.Header("Content-Length"));
        Assert.Equal("no-store", r.Header("Cache-Control"));
        Assert.Equal("nosniff", r.Header("X-Content-Type-Options"));
        Assert.Equal("close", r.Header("Connection"));
    }

    // ---- the two routes --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Get_root_serves_the_overlay_page()
    {
        var r = await Get("/");
        Assert.Equal(200, r.Status);
        AssertCommonHeaders(r, "text/html; charset=utf-8");
        Assert.Equal(OverlayPage.Served, r.Body);
        Assert.Null(r.Header("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Get_state_serves_the_current_state_and_follows_every_change()
    {
        var r = await Get();
        Assert.Equal(200, r.Status);
        AssertCommonHeaders(r, "application/json; charset=utf-8");
        Assert.Equal("Origin", r.Header("Vary"));
        Assert.Null(r.Header("Access-Control-Allow-Origin"));
        Assert.Equal(_publisher.StateJson, r.Body);

        _publisher.Publish(new OverlayResult("Weather", "14", [new OverlayLine("", [new OverlaySegment("Storm 🎲 雨")])]));
        r = await Get();
        using var doc = JsonDocument.Parse(r.Body);
        Assert.Equal(("inst", 1L, "Storm 🎲 雨"), (doc.RootElement.GetProperty("instance").GetString(), doc.RootElement.GetProperty("version").GetInt64(),
            doc.RootElement.GetProperty("lines")[0].GetProperty("segments")[0].GetProperty("text").GetString()));
        Assert.Equal(r.Body.Length.ToString(), r.Header("Content-Length"));                 // bytes, not characters
    }

    [Fact]
    public async Task Http_1_0_requests_are_answered_too()
    {
        Assert.Equal(200, (await Send($"GET /state HTTP/1.0\r\nHost: 127.0.0.1:{_port}\r\n\r\n")).Status);
    }

    // ---- the OBS local-file origin -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_the_exact_obs_local_file_origin_may_read_the_state()
    {
        var r = await Get("/state", "Origin: http://absolute\r\n");
        Assert.Equal(200, r.Status);
        Assert.Equal("http://absolute", r.Header("Access-Control-Allow-Origin"));
        Assert.Equal("Origin", r.Header("Vary"));

        foreach (var origin in new[] { "null", "https://evil.example", "http://absolute.evil.example", "http://absolute/", "HTTP://ABSOLUTE",
                     "https://absolute", "http://absolute:80", "file://", $"http://127.0.0.1:{_port}" })
        {
            var other = await Get("/state", $"Origin: {origin}\r\n");
            Assert.Equal(200, other.Status);
            Assert.True(other.Header("Access-Control-Allow-Origin") is null, $"origin {origin} was given access");
        }

        var duplicate = await Get("/state", "Origin: http://absolute\r\nOrigin: http://absolute\r\n");
        Assert.Null(duplicate.Header("Access-Control-Allow-Origin"));
        var page = await Get("/", "Origin: http://absolute\r\n");
        Assert.Null(page.Header("Access-Control-Allow-Origin"));                            // only /state is ever shared
    }

    // ---- Host -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Exactly_one_host_naming_this_loopback_port_is_required()
    {
        Assert.Equal(200, (await Send($"GET /state HTTP/1.1\r\nHost: localhost:{_port}\r\n\r\n")).Status);
        Assert.Equal(200, (await Send($"GET /state HTTP/1.1\r\nHost: LocalHost:{_port}\r\n\r\n")).Status);

        foreach (var hostLines in new[] { "", $"Host: 127.0.0.1:{_port}\r\nHost: 127.0.0.1:{_port}\r\n", "Host: 127.0.0.1\r\n", "Host: localhost\r\n",
                     $"Host: 127.0.0.1:{_port + 1}\r\n", $"Host: evil.example:{_port}\r\n", $"Host: [::1]:{_port}\r\n", $"Host: 127.0.0.2:{_port}\r\n",
                     "Host: \r\n", $"Host: 0x7f.0.0.1:{_port}\r\n" })
        {
            var r = await Send($"GET /state HTTP/1.1\r\n{hostLines}\r\n");
            Assert.True(r.Status == 400, $"'{hostLines.Replace("\r\n", "\\r\\n")}' got {r.Status}");
            AssertCommonHeaders(r, "text/plain; charset=utf-8");
            Assert.Equal("Bad Request", r.Text);
        }
    }

    // ---- syntax ---------------------------------------------------------------------------------------------------------------

    public static TheoryData<string, string> Malformed => new()
    {
        { "two-part request line", "GET /state\r\nHost: H\r\n\r\n" },
        { "double space", "GET  /state HTTP/1.1\r\nHost: H\r\n\r\n" },
        { "HTTP/2.0", "GET /state HTTP/2.0\r\nHost: H\r\n\r\n" },
        { "lower-case version", "GET /state http/1.1\r\nHost: H\r\n\r\n" },
        { "HTTP/1.1 with trailing text", "GET /state HTTP/1.1 x\r\nHost: H\r\n\r\n" },
        { "method that is not a token", "G@T /state HTTP/1.1\r\nHost: H\r\n\r\n" },
        { "empty request line", "\r\nHost: H\r\n\r\n" },
        { "header without a colon", "GET /state HTTP/1.1\r\nHost: H\r\ngarbage\r\n\r\n" },
        { "empty header name", "GET /state HTTP/1.1\r\nHost: H\r\n: value\r\n\r\n" },
        { "space in a header name", "GET /state HTTP/1.1\r\nHost: H\r\nBad Name: value\r\n\r\n" },
        { "space before the colon", "GET /state HTTP/1.1\r\nHost : H\r\n\r\n" },
        { "continuation line (space)", "GET /state HTTP/1.1\r\nHost: H\r\nX-A: one\r\n two\r\n\r\n" },
        { "continuation line (tab)", "GET /state HTTP/1.1\r\nHost: H\r\nX-A: one\r\n\ttwo\r\n\r\n" },
        { "bare LF ending the request line", "GET /state HTTP/1.1\nHost: H\r\n\r\n" },
        { "bare LF ending the head", "GET /state HTTP/1.1\r\nHost: H\n\n" },
        { "CR not followed by LF", "GET /state HTTP/1.1\r\nHost: H\rX-A: 1\r\n\r\n" },
        { "NUL in the request line", "GET /st\0ate HTTP/1.1\r\nHost: H\r\n\r\n" },
        { "NUL in a header", "GET /state HTTP/1.1\r\nHost: H\r\nX-A: a\0b\r\n\r\n" },
        { "other control character", "GET /state HTTP/1.1\r\nHost: H\r\nX-A: a\u0007b\r\n\r\n" },
        { "DEL", "GET /state HTTP/1.1\r\nHost: H\r\nX-A: a\u007Fb\r\n\r\n" },
    };

    [Theory]
    [MemberData(nameof(Malformed))]
    public async Task Malformed_requests_get_400(string what, string request)
    {
        var r = await Send(request.Replace("Host: H", $"Host: 127.0.0.1:{_port}"));
        Assert.True(r.Status == 400 || r.ClosedWithoutAnswer, $"{what}: got {r.Status}");
    }

    [Fact]
    public async Task Non_ascii_request_syntax_gets_400()
    {
        foreach (var request in new[] { $"GET /état HTTP/1.1\r\nHost: 127.0.0.1:{_port}\r\n\r\n", $"GET /state HTTP/1.1\r\nHost: 127.0.0.1:{_port}\r\nX-Name: café\r\n\r\n" })
        {
            var r = await RawHttp.Send(_port, Encoding.UTF8.GetBytes(request));
            Assert.True(r.Status == 400 || r.ClosedWithoutAnswer, $"got {r.Status}");   // refused at the first bad byte, unread bytes may reset
        }
    }

    // ---- sizes ----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_request_line_may_be_1024_bytes_but_no_more()
    {
        static string LineOf(int length) => "GET /" + new string('a', length - "GET / HTTP/1.1".Length) + " HTTP/1.1";
        Assert.Equal(1024, LineOf(1024).Length);
        Assert.Equal(404, (await Send($"{LineOf(1024)}\r\nHost: 127.0.0.1:{_port}\r\n\r\n")).Status);   // read, then an unknown route
        var tooLong = await Send($"{LineOf(1025)}\r\nHost: 127.0.0.1:{_port}\r\n\r\n");
        Assert.True(tooLong.Status == 400 || tooLong.ClosedWithoutAnswer, $"got {tooLong.Status}");   // refused mid-line; unread bytes may reset
    }

    [Fact]
    public async Task The_whole_head_may_be_8_KiB_but_no_more()
    {
        string Head(int total)
        {
            var start = $"GET /state HTTP/1.1\r\nHost: 127.0.0.1:{_port}\r\nX-Pad: ";
            return start + new string('p', total - start.Length - 4) + "\r\n\r\n";
        }
        Assert.Equal(8192, Head(8192).Length);
        Assert.Equal(200, (await Send(Head(8192))).Status);
        var r = await Send(Head(8193));
        Assert.True(r.Status == 400 || r.ClosedWithoutAnswer, $"got {r.Status}");
    }

    [Fact]
    public async Task At_most_32_header_lines()
    {
        string With(int headers) => $"GET /state HTTP/1.1\r\nHost: 127.0.0.1:{_port}\r\n" + string.Concat(Enumerable.Range(1, headers - 1).Select(i => $"X-{i}: v\r\n")) + "\r\n";
        Assert.Equal(200, (await Send(With(32))).Status);
        Assert.Equal(400, (await Send(With(33))).Status);
    }

    // ---- methods, bodies, routes -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Only_get_is_allowed()
    {
        foreach (var method in new[] { "POST", "HEAD", "PUT", "DELETE", "OPTIONS", "PATCH", "get" })
        {
            var r = await Send($"{method} /state HTTP/1.1\r\nHost: 127.0.0.1:{_port}\r\n\r\n");
            Assert.True(r.Status == 405, $"{method} got {r.Status}");
            Assert.Equal("GET", r.Header("Allow"));
            AssertCommonHeaders(r, "text/plain; charset=utf-8");
        }
    }

    [Fact]
    public async Task Request_bodies_are_refused_and_never_read()
    {
        Assert.Equal(200, (await Get("/state", "Content-Length: 0\r\n")).Status);
        Assert.Equal(400, (await Get("/state", "Content-Length: 5\r\n")).Status);
        var withBody = await Send($"POST /state HTTP/1.1\r\nHost: 127.0.0.1:{_port}\r\nContent-Length: 5\r\n\r\nhello");
        Assert.True(withBody.Status == 400 || withBody.ClosedWithoutAnswer, $"got {withBody.Status}");   // the body is never read
        Assert.Equal(400, (await Get("/state", "Transfer-Encoding: chunked\r\n")).Status);
        Assert.Equal(400, (await Get("/state", "Transfer-Encoding: identity\r\n")).Status);
        Assert.Equal(400, (await Get("/state", "content-length: 1\r\n")).Status);
    }

    [Fact]
    public async Task Only_the_two_exact_routes_exist()
    {
        foreach (var target in new[] { "/state?x=1", "/?x=1", "/state/", "//state", "/STATE", "/State", "/index.html", "/state#x", "/state.json",
                     "/%73tate", $"http://127.0.0.1:{_port}/state", "*", "/../state", "/./state" })
        {
            var r = await Get(target);
            Assert.True(r.Status == 404, $"{target} got {r.Status}");
            Assert.Equal("Not Found", r.Text);
        }
    }

    // ---- time, concurrency, lifecycle --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stalled_request_is_answered_400_and_closed_after_the_deadline()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET /state HTTP/1.1\r\nHost: 127.0.0.1:{_port}\r\n"));   // never finished
        var clock = Stopwatch.StartNew();
        var r = await RawHttp.ReadAll(stream, 5000);
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.5, 3.5);
        Assert.True(r.Status == 400 || r.ClosedWithoutAnswer);
    }

    [Fact]
    public async Task At_most_8_connections_are_handled_at_once_and_the_rest_are_closed()
    {
        var idle = new List<TcpClient>();
        try
        {
            for (var i = 0; i < OverlayServer.MaxConnections; i++)
            {
                var c = new TcpClient();
                await c.ConnectAsync(IPAddress.Loopback, _port);
                idle.Add(c);
            }
            await Wait.Until(() => _server.OpenConnections == OverlayServer.MaxConnections, "eight connections being handled");

            var clock = Stopwatch.StartNew();
            var refused = await RawHttp.Send(_port, RawHttp.Get(_port));
            Assert.True(refused.ClosedWithoutAnswer, $"a ninth connection got {refused.Status}");
            Assert.True(clock.Elapsed.TotalSeconds < 1.5, "the ninth connection was not closed straight away");
        }
        finally { foreach (var c in idle) c.Dispose(); }

        await Wait.Until(() => _server.OpenConnections == 0, "the idle connections to end");
        Assert.Equal(200, (await Get()).Status);
    }

    [Fact]
    public async Task Stopping_closes_connections_in_progress_promptly_and_the_server_can_start_again()
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, _port);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET /sta"));                  // a request in progress
        await Wait.Until(() => _server.OpenConnections == 1, "the connection to be handled");

        var clock = Stopwatch.StartNew();
        _server.Stop();
        Assert.True(clock.Elapsed < OverlayServer.StopWait + TimeSpan.FromMilliseconds(500), $"Stop took {clock.Elapsed}");
        Assert.False(_server.IsRunning);
        Assert.Equal(0, _server.OpenConnections);
        var r = await RawHttp.ReadAll(client.GetStream(), 2000);
        Assert.True(r.ClosedWithoutAnswer);

        await Assert.ThrowsAnyAsync<SocketException>(() => RawHttp.Send(_port, RawHttp.Get(_port)));   // nothing listening
        _server.Stop();                                                                                 // a second Stop is harmless

        _server.Start();
        _server.Start();                                                                                // and a second Start too
        Assert.Equal(200, (await Get()).Status);
    }

    [Fact]
    public void A_port_already_in_use_is_reported_never_replaced_by_another()
    {
        using var other = new OverlayServer(new OverlayPublisher(), _port);
        var ex = Assert.Throws<OverlayPortUnavailableException>(other.Start);
        Assert.Equal(_port, ex.Port);
        Assert.False(other.IsRunning);
        Assert.True(_server.IsRunning);

        var busyPort = RawHttp.FreePort();
        var squatter = new TcpListener(IPAddress.Loopback, busyPort);
        squatter.Start();
        try { Assert.Throws<OverlayPortUnavailableException>(new OverlayServer(new OverlayPublisher(), busyPort).Start); }
        finally { squatter.Stop(); }
    }

    [Fact]
    public async Task It_listens_on_127_0_0_1_only()
    {
        var addresses = (await Dns.GetHostAddressesAsync(Dns.GetHostName()))
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)).ToList();
        foreach (var address in addresses)
        {
            using var client = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(address, _port));
        }
        using var v6 = new TcpClient(AddressFamily.InterNetworkV6);
        await Assert.ThrowsAnyAsync<SocketException>(() => v6.ConnectAsync(IPAddress.IPv6Loopback, _port));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1023)]
    [InlineData(65536)]
    public void Ports_outside_1024_to_65535_are_refused(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OverlayServer(new OverlayPublisher(), port));
    }
}

/// <summary>RC27 Streaming Overlay, phase 3: the overlay page and the local file OBS loads.</summary>
public class StreamingOverlayPageTests
{
    [Fact]
    public void The_served_page_polls_its_own_origin_and_the_local_file_polls_the_loopback_port()
    {
        Assert.Contains("var STATE_URL = \"/state\";", Encoding.UTF8.GetString(OverlayPage.Served));
        Assert.Contains("var STATE_URL = \"http://127.0.0.1:41285/state\";", OverlayPage.Html(OverlayPage.StateUrl(41285)));
    }

    [Fact]
    public void The_page_never_turns_text_into_markup()
    {
        var page = OverlayPage.Html("/state");
        foreach (var forbidden in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "new Function", "setTimeout(\"", "DOMParser", "createContextualFragment" })
            Assert.DoesNotContain(forbidden, page);
        Assert.Contains("node.textContent = String(text);", page);
    }

    [Fact]
    public void The_page_follows_the_approved_polling_contract()
    {
        var page = OverlayPage.Html("/state");
        Assert.Equal(500, OverlayPage.PollMilliseconds);
        Assert.Equal(2, OverlayPage.FailuresBeforeClear);
        Assert.Contains("var POLL_MS = 500;", page);
        Assert.Contains("var FAILURES_BEFORE_CLEAR = 2;", page);
        Assert.Contains("var key = state.instance + ':' + state.version;", page);
        Assert.Contains("if (key !== shown) { render(state); shown = key; }", page);
        Assert.Contains("if (failures >= FAILURES_BEFORE_CLEAR) clear();", page);
        Assert.Contains("{ cache: 'no-store' }", page);
        Assert.Contains("state.showTableName", page);
        Assert.Contains("state.showRollValue", page);
    }

    [Fact]
    public void The_page_is_transparent_ascii_and_offers_the_tf_hooks()
    {
        var page = OverlayPage.Html("/state");
        Assert.All(page, c => Assert.True(c < 128, $"non-ASCII character U+{(int)c:X4} in the page"));
        Assert.Contains("background: transparent", page);
        Assert.Contains(".tf-overlay:empty { display: none; }", page);
        foreach (var hook in new[] { "tf-overlay", "tf-table-name", "tf-result", "tf-line", "tf-roll-value", "tf-roll-separator", "tf-set-heading", "tf-result-text", "tf-bold", "tf-italic" })
            Assert.Contains($"'{hook}'", page.Replace("\"tf-overlay\"", "'tf-overlay'"));
        Assert.DoesNotContain("@keyframes", page);
        Assert.DoesNotContain("transition", page);
    }

    [Fact]
    public void The_local_file_is_written_into_the_data_folder_and_rewritten_for_a_new_port()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"tableforge-overlay-{Guid.NewGuid():N}", "nested");
        try
        {
            var path = OverlayPage.WriteLocalFile(folder, 41285);
            Assert.Equal(Path.Combine(Path.GetFullPath(folder), "streaming-overlay.html"), path);
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..]);
            Assert.Equal(OverlayPage.Html("http://127.0.0.1:41285/state"), Encoding.UTF8.GetString(bytes));

            Assert.Equal(path, OverlayPage.WriteLocalFile(folder, 43000));
            Assert.Contains("http://127.0.0.1:43000/state", File.ReadAllText(path));
            Assert.DoesNotContain("41285", File.ReadAllText(path));
            Assert.Equal(["streaming-overlay.html"], Directory.GetFiles(folder).Select(f => Path.GetFileName(f)!).ToArray());   // no temp file left
        }
        finally { Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true); }
    }

    [Fact]
    public void The_local_file_goes_wherever_the_data_folder_is_including_tableforge_data_dir()
    {
        var overridden = Path.Combine(Path.GetTempPath(), $"tableforge-data-{Guid.NewGuid():N}");
        try
        {
            var folder = Data.AppDatabase.ResolveDataFolder(overridden);
            var path = OverlayPage.WriteLocalFile(folder, 41285);
            Assert.Equal(Path.Combine(overridden, "streaming-overlay.html"), path);
        }
        finally { if (Directory.Exists(overridden)) Directory.Delete(overridden, recursive: true); }
    }
}
