using System.Buffers.Binary;

namespace PokeStudio.Modules.Nds;

/// <summary>
/// A Nintendo DS ROM: its header, file name table and file allocation table. Files are replaced in place when they
/// fit; a file that grows moves to the end of the ROM (the FAT is updated and the header checksum recalculated).
/// </summary>
internal sealed class NdsRom
{
    private byte[] _bytes;
    private readonly Dictionary<string, int> _paths = new(StringComparer.Ordinal);

    public NdsRom(byte[] bytes)
    {
        _bytes = bytes;
        if (bytes.Length < 0x200)
        {
            throw new InvalidDataException("This file is too small to be a DS game.");
        }

        Walk(0xF000, "");
    }

    public byte[] Bytes => _bytes;
    public string GameCode => System.Text.Encoding.ASCII.GetString(_bytes, 0x0C, 4);
    public string Title => System.Text.Encoding.ASCII.GetString(_bytes, 0, 12).TrimEnd('\0', ' ');
    public bool IsDsiEnhanced => (_bytes[0x12] & 0x02) != 0;

    private int FntOffset => (int)U32(0x40);
    private int FatOffset => (int)U32(0x48);
    private int FatCount => (int)(U32(0x4C) / 8);

    private uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan(o));
    private ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(_bytes.AsSpan(o));

    public IReadOnlyCollection<string> Paths => _paths.Keys;

    public bool Exists(string path) => _paths.ContainsKey(path);

    public int IdOf(string path) => _paths.TryGetValue(path, out var id) ? id : throw new FileNotFoundException($"The game has no file {path}.");

    public (int Start, int End) Extent(int id)
    {
        var at = FatOffset + (8 * id);
        return ((int)U32(at), (int)U32(at + 4));
    }

    public byte[] Read(string path) => Read(IdOf(path));

    public byte[] Read(int id)
    {
        var (start, end) = Extent(id);
        return _bytes.AsSpan(start, end - start).ToArray();
    }

    /// <summary>Replaces a file: in place when it fits in its slot, otherwise appended at the end (512-byte aligned).</summary>
    public void Write(int id, byte[] data)
    {
        var (start, end) = Extent(id);
        var slot = NextStart(start) - start;
        int at;
        if (data.Length <= slot)
        {
            at = start;
        }
        else
        {
            at = Align(UsedSize());
            if (at + data.Length > _bytes.Length)
            {
                var grown = new byte[Align(at + data.Length + 0x1000)];
                _bytes.CopyTo(grown, 0);
                grown.AsSpan(_bytes.Length).Fill(0xFF);
                _bytes = grown;
            }
        }

        // clear what the old file used (padding stays 0xFF like the original tools write)
        if (at == start && data.Length < end - start)
        {
            _bytes.AsSpan(start + data.Length, end - start - data.Length).Fill(0xFF);
        }

        data.CopyTo(_bytes, at);
        var fat = FatOffset + (8 * id);
        BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(fat), (uint)at);
        BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(fat + 4), (uint)(at + data.Length));
        var used = Math.Max(UsedSize(), at + data.Length);
        if (at + data.Length > U32(0x80))
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan(0x80), (uint)(at + data.Length));
        }

        while ((128 * 1024 << _bytes[0x14]) < used)
        {
            _bytes[0x14]++; // device capacity
        }

        BinaryPrimitives.WriteUInt16LittleEndian(_bytes.AsSpan(0x15E), Crc16(_bytes.AsSpan(0, 0x15E)));
    }

    public void Write(string path, byte[] data) => Write(IdOf(path), data);

    private static int Align(int value) => (value + 0x1FF) & ~0x1FF;

    /// <summary>End of the used ROM; DSi-enhanced games keep their DSi part after the DS part (header 0x210).</summary>
    private int UsedSize() => (int)Math.Max(U32(0x80), IsDsiEnhanced ? U32(0x210) : 0);

    /// <summary>Where the next file (or the end of the used ROM) starts after <paramref name="start"/>.</summary>
    private int NextStart(int start)
    {
        var next = UsedSize();
        for (var i = 0; i < FatCount; i++)
        {
            var (s, e) = Extent(i);
            if (s > start && s < next && e > s)
            {
                next = s;
            }
        }

        return next;
    }

    /// <summary>CRC-16 (MODBUS) used by the DS header.</summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }

        return crc;
    }

    private void Walk(int dirId, string prefix)
    {
        var fnt = FntOffset;
        var entry = fnt + (8 * (dirId & 0xFFF));
        var p = fnt + (int)U32(entry);
        int fileId = U16(entry + 4);
        while (p < _bytes.Length)
        {
            var b = _bytes[p++];
            if (b == 0)
            {
                break;
            }

            var length = b & 0x7F;
            var name = System.Text.Encoding.ASCII.GetString(_bytes, p, length);
            p += length;
            if ((b & 0x80) != 0)
            {
                var sub = U16(p);
                p += 2;
                Walk(sub, prefix + name + "/");
            }
            else
            {
                _paths[prefix + name] = fileId++;
            }
        }
    }
}

/// <summary>A NARC archive (BTAF member table, BTNF names, GMIF data). Members are rebuilt in order when one changes.</summary>
internal sealed class Narc
{
    private readonly byte[] _btnf;

    public List<byte[]> Members { get; }

    public Narc(byte[] data)
    {
        if (data.Length < 16 || System.Text.Encoding.ASCII.GetString(data, 0, 4) != "NARC")
        {
            throw new InvalidDataException("Not a NARC archive.");
        }

        var btaf = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(12));
        var btafSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(btaf + 4));
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(btaf + 8));
        var btnf = btaf + btafSize;
        var btnfSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(btnf + 4));
        _btnf = data.AsSpan(btnf, btnfSize).ToArray();
        var gmif = btnf + btnfSize + 8;
        Members = new List<byte[]>(count);
        for (var i = 0; i < count; i++)
        {
            var s = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(btaf + 12 + (8 * i)));
            var e = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(btaf + 16 + (8 * i)));
            Members.Add(data.AsSpan(gmif + s, e - s).ToArray());
        }
    }

    public byte[] Build()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var btafSize = 12 + (8 * Members.Count);
        var offsets = new List<(int, int)>();
        var pos = 0;
        foreach (var m in Members)
        {
            offsets.Add((pos, pos + m.Length));
            pos += m.Length;
            pos = (pos + 3) & ~3;
        }

        var gmifSize = 8 + pos;
        var total = 16 + btafSize + _btnf.Length + gmifSize;
        w.Write("NARC"u8);
        w.Write((ushort)0xFFFE);
        w.Write((ushort)0x0100);
        w.Write(total);
        w.Write((ushort)16);
        w.Write((ushort)3);
        w.Write("BTAF"u8);
        w.Write(btafSize);
        w.Write(Members.Count);
        foreach (var (s, e) in offsets)
        {
            w.Write(s);
            w.Write(e);
        }

        w.Write(_btnf);
        w.Write("GMIF"u8);
        w.Write(gmifSize);
        foreach (var m in Members)
        {
            w.Write(m);
            while (ms.Position % 4 != 0)
            {
                w.Write((byte)0xFF);
            }
        }

        return ms.ToArray();
    }
}
