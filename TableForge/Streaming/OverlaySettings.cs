using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TableForge.Streaming;

/// <summary>The Streaming Overlay's own settings (<see cref="OverlaySettingsStore.FileName"/>). Off until the person turns it on.</summary>
public sealed record OverlaySettings(bool Enabled = false, int Port = OverlaySettings.DefaultPort, bool ShowTableName = true, bool ShowRollValue = true)
{
    /// <summary>IANA-unassigned, below Windows' dynamic port range, and no known common use (see the RC27 spike).</summary>
    public const int DefaultPort = 41285;
    public const int MinPort = 1024;
    public const int MaxPort = 65535;

    public static OverlaySettings Default { get; } = new();

    public static bool IsValidPort(int port) => port is >= MinPort and <= MaxPort;
}

/// <summary>
/// Reads and writes <see cref="FileName"/> beside the database (so TABLEFORGE_DATA_DIR keeps it with everything else). A missing,
/// unreadable or unexpected file gives the defaults, and a single bad value gives that value's default: it never stops
/// TableForge starting. Writes go to a temporary file first and then replace the old one.
/// </summary>
public sealed class OverlaySettingsStore(string dataFolder)
{
    public const string FileName = "streaming-overlay.json";
    private const int Format = 1;

    public string FilePath { get; } = Path.Combine(dataFolder, FileName);

    public OverlaySettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return OverlaySettings.Default;
            if (JsonNode.Parse(File.ReadAllText(FilePath)) is not JsonObject json || Number(json["format"]) != Format) return OverlaySettings.Default;
            var defaults = OverlaySettings.Default;
            var port = Number(json["port"]);
            return new OverlaySettings(
                Flag(json["enabled"]) ?? defaults.Enabled,
                port is { } p && OverlaySettings.IsValidPort(p) ? p : defaults.Port,
                Flag(json["showTableName"]) ?? defaults.ShowTableName,
                Flag(json["showRollValue"]) ?? defaults.ShowRollValue);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return OverlaySettings.Default;
        }
    }

    /// <summary>Saves <paramref name="settings"/>. Throws (IO or access errors) if it cannot; the old file is then left as it was.</summary>
    public void Save(OverlaySettings settings)
    {
        if (!OverlaySettings.IsValidPort(settings.Port)) throw new ArgumentOutOfRangeException(nameof(settings), "The port must be from 1024 to 65535.");
        var json = new JsonObject
        {
            ["format"] = Format,
            ["enabled"] = settings.Enabled,
            ["port"] = settings.Port,
            ["showTableName"] = settings.ShowTableName,
            ["showRollValue"] = settings.ShowRollValue,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        try { File.Move(temp, FilePath, overwrite: true); }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private static bool? Flag(JsonNode? node) => node is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
    private static int? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
}
