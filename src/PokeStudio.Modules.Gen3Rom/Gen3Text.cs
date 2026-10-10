using System.Text;

namespace PokeStudio.Modules.Gen3Rom;

/// <summary>The Gen 3 (Western) character encoding: 0xFF ends a string, 0xFE is a line break.</summary>
internal static class Gen3Text
{
    public const byte End = 0xFF;

    private static readonly Dictionary<byte, string> Decode = BuildDecode();
    private static readonly Dictionary<string, byte> Encode = Decode.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);

    private static Dictionary<byte, string> BuildDecode()
    {
        var d = new Dictionary<byte, string>
        {
            [0x00] = " ", [0x06] = "É", [0x1B] = "é", [0x2D] = "&", [0x2E] = "+", [0x35] = "=", [0x36] = ";", [0x39] = "~",
            [0x53] = "ᴾᴷ", [0x54] = "ᴹᴺ", [0x5B] = "%", [0x5C] = "(", [0x5D] = ")", [0x85] = "<", [0x86] = ">",
            [0xAB] = "!", [0xAC] = "?", [0xAD] = ".", [0xAE] = "-", [0xB0] = "…", [0xB1] = "“", [0xB2] = "”", [0xB3] = "‘", [0xB4] = "’",
            [0xB5] = "♂", [0xB6] = "♀", [0xB7] = "¥", [0xB8] = ",", [0xBA] = "/", [0xF0] = ":", [0xF4] = "ä", [0xF5] = "ö", [0xF6] = "ü", [0xFE] = "\n",
        };
        for (var i = 0; i < 10; i++)
        {
            d[(byte)(0xA1 + i)] = ((char)('0' + i)).ToString();
        }

        for (var i = 0; i < 26; i++)
        {
            d[(byte)(0xBB + i)] = ((char)('A' + i)).ToString();
            d[(byte)(0xD5 + i)] = ((char)('a' + i)).ToString();
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

    /// <summary>True when the byte is a printable character of the font.</summary>
    public static bool IsCharacter(byte b) => Decode.ContainsKey(b) && b != 0xFE;

    /// <summary>True when every character can be written in this game's font.</summary>
    public static bool CanWrite(string text) => Tokens(text).All(t => t is not null);

    /// <summary>The bytes for a string, plus the end marker. Unknown characters become "?".</summary>
    public static byte[] Write(string text)
    {
        var bytes = Tokens(text).Select(t => t ?? Encode["?"]).ToList();
        bytes.Add(End);
        return [.. bytes];
    }

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
            else if (text[i] == '\'')
            {
                yield return Encode["’"];
            }
            else
            {
                yield return Encode.TryGetValue(text[i].ToString(), out var b) ? b : null;
            }
        }
    }

    /// <summary>True when the bytes look like a name: known characters, ended within <paramref name="size"/> bytes.</summary>
    public static bool LooksLikeName(ReadOnlySpan<byte> bytes, int size, bool allowEmpty = false)
    {
        var end = bytes[..Math.Min(size, bytes.Length)].IndexOf(End);
        if (end < 0 || (end == 0 && !allowEmpty))
        {
            return false;
        }

        foreach (var b in bytes[..end])
        {
            if (!Decode.ContainsKey(b) || b == 0xFE)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>"MR. MIME" → "MR_MIME", "NIDORAN♀" → "NIDORAN_F", "FARFETCH’D" → "FARFETCHD".</summary>
    public static string Constant(string name)
    {
        var s = name.ToUpperInvariant().Replace("♀", "_F", StringComparison.Ordinal).Replace("♂", "_M", StringComparison.Ordinal)
            .Replace("É", "E", StringComparison.Ordinal).Replace("ᴾᴷ", "PK", StringComparison.Ordinal).Replace("ᴹᴺ", "MN", StringComparison.Ordinal);
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if ((c is ' ' or '-' or '_') && sb.Length > 0 && sb[^1] != '_')
            {
                sb.Append('_');
            }
        }

        return sb.ToString().Trim('_');
    }
}
