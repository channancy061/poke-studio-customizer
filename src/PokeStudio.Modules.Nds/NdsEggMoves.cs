using System.Buffers.Binary;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Validation;

namespace PokeStudio.Modules.Nds;

/// <summary>
/// Egg moves in the DS games. Black/White: one file per Pokémon (count, then moves). HeartGold/SoulSilver: one table
/// file (20000 + species, moves, 0xFFFF). Diamond/Pearl/Platinum: the same table inside a program overlay, which can't
/// grow, so edits must fit its current size.
/// </summary>
internal static class NdsEggMoves
{
    private const int Marker = 20000;
    private const string Gen5Archive = "a/1/2/3";
    private const string HgssArchive = "a/2/2/9";

    /// <summary>The games copy a limited number of egg moves per Pokémon into a fixed buffer.</summary>
    public static int MaxPerSpecies(NdsGame game) => game.Generation == 5 ? 16 : 16;

    private static ushort W(byte[] b, int i) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i));

    /// <summary>Finds the overlay holding the table in Diamond/Pearl/Platinum: (overlay number, offset), or (-1, -1).</summary>
    public static (int Overlay, int Offset) LocateOverlayTable(NdsRom rom, int moveCount)
    {
        var count = Arm9.OverlayCount(rom);
        for (var o = 0; o < count; o++)
        {
            if (Arm9.Overlay(rom, o) is not { } data)
            {
                continue;
            }

            for (var i = 0; i + 4 < data.Length; i += 2)
            {
                if (W(data, i) == Marker + 1 && Parse(data, i, moveCount) is { Count: > 50 })
                {
                    return (o, i);
                }
            }
        }

        return (-1, -1);
    }

    private static Dictionary<int, List<int>>? Parse(byte[] b, int at, int moveCount)
    {
        var result = new Dictionary<int, List<int>>();
        List<int>? current = null;
        for (var p = at; p + 2 <= b.Length && p - at < 0x8000; p += 2)
        {
            var v = W(b, p);
            if (v == 0xFFFF)
            {
                return result;
            }

            if (v > Marker && v < Marker + 1000)
            {
                result[v - Marker] = current = [];
            }
            else if (current is not null && v > 0 && v < moveCount)
            {
                current.Add(v);
            }
            else
            {
                return null;
            }
        }

        return null;
    }

    private static int TableSize(byte[] b, int at)
    {
        var p = at;
        while (p + 2 <= b.Length && W(b, p) != 0xFFFF)
        {
            p += 2;
        }

        return p - at + 2;
    }

    private static byte[] BuildTable(NdsState s, GameData data)
    {
        var words = new List<ushort>();
        foreach (var l in data.Learnsets.Where(l => l.EggMoves.Count > 0 && s.SpeciesIndex.ContainsKey(l.Key)).OrderBy(l => s.SpeciesIndex[l.Key]))
        {
            words.Add((ushort)(Marker + s.SpeciesIndex[l.Key]));
            words.AddRange(l.EggMoves.Where(s.MoveIndex.ContainsKey).Select(m => (ushort)s.MoveIndex[m]));
        }

        words.Add(0xFFFF);
        var bytes = new byte[2 * words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2 * i), words[i]);
        }

        return bytes;
    }

    /// <summary>Species number → move numbers, or null when this game's egg moves weren't found.</summary>
    private static Dictionary<int, List<int>>? ReadAll(NdsState s)
    {
        var rom = s.Rom;
        var moveCount = s.MoveIndex.Count;
        if (s.Game.Generation == 5)
        {
            if (!rom.Exists(Gen5Archive))
            {
                return null;
            }

            var narc = new Narc(rom.Read(Gen5Archive));
            var result = new Dictionary<int, List<int>>();
            for (var i = 0; i < narc.Members.Count; i++)
            {
                var f = narc.Members[i];
                if (f.Length < 2)
                {
                    continue;
                }

                var n = W(f, 0);
                result[i] = [.. Enumerable.Range(0, Math.Min(n, (f.Length - 2) / 2)).Select(k => (int)W(f, 2 + (2 * k)))];
            }

            return result;
        }

        if (rom.Exists(HgssArchive))
        {
            var file = new Narc(rom.Read(HgssArchive)).Members[0];
            return Parse(file, 0, moveCount);
        }

        if (s.Tables.EggMoveOverlay >= 0 && Arm9.Overlay(rom, s.Tables.EggMoveOverlay) is { } overlay)
        {
            return Parse(overlay, s.Tables.EggMoveOffset, moveCount);
        }

        return null;
    }

    public static void Read(NdsState s, GameData data)
    {
        if (ReadAll(s) is not { } table)
        {
            return;
        }

        var moveKeys = s.MoveIndex.ToDictionary(kv => kv.Value, kv => kv.Key);
        for (var i = 0; i < data.Learnsets.Count; i++)
        {
            var l = data.Learnsets[i];
            if (!s.SpeciesIndex.TryGetValue(l.Key, out var index))
            {
                continue;
            }

            var moves = table.TryGetValue(index, out var list) ? list.Select(m => moveKeys.GetValueOrDefault(m, "MOVE_NONE")).ToList() : [];
            var updated = l with { EggMoves = moves, LockedParts = l.LockedParts.Where(kv => kv.Key != "egg").ToDictionary(kv => kv.Key, kv => kv.Value) };
            data.Learnsets[i] = updated;
            s.Learnsets[l.Key] = updated;
        }

        s.EggMovesFound = true;
    }

    public static void Write(NdsState s, GameData data)
    {
        if (!s.EggMovesFound || !data.Learnsets.Any(l => s.Learnsets.TryGetValue(l.Key, out var before) && !before.EggMoves.SequenceEqual(l.EggMoves)))
        {
            return;
        }

        var rom = s.Rom;
        if (s.Game.Generation == 5)
        {
            var narc = new Narc(rom.Read(Gen5Archive));
            foreach (var l in data.Learnsets)
            {
                if (!s.Learnsets.TryGetValue(l.Key, out var before) || before.EggMoves.SequenceEqual(l.EggMoves) || !s.SpeciesIndex.TryGetValue(l.Key, out var i) || i >= narc.Members.Count)
                {
                    continue;
                }

                var moves = l.EggMoves.Where(s.MoveIndex.ContainsKey).Select(m => s.MoveIndex[m]).ToList();
                var file = new byte[2 + (2 * moves.Count)];
                BinaryPrimitives.WriteUInt16LittleEndian(file, (ushort)moves.Count);
                for (var k = 0; k < moves.Count; k++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2 + (2 * k)), (ushort)moves[k]);
                }

                narc.Members[i] = file;
            }

            rom.Write(Gen5Archive, narc.Build());
            return;
        }

        var table = BuildTable(s, data);
        if (rom.Exists(HgssArchive))
        {
            var narc = new Narc(rom.Read(HgssArchive));
            narc.Members[0] = table;
            rom.Write(HgssArchive, narc.Build());
            return;
        }

        var overlay = Arm9.Overlay(rom, s.Tables.EggMoveOverlay)!;
        if (table.Length > TableSize(overlay, s.Tables.EggMoveOffset))
        {
            throw new InvalidOperationException("The egg move list is full in this game. Remove some egg moves to add others.");
        }

        table.CopyTo(overlay, s.Tables.EggMoveOffset);
        Arm9.WriteOverlay(rom, s.Tables.EggMoveOverlay, overlay);
    }

    public static IEnumerable<ValidationIssue> Check(NdsState s, GameData data)
    {
        if (!s.EggMovesFound)
        {
            yield break;
        }

        var max = MaxPerSpecies(s.Game);
        foreach (var l in data.Learnsets.Where(l => l.EggMoves.Count > max))
        {
            yield return new ValidationIssue(IssueSeverity.Error, "Learnsets", l.Key, $"These games use at most {max} egg moves per Pokémon.") { TargetKey = l.Key };
        }

        // Diamond/Pearl/Platinum keep the table inside the program: it can't get bigger
        if (s.Game.Generation == 4 && !s.Rom.Exists(HgssArchive) && s.Tables.EggMoveOverlay >= 0
            && data.Learnsets.Any(l => s.Learnsets.TryGetValue(l.Key, out var before) && !before.EggMoves.SequenceEqual(l.EggMoves))
            && Arm9.Overlay(s.Rom, s.Tables.EggMoveOverlay) is { } overlay)
        {
            var room = TableSize(overlay, s.Tables.EggMoveOffset);
            var need = BuildTable(s, data).Length;
            if (need > room)
            {
                yield return new ValidationIssue(IssueSeverity.Error, "Learnsets", "Egg moves",
                    $"This game has room for {(room / 2) - 1} egg move entries in total and the changes need {(need / 2) - 1}. Remove {(need - room) / 2} egg moves somewhere.");
            }
        }
    }
}
