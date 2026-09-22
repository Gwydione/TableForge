using System.Text.Json;

namespace TableForge.Dice;

/// <summary>
/// Turns the stream of events from the dddice page into "this roll has settled". Pure logic with no WebView in it, so the rules
/// are tested directly:
///  - roll:started carries the faces (dddice rolls on its server first) but is NEVER a result: it is remembered and ignored;
///  - only roll:finished for OUR roll (matched by the external id TableForge attached) completes it;
///  - events for other rolls, and anything after the roll has been cancelled or timed out, change nothing.
/// </summary>
public sealed class DddiceRollTracker
{
    private Pending? _pending;

    private sealed class Pending(string externalId)
    {
        public string ExternalId { get; } = externalId;
        public TaskCompletionSource<IReadOnlyList<DddiceFace>> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Started;
    }

    /// <summary>True between <see cref="Begin"/> and the roll settling, failing or being abandoned.</summary>
    public bool IsRolling => _pending is not null;

    /// <summary>True once dddice has said the dice are in the air (the faces are then known but must stay hidden).</summary>
    public bool HasStarted => _pending?.Started ?? false;

    /// <summary>Starts waiting for the roll tagged <paramref name="externalId"/>. Only one roll at a time.</summary>
    public Task<IReadOnlyList<DddiceFace>> Begin(string externalId)
    {
        if (_pending is not null) throw new InvalidOperationException("A dddice roll is already in progress.");
        _pending = new Pending(externalId);
        return _pending.Done.Task;
    }

    /// <summary>Stops waiting (cancelled or timed out). Late events for it are ignored.</summary>
    public void Abandon() => _pending = null;

    /// <summary>Fails the current roll with a readable reason.</summary>
    public void Fail(string message)
    {
        var p = _pending;
        _pending = null;
        p?.Done.TrySetException(new DddiceException(message));
    }

    /// <summary>Feeds one message from the page (JSON: {"kind": "...", "roll": {...}}). Unknown messages are ignored.</summary>
    public void OnPageMessage(string json)
    {
        var p = _pending;
        if (p is null) return;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("kind", out var kindElement)) return;
        var kind = kindElement.GetString();

        switch (kind)
        {
            case "rollError":
                Fail("dddice refused the roll: " + (root.TryGetProperty("message", out var m) ? m.GetString() : "unknown error"));
                return;

            case "roll:started" or "roll:finished":
                if (!root.TryGetProperty("roll", out var roll) || roll.ValueKind != JsonValueKind.Object || !IsOurs(p, roll)) return;
                if (kind == "roll:started") { p.Started = true; return; }   // the faces are known now; nothing is revealed yet

                var faces = ReadFaces(roll);
                _pending = null;
                p.Done.TrySetResult(faces);
                return;
        }
    }

    // dddice puts the external id we sent on the roll. A roll without one is not treated as ours only when there is a different id.
    private static bool IsOurs(Pending p, JsonElement roll) =>
        !roll.TryGetProperty("externalId", out var id) || id.ValueKind != JsonValueKind.String || id.GetString() == p.ExternalId;

    private static IReadOnlyList<DddiceFace> ReadFaces(JsonElement roll) =>
        roll.GetProperty("values").EnumerateArray()
            .Select(v => new DddiceFace(v.GetProperty("type").GetString()!, v.GetProperty("value").GetInt32()))
            .ToList();
}
