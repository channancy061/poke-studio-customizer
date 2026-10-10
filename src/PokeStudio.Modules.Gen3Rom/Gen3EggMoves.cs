using System.Buffers.Binary;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Validation;

namespace PokeStudio.Modules.Gen3Rom;

/// <summary>
/// The egg move table: for each Pokémon with egg moves, 20000 + its number, then its moves; 0xFFFF ends the table. Found
/// by its shape once; the places in the game's code that point to it are remembered, so a bigger table can move to free
/// space.
/// </summary>
internal static class Gen3EggMoves
{
    public const int Marker = 20000;

    /// <summary>The games copy at most 10 egg moves per Pokémon into a fixed buffer.</summary>
    public const int MaxPerSpecies = 10;

    /// <summary>Where the code points to the table, or empty when it isn't found.</summary>
    public static List<int> Locate(RomImage rom, int moveCount)
    {
        for (var at = 0; at + 64 < rom.Length; at += 2)
        {
            if (rom.U16(at) != Marker + 1 || Parse(rom, at, moveCount) is not { Count: >= 50 })
            {
                continue;
            }

            var pointers = rom.PointersTo(at).ToList();
            if (pointers.Count > 0)
            {
                return pointers;
            }
        }

        return [];
    }

    /// <summary>Species number → egg moves (move numbers), or null when the data there isn't an egg move table.</summary>
    public static Dictionary<int, List<int>>? Parse(RomImage rom, int at, int moveCount)
    {
        var result = new Dictionary<int, List<int>>();
        List<int>? current = null;
        for (var p = at; p + 2 <= rom.Length && p - at < 0x4000; p += 2)
        {
            var v = rom.U16(p);
            if (v == 0xFFFF)
            {
                return result;
            }

            if (v > Marker && v < Marker + 1000)
            {
                current = [];
                result[v - Marker] = current;
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

    private static int Size(RomImage rom, int at)
    {
        var p = at;
        while (p + 2 <= rom.Length && rom.U16(p) != 0xFFFF)
        {
            p += 2;
        }

        return p - at + 2;
    }

    public static void Read(Gen3RomModule.State state, GameData data)
    {
        var t = state.Tables;
        if (t.EggMovePointers.Count == 0 || state.Rom.Offset(t.EggMovePointers[0]) is not (var at and >= 0) || Parse(state.Rom, at, t.MoveCount) is not { } table)
        {
            return;
        }

        var moveKeys = state.MoveIndex.ToDictionary(kv => kv.Value, kv => kv.Key);
        for (var i = 0; i < data.Learnsets.Count; i++)
        {
            var l = data.Learnsets[i];
            if (!state.SpeciesIndex.TryGetValue(l.Key, out var index))
            {
                continue;
            }

            var moves = table.TryGetValue(index, out var list) ? list.Select(m => moveKeys.GetValueOrDefault(m, "MOVE_NONE")).ToList() : [];
            var updated = l with { EggMoves = moves, LockedParts = l.LockedParts.Where(kv => kv.Key != "egg").ToDictionary(kv => kv.Key, kv => kv.Value) };
            data.Learnsets[i] = updated;
            state.Learnsets[l.Key] = updated;
        }

        state.EggMovesFound = true;
    }

    /// <summary>Rebuilds the table when any egg moves changed: in place when it fits, otherwise in free space (repointed).</summary>
    public static void Write(Gen3RomModule.State state, GameData data)
    {
        var t = state.Tables;
        if (!state.EggMovesFound || state.Rom.Offset(t.EggMovePointers[0]) is not (var at and >= 0))
        {
            return;
        }

        var changed = data.Learnsets.Any(l => state.Learnsets.TryGetValue(l.Key, out var before) && !before.EggMoves.SequenceEqual(l.EggMoves));
        if (!changed)
        {
            return;
        }

        var words = new List<ushort>();
        foreach (var l in data.Learnsets.Where(l => l.EggMoves.Count > 0 && state.SpeciesIndex.ContainsKey(l.Key)).OrderBy(l => state.SpeciesIndex[l.Key]))
        {
            words.Add((ushort)(Marker + state.SpeciesIndex[l.Key]));
            words.AddRange(l.EggMoves.Where(state.MoveIndex.ContainsKey).Select(m => (ushort)state.MoveIndex[m]));
        }

        words.Add(0xFFFF);
        var bytes = new byte[2 * words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2 * i), words[i]);
        }

        var rom = state.Rom;
        if (bytes.Length <= Size(rom, at))
        {
            bytes.CopyTo(rom.Bytes, at);
            return;
        }

        var target = rom.FindFree(bytes.Length);
        if (target < 0)
        {
            throw new InvalidOperationException("The ROM has no free space left for the bigger egg move table.");
        }

        bytes.CopyTo(rom.Bytes, target);
        foreach (var p in t.EggMovePointers)
        {
            rom.WritePointer(p, target);
        }
    }

    public static IEnumerable<ValidationIssue> Check(GameData data)
    {
        foreach (var l in data.Learnsets.Where(l => l.EggMoves.Count > MaxPerSpecies))
        {
            yield return new ValidationIssue(IssueSeverity.Error, "Learnsets", l.Key, $"These games use at most {MaxPerSpecies} egg moves per Pokémon.") { TargetKey = l.Key };
        }
    }
}
