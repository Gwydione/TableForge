using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using TableForge.Data;
using TableForge.Domain;
using TableForge.Portable;

namespace TableForge.Tests;

/// <summary>Collections for the Portable Collection tests, and ways to compare databases without ever comparing their ids.</summary>
internal static class PortableFixtures
{
    public static TableEntry E(int min, int max, string text, string? display = null, TextStyles? styles = null, long? link = null,
        string? unresolved = null) =>
        new() { Min = min, Max = max, Text = text, DisplayRange = display, Styles = styles ?? TextStyles.Empty, LinkedTableId = link, UnresolvedLinkName = unresolved };

    public static ResultSet Set(string name, params TableEntry[] entries) => new() { Name = name, Entries = [.. entries] };

    public static TextStyles Runs(int textLength, params (int Start, int Length, TextStyle Style)[] runs) =>
        TextStyles.FromRuns(runs.Select(r => new StyleRun(r.Start, r.Length, r.Style)), textLength);

    public static RollableTable Save(AppDatabase db, long collectionId, string name, string dice, long? folderId, params ResultSet[] sets) =>
        db.SaveTable(new RollableTable { CollectionId = collectionId, Name = name, Dice = DiceExpression.Parse(dice), FolderId = folderId, ResultSets = [.. sets] });

    /// <summary>
    /// "Mythic GME": everything a Collection can hold in RC24 — folders (one empty), Unfiled tables, d20, 2d6+1, d66, d100 written
    /// forms (01–07, 08, 96–00, 00), several result sets, negative, extended and open-ended ranges, Clamp, bold, italic, both, a
    /// formatted emoji (a surrogate pair), inline dice, a link, a self-link, a cycle, an unresolved link and two tables with one name.
    /// </summary>
    public static Collection SeedMythic(AppDatabase db, string name = "Mythic GME")
    {
        var c = db.CreateCollection(name);
        var oracles = db.CreateFolder(c.Id, "Oracles");
        db.CreateFolder(c.Id, "Empty Folder");
        var combat = db.CreateFolder(c.Id, "Combat");

        const string rain = "Rain for 1d4 hours, then 2d6 minutes of fog";
        var weather = Save(db, c.Id, "Weather", "d20", null,
            Set("", E(1, 10, "Clear skies"),
                E(11, 17, rain, styles: Runs(rain.Length, (0, 4, TextStyle.Bold), (14, 5, TextStyle.Italic))),
                E(18, 19, "Storm", styles: Runs(5, (0, 5, TextStyle.Italic))),
                E(20, 20, "Hurricane!", styles: Runs(10, (0, 9, TextStyle.Bold | TextStyle.Italic)))));

        Save(db, c.Id, "Encounter", "2d6+1", oracles.Id,
            Set("Who", E(3, 6, "Bandits"), E(7, 10, "Merchants", link: weather.Id), E(11, 13, "A dragon", unresolved: "Dragons")),
            Set("What", E(3, 8, "Ambush"), E(9, 13, "Parley")));

        Save(db, c.Id, "Mishap", "d66", null, Set("", E(11, 36, "Slip"), E(41, 66, "Fall")));

        Save(db, c.Id, "Loot", "d100", oracles.Id,
            Set("Coin", E(1, 7, "Copper", "01–07"), E(8, 8, "Silver", "08"), E(9, 95, "Nothing"), E(96, 100, "Gold", "96–00")),
            Set("Jackpot", E(1, 99, "No"), E(100, 100, "Yes", "00")));

        db.SaveTable(new RollableTable
        {
            CollectionId = c.Id, Name = "Morale", Dice = DiceExpression.Parse("d20"), FolderId = combat.Id, ClampResultsToRange = true,
            ResultSets = [Set("", E(3, 10, "Flee"), E(11, 18, "Fight"))],
        });

        Save(db, c.Id, "Critical", "d20", combat.Id,
            Set("Effect", E(RangeBounds.OpenBelow, -5, "Instant death", "-5 or less"), E(-4, 0, "Maimed"), E(1, 20, "Wounded"),
                E(21, 25, "Scratch"), E(26, RangeBounds.OpenAbove, "Unharmed", "26+")),
            Set("Note", E(RangeBounds.OpenBelow, 1, "Bad", "1 or less"), E(2, 19, "Ok"), E(20, RangeBounds.OpenAbove, "Great", "20 or more")));

        var chain = Save(db, c.Id, "Chain", "d6", null, Set("", E(1, 3, "Again"), E(4, 6, "Stop")));
        chain.ResultSets[0].Entries[0].LinkedTableId = chain.Id; // a self-link
        db.SaveTable(chain);

        var ping = Save(db, c.Id, "Ping", "d4", null, Set("", E(1, 4, "Ping row")));
        var pong = Save(db, c.Id, "Pong", "d4", null, Set("", E(1, 4, "Pong row", link: ping.Id)));
        ping.ResultSets[0].Entries[0].LinkedTableId = pong.Id; // Ping -> Pong -> Ping
        db.SaveTable(ping);

        Save(db, c.Id, "Rumors", "d6", oracles.Id, Set("", E(1, 6, "The king is ill")));
        Save(db, c.Id, "Rumors", "d6", null, Set("", E(1, 6, "The bridge is out")));

        const string dragon = "A 🐉 sleeps";
        Save(db, c.Id, "Dragon", "d4", null, Set("", E(1, 4, dragon, styles: Runs(dragon.Length, (2, 2, TextStyle.Bold)))));
        return c;
    }

    /// <summary>A second, unrelated Collection that must never be exported with, or changed by, anything done to another.</summary>
    public static Collection SeedOther(AppDatabase db, string name = "Other Game")
    {
        var c = db.CreateCollection(name);
        var folder = db.CreateFolder(c.Id, "Oracles");
        var target = Save(db, c.Id, "Names", "d6", folder.Id, Set("", E(1, 6, "Ann")));
        Save(db, c.Id, "Towns", "d8", null, Set("", E(1, 4, "Ford"), E(5, 8, "Bridge", link: target.Id)));
        return c;
    }

    /// <summary>
    /// Everything a Collection means, as text, with no database id in it: its name, folders, and each table (in TableForge's own
    /// order) with its folder, dice, clamp, result sets and rows — open bounds as "open", formatting, and links as the POSITION of
    /// their destination in that order (or "outside"), never an id.
    /// </summary>
    public static string Describe(AppDatabase db, long collectionId, bool includeName = true)
    {
        var sb = new StringBuilder();
        if (includeName) sb.AppendLine($"collection {db.GetCollections().Single(c => c.Id == collectionId).Name}");
        foreach (var f in db.GetFolders(collectionId)) sb.AppendLine($"folder {f.Name}");
        var summaries = db.GetTableSummaries(collectionId);
        var position = summaries.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => x.i);
        foreach (var summary in summaries)
        {
            var t = db.LoadTable(summary.Id)!;
            sb.AppendLine($"table {t.Name} | folder {summary.FolderName} | dice {t.Dice} ({t.Dice.Count},{t.Dice.Sides},{t.Dice.Modifier},{t.Dice.Convention}) | clamp {t.ClampResultsToRange}");
            foreach (var set in t.ResultSets)
            {
                sb.AppendLine($"  set [{set.Name}]");
                foreach (var e in set.Entries)
                {
                    var link = e.LinkedTableId is { } id ? position.TryGetValue(id, out var p) ? $"table #{p}" : "outside" : "none";
                    sb.AppendLine($"    {(e.IsOpenBelow ? "open" : e.Min)}..{(e.IsOpenAbove ? "open" : e.Max)} display [{e.DisplayRange ?? "null"}] " +
                                  $"text [{e.Text}] styles [{e.Styles}] link {link} unresolved [{e.UnresolvedLinkName ?? "null"}]");
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>Every row of every TableForge table (and SQLite's id counters), as text: equal snapshots mean an unchanged database.</summary>
    public static string Snapshot(string path)
    {
        using var raw = Raw(path);
        var sb = new StringBuilder();
        foreach (var table in new[] { "Collections", "Folders", "Tables", "ResultSets", "Entries", "RollHistory", "sqlite_sequence" })
        {
            using var cmd = raw.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table} ORDER BY rowid";
            using var reader = cmd.ExecuteReader();
            sb.AppendLine($"[{table}]");
            while (reader.Read())
                sb.AppendLine(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString())));
        }
        return sb.ToString();
    }

    public static SqliteConnection Raw(string path)
    {
        var raw = new SqliteConnection($"Data Source={path};Pooling=False");
        raw.Open();
        return raw;
    }

    public static void Execute(string path, string sql)
    {
        using var raw = Raw(path);
        using var cmd = raw.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static long Count(string path, string sql)
    {
        using var raw = Raw(path);
        using var cmd = raw.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>Exports a Collection, failing the test (with the reasons) if it is refused.</summary>
    public static byte[] ExportBytes(AppDatabase db, Collection collection)
    {
        var export = PortableCollectionExporter.Export(db, collection);
        Assert.True(export.Succeeded, string.Join("\n", export.Problems));
        return export.Json!;
    }

    /// <summary>Reads a file's bytes, failing the test (with the reasons) if it is refused.</summary>
    public static PortableCollection Read(byte[] bytes)
    {
        var read = PortableCollectionReader.Read(bytes);
        Assert.True(read.Succeeded, $"{read.Failure}: {string.Join("\n", read.Problems)}");
        return read.Collection!;
    }

    public static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    /// <summary>A temporary file, removed afterwards.</summary>
    public sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"tableforge-test-{Guid.NewGuid():N}.tfcollection");
        public void Dispose() { if (File.Exists(Path)) File.Delete(Path); }
    }
}
