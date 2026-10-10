using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PokeStudio.Modules.Nds;

/// <summary>
/// Gen 4 message files: encrypted 16-bit character codes in the games' own table. Codes the app doesn't know (and
/// control codes) read as "{XXXX}" and are written back unchanged.
/// </summary>
internal static class Gen4Text
{
    private static readonly Dictionary<int, string> Decode = BuildTable();
    private static readonly Dictionary<string, int> Encode = Decode.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);

    private static Dictionary<int, string> BuildTable()
    {
        var d = new Dictionary<int, string>
        {
            [0x1DE] = " ", [0x188] = "é", [0x1AB] = "!", [0x1AC] = "?", [0x1AD] = ",", [0x1AE] = ".", [0x1B3] = "’", [0x1B4] = "“", [0x1B5] = "”",
            [0x1BB] = "♂", [0x1BC] = "♀", [0x1BE] = "-", [0x1C2] = "&", [0x1C4] = ":", [0xE000] = "\n",
        };
        for (var i = 0; i < 10; i++)
        {
            d[0x121 + i] = ((char)('0' + i)).ToString();
        }

        for (var i = 0; i < 26; i++)
        {
            d[0x12B + i] = ((char)('A' + i)).ToString();
            d[0x145 + i] = ((char)('a' + i)).ToString();
        }

        return d;
    }

    public static List<string> ReadFile(byte[] data, out ushort seed)
    {
        var count = BinaryPrimitives.ReadUInt16LittleEndian(data);
        seed = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var key = (ushort)(seed * 0x2FD * (i + 1));
            var k32 = key | ((uint)key << 16);
            var offset = (int)(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4 + (8 * i))) ^ k32);
            var length = (int)(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8 + (8 * i))) ^ k32);
            var ck = (ushort)(0x91BD3 * (i + 1));
            var codes = new List<int>(length);
            for (var j = 0; j < length && offset + (2 * j) + 1 < data.Length; j++)
            {
                codes.Add(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + (2 * j))) ^ ck);
                ck = (ushort)(ck + 0x493D);
            }

            result.Add(ToText(codes));
        }

        return result;
    }

    public static byte[] WriteFile(IReadOnlyList<string> texts, ushort seed)
    {
        var encoded = texts.Select(t => ToCodes(t).Append(0xFFFF).ToList()).ToList();
        var size = 4 + (8 * texts.Count) + encoded.Sum(e => 2 * e.Count);
        var data = new byte[size];
        BinaryPrimitives.WriteUInt16LittleEndian(data, (ushort)texts.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), seed);
        var offset = 4 + (8 * texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            var key = (ushort)(seed * 0x2FD * (i + 1));
            var k32 = key | ((uint)key << 16);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4 + (8 * i)), (uint)offset ^ k32);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8 + (8 * i)), (uint)encoded[i].Count ^ k32);
            var ck = (ushort)(0x91BD3 * (i + 1));
            foreach (var c in encoded[i])
            {
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), (ushort)(c ^ ck));
                ck = (ushort)(ck + 0x493D);
                offset += 2;
            }
        }

        return data;
    }

    private static string ToText(List<int> codes)
    {
        // trainer names are often packed 9 bits per character after 0xF100
        if (codes.Count > 0 && codes[0] == 0xF100)
        {
            var unpacked = new List<int>();
            var bits = 0;
            var buffer = 0;
            foreach (var c in codes.Skip(1))
            {
                buffer |= (c & 0x7FFF) << bits; // 15 bits per word
                bits += 15;
                while (bits >= 9)
                {
                    var ch = buffer & 0x1FF;
                    buffer >>= 9;
                    bits -= 9;
                    if (ch == 0x1FF)
                    {
                        return ToText(unpacked);
                    }

                    unpacked.Add(ch);
                }
            }

            return ToText(unpacked);
        }

        var sb = new StringBuilder();
        foreach (var c in codes)
        {
            if (c == 0xFFFF)
            {
                break;
            }

            sb.Append(Decode.TryGetValue(c, out var s) ? s : "{" + c.ToString("X4", CultureInfo.InvariantCulture) + "}");
        }

        return sb.ToString();
    }

    public static IEnumerable<int> ToCodes(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '{' && i + 5 < text.Length && text[i + 5] == '}' && int.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
            {
                yield return raw;
                i += 5;
            }
            else if (text[i] == '\'')
            {
                yield return Encode["’"];
            }
            else
            {
                yield return Encode.TryGetValue(text[i].ToString(), out var c) ? c : Encode["?"];
            }
        }
    }

    public static bool CanWrite(string text) => text.All(ch => ch == '\'' || ch == '{' || ch == '}' || char.IsAsciiHexDigit(ch) || Encode.ContainsKey(ch.ToString()));
}

/// <summary>Gen 5 text files: UTF-16 characters, each string encrypted with a rolling key; control codes kept as "{XXXX}".</summary>
internal static class Gen5Text
{
    public static List<string> ReadFile(byte[] data)
    {
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(2));
        var section = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
        var result = new List<string>(entries);
        for (var i = 0; i < entries; i++)
        {
            var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(section + 4 + (8 * i)));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(section + 8 + (8 * i)));
            var codes = new ushort[length];
            for (var j = 0; j < length; j++)
            {
                codes[j] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(section + offset + (2 * j)));
            }

            if (length == 0)
            {
                result.Add("");
                continue;
            }

            var key = (ushort)(codes[^1] ^ 0xFFFF);
            for (var j = length - 1; j >= 0; j--)
            {
                codes[j] ^= key;
                key = (ushort)((key >> 3) | (key << 13));
            }

            var sb = new StringBuilder();
            foreach (var c in codes)
            {
                if (c == 0xFFFF)
                {
                    break;
                }

                if (c == 0xFFFE)
                {
                    sb.Append('\n');
                }
                else if (c < 0x20 || c >= 0xE000 || c == '{' || c == '}')
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

    public static byte[] WriteFile(IReadOnlyList<string> texts, byte[] original)
    {
        var unk = BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(8));
        var encoded = texts.Select(Codes).ToList();
        var tableSize = 8 * texts.Count;
        var sectionSize = 4 + tableSize + encoded.Sum(e => 2 * e.Length);
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
            var codes = encoded[i];
            ushort key = 0x7C89;
            for (var j = codes.Length - 1; j >= 0; j--)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16 + offset + (2 * j)), (ushort)(codes[j] ^ key));
                key = (ushort)((key >> 3) | (key << 13));
            }

            offset += 2 * codes.Length;
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
                list.Add(text[i] == '\n' ? (ushort)0xFFFE : text[i]);
            }
        }

        list.Add(0xFFFF);
        return [.. list];
    }
}
