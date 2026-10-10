using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PokeStudio.Modules.N3ds;

/// <summary>
/// A decrypted 3DS game (.3ds / NCSD): the first partition's RomFS file tree and ExeFS code, read straight from the file
/// (these images are gigabytes, so nothing is loaded whole).
/// </summary>
internal sealed class CtrRom : IDisposable
{
    private readonly FileStream _file;
    private readonly Dictionary<string, (long Offset, long Size)> _files = new(StringComparer.Ordinal);
    private readonly long _ncch;

    public ulong TitleId { get; }
    public string TitleIdHex => TitleId.ToString("X16", CultureInfo.InvariantCulture);
    public IReadOnlyCollection<string> Paths => _files.Keys;

    public CtrRom(string path)
    {
        _file = File.OpenRead(path);
        if (Ascii(0x100, 4) != "NCSD")
        {
            throw new InvalidDataException("This isn't a 3DS game image (.3ds).");
        }

        _ncch = U32(0x120) * 0x200L;
        if (Ascii(_ncch + 0x100, 4) != "NCCH")
        {
            throw new InvalidDataException("The game's main partition couldn't be read.");
        }

        TitleId = U64(_ncch + 0x108);
        var flags = Read(_ncch + 0x188, 8);
        if ((flags[7] & 0x04) == 0)
        {
            throw new InvalidDataException("This 3DS game is still encrypted. Poke Studio needs a decrypted copy (made with your own console).");
        }

        var romfs = _ncch + (U32(_ncch + 0x1B0) * 0x200L);
        if (Ascii(romfs, 4) != "IVFC")
        {
            throw new InvalidDataException("The game's RomFS couldn't be read.");
        }

        var master = U32(romfs + 8);
        var log = (int)U32(romfs + 0x4C);
        var level3 = romfs + ((0x60 + master + (1L << log) - 1) & ~((1L << log) - 1));
        var header = Read(level3, 40);
        long H(int i) => BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4 * i));
        WalkDirectory(level3 + H(3), level3 + H(7), level3 + H(9), 0, "");
    }

    public void Dispose() => _file.Dispose();

    public bool Exists(string path) => _files.ContainsKey(path);

    public byte[] ReadFile(string path)
    {
        var (offset, size) = _files.TryGetValue(path, out var f) ? f : throw new FileNotFoundException($"The game has no file {path}.");
        return Read(offset, (int)size);
    }

    /// <summary>The program code (ExeFS ".code"), decompressed when stored compressed.</summary>
    public byte[] ReadCode()
    {
        var exefs = _ncch + (U32(_ncch + 0x1A0) * 0x200L);
        var header = Read(exefs, 0xA0);
        for (var i = 0; i < 10; i++)
        {
            var name = Encoding.ASCII.GetString(header, 16 * i, 8).TrimEnd('\0');
            if (name != ".code")
            {
                continue;
            }

            var offset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan((16 * i) + 8));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan((16 * i) + 12));
            var code = Read(exefs + 0x200 + offset, size);
            try
            {
                return Blz.Decompress(code, code.Length);
            }
            catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException)
            {
                return code; // not compressed
            }
        }

        return [];
    }

    private void WalkDirectory(long dirMeta, long fileMeta, long data, long dirOffset, string path)
    {
        var entry = Read(dirMeta + dirOffset, 24);
        var child = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(8));
        var firstFile = BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(12));
        var nameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.AsSpan(20));
        var name = nameLength > 0 ? Encoding.Unicode.GetString(Read(dirMeta + dirOffset + 24, nameLength)) : "";
        var full = dirOffset == 0 ? "" : path + name + "/";
        for (var file = firstFile; file != 0xFFFFFFFF;)
        {
            var fe = Read(fileMeta + file, 32);
            var sibling = BinaryPrimitives.ReadUInt32LittleEndian(fe.AsSpan(4));
            var offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(fe.AsSpan(8));
            var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(fe.AsSpan(16));
            var fileNameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(fe.AsSpan(28));
            _files[full + Encoding.Unicode.GetString(Read(fileMeta + file + 32, fileNameLength))] = (data + offset, size);
            file = sibling;
        }

        for (var dir = child; dir != 0xFFFFFFFF;)
        {
            WalkDirectory(dirMeta, fileMeta, data, dir, full);
            dir = BinaryPrimitives.ReadUInt32LittleEndian(Read(dirMeta + dir + 4, 4));
        }
    }

    private byte[] Read(long offset, int count)
    {
        var buffer = new byte[count];
        _file.Position = offset;
        _file.ReadExactly(buffer);
        return buffer;
    }

    private uint U32(long o) => BinaryPrimitives.ReadUInt32LittleEndian(Read(o, 4));
    private ulong U64(long o) => BinaryPrimitives.ReadUInt64LittleEndian(Read(o, 8));
    private string Ascii(long o, int n) => Encoding.ASCII.GetString(Read(o, n));
}

/// <summary>"Backwards LZ" (same scheme the DS used) for 3DS program code.</summary>
internal static class Blz
{
    public static byte[] Decompress(byte[] data, int end)
    {
        var footer = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(end - 8));
        var extra = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(end - 4));
        var compressedLength = (int)(footer & 0xFFFFFF);
        var headerLength = (int)(footer >> 24);
        if (headerLength is < 8 or > 16 || compressedLength > end)
        {
            throw new InvalidDataException("Not compressed.");
        }

        var output = new byte[end + extra];
        data.AsSpan(0, end).CopyTo(output);
        var src = end - headerLength;
        var dst = end + extra;
        var stop = end - compressedLength;
        while (src > stop)
        {
            var flags = data[--src];
            for (var bit = 0; bit < 8 && src > stop; bit++, flags <<= 1)
            {
                if ((flags & 0x80) == 0)
                {
                    output[--dst] = data[--src];
                    continue;
                }

                var b1 = data[--src];
                var b2 = data[--src];
                var length = (b1 >> 4) + 3;
                var distance = (((b1 & 0x0F) << 8) | b2) + 3;
                for (var i = 0; i < length; i++)
                {
                    dst--;
                    output[dst] = output[dst + distance];
                }
            }
        }

        return output;
    }
}

/// <summary>A GARC archive (versions 4/6): entries, each with one or more sub-files (language variants).</summary>
internal sealed class Garc
{
    private readonly ushort _version;

    /// <summary>Per entry: its sub-file bit vector and the sub-files' data.</summary>
    public List<(uint Bits, List<byte[]> Files)> Entries { get; } = [];

    public Garc(byte[] data)
    {
        if (Encoding.ASCII.GetString(data, 0, 4) != "CRAG")
        {
            throw new InvalidDataException("Not a GARC archive.");
        }

        var headerSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        _version = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(10));
        var dataOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
        var fato = headerSize;
        var fatoSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(fato + 4));
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(fato + 8));
        var fatb = fato + fatoSize;
        for (var i = 0; i < count; i++)
        {
            var e = fatb + 12 + (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(fato + 12 + (4 * i)));
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(e));
            var files = new List<byte[]>();
            var p = e + 4;
            for (var b = 0; b < 32; b++)
            {
                if ((bits >> b & 1) == 0)
                {
                    continue;
                }

                var start = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p));
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p + 8));
                files.Add(data.AsSpan(dataOffset + start, length).ToArray());
                p += 12;
            }

            Entries.Add((bits, files));
        }
    }

    /// <summary>The first sub-file of entry <paramref name="i"/> (data archives have exactly one).</summary>
    public byte[] this[int i]
    {
        get => Entries[i].Files[0];
        set => Entries[i].Files[0] = value;
    }

    public int Count => Entries.Count;

    public byte[] Build()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var fatoSize = 12 + (4 * Entries.Count);
        var fatbEntries = Entries.Select(e => 4 + (12 * e.Files.Count)).ToList();
        var fatbSize = 12 + fatbEntries.Sum();
        var headerSize = 0x1C;
        var dataOffset = headerSize + fatoSize + fatbSize + 12;
        var position = 0;
        var placed = new List<List<(int Start, int End, int Length)>>();
        var largest = 0;
        foreach (var (_, files) in Entries)
        {
            var list = new List<(int, int, int)>();
            foreach (var f in files)
            {
                var padded = (f.Length + 3) & ~3;
                list.Add((position, position + padded, f.Length));
                position += padded;
                largest = Math.Max(largest, f.Length);
            }

            placed.Add(list);
        }

        w.Write("CRAG"u8);
        w.Write(headerSize);
        w.Write((ushort)0xFEFF);
        w.Write(_version);
        w.Write(4);
        w.Write(dataOffset);
        w.Write(dataOffset + position);
        w.Write(largest);
        w.Write("OTAF"u8);
        w.Write(fatoSize);
        w.Write((ushort)Entries.Count);
        w.Write((ushort)0xFFFF);
        var offset = 0;
        foreach (var size in fatbEntries)
        {
            w.Write(offset);
            offset += size;
        }

        w.Write("BTAF"u8);
        w.Write(fatbSize);
        w.Write(Entries.Count);
        for (var i = 0; i < Entries.Count; i++)
        {
            w.Write(Entries[i].Bits);
            foreach (var (start, end, length) in placed[i])
            {
                w.Write(start);
                w.Write(end);
                w.Write(length);
            }
        }

        w.Write("BMIF"u8);
        w.Write(12);
        w.Write(position);
        foreach (var (_, files) in Entries)
        {
            foreach (var f in files)
            {
                w.Write(f);
                for (var pad = f.Length; pad % 4 != 0; pad++)
                {
                    w.Write((byte)0xFF);
                }
            }
        }

        return ms.ToArray();
    }
}

/// <summary>Gen 6 text: UTF-16, each line encrypted with a key that starts at 0x7C89 + line × 0x2983; strings end with 0.</summary>
internal static class Gen6Text
{
    public static List<string> Read(byte[] data)
    {
        var lines = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        var section = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        var result = new List<string>(lines);
        for (var i = 0; i < lines; i++)
        {
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(section + 4 + (8 * i)));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(section + 8 + (8 * i)));
            var key = (ushort)(0x7C89 + (i * 0x2983));
            var sb = new StringBuilder();
            for (var j = 0; j < length; j++)
            {
                var c = (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(section + offset + (2 * j))) ^ key);
                key = (ushort)((key << 3) | (key >> 13));
                if (c == 0)
                {
                    break;
                }

                if (c < 0x20 || c >= 0xE000 || c is '{' or '}')
                {
                    sb.Append('{').Append(c.ToString("X4", CultureInfo.InvariantCulture)).Append('}');
                }
                else
                {
                    sb.Append((char)c);
                }
            }

            result.Add(sb.ToString());
        }

        return result;
    }

    public static byte[] Write(IReadOnlyList<string> texts, byte[] original)
    {
        var unk = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(8));
        var encoded = texts.Select(Codes).ToList();
        var tableSize = 8 * texts.Count;
        var body = encoded.Sum(e => (2 * e.Length + 3) & ~3);
        var sectionSize = 4 + tableSize + body;
        var data = new byte[16 + sectionSize];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), (ushort)texts.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)sectionSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), unk);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), (uint)sectionSize);
        var offset = 4 + tableSize;
        for (var i = 0; i < texts.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16 + 4 + (8 * i)), (uint)offset);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16 + 8 + (8 * i)), (ushort)encoded[i].Length);
            var key = (ushort)(0x7C89 + (i * 0x2983));
            for (var j = 0; j < encoded[i].Length; j++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16 + offset + (2 * j)), (ushort)(encoded[i][j] ^ key));
                key = (ushort)((key << 3) | (key >> 13));
            }

            offset += (2 * encoded[i].Length + 3) & ~3;
        }

        return data;
    }

    private static ushort[] Codes(string text)
    {
        var list = new List<ushort>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '{' && i + 5 < text.Length && text[i + 5] == '}' && ushort.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
            {
                list.Add(raw);
                i += 5;
            }
            else
            {
                list.Add(text[i]);
            }
        }

        list.Add(0);
        return [.. list];
    }
}
