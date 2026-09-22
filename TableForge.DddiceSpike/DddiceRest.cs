using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TableForge.DddiceSpike;

public sealed class DddiceException(string message, HttpStatusCode? status = null, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Only what the spike needs from the dddice REST API. Base https://dddice.com/api/1.0, bearer token, JSON.</summary>
public sealed class DddiceRest : IDisposable
{
    public const string FreeGuestTheme = "dddice-bees";

    private readonly HttpClient _http;

    public DddiceRest(HttpMessageHandler? handler = null, string baseUrl = "https://dddice.com/api/1.0/")
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(baseUrl);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string? Token { get; set; }

    /// <summary>POST /user with no body creates an anonymous guest and returns {"type":"token","data":"..."}.</summary>
    public async Task<string> CreateGuestTokenAsync(CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Post, "user", new JsonObject(), auth: false, ct);
        Token = json["data"]?.GetValue<string>() ?? throw new DddiceException("dddice returned no guest token.");
        return Token;
    }

    /// <summary>POST /room. Returns the slug. The creating user becomes "Game Master" of the room.</summary>
    public async Task<string> CreateRoomAsync(string name, CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Post, "room", new JsonObject { ["name"] = name, ["is_public"] = false }, auth: true, ct);
        return json["data"]?["slug"]?.GetValue<string>() ?? throw new DddiceException("dddice returned no room slug.");
    }

    /// <summary>POST /roll. Server side RNG: the response already contains every die value and the total (no animation involved).</summary>
    public async Task<RestRoll> RollAsync(string room, IReadOnlyList<string> dice, string theme, JsonObject? operatorObject = null, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["room"] = room,
            ["dice"] = new JsonArray(dice.Select(d => (JsonNode)new JsonObject { ["type"] = d, ["theme"] = theme }).ToArray()),
        };
        if (operatorObject is not null) body["operator"] = operatorObject;
        var json = await SendAsync(HttpMethod.Post, "roll", body, auth: true, ct);
        var data = json["data"]!;
        var values = data["values"]!.AsArray().Select(v => new RestDie(v!["type"]!.GetValue<string>(), v["value"]!.GetValue<int>())).ToList();
        return new RestRoll(data["uuid"]!.GetValue<string>(), data["equation"]?.GetValue<string>() ?? "", data["total_value"]!.GetValue<int>(), values);
    }

    public async Task<JsonNode> GetAsync(string path, CancellationToken ct = default) => await SendAsync(HttpMethod.Get, path, null, auth: true, ct);

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body, bool auth, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (auth) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token ?? throw new DddiceException("No dddice token."));
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            using var response = await _http.SendAsync(request, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new DddiceException(Describe(response.StatusCode, path), response.StatusCode);
            return JsonNode.Parse(text) ?? throw new DddiceException("dddice returned an empty response.");
        }
        catch (DddiceException) { throw; }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new DddiceException($"dddice did not answer in time ({path}).", null, ex); }
        catch (HttpRequestException ex) { throw new DddiceException($"Could not reach dddice ({path}): {ex.Message}", null, ex); }
        catch (JsonException ex) { throw new DddiceException($"dddice sent something unreadable ({path}).", null, ex); }
    }

    private static string Describe(HttpStatusCode status, string path) => status switch
    {
        HttpStatusCode.Unauthorized => "dddice rejected the login token (expired or revoked).",
        HttpStatusCode.Forbidden => $"dddice refused this request (403, {path}) - for a guest this usually means a theme that needs an account.",
        HttpStatusCode.TooManyRequests => "dddice is rate limiting this connection (429).",
        _ => $"dddice answered {(int)status} {status} ({path}).",
    };

    public void Dispose() => _http.Dispose();
}

public sealed record RestDie(string Type, int Value);
public sealed record RestRoll(string Uuid, string Equation, int Total, IReadOnlyList<RestDie> Values);
