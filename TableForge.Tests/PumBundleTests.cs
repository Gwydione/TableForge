using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using TableForge.Domain;
using TableForge.Export.Pum;

namespace TableForge.Tests;

/// <summary>
/// The <c>.pumb</c> bytes: checked against the reference bundle PUM Companion is known to import
/// (<c>TableForge_PUM_Import_Test.pumb</c>, the layout of every F01–F10R fixture), field by field, and read back independently.
/// </summary>
public class PumBundleTests
{
    /// <summary>The known-good reference bundle (598 bytes): one simple 1d6 table, made 2026-10-19 13:58:58.</summary>
    private static readonly byte[] Reference = Convert.FromBase64String(
        "UFVNX0FSQ0hJVkVQSwMEFAAAAAgAXW9TXT/FbBB/AAAABQEAACkAAABURiBJTVBPUlQgVEVTVCAtIDFkNi9URiBJTVBPUlQgVEVTVCAtIDFkNnXPTQqDMBAF4KsMs7bQ+LdwX6ELabG5QEqnRUhUknEh4t07OcAsH997i3fg7AJhh7aH+/B8jBbs7WXhAubTYoG8r1nTFFZPkmnmOFHC7sBrXrm3p36JPwKmxBApbZ7BSNPoXAqXOlfClc61cK1zI9zo3OJZ4HeJYfNOWvnl+QdQSwMEFAAAAAgAXW9TXbjAAnC2AAAAAQEAAAsAAAB0YWJsZXMuanNvbm2PPQ+CQAyG/0pzsxpdHNwlYTAauc04lKNgk4Mjd4VICP/dEuPm2L4fTzsbwdJTMqfHbLgyJyO1C22Psj+YjemwJd3ZDPLL7Xq3YM+FhS0cqqOqzmNKXLND4dCtvrUrC7EhyNs+RAFLSdTZaM/57fyQeKRcMd3gvRagkrjp/kg4yCtE7dT0SDF9ATpUlFzk/keklYNxgu/VXLJnmUCUu4MCawIJUJEnoZ2m6xDbwaMm1xeW5/IBUEsBAhQDFAAAAAgAXW9TXT/FbBB/AAAABQEAACkAAAAAAAAAAAAAAIABAAAAAFRGIElNUE9SVCBURVNUIC0gMWQ2L1RGIElNUE9SVCBURVNUIC0gMWQ2UEsBAhQDFAAAAAgAXW9TXbjAAnC2AAAAAQEAAAsAAAAAAAAAAAAAAIABxgAAAHRhYmxlcy5qc29uUEsFBgAAAAACAAIAkAAAAKUBAAAAAA==");

    private static readonly DateTime ReferenceStamp = new(2026, 10, 19, 13, 58, 58);
    private static readonly DateTime Stamp = new(2026, 10, 24, 12, 0, 0);

    /// <summary>What a ZIP's central directory says about one entry, read straight from the bytes.</summary>
    private sealed record CentralEntry(string Name, ushort MadeBy, ushort Needed, ushort Flags, ushort Method, ushort Time, ushort Date,
        uint Crc, uint ExternalAttributes, int ExtraLength, int CommentLength, uint LocalOffset);

    private static byte[] ZipPart(byte[] bundle)
    {
        Assert.True(bundle.AsSpan().StartsWith("PUM_ARCHIVE"u8), "the bundle starts with PUM_ARCHIVE");
        return bundle[11..];
    }

    private static List<CentralEntry> Central(byte[] zip)
    {
        var end = zip.Length - 22;
        Assert.Equal(0x06054b50u, BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(end + 20)));   // no ZIP comment
        var count = BinaryPrimitives.ReadUInt16LittleEndian(zip.AsSpan(end + 10));
        var at = (int)BinaryPrimitives.ReadUInt32LittleEndian(zip.AsSpan(end + 16));
        var entries = new List<CentralEntry>();
        for (var i = 0; i < count; i++)
        {
            var s = zip.AsSpan(at);
            Assert.Equal(0x02014b50u, BinaryPrimitives.ReadUInt32LittleEndian(s));
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(s[28..]);
            var extra = BinaryPrimitives.ReadUInt16LittleEndian(s[30..]);
            var comment = BinaryPrimitives.ReadUInt16LittleEndian(s[32..]);
            entries.Add(new(Encoding.UTF8.GetString(s.Slice(46, nameLength)),
                BinaryPrimitives.ReadUInt16LittleEndian(s[4..]), BinaryPrimitives.ReadUInt16LittleEndian(s[6..]),
                BinaryPrimitives.ReadUInt16LittleEndian(s[8..]), BinaryPrimitives.ReadUInt16LittleEndian(s[10..]),
                BinaryPrimitives.ReadUInt16LittleEndian(s[12..]), BinaryPrimitives.ReadUInt16LittleEndian(s[14..]),
                BinaryPrimitives.ReadUInt32LittleEndian(s[16..]), BinaryPrimitives.ReadUInt32LittleEndian(s[38..]),
                extra, comment, BinaryPrimitives.ReadUInt32LittleEndian(s[42..])));
            at += 46 + nameLength + extra + comment;
        }
        return entries;
    }

    /// <summary>Every file in the bundle, read by .NET's own ZIP reader (which checks each CRC), in order.</summary>
    private static List<(string Name, byte[] Data)> Files(byte[] bundle)
    {
        using var archive = new ZipArchive(new MemoryStream(ZipPart(bundle)), ZipArchiveMode.Read);
        return archive.Entries.Select(e =>
        {
            using var stream = e.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return (e.FullName, copy.ToArray());
        }).ToList();
    }

    private static RollableTable Table(string name, string dice, params (int Min, int Max, string Text)[] rows) => new()
    {
        Name = name, Dice = DiceExpression.Parse(dice),
        ResultSets = [new ResultSet { Entries = [.. rows.Select(r => new TableEntry { Min = r.Min, Max = r.Max, Text = r.Text })] }],
    };

    private static byte[] Export(params RollableTable[] tables) =>
        PumExporter.Export(new PumExportRequest("Bundle Tests", tables.Select((t, i) => new PumSourceTable(i, t)).ToList()), Stamp).Bundle!;

    // ---- The ZIP layer, against the reference ------------------------------------------------------------------------

    /// <summary>A local file header's fields, read straight from the bytes (the compressed size is left out).</summary>
    private static (ushort Needed, ushort Flags, ushort Method, ushort Time, ushort Date, uint Crc, uint Size, string Name, int Extra) Local(byte[] zip, uint offset)
    {
        var s = zip.AsSpan((int)offset);
        Assert.Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(s));
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(s[26..]);
        return (BinaryPrimitives.ReadUInt16LittleEndian(s[4..]), BinaryPrimitives.ReadUInt16LittleEndian(s[6..]), BinaryPrimitives.ReadUInt16LittleEndian(s[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(s[10..]), BinaryPrimitives.ReadUInt16LittleEndian(s[12..]), BinaryPrimitives.ReadUInt32LittleEndian(s[14..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[22..]), Encoding.UTF8.GetString(s.Slice(30, nameLength)), BinaryPrimitives.ReadUInt16LittleEndian(s[28..]));
    }

    [Fact]
    public void Rebuilt_from_its_contents_the_reference_bundle_matches_in_every_field_and_byte_of_content()
    {
        // .NET's deflate compresses differently from the zlib that wrote the reference (126 bytes instead of 127 for the first
        // file), so the compressed data, its sizes and the offsets after it differ. Everything else must be identical.
        var files = Files(Reference);
        Assert.Equal(["TF IMPORT TEST - 1d6/TF IMPORT TEST - 1d6", "tables.json"], files.Select(f => f.Name));
        byte[] rebuilt = [.. PumBundleWriter.Prefix, .. PumBundleWriter.Zip(files, ReferenceStamp)];

        Assert.Equal(Reference[..11], rebuilt[..11]);
        var (expected, actual) = (Central(ZipPart(Reference)), Central(ZipPart(rebuilt)));
        Assert.Equal(expected.Select(e => e with { LocalOffset = 0 }), actual.Select(e => e with { LocalOffset = 0 }));
        for (var i = 0; i < expected.Count; i++)
            Assert.Equal(Local(ZipPart(Reference), expected[i].LocalOffset), Local(ZipPart(rebuilt), actual[i].LocalOffset));
        Assert.Equal(files, Files(rebuilt), new FileComparer());
    }

    [Fact]
    public void Every_entry_of_an_export_has_exactly_the_reference_entries_header_fields()
    {
        var reference = Central(ZipPart(Reference))[0];
        var bundle = Export(Table("Plain", "d6", (1, 6, "x")), Table("King’s Road – En Dash", "d6", (1, 6, "y")));
        var entries = Central(ZipPart(bundle));

        Assert.Equal(["Plain/Plain", "King’s Road – En Dash/King’s Road – En Dash", "tables.json"], entries.Select(e => e.Name));
        foreach (var e in entries)
        {
            Assert.Equal((reference.MadeBy, reference.Needed, reference.Method, reference.ExternalAttributes), (e.MadeBy, e.Needed, e.Method, e.ExternalAttributes));
            Assert.Equal((0x0314, 20, 8, 0x180u << 16), (e.MadeBy, e.Needed, e.Method, e.ExternalAttributes));   // made on Unix, deflate, mode 0600
            Assert.Equal((0, 0), (e.ExtraLength, e.CommentLength));
            // Plain ASCII names have no flags, like the reference; others carry the UTF-8 flag, like the imported F09 fixtures.
            Assert.Equal(e.Name.All(char.IsAscii) ? 0 : 0x0800, e.Flags);
        }
        Assert.Equal(0u, entries[0].LocalOffset);   // offsets count from the ZIP itself, after the PUM_ARCHIVE prefix
    }

    [Fact]
    public void The_stored_date_and_time_are_the_requested_ones()
    {
        var e = Central(ZipPart(Export(Table("T", "d6", (1, 6, "x")))))[0];
        Assert.Equal(((12 << 11) | (0 << 5) | 0, ((2026 - 1980) << 9) | (10 << 5) | 24), ((int)e.Time, (int)e.Date));
    }

    // ---- The JSON, against the reference ------------------------------------------------------------------------------

    [Fact]
    public void Each_table_file_is_compact_UTF8_JSON_with_the_reference_fields_in_order_and_weighted_string_keys()
    {
        var referenceTable = JsonDocument.Parse(Files(Reference)[0].Data).RootElement;
        var file = Files(Export(Table("Loot", "2d6+1", (3, 7, "Copper & “silver” – 5 < 6"), (8, 13, "Gold"))))[0];

        Assert.False(file.Data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]), "no byte-order mark");
        Assert.Equal("{\"name\":\"Loot\",\"type\":\"weighted\",\"entries\":{\"2\":\"(No result)\",\"7\":\"Copper & “silver” – 5 < 6\",\"13\":\"Gold\",\"14\":\"(No result)\"},\"formula\":\"2d6+1\"}",
            Encoding.UTF8.GetString(file.Data));
        Assert.Equal(referenceTable.EnumerateObject().Select(p => p.Name), JsonDocument.Parse(file.Data).RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public void The_metadata_file_lists_every_table_with_the_reference_fields_in_order_and_is_the_last_entry()
    {
        var referenceMeta = JsonDocument.Parse(Files(Reference)[^1].Data).RootElement.GetProperty("tables")[0];
        var files = Files(Export(Table("A", "d6", (1, 6, "x")), Table("B", "d66", (11, 66, "y"))));

        Assert.Equal("tables.json", files[^1].Name);
        Assert.Equal(3, files.Count);
        var tables = JsonDocument.Parse(files[^1].Data).RootElement.GetProperty("tables").EnumerateArray().ToList();
        Assert.Equal(["A", "B"], tables.Select(t => t.GetProperty("name").GetString()));
        Assert.Equal(["1d6", "1D66"], tables.Select(t => t.GetProperty("formula").GetString()));
        foreach (var t in tables)
        {
            Assert.Equal(referenceMeta.EnumerateObject().Select(p => p.Name), t.EnumerateObject().Select(p => p.Name));
            Assert.Equal(JsonValueKind.Null, t.GetProperty("gameExclusiveId").ValueKind);
            Assert.Equal(JsonValueKind.Null, t.GetProperty("campaignExclusiveId").ValueKind);
            Assert.Equal("Bundle Tests", t.GetProperty("classification").GetString());
            Assert.Equal("", t.GetProperty("description").GetString());
        }
        Assert.Equal(["A/A", "B/B"], files.Take(2).Select(f => f.Name));
    }

    [Fact]
    public void Descriptions_are_never_written()
    {
        var table = Table("Described", "d6", (1, 6, "x"));
        table.Description = "Roll when the party camps.\nSECRET-DESCRIPTION";
        var bundle = Export(table);
        Assert.DoesNotContain(Files(bundle), f => Encoding.UTF8.GetString(f.Data).Contains("SECRET-DESCRIPTION"));
    }

    // ---- Determinism --------------------------------------------------------------------------------------------------

    [Fact]
    public void The_same_tables_and_time_always_give_the_same_bytes()
    {
        RollableTable[] Build() => [Fixtures.RoomFeatures(), Table("Gappy", "d8", (3, 4, "A"), (7, 8, "B")), Table("D66", "d66", (11, 36, "x"), (41, 66, "y"))];
        Assert.Equal(Export(Build()), Export(Build()));

        var later = PumExporter.Export(new PumExportRequest("Bundle Tests", Build().Select((t, i) => new PumSourceTable(i, t)).ToList()), Stamp.AddSeconds(2)).Bundle!;
        Assert.NotEqual(Export(Build()), later);
        Assert.Equal(Files(Export(Build())), Files(later), new FileComparer());   // only the stored time differs
    }

    private sealed class FileComparer : IEqualityComparer<(string Name, byte[] Data)>
    {
        public bool Equals((string Name, byte[] Data) x, (string Name, byte[] Data) y) => x.Name == y.Name && x.Data.AsSpan().SequenceEqual(y.Data);
        public int GetHashCode((string Name, byte[] Data) obj) => obj.Name.GetHashCode();
    }
}
