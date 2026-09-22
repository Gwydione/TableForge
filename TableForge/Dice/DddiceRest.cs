using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace TableForge.Dice;

/// <summary>
/// The two dddice REST calls TableForge makes itself: create a guest user (no account, no login) and create a room for it.
/// Base https://dddice.com/api/1.0, JSON. Everything else (rolling, the realtime connection) happens inside dddice-js.
/// Nothing sent here contains anything from a TableForge table.
/// </summary>
public sealed class DddiceRest : IDisposable
{
    private readonly HttpClient _http;

    public DddiceRest(HttpMessageHandler? handler = null, string baseUrl = "https://dddice.com/api/1.0/", TimeSpan? timeout = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(baseUrl);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>POST /user with no body: an anonymous guest. dddice limits this to about three a minute per address.</summary>
    public async Task<string> CreateGuestTokenAsync(CancellationToken ct)
    {
        var json = await SendAsync("user", new JsonObject(), token: null, ct);
        return json["data"]?.GetValue<string>() ?? throw new DddiceException("dddice did not give a guest login.");
    }

    /// <summary>POST /room. The guest becomes the room's "Game Master". The name is generic on purpose.</summary>
    public async Task<string> CreateRoomAsync(string token, CancellationToken ct)
    {
        var json = await SendAsync("room", new JsonObject { ["name"] = "TableForge", ["is_public"] = false }, token, ct);
        return json["data"]?["slug"]?.GetValue<string>() ?? throw new DddiceException("dddice did not create a room.");
    }

    private async Task<JsonNode> SendAsync(string path, JsonNode body, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw Describe(response.StatusCode, path);
            return JsonNode.Parse(text) ?? throw new DddiceException("dddice sent an empty answer.");
        }
        catch (DddiceException) { throw; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new DddiceException("dddice did not answer in time.", inner: null); }
        catch (HttpRequestException ex) { throw new DddiceException("TableForge could not reach dddice. Check the internet connection.", inner: ex); }
        catch (System.Text.Json.JsonException ex) { throw new DddiceException("dddice sent an answer TableForge could not read.", inner: ex); }
    }

    internal static DddiceException Describe(HttpStatusCode status, string path) => status switch
    {
        HttpStatusCode.TooManyRequests => new DddiceException("dddice is limiting new guest sessions right now. Wait a minute and try again."),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new DddiceException($"dddice refused the guest login ({(int)status}).", isAuthProblem: true),
        _ => new DddiceException($"dddice answered with an error ({(int)status}) while contacting {path}."),
    };

    public void Dispose() => _http.Dispose();
}
