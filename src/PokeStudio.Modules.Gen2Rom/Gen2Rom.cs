using System.Buffers.Binary;
using System.Text;

namespace PokeStudio.Modules.Gen2Rom;

/// <summary>Gen 2 text: 0x50 ends a name ("@"); 0x80… letters, 0xF6… digits.</summary>
internal static class Gen2Text
{
    public const byte End = 0x50;

    private static readonly Dictionary<byte, string> Decode = Build();
    private static readonly Dictionary<string, byte> Encode = Decode.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);

    private static Dictionary<byte, string> Build()
    {
        var d = new Dictionary<byte, string>
        {
            [0x7F] = " ", [0x54] = "#", [0x9A] = "(", [0x9B] = ")", [0x9C] = ":", [0x9D] = ";", [0x9E] = "[", [0x9F] = "]",
            [0xE0] = "'", [0xE1] = "ᴾᴷ", [0xE2] = "ᴹᴺ", [0xE3] = "-", [0xE6] = "?", [0xE7] = "!", [0xE8] = ".", [0xE9] = "&",
            [0xEA] = "é", [0xEF] = "♂", [0xF0] = "¥", [0xF1] = "×", [0xF3] = "/", [0xF4] = ",", [0xF5] = "♀",
        };
        for (var i = 0; i < 26; i++)
        {
            d[(byte)(0x80 + i)] = ((char)('A' + i)).ToString();
            d[(byte)(0xA0 + i)] = ((char)('a' + i)).ToString();
        }

        for (var i = 0; i < 10; i++)
        {
            d[(byte)(0xF6 + i)] = ((char)('0' + i)).ToString();
        }

        return d;
    }

    public static string Read(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        foreach (var b in bytes)
        {
            if (b == End)
            {
                break;
            }

            sb.Append(Decode.TryGetValue(b, out var s) ? s : "?");
        }

        return sb.ToString();
    }

    public static bool CanWrite(string text) => Tokens(text).All(t => t is not null);

    /// <summary>True when the byte is a printable character of the font.</summary>
    public static bool IsCharacter(byte b) => Decode.ContainsKey(b);

    public static byte[] Write(string text) => [.. Tokens(text).Select(t => t ?? Encode["?"]), End];

    private static IEnumerable<byte?> Tokens(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var two = i + 1 < text.Length ? text.Substring(i, 2) : null;
            if (two is not null && Encode.TryGetValue(two, out var pair))
            {
                yield return pair;
                i++;
            }
            else
            {
                yield return Encode.TryGetValue(text[i].ToString(), out var b) ? b : null;
            }
        }
    }

    public static bool LooksLikeName(ReadOnlySpan<byte> bytes, int max)
    {
        var end = bytes[..Math.Min(max + 1, bytes.Length)].IndexOf(End);
        if (end <= 0)
        {
            return false;
        }

        foreach (var b in bytes[..end])
        {
            if (!Decode.ContainsKey(b))
            {
                return false;
            }
        }

        return true;
    }

    public static string Constant(string name)
    {
        var s = name.ToUpperInvariant().Replace("♀", "_F", StringComparison.Ordinal).Replace("♂", "_M", StringComparison.Ordinal)
            .Replace("ᴾᴷ", "PK", StringComparison.Ordinal).Replace("ᴹᴺ", "MN", StringComparison.Ordinal).Replace("É", "E", StringComparison.Ordinal);
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if ((c is ' ' or '-' or '_' or '.') && sb.Length > 0 && sb[^1] != '_')
            {
                sb.Append('_');
            }
        }

        return sb.ToString().Trim('_');
    }
}

/// <summary>A Game Boy ROM with banked 16-bit pointers (0x4000–0x7FFF point into the pointer's own bank).</summary>
internal sealed class GbRom(byte[] bytes)
{
    public byte[] Bytes { get; } = bytes;
    public int Length => Bytes.Length;
    public string Title => Encoding.ASCII.GetString(Bytes, 0x134, 11).TrimEnd('\0');

    public ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(o));
    public void W16(int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(o), (ushort)v);

    public static int Bank(int offset) => offset / 0x4000;

    /// <summary>A 16-bit pointer stored at <paramref name="at"/> → ROM offset in the same bank (or bank 0).</summary>
    public int Follow(int at)
    {
        var p = U16(at);
        return p < 0x4000 ? p : p < 0x8000 ? (Bank(at) * 0x4000) + (p - 0x4000) : -1;
    }

    public static int PointerTo(int offset) => offset < 0x4000 ? offset : 0x4000 + (offset % 0x4000);

    public int Find(ReadOnlySpan<byte> pattern, int start = 0)
    {
        var i = Bytes.AsSpan(start).IndexOf(pattern);
        return i < 0 ? -1 : start + i;
    }

    /// <summary>Free space at the end of a bank (the games pad unused space with 0x00), or -1.</summary>
    public int FindFree(int bank, int size)
    {
        var end = (bank + 1) * 0x4000;
        var start = end;
        while (start > bank * 0x4000 && Bytes[start - 1] == 0x00)
        {
            start--;
        }

        start += 8; // keep a small gap after the last data
        return end - start >= size ? start : -1;
    }

    /// <summary>The cartridge's global checksum (0x14E), which some emulators check.</summary>
    public void FixChecksum()
    {
        var sum = 0;
        for (var i = 0; i < Length; i++)
        {
            if (i is not (0x14E or 0x14F))
            {
                sum += Bytes[i];
            }
        }

        Bytes[0x14E] = (byte)(sum >> 8);
        Bytes[0x14F] = (byte)sum;
    }
}

/// <summary>Where Gold / Silver / Crystal keep their tables (found by contents, then remembered by the project).</summary>
internal sealed record Gen2Tables
{
    public const int SpeciesCount = 251;
    public const int MoveCount = 251;

    public required int BaseStats { get; init; }
    public required int Names { get; init; }
    public required int Moves { get; init; }
    public required int MoveNames { get; init; }
    public required int EvosAttacks { get; init; }
    public required int ItemNames { get; init; }
    public required int ItemAttributes { get; init; }
    public required int TmMoves { get; init; }
    public required int TrainerGroups { get; init; }
    public required int GroupCount { get; init; }
    public required int ClassNames { get; init; }
    public required int Wild { get; init; }

    public static Gen2Tables? Locate(GbRom rom, out string? problem)
    {
        problem = null;
        int Need(int v, string what) => v >= 0 ? v : throw new InvalidDataException(what);
        try
        {
            var bulba = Need(rom.Find([1, 45, 49, 49, 45, 65, 65, 22, 3]), "the Pokémon stats table");
            var names = Need(rom.Find(Gen2Text.Write("BULBASAUR")), "the Pokémon names");
            var pound = Need(rom.Find([1, 0, 40, 0, 255, 35, 0]), "the moves table");
            var moveNames = Need(rom.Find(Gen2Text.Write("POUND")), "the move names");
            var evo = Need(rom.Find([1, 16, 2, 0, 1, 33, 4, 45, 7, 73, 10, 22]), "Bulbasaur's evolutions and moves");
            var evoTable = -1;
            for (var at = rom.Find(BitConverter.GetBytes((ushort)GbRom.PointerTo(evo))); at >= 0; at = rom.Find(BitConverter.GetBytes((ushort)GbRom.PointerTo(evo)), at + 1))
            {
                if (GbRom.Bank(at) == GbRom.Bank(evo) && rom.Follow(at + 2) is var ivy && ivy > evo && ivy - evo < 64)
                {
                    evoTable = at;
                    break;
                }
            }

            Need(evoTable, "the evolution table");
            var itemNames = Need(rom.Find(Gen2Text.Write("MASTER BALL")), "the item names");
            var itemAttributes = -1;
            for (var o = 0; o < rom.Length - 40 && itemAttributes < 0; o++)
            {
                if (rom.U16(o) == 0 && rom.U16(o + 7) == 1200 && rom.U16(o + 14) == 10 && rom.U16(o + 21) == 600 && rom.U16(o + 28) == 200)
                {
                    itemAttributes = o - 7; // entry 0 is "no item"
                }
            }

            Need(itemAttributes, "the item prices");
            var tm = Need(rom.Find([223, 29, 174, 205, 46]), "the TM list");
            var (groups, groupCount) = FindTrainerGroups(rom);
            var wild = FindWild(rom);
            return new Gen2Tables
            {
                BaseStats = bulba,
                Names = names,
                Moves = pound,
                MoveNames = moveNames,
                EvosAttacks = evoTable,
                ItemNames = itemNames,
                ItemAttributes = itemAttributes,
                TmMoves = tm,
                TrainerGroups = groups,
                GroupCount = groupCount,
                ClassNames = groups < 0 ? -1 : rom.Find(Gen2Text.Write("LEADER")),
                Wild = wild,
            };
        }
        catch (InvalidDataException ex)
        {
            problem = $"Couldn't find {ex.Message} in this ROM. It may be a hack that moved or changed it.";
            return null;
        }
    }

    /// <summary>The trainer groups table: pointers in the bank of the first gym leader's group, which come right after it.</summary>
    private static (int Start, int Count) FindTrainerGroups(GbRom rom)
    {
        foreach (var leader in new[] { "FALKNER", "BROCK" })
        {
            var name = rom.Find(Gen2Text.Write(leader));
            if (name < 0)
            {
                continue;
            }

            var pointer = BitConverter.GetBytes((ushort)GbRom.PointerTo(name));
            for (var at = rom.Find(pointer); at >= 0; at = rom.Find(pointer, at + 1))
            {
                if (GbRom.Bank(at) != GbRom.Bank(name) || at > name)
                {
                    continue;
                }

                // the table runs from "at" up to the first group's data; each entry points forward into the bank
                var count = (name - at) / 2;
                if (count is > 20 and < 100 && Enumerable.Range(0, count).All(k => rom.Follow(at + (2 * k)) is var g && g >= name && GbRom.Bank(g) == GbRom.Bank(at)))
                {
                    return (at, count);
                }
            }
        }

        return (-1, 0);
    }

    /// <summary>Johto grass: 47-byte areas (map, three rates, 3 × 7 slots of level + species), ended by 0xFF.</summary>
    private static int FindWild(GbRom rom)
    {
        bool Area(int o)
        {
            if (o + 47 > rom.Length || rom.Bytes[o] == 0xFF || rom.Bytes[o] > 30 || rom.Bytes[o + 1] > 100)
            {
                return false;
            }

            for (var s = 0; s < 21; s++)
            {
                if (rom.Bytes[o + 5 + (2 * s)] is 0 or > 100 || rom.Bytes[o + 6 + (2 * s)] is 0 or > SpeciesCount)
                {
                    return false;
                }
            }

            return true;
        }

        for (var o = 0; o < rom.Length - (47 * 20); o++)
        {
            if (Enumerable.Range(0, 15).All(k => Area(o + (47 * k))))
            {
                return o;
            }
        }

        return -1;
    }
}
