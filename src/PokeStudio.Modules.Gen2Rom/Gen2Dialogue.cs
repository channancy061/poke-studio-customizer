using System.Globalization;
using System.Text;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Validation;

namespace PokeStudio.Modules.Gen2Rom;

/// <summary>
/// Dialogue in Gold/Silver/Crystal. Messages are found by their contents, and only kept when a script points to them in
/// a way this app can update: a "far text" link (0x16, pointer, bank, end), a text command (writetext,
/// jumptextfaceplayer, jumptext, winlosstext) or a trainer's pair of seen/beaten texts. Text uses the decomp spellings: \n second line, \l more lines, \p new
/// box, {PLAYER}, {RIVAL}; a message that waits for a button at the end shows {PROMPT}. Other commands (insert a number
/// or name, sounds…) read as {CMD 01 …} and must stay.
/// </summary>
internal static class Gen2Dialogue
{
    /// <summary>A message, the far links (pointer + bank) and the near pointers (same bank) that lead to it.</summary>
    public sealed record Message(int Offset, int Size, List<int> Links, List<int> Near);

    // script commands followed by a pointer to text in the same bank: writetext, jumptextfaceplayer, jumptext
    private static readonly byte[] TextCommands = [0x4C, 0x51, 0x53];
    private const byte WinLossText = 0x64;

    private const int MaxMessage = 1200;

    // the text box is 18 characters wide: a longer run without a break isn't dialogue (it's usually graphics)
    private const int MaxRun = 24;

    // command-mode codes and how many bytes follow them
    private static readonly Dictionary<byte, int> Commands = new()
    {
        [0x01] = 2, [0x02] = 3, [0x03] = 2, [0x04] = 4, [0x05] = 0, [0x06] = 0, [0x07] = 0, [0x09] = 3, [0x0A] = 0, [0x0B] = 0,
        [0x0C] = 1, [0x0D] = 0, [0x0E] = 0, [0x0F] = 0, [0x10] = 0, [0x11] = 0, [0x12] = 0, [0x13] = 0, [0x14] = 1, [0x15] = 0,
    };

    private static readonly Dictionary<byte, string> Specials = new()
    {
        [0x4F] = "\\n", [0x51] = "\\p", [0x55] = "\\l", [0x52] = "{PLAYER}", [0x53] = "{RIVAL}",
    };

    private static readonly Dictionary<byte, string> Extra = new()
    {
        [0xD0] = "'d", [0xD1] = "'l", [0xD2] = "'m", [0xD3] = "'r", [0xD4] = "'s", [0xD5] = "'t", [0xD6] = "'v", [0x75] = "…",
    };

    // what each byte means inside text (null = not a printable character)
    private static readonly string?[] Characters = BuildCharacters();

    private static string?[] BuildCharacters()
    {
        var table = new string?[256];
        for (var b = 0; b < 256; b++)
        {
            table[b] = Extra.TryGetValue((byte)b, out var e) ? e : Gen2Text.IsCharacter((byte)b) ? Gen2Text.Read([(byte)b, Gen2Text.End]) : null;
        }

        return table;
    }

    /// <summary>Finds every far-text link and the message it points to (key = the first link, which never moves).</summary>
    public static Dictionary<string, Message> Scan(GbRom rom)
    {
        var found = new Dictionary<int, List<int>>();
        var bytes = rom.Bytes;
        for (var at = 0; at + 5 <= rom.Length; at++)
        {
            if (bytes[at] != 0x16 || bytes[at + 4] != 0x50 || bytes[at + 2] is < 0x40 or > 0x7F)
            {
                continue;
            }

            var target = Target(rom, at);
            if (target < 0 || target >= rom.Length || Decode(rom, target, out _) is null)
            {
                continue;
            }

            if (!found.TryGetValue(target, out var links))
            {
                found[target] = links = [];
            }

            links.Add(at);
        }

        // messages next to their scripts, reached by 16-bit pointers in the same bank
        var near = new Dictionary<int, List<int>>();
        for (var bank = 1; bank < rom.Length / 0x4000; bank++)
        {
            var start = bank * 0x4000;
            var texts = new Dictionary<int, int>(); // pointer value → offset
            for (var t = start; t < start + 0x4000 - 2; t++)
            {
                if (bytes[t] == 0x00 && Decode(rom, t, out var size) is { } s && !s.EndsWith("{END}", StringComparison.Ordinal))
                {
                    texts[GbRom.PointerTo(t)] = t;
                    t += size - 1;
                }
            }

            int? TextAt(int i) => i + 1 < start + 0x4000 && texts.TryGetValue(bytes[i] | (bytes[i + 1] << 8), out var t) ? t : null;
            void Add(int t, int pointerAt)
            {
                if (!near.TryGetValue(t, out var list))
                {
                    near[t] = list = [];
                }

                if (!list.Contains(pointerAt))
                {
                    list.Add(pointerAt);
                }
            }

            for (var i = start + 1; i < start + 0x4000 - 1; i++)
            {
                if (TextAt(i) is not { } t)
                {
                    continue;
                }

                var before = bytes[i - 1];
                if (TextCommands.Contains(before) || before == WinLossText || (i >= start + 3 && bytes[i - 3] == WinLossText && TextAt(i - 2) is not null))
                {
                    Add(t, i);
                }
                else if (TextAt(i + 2) is { } beaten && i >= start + 4)
                {
                    // a trainer: event flag, group, number, then the seen and beaten texts side by side
                    Add(t, i);
                    Add(beaten, i + 2);
                }
            }
        }

        var result = new Dictionary<string, Message>(StringComparer.Ordinal);
        foreach (var target in found.Keys.Union(near.Keys).OrderBy(t => Math.Min(found.GetValueOrDefault(t)?[0] ?? int.MaxValue, near.GetValueOrDefault(t)?.Min() ?? int.MaxValue)))
        {
            Decode(rom, target, out var size);
            var links = found.GetValueOrDefault(target) ?? [];
            var pointers = near.GetValueOrDefault(target) ?? [];
            pointers.Sort();
            var first = Math.Min(links.Count > 0 ? links[0] : int.MaxValue, pointers.Count > 0 ? pointers[0] : int.MaxValue);
            result[string.Create(CultureInfo.InvariantCulture, $"GBTEXT_{first:X6}")] = new Message(target, size, links, pointers);
        }

        return result;
    }

    private static int Target(GbRom rom, int link)
    {
        var bank = rom.Bytes[link + 3];
        var pointer = rom.Bytes[link + 1] | (rom.Bytes[link + 2] << 8);
        return (bank * 0x4000) + (pointer - 0x4000);
    }

    private static void Repoint(GbRom rom, int link, int target)
    {
        var pointer = GbRom.PointerTo(target);
        rom.Bytes[link + 1] = (byte)pointer;
        rom.Bytes[link + 2] = (byte)(pointer >> 8);
        rom.Bytes[link + 3] = (byte)GbRom.Bank(target);
    }

    /// <summary>The message as text, or null when it isn't a message this app can safely edit.</summary>
    public static string? Decode(GbRom rom, int at, out int size)
    {
        size = 0;
        var sb = new StringBuilder();
        var text = false;
        var letters = 0;
        var run = 0;
        var i = at;
        if (rom.Bytes[i] != 0x00)
        {
            return null; // messages start in text mode
        }

        while (i < rom.Length && i - at < MaxMessage)
        {
            var b = rom.Bytes[i++];
            if (!text)
            {
                if (b == 0x00)
                {
                    text = true;
                }
                else if (b == 0x50)
                {
                    sb.Append("{END}");
                    size = i - at;
                    return letters >= 2 ? sb.ToString() : null;
                }
                else if (Commands.TryGetValue(b, out var n) && i + n <= rom.Length)
                {
                    sb.Append("{CMD ").Append(b.ToString("X2", CultureInfo.InvariantCulture));
                    for (var k = 0; k < n; k++)
                    {
                        sb.Append(' ').Append(rom.Bytes[i + k].ToString("X2", CultureInfo.InvariantCulture));
                    }

                    sb.Append('}');
                    i += n;
                }
                else
                {
                    return null; // code that runs a program, a nested far link…
                }

                continue;
            }

            if (++run > MaxRun)
            {
                return null;
            }

            switch (b)
            {
                case 0x4F or 0x51 or 0x55:
                    run = 0;
                    sb.Append(Specials[b]);
                    break;
                case 0x50:
                    run = 0;
                    text = false;
                    break;
                case 0x57:
                    size = i - at;
                    return letters >= 2 ? sb.ToString() : null;
                case 0x58:
                    sb.Append("{PROMPT}");
                    size = i - at;
                    return letters >= 2 ? sb.ToString() : null;
                default:
                    if (Specials.TryGetValue(b, out var special))
                    {
                        sb.Append(special);
                    }
                    else if (Characters[b] is { } s)
                    {
                        sb.Append(s);
                        if (b is >= 0x80 and <= 0xB9)
                        {
                            letters++;
                        }
                    }
                    else if (b is >= 0x01 and < 0x50)
                    {
                        return null; // unusual control codes inside text: leave such messages alone
                    }
                    else
                    {
                        sb.Append('{').Append(b.ToString("X2", CultureInfo.InvariantCulture)).Append('}');
                    }

                    break;
            }
        }

        return null;
    }

    /// <summary>The bytes for edited text, or null with the reason.</summary>
    public static byte[]? Encode(string text, out string? problem)
    {
        problem = null;
        var bytes = new List<byte> { 0x00 };
        var inText = true;
        void TextMode()
        {
            if (!inText)
            {
                bytes.Add(0x00);
                inText = true;
            }
        }

        var end = text.EndsWith("{END}", StringComparison.Ordinal) ? "{END}" : text.EndsWith("{PROMPT}", StringComparison.Ordinal) ? "{PROMPT}" : "";
        var body = text[..^end.Length];
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (c == '\\' && i + 1 < body.Length && Specials.FirstOrDefault(s => s.Value == "\\" + body[i + 1]) is { Value: not null } esc)
            {
                TextMode();
                bytes.Add(esc.Key);
                i++;
                continue;
            }

            if (c == '{' && body.IndexOf('}', i) is var close and > 0)
            {
                var token = body[(i + 1)..close];
                if (Specials.FirstOrDefault(s => s.Value == "{" + token + "}") is { Value: not null } ph)
                {
                    TextMode();
                    bytes.Add(ph.Key);
                }
                else if (token.StartsWith("CMD ", StringComparison.Ordinal))
                {
                    if (inText)
                    {
                        bytes.Add(0x50);
                        inText = false;
                    }

                    foreach (var part in token[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!byte.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
                        {
                            problem = $"“{{{token}}}” isn't a code this game knows.";
                            return null;
                        }

                        bytes.Add(v);
                    }
                }
                else if (token.Length == 2 && byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
                {
                    TextMode();
                    bytes.Add(raw);
                }
                else
                {
                    problem = $"“{{{token}}}” isn't a code this game knows.";
                    return null;
                }

                i = close;
                continue;
            }

            TextMode();
            var two = i + 1 < body.Length ? body.Substring(i, 2) : null;
            if (two is not null && Extra.FirstOrDefault(e => e.Value == two) is { Value: not null } pair)
            {
                bytes.Add(pair.Key);
                i++;
            }
            else if (c == '…')
            {
                bytes.Add(0x75);
            }
            else if (Gen2Text.CanWrite(c.ToString()))
            {
                bytes.Add(Gen2Text.Write(c.ToString())[0]);
            }
            else
            {
                problem = $"“{c}” isn't in this game's font.";
                return null;
            }
        }

        switch (end)
        {
            case "{END}":
                if (inText)
                {
                    bytes.Add(0x50);
                }

                bytes.Add(0x50);
                break;
            case "{PROMPT}":
                TextMode();
                bytes.Add(0x58);
                break;
            default:
                TextMode();
                bytes.Add(0x57);
                break;
        }

        return [.. bytes];
    }

    public static void Load(Gen2RomModule.State state, GameData data)
    {
        state.Messages = Scan(state.Rom);
        foreach (var (key, m) in state.Messages)
        {
            var text = Decode(state.Rom, m.Offset, out _)!;
            var entry = new TextEntry { Key = key, Place = $"Text bank {GbRom.Bank(m.Offset):X2}", Text = text, Original = text };
            data.Texts.Add(entry);
            state.Texts[key] = entry;
        }
    }

    /// <summary>Writes edited messages: in place when they fit, otherwise at the end of a bank with room, relinked.</summary>
    public static void Save(Gen2RomModule.State state, GameData data)
    {
        var rom = state.Rom;
        foreach (var t in data.Texts)
        {
            if (!state.Texts.TryGetValue(t.Key, out var before) || before.Text == t.Text || !state.Messages.TryGetValue(t.Key, out var m))
            {
                continue;
            }

            var bytes = Encode(t.Text, out var problem) ?? throw new InvalidOperationException(problem);
            if (bytes.Length <= m.Size)
            {
                bytes.CopyTo(rom.Bytes, m.Offset);
                continue; // the rest of the old message is never reached
            }

            // near pointers can only reach their own bank; far links reach any bank
            var home = GbRom.Bank(m.Offset);
            var banks = m.Near.Count > 0 ? [home] : Enumerable.Range(1, (rom.Length / 0x4000) - 1).OrderBy(b => b == home ? -1 : b).ToList();
            var at = banks.Select(b => rom.FindFree(b, bytes.Length)).FirstOrDefault(a => a >= 0, -1);
            if (at < 0)
            {
                throw new InvalidOperationException($"“{Preview(t.Text)}” is too long: there's no free space left near it. Shorten it a little.");
            }

            bytes.CopyTo(rom.Bytes, at);
            foreach (var link in m.Links)
            {
                Repoint(rom, link, at);
            }

            foreach (var pointer in m.Near)
            {
                rom.W16(pointer, GbRom.PointerTo(at));
            }
        }
    }

    /// <summary>Width of a line in pixels: every character is 8 pixels; names the game fills in count as 7 characters.</summary>
    public static int Measure(string line)
    {
        var chars = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '{' && line.IndexOf('}', i) is var close and > 0)
            {
                var token = line[(i + 1)..close];
                chars += token is "PROMPT" or "END" ? 0 : token.Length == 2 ? 1 : 7;
                i = close;
            }
            else if (line[i] == '\'' && i + 1 < line.Length && "dlmrstv".Contains(line[i + 1], StringComparison.Ordinal))
            {
                chars++;
                i++;
            }
            else
            {
                chars++;
            }
        }

        return chars * 8;
    }

    private static string Preview(string text) => text.Length > 30 ? text[..29] + "…" : text;

    public static IEnumerable<ValidationIssue> Check(Gen2RomModule.State state, GameData data)
    {
        foreach (var t in data.Texts)
        {
            if (!state.Texts.TryGetValue(t.Key, out var before) || before.Text == t.Text)
            {
                continue;
            }

            if (Encode(t.Text, out var problem) is not { } bytes)
            {
                yield return new ValidationIssue(IssueSeverity.Error, "Text", t.Place, problem!) { TargetKey = t.Key };
            }
            else if (Decode(new GbRom([.. bytes, .. new byte[4]]), 0, out _) != t.Text)
            {
                yield return new ValidationIssue(IssueSeverity.Error, "Text", t.Place, "A line is too long: the text box fits 18 characters per line.",
                    "Break long lines into shorter ones.") { TargetKey = t.Key };
            }
        }
    }
}
