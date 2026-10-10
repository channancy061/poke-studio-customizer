using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Modules.Gen2Rom;

/// <summary>
/// Egg moves in Gold/Silver/Crystal: a table of 251 pointers (one per Pokémon, in the same bank) to lists of move
/// numbers ended by 0xFF. Pokémon without egg moves share one empty list. Found by Bulbasaur's list (it has Skull Bash).
/// </summary>
internal static class Gen2EggMoves
{
    private const byte SkullBash = 0x82;

    /// <summary>Longest list the app reads back (the original games have at most 8).</summary>
    public const int MaxPerSpecies = 20;

    public static int Locate(GbRom rom)
    {
        var bytes = rom.Bytes;
        for (var bank = 1; bank < rom.Length / 0x4000; bank++)
        {
            var start = bank * 0x4000;
            for (var at = start; at + (2 * Gen2Tables.SpeciesCount) <= start + 0x4000; at++) // the table isn't always at an even address
            {
                if (Looks(rom, at))
                {
                    return at;
                }
            }
        }

        return -1;
    }

    private static bool Looks(GbRom rom, int table)
    {
        // every entry points forward into this bank, to a list ended within 20 moves; Bulbasaur's has Skull Bash
        var bank = GbRom.Bank(table);
        var empty = 0;
        for (var i = 0; i < Gen2Tables.SpeciesCount; i++)
        {
            var p = rom.U16(table + (2 * i));
            if (p is < 0x4000 or > 0x7FFF)
            {
                return false;
            }

            var target = (bank * 0x4000) + (p - 0x4000);
            if (target < table + (2 * Gen2Tables.SpeciesCount) || List(rom, target) is not { } list)
            {
                return false;
            }

            if (i == 0 && !list.Contains(SkullBash))
            {
                return false;
            }

            empty += list.Count == 0 ? 1 : 0;
        }

        return empty > 50;
    }

    private static List<byte>? List(GbRom rom, int at)
    {
        var list = new List<byte>();
        for (var i = at; i < rom.Length && list.Count <= MaxPerSpecies; i++)
        {
            var b = rom.Bytes[i];
            if (b == 0xFF)
            {
                return list;
            }

            if (b is 0 or > 251)
            {
                return null;
            }

            list.Add(b);
        }

        return null;
    }

    public static void Read(Gen2RomModule.State s, GameData data)
    {
        if (s.Tables.EggMoves < 0)
        {
            return;
        }

        var bank = GbRom.Bank(s.Tables.EggMoves);
        var moveKeys = s.MoveIndex.ToDictionary(kv => kv.Value, kv => kv.Key);
        for (var i = 0; i < data.Learnsets.Count; i++)
        {
            var l = data.Learnsets[i];
            if (!s.SpeciesIndex.TryGetValue(l.Key, out var index) || index is < 1 or > Gen2Tables.SpeciesCount)
            {
                continue;
            }

            var target = (bank * 0x4000) + (s.Rom.U16(s.Tables.EggMoves + (2 * (index - 1))) - 0x4000);
            var moves = (List(s.Rom, target) ?? []).Select(m => moveKeys.GetValueOrDefault(m, "MOVE_NONE")).ToList();
            var updated = l with { EggMoves = moves, LockedParts = l.LockedParts.Where(kv => kv.Key != "egg").ToDictionary(kv => kv.Key, kv => kv.Value) };
            data.Learnsets[i] = updated;
            s.Learnsets[l.Key] = updated;
        }
    }

    /// <summary>Changed lists are written in place when they fit and aren't shared, otherwise at the end of the bank.</summary>
    public static void Write(Gen2RomModule.State s, GameData data)
    {
        if (s.Tables.EggMoves < 0)
        {
            return;
        }

        var rom = s.Rom;
        var table = s.Tables.EggMoves;
        var bank = GbRom.Bank(table);
        var users = Enumerable.Range(0, Gen2Tables.SpeciesCount).GroupBy(i => rom.U16(table + (2 * i))).ToDictionary(g => g.Key, g => g.Count());
        foreach (var l in data.Learnsets)
        {
            if (!s.Learnsets.TryGetValue(l.Key, out var before) || before.EggMoves.SequenceEqual(l.EggMoves) || !s.SpeciesIndex.TryGetValue(l.Key, out var index)
                || index is < 1 or > Gen2Tables.SpeciesCount)
            {
                continue;
            }

            byte[] bytes = [.. l.EggMoves.Where(s.MoveIndex.ContainsKey).Select(m => (byte)s.MoveIndex[m]), 0xFF];
            var entry = table + (2 * (index - 1));
            var pointer = rom.U16(entry);
            var target = (bank * 0x4000) + (pointer - 0x4000);
            if (users[pointer] == 1 && bytes.Length <= before.EggMoves.Count + 1)
            {
                bytes.CopyTo(rom.Bytes, target);
                continue;
            }

            var at = rom.FindFree(bank, bytes.Length);
            if (at < 0)
            {
                throw new InvalidOperationException("There's no free space left near the egg move lists. Remove some egg moves elsewhere first.");
            }

            bytes.CopyTo(rom.Bytes, at);
            users[pointer]--;
            rom.W16(entry, GbRom.PointerTo(at));
        }
    }

    public static IEnumerable<PokeStudio.Core.Validation.ValidationIssue> Check(Gen2RomModule.State s, GameData data)
    {
        if (s.Tables.EggMoves < 0)
        {
            yield break;
        }

        foreach (var l in data.Learnsets.Where(l => l.EggMoves.Count > MaxPerSpecies))
        {
            yield return new PokeStudio.Core.Validation.ValidationIssue(PokeStudio.Core.Validation.IssueSeverity.Error, "Learnsets", l.Key,
                $"Up to {MaxPerSpecies} egg moves per Pokémon.") { TargetKey = l.Key };
        }
    }
}
