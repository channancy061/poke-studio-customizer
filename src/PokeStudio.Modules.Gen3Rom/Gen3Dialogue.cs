using System.Globalization;
using System.Text;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Modules.Gen3Rom;

/// <summary>
/// Dialogue in a Gen 3 ROM: the messages the maps' scripts show (people, signs, step-on events, map scripts, trainer
/// battles). Found by following every map's script pointers; text that grows moves to free space and every place that
/// pointed to it is repointed. Text uses the decomp spellings: \n new line, \l scroll, \p new box, {PLAYER}, {RIVAL}…;
/// other control codes appear as hex, e.g. {FC 01 02}.
/// </summary>
internal static class Gen3Dialogue
{
    /// <summary>One message: where it is, how many bytes it may use in place, and every pointer to it the scripts hold.</summary>
    public sealed record Message(int Offset, int Size, List<int> References, string Place);

    private const int ScriptWindow = 0x180;
    private const int MaxMessage = 1200;

    private static readonly Dictionary<int, string> Placeholders = new()
    {
        [0x01] = "PLAYER", [0x02] = "STR_VAR_1", [0x03] = "STR_VAR_2", [0x04] = "STR_VAR_3", [0x05] = "KUN", [0x06] = "RIVAL",
    };

    // bytes per extended control code (after the 0xFC), from the games' text printer
    private static readonly int[] FcArguments = [0, 1, 1, 1, 3, 1, 1, 0, 1, 0, 0, 2, 1, 1, 1, 0, 2, 1, 1, 1, 1, 0, 0, 0, 0];

    /// <summary>Finds every message the map scripts use (key = the first place that points to it, which never moves).</summary>
    public static Dictionary<string, Message> Scan(RomImage rom, Gen3Tables t)
    {
        var found = new Dictionary<int, (List<int> Refs, string Place)>();
        if (t.MapGroups < 0)
        {
            return [];
        }

        var visited = new HashSet<int>();
        var queue = new Queue<int>();
        foreach (var (header, place) in MapHeaders(rom, t))
        {
            foreach (var script in MapScripts(rom, header))
            {
                queue.Enqueue(script);
            }

            while (queue.Count > 0)
            {
                var start = queue.Dequeue();
                if (!visited.Add(start))
                {
                    continue;
                }

                ScanScript(rom, start, place, found, queue);
            }
        }

        var result = new Dictionary<string, Message>(StringComparer.Ordinal);
        foreach (var (offset, (refs, place)) in found.OrderBy(f => f.Value.Refs.Min()))
        {
            refs.Sort();
            var size = Length(rom, offset);
            result[$"ROMTEXT_{refs[0]:X6}"] = new Message(offset, size, refs, place);
        }

        return result;
    }

    private static IEnumerable<(int Header, string Place)> MapHeaders(RomImage rom, Gen3Tables t)
    {
        var groupStarts = new SortedSet<int>();
        var groups = new List<int>();
        for (var g = 0; g < 64; g++)
        {
            var list = rom.Offset(t.MapGroups + (4 * g));
            if (list < 0 || !IsHeader(rom, rom.Offset(list)))
            {
                break;
            }

            groups.Add(list);
            groupStarts.Add(list);
        }

        groupStarts.Add(t.MapGroups);
        for (var g = 0; g < groups.Count; g++)
        {
            var list = groups[g];
            var limit = groupStarts.GetViewBetween(list + 1, int.MaxValue).DefaultIfEmpty(list + (4 * 256)).First();
            for (var n = 0; list + (4 * n) < limit && n < 256; n++)
            {
                var header = rom.Offset(list + (4 * n));
                if (!IsHeader(rom, header))
                {
                    break;
                }

                var name = Gen3Reader.PlaceName(rom, t, g, n);
                yield return (header, string.IsNullOrWhiteSpace(name) ? $"Map {g}.{n}" : name);
            }
        }
    }

    private static bool IsHeader(RomImage rom, int header) =>
        header > 0 && header + 0x1C <= rom.Length && rom.Offset(header) >= 0 && rom.Offset(header + 4) >= 0 && rom.Offset(header + 8) >= 0;

    /// <summary>The scripts a map starts: its people, step-on triggers, signs, and its own map scripts.</summary>
    private static IEnumerable<int> MapScripts(RomImage rom, int header)
    {
        var events = rom.Offset(header + 4);
        if (events >= 0 && events + 20 <= rom.Length)
        {
            int objects = rom.U8(events), coords = rom.U8(events + 2), bgs = rom.U8(events + 3);
            var o = rom.Offset(events + 4);
            for (var i = 0; o >= 0 && i < objects && o + (24 * (i + 1)) <= rom.Length; i++)
            {
                if (rom.Offset(o + (24 * i) + 16) is var s and >= 0)
                {
                    yield return s;
                }
            }

            var c = rom.Offset(events + 12);
            for (var i = 0; c >= 0 && i < coords && c + (16 * (i + 1)) <= rom.Length; i++)
            {
                if (rom.Offset(c + (16 * i) + 12) is var s and >= 0)
                {
                    yield return s;
                }
            }

            var b = rom.Offset(events + 16);
            for (var i = 0; b >= 0 && i < bgs && b + (12 * (i + 1)) <= rom.Length; i++)
            {
                if (rom.U8(b + (12 * i) + 5) <= 4 && rom.Offset(b + (12 * i) + 8) is var s and >= 0) // kinds 0-4 run a script
                {
                    yield return s;
                }
            }
        }

        var scripts = rom.Offset(header + 8);
        for (var at = scripts; at >= 0 && at + 5 <= rom.Length && rom.U8(at) != 0; at += 5)
        {
            var type = rom.U8(at);
            var target = rom.Offset(at + 1);
            if (target < 0)
            {
                break;
            }

            if (type is 2 or 4) // a table of {variable, value, script}
            {
                for (var e = target; e + 8 <= rom.Length && rom.U16(e) != 0; e += 8)
                {
                    if (rom.Offset(e + 4) is var s and >= 0)
                    {
                        yield return s;
                    }
                }
            }
            else
            {
                yield return target;
            }
        }
    }

    /// <summary>Looks through a script for the commands that show text (and the ones that jump to more script).</summary>
    private static void ScanScript(RomImage rom, int start, string place, Dictionary<int, (List<int> Refs, string Place)> found, Queue<int> queue)
    {
        void Text(int pointerAt)
        {
            if (pointerAt + 4 > rom.Length || rom.Offset(pointerAt) is not (var p and >= 0) || !LooksLikeText(rom, p))
            {
                return;
            }

            if (!found.TryGetValue(p, out var f))
            {
                found[p] = f = ([], place);
            }

            if (!f.Refs.Contains(pointerAt))
            {
                f.Refs.Add(pointerAt);
            }
        }

        void Script(int pointerAt)
        {
            if (pointerAt + 4 <= rom.Length && rom.Offset(pointerAt) is var s and >= 0)
            {
                queue.Enqueue(s);
            }
        }

        var end = Math.Min(rom.Length - 1, start + ScriptWindow);
        for (var at = start; at < end; at++)
        {
            switch (rom.U8(at))
            {
                case 0x0F when rom.U8(at + 1) == 0: // loadword 0, text (then a message box)
                    Text(at + 2);
                    break;
                case 0x67: // message
                    Text(at + 1);
                    break;
                case 0x04 or 0x05: // call / goto
                    Script(at + 1);
                    break;
                case 0x06 or 0x07: // goto_if / call_if
                    Script(at + 2);
                    break;
                case 0x5C when at + 10 <= rom.Length: // trainerbattle type, trainer, local id, texts…
                    var type = rom.U8(at + 1);
                    if (type > 8)
                    {
                        break;
                    }

                    Text(at + 6);
                    if (type != 3)
                    {
                        Text(at + 10);
                    }

                    if (type is 4 or 6 or 7 or 8)
                    {
                        Text(at + 14);
                    }

                    if (type is 1 or 2)
                    {
                        Script(at + 14);
                    }
                    else if (type is 6 or 8)
                    {
                        Script(at + 18);
                    }

                    break;
            }

            if (rom.U8(at) == 0x02 && at == start) // a script that just ends
            {
                break;
            }
        }
    }

    /// <summary>Strict check: known characters and codes only, ended within a sensible length, with some letters in it.</summary>
    private static bool LooksLikeText(RomImage rom, int at)
    {
        var letters = 0;
        var i = at;
        for (; i < rom.Length && i - at < MaxMessage; i++)
        {
            var b = rom.U8(i);
            if (b == Gen3Text.End)
            {
                break;
            }

            switch (b)
            {
                case 0xFC:
                    var code = i + 1 < rom.Length ? rom.U8(i + 1) : 0xFF;
                    if (code >= FcArguments.Length)
                    {
                        return false;
                    }

                    i += 1 + FcArguments[code];
                    break;
                case 0xFD:
                    if (i + 1 >= rom.Length || rom.U8(i + 1) is 0 or > 0x10)
                    {
                        return false;
                    }

                    i++;
                    break;
                case 0xF9:
                    i++;
                    break;
                case 0xFA or 0xFB or 0xFE:
                    break;
                default:
                    if (!Gen3Text.IsCharacter(b))
                    {
                        return false;
                    }

                    if (b is >= 0xBB and <= 0xEE)
                    {
                        letters++;
                    }

                    break;
            }
        }

        return i < rom.Length && rom.U8(i) == Gen3Text.End && letters >= 2;
    }

    /// <summary>Bytes used by the message, end marker included.</summary>
    private static int Length(RomImage rom, int at)
    {
        var i = at;
        while (i < rom.Length && rom.U8(i) != Gen3Text.End)
        {
            i += rom.U8(i) switch
            {
                0xFC => 2 + FcArguments[Math.Min(rom.U8(i + 1), FcArguments.Length - 1)],
                0xFD or 0xF9 => 2,
                _ => 1,
            };
        }

        return i - at + 1;
    }

    public static string Read(RomImage rom, int at)
    {
        var sb = new StringBuilder();
        for (var i = at; i < rom.Length; i++)
        {
            var b = rom.U8(i);
            switch (b)
            {
                case Gen3Text.End:
                    return sb.ToString();
                case 0xFE:
                    sb.Append("\\n");
                    break;
                case 0xFA:
                    sb.Append("\\l");
                    break;
                case 0xFB:
                    sb.Append("\\p");
                    break;
                case 0xFD:
                    var p = rom.U8(++i);
                    sb.Append(Placeholders.TryGetValue(p, out var name) ? $"{{{name}}}" : $"{{FD {p:X2}}}");
                    break;
                case 0xFC:
                    var code = rom.U8(i + 1);
                    var count = 1 + (code < FcArguments.Length ? FcArguments[code] : 0);
                    sb.Append("{FC");
                    for (var k = 1; k <= count; k++)
                    {
                        sb.Append(CultureInfo.InvariantCulture, $" {rom.U8(i + k):X2}");
                    }

                    sb.Append('}');
                    i += count;
                    break;
                case 0xF9:
                    sb.Append(CultureInfo.InvariantCulture, $"{{F9 {rom.U8(++i):X2}}}");
                    break;
                default:
                    var s = Gen3Text.Read([b]);
                    sb.Append(s == "?" && b != 0xAC ? $"{{{b:X2}}}" : s == "\n" ? "\\n" : s);
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>The bytes for edited text, or null with the reason when something can't be written.</summary>
    public static byte[]? Write(string text, out string? problem)
    {
        problem = null;
        var bytes = new List<byte>();
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length > 0)
            {
                var b = Gen3Text.Write(plain.ToString());
                bytes.AddRange(b.AsSpan(0, b.Length - 1).ToArray());
                plain.Clear();
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] is 'n' or 'l' or 'p')
            {
                Flush();
                bytes.Add(text[++i] switch { 'n' => (byte)0xFE, 'l' => (byte)0xFA, _ => (byte)0xFB });
            }
            else if (c == '{' && text.IndexOf('}', i) is var close and > 0)
            {
                Flush();
                var token = text[(i + 1)..close];
                if (Placeholders.FirstOrDefault(p => p.Value == token) is { Value: not null } ph)
                {
                    bytes.Add(0xFD);
                    bytes.Add((byte)ph.Key);
                }
                else
                {
                    foreach (var part in token.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (!byte.TryParse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                        {
                            problem = $"“{{{token}}}” isn't a code this game knows.";
                            return null;
                        }

                        bytes.Add(b);
                    }
                }

                i = close;
            }
            else if (c == '\n')
            {
                Flush();
                bytes.Add(0xFE);
            }
            else
            {
                if (!Gen3Text.CanWrite(c.ToString()))
                {
                    problem = $"“{c}” isn't in this game's font.";
                    return null;
                }

                plain.Append(c);
            }
        }

        Flush();
        bytes.Add(Gen3Text.End);
        return [.. bytes];
    }

    public static void Load(Gen3RomModule.State state, GameData data)
    {
        state.Messages = Scan(state.Rom, state.Tables);
        foreach (var (key, m) in state.Messages)
        {
            var text = Read(state.Rom, m.Offset);
            var entry = new TextEntry { Key = key, Place = m.Place, Text = text, Original = text };
            data.Texts.Add(entry);
            state.Texts[key] = entry;
        }
    }

    /// <summary>Writes edited messages: in place when they fit, otherwise into free space with every pointer updated.</summary>
    public static void Save(Gen3RomModule.State state, GameData data)
    {
        var rom = state.Rom;
        foreach (var entry in data.Texts)
        {
            if (!state.Texts.TryGetValue(entry.Key, out var before) || before.Text == entry.Text || !state.Messages.TryGetValue(entry.Key, out var m))
            {
                continue;
            }

            var bytes = Write(entry.Text, out var problem) ?? throw new InvalidOperationException(problem);
            if (bytes.Length <= m.Size)
            {
                bytes.CopyTo(rom.Bytes, m.Offset);
                rom.Bytes.AsSpan(m.Offset + bytes.Length, m.Size - bytes.Length).Fill(Gen3Text.End);
                continue;
            }

            var at = rom.FindFree(bytes.Length);
            if (at < 0)
            {
                throw new InvalidOperationException("The ROM has no free space left for the longer text.");
            }

            bytes.CopyTo(rom.Bytes, at);
            foreach (var r in m.References)
            {
                rom.WritePointer(r, at);
            }
        }
    }

    public static IEnumerable<string> Check(string text)
    {
        if (Write(text, out var problem) is null)
        {
            yield return problem!;
        }
    }
}
