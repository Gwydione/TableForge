using TableForge.Domain;
using TableForge.Import;

namespace TableForge.Tests;

internal static class Fixtures
{
    /// <summary>The primary regression source, including the wrapped final row.</summary>
    public const string RandomStartingGear =
        "D10 RANDOM STARTING GEAR\n" +
        "1-2 Backpack\n" +
        "3 Knife\n" +
        "4 1x Torch (UD6)\n" +
        "5 Fishing rod\n" +
        "6 Rope (15 m)\n" +
        "7 Tinderbox\n" +
        "8 D4 Bandages\n" +
        "9-10 D20 Construction\n" +
        "Supplies";

    public static RollableTable ParseAndBuild(string source, long collectionId = 1)
    {
        var draft = TableTextParser.Parse(source);
        Assert.True(draft.TryBuildTable(collectionId, out var table, out var errors), string.Join("; ", errors));
        return table!;
    }

    private static TableEntry E(int min, int max, string text, string? display = null, long? link = null, string? unresolved = null) =>
        new() { Min = min, Max = max, Text = text, DisplayRange = display, LinkedTableId = link, UnresolvedLinkName = unresolved };

    private static ResultSet Set(string name, params TableEntry[] entries) => new() { Name = name, Entries = [.. entries] };

    /// <summary>ROOM FEATURES (d100): three independently ranged result sets. A roll of 68 gives burning flesh / Hissing / Grated floors.</summary>
    public static RollableTable RoomFeatures(long collectionId = 1) => new()
    {
        CollectionId = collectionId,
        Name = "Room Features",
        Dice = DiceExpression.Parse("d100"),
        ResultSets =
        [
            Set("Ambient",
                E(1, 30, "Cold stale air", "01–30"), E(31, 65, "Damp stone"),
                E(66, 70, "Smell of burning flesh"), E(71, 100, "Heavy incense", "71–00")),
            Set("Noise",
                E(1, 25, "Silence", "01–25"), E(26, 50, "Distant scratching"), E(51, 66, "Dripping water"),
                E(67, 71, "Hissing"), E(72, 100, "Low chanting", "72–00")),
            Set("General Feature",
                E(1, 40, "Cracked stone walls", "01–40"), E(41, 67, "Broken furniture"),
                E(68, 69, "Grated floors reveal dozens of people below"), E(70, 100, "Carved pillars", "70–00")),
        ],
    };

    /// <summary>SCAVENGED ITEMS (d20).</summary>
    public static RollableTable ScavengedItems(long collectionId = 1) => new()
    {
        CollectionId = collectionId,
        Name = "Scavenged Items",
        Dice = DiceExpression.Parse("d20"),
        ResultSets =
        [
            Set("", E(1, 4, "Rusty nails"), E(5, 8, "Torn cloth"), E(9, 12, "D4 rations"),
                E(13, 16, "Waterskin"), E(17, 19, "Small knife"), E(20, 20, "Silver coin")),
        ],
    };

    /// <summary>SCAVENGING (2d6): the 6–8 and 9–10 rows link to the given destination, or carry an unresolved name.</summary>
    public static RollableTable Scavenging(long collectionId, long? itemsTableId, string? unresolvedName = null) => new()
    {
        CollectionId = collectionId,
        Name = "Scavenging",
        Dice = DiceExpression.Parse("2d6"),
        ResultSets =
        [
            Set("", E(2, 5, "Nothing useful"),
                E(6, 8, "1x Scavenged Item", link: itemsTableId, unresolved: unresolvedName),
                E(9, 10, "2x Scavenged Items", link: itemsTableId, unresolved: unresolvedName),
                E(11, 12, "Valuable find")),
        ],
    };

    /// <summary>Saves Scavenged Items, then Scavenging linking to it. Returns both, with ids assigned.</summary>
    public static (RollableTable Items, RollableTable Scavenging) SeedScavenging(TableForge.Data.AppDatabase db, long collectionId)
    {
        var items = db.SaveTable(ScavengedItems(collectionId));
        var scavenging = db.SaveTable(Scavenging(collectionId, items.Id));
        return (items, scavenging);
    }

    public static RollableTable Table(DiceExpression dice, params (int Min, int Max, string Text)[] entries) => new()
    {
        Name = "Test",
        Dice = dice,
        ResultSets =
        [
            new ResultSet
            {
                Entries = entries.Select((e, i) => new TableEntry { Min = e.Min, Max = e.Max, Text = e.Text, SortOrder = i }).ToList(),
            },
        ],
    };
}
