using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TableForge.Export.Pum;

/// <summary>
/// A <c>.pumb</c> bundle, written exactly in the layout PUM Companion has imported (the reference bundle and fixtures F01–F10R):
/// the 11 ASCII bytes <c>PUM_ARCHIVE</c>, then a ZIP whose offsets count from the ZIP's own start. It holds one
/// <c>Name/Name</c> file per table (the table as compact UTF-8 JSON, no byte-order mark) and then <c>tables.json</c> (the list of
/// table metadata). Every entry is deflated and marked as made on Unix with mode 0600; a name that isn't plain ASCII carries the
/// ZIP UTF-8 flag. The ZIP is written here rather than by <see cref="ZipArchive"/>, which can't mark entries as made on Unix.
/// </summary>
public static class PumBundleWriter
{
    public static ReadOnlySpan<byte> Prefix => "PUM_ARCHIVE"u8;

    public static byte[] Write(IReadOnlyList<PumTable> tables, DateTime timestamp)
    {
        var files = tables.Select(t => ($"{t.Name}/{t.Name}", TableJson(t))).Append(("tables.json", MetadataJson(tables))).ToList();
        return [.. Prefix, .. Zip(files, timestamp)];
    }

    /// <summary>One table: <c>{"name":…,"type":"weighted","entries":{"4":…,"8":…},"formula":"1d8"}</c>.</summary>
    public static byte[] TableJson(PumTable table) => Json(w =>
    {
        w.WriteStartObject();
        w.WriteString("name", table.Name);
        w.WriteString("type", "weighted");
        w.WriteStartObject("entries");
        foreach (var row in table.Rows) w.WriteString(row.Key.ToString(CultureInfo.InvariantCulture), row.Text);
        w.WriteEndObject();
        w.WriteString("formula", table.Formula);
        w.WriteEndObject();
    });

    /// <summary><c>{"tables":[…]}</c>, each with the same fields, in the same order, as the reference bundle. Descriptions stay empty.</summary>
    public static byte[] MetadataJson(IReadOnlyList<PumTable> tables) => Json(w =>
    {
        w.WriteStartObject();
        w.WriteStartArray("tables");
        foreach (var table in tables)
        {
            w.WriteStartObject();
            w.WriteString("id", table.Id);
            w.WriteString("name", table.Name);
            w.WriteString("classification", table.Category);
            w.WriteNull("gameExclusiveId");
            w.WriteNull("campaignExclusiveId");
            w.WriteString("author", "");
            w.WriteString("version", "");
            w.WriteString("description", "");
            w.WriteString("formula", table.Formula);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    });

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            write(writer);
        return stream.ToArray();
    }

    /// <summary>A plain ZIP of <paramref name="files"/>, in order, each deflated and stamped with <paramref name="timestamp"/>.</summary>
    public static byte[] Zip(IReadOnlyList<(string Path, byte[] Data)> files, DateTime timestamp)
    {
        const ushort Version = 20;                  // 2.0: deflate
        const ushort MadeByUnix = (3 << 8) | Version;
        const ushort Utf8Name = 0x0800;
        const uint UnixMode0600 = 0x0180u << 16;    // mode 0600 in the high half of the external attributes, exactly as the reference
        var (time, date) = DosDateTime(timestamp);

        using var zip = new MemoryStream();
        var central = new MemoryStream();
        foreach (var (path, data) in files)
        {
            var name = Encoding.UTF8.GetBytes(path);
            var flags = path.All(char.IsAscii) ? (ushort)0 : Utf8Name;
            var compressed = Deflate(data);
            var crc = Crc32(data);
            var offset = (uint)zip.Position;

            Write(zip, 0x04034b50u, Version, flags, (ushort)8, time, date, crc, (uint)compressed.Length, (uint)data.Length, (ushort)name.Length, (ushort)0);
            zip.Write(name);
            zip.Write(compressed);

            Write(central, 0x02014b50u, MadeByUnix, Version, flags, (ushort)8, time, date, crc, (uint)compressed.Length, (uint)data.Length,
                (ushort)name.Length, (ushort)0, (ushort)0, (ushort)0, (ushort)0, UnixMode0600, offset);
            central.Write(name);
        }

        var centralOffset = (uint)zip.Position;
        central.Position = 0;
        central.CopyTo(zip);
        Write(zip, 0x06054b50u, (ushort)0, (ushort)0, (ushort)files.Count, (ushort)files.Count, (uint)central.Length, centralOffset, (ushort)0);
        return zip.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)) deflate.Write(data);
        return output.ToArray();
    }

    /// <summary>MS-DOS time and date, as ZIP stores them (two-second resolution).</summary>
    private static (ushort Time, ushort Date) DosDateTime(DateTime t) =>
        ((ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2)), (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day));

    /// <summary>Little-endian fields, in order: each <see cref="ushort"/> is 2 bytes and each <see cref="uint"/> 4.</summary>
    private static void Write(Stream stream, params object[] fields)
    {
        Span<byte> buffer = stackalloc byte[4];
        foreach (var field in fields)
        {
            switch (field)
            {
                case ushort u16:
                    BinaryPrimitives.WriteUInt16LittleEndian(buffer, u16);
                    stream.Write(buffer[..2]);
                    break;
                case uint u32:
                    BinaryPrimitives.WriteUInt32LittleEndian(buffer, u32);
                    stream.Write(buffer);
                    break;
                default:
                    throw new ArgumentException($"Unexpected ZIP field type {field.GetType()}.", nameof(fields));
            }
        }
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
