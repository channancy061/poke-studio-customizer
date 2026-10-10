using System.Buffers.Binary;

namespace PokeStudio.Modules.Gen3Rom;

/// <summary>A GBA ROM in memory with little helpers for its pointers (0x08xxxxxx) and free space (0xFF bytes).</summary>
internal sealed class RomImage(byte[] bytes)
{
    public const uint Base = 0x08000000;

    public byte[] Bytes { get; } = bytes;
    public int Length => Bytes.Length;

    public string GameCode => System.Text.Encoding.ASCII.GetString(Bytes, 0xAC, 4);
    public string Title => System.Text.Encoding.ASCII.GetString(Bytes, 0xA0, 12).TrimEnd('\0');
    public byte Revision => Bytes[0xBC];

    public byte U8(int o) => Bytes[o];
    public ushort U16(int o) => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(o));
    public uint U32(int o) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(o));
    public void W8(int o, int v) => Bytes[o] = (byte)v;
    public void W16(int o, int v) => BinaryPrimitives.WriteUInt16LittleEndian(Bytes.AsSpan(o), (ushort)v);
    public void W32(int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(Bytes.AsSpan(o), v);

    public bool IsPointer(uint p) => p >= Base && p < Base + (uint)Length;

    /// <summary>The offset a pointer points to, or -1.</summary>
    public int Offset(int at) => U32(at) is var p && IsPointer(p) ? (int)(p - Base) : -1;

    public void WritePointer(int at, int offset) => W32(at, Base + (uint)offset);

    public int Find(ReadOnlySpan<byte> pattern, int start = 0, int align = 1)
    {
        var span = Bytes.AsSpan();
        for (var i = span[start..].IndexOf(pattern); i >= 0; )
        {
            var at = start + i;
            if (at % align == 0)
            {
                return at;
            }

            start = at + 1;
            i = span[start..].IndexOf(pattern);
        }

        return -1;
    }

    /// <summary>Every 4-aligned place a pointer to <paramref name="offset"/> is stored.</summary>
    public IEnumerable<int> PointersTo(int offset)
    {
        Span<byte> p = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(p, Base + (uint)offset);
        var pattern = p.ToArray();
        for (var at = Find(pattern, 0, 4); at >= 0; at = Find(pattern, at + 4, 4))
        {
            yield return at;
        }
    }

    /// <summary>
    /// A 4-aligned run of free space (0xFF) for <paramref name="size"/> bytes, searched from the last quarter of the ROM
    /// (where the original games leave their unused space), with a few bytes of margin on each side.
    /// </summary>
    public int FindFree(int size)
    {
        const int Margin = 16;
        var need = size + (2 * Margin);
        for (var start = Length / 4 * 3; start < Length - need; start += 4)
        {
            if (Bytes[start] != 0xFF)
            {
                continue;
            }

            var run = 0;
            while (run < need && Bytes[start + run] == 0xFF)
            {
                run++;
            }

            if (run >= need)
            {
                return start + Margin;
            }

            start += run & ~3;
        }

        return -1;
    }
}

/// <summary>Where the game's tables are, found by their contents (works across versions and many light hacks).</summary>
internal sealed record Gen3Tables
{
    public const int SpeciesCount = 412;
    public const int BaseStatsSize = 28;
    public const int MoveSize = 12;
    public const int ItemSize = 44;
    public const int TrainerSize = 40;
    public const int EvolutionsPerSpecies = 5;

    public required int BaseStats { get; init; }
    public required int SpeciesNames { get; init; }
    public required int Moves { get; init; }
    public required int MoveCount { get; init; }
    public required int MoveNames { get; init; }
    public required int AbilityNames { get; init; }
    public required int AbilityCount { get; init; }
    public required int Items { get; init; }
    public required int ItemCount { get; init; }
    public required int Evolutions { get; init; }
    public required int LevelUpPointers { get; init; }
    public required int NationalDex { get; init; }
    public required int TmMoves { get; init; }
    public required int TmCompatibility { get; init; }
    public required int Trainers { get; init; }
    public required int TrainerCount { get; init; }
    public int ClassNames { get; init; } = -1;
    public int ClassCount { get; init; }
    public required int WildHeaders { get; init; }
    public required int WildCount { get; init; }
    public int MoveDescriptions { get; init; } = -1;
    public int MapGroups { get; init; } = -1;
    public int MapNames { get; init; } = -1;

    /// <summary>"Emerald style": 8-byte entries with a name pointer at +4; "FireRed style": a list of name pointers.</summary>
    public bool MapNamesArePointerList { get; init; }

    public int MapNamesFirstSection { get; init; }

    public static Gen3Tables? Locate(RomImage rom, out string? problem)
    {
        problem = null;
        int Need(int value, string what)
        {
            if (value < 0)
            {
                throw new InvalidDataException(what);
            }

            return value;
        }

        try
        {
            // Bulbasaur's base stats: 45/49/49/45/65/65, Grass/Poison (entry 0 is the empty "?" species)
            var bulba = Need(rom.Find([45, 49, 49, 45, 65, 65, 12, 3]), "the Pokémon stats table");
            var names = Need(rom.Find(Gen3Text.Write("BULBASAUR")), "the Pokémon names");
            // Pound: power 40, Normal, 100% accuracy, 35 PP (entry 0 is "no move")
            var pound = Need(rom.Find([0, 40, 0, 100, 35, 0, 0, 0]), "the moves table");
            var moveNames = Need(rom.Find(Gen3Text.Write("POUND")), "the move names") - 13;
            var abilityNames = Need(rom.Find(Gen3Text.Write("STENCH")), "the ability names") - 13;
            var masterBall = Need(rom.Find(Gen3Text.Write("MASTER BALL")), "the items table");
            var evolutions = Need(rom.Find([4, 0, 16, 0, 2, 0, 0, 0, .. new byte[32]]), "the evolution table") - (EvolutionsPerSpecies * 8);
            // Bulbasaur learns Tackle (1), Growl (4), Leech Seed (7), Vine Whip (10); entry 0 of the pointer table points there too
            Span<byte> learn = stackalloc byte[8];
            BitConverter.TryWriteBytes(learn, (ushort)((1 << 9) | 33));
            BitConverter.TryWriteBytes(learn[2..], (ushort)((4 << 9) | 45));
            BitConverter.TryWriteBytes(learn[4..], (ushort)((7 << 9) | 73));
            BitConverter.TryWriteBytes(learn[6..], (ushort)((10 << 9) | 22));
            var bulbaLearnset = Need(rom.Find(learn), "Bulbasaur's level-up moves");
            var learnsetTable = Need(rom.PointersTo(bulbaLearnset).DefaultIfEmpty(-1).First(), "the level-up move table");

            var natDex = Need(rom.Find(Enumerable.Range(1, 40).SelectMany(i => BitConverter.GetBytes((ushort)i)).ToArray(), 0, 2), "the National Dex order");
            var tmMoves = Need(rom.Find([.. BitConverter.GetBytes((ushort)264), .. BitConverter.GetBytes((ushort)337), .. BitConverter.GetBytes((ushort)352), .. BitConverter.GetBytes((ushort)347)], 0, 2), "the TM list");
            var tmCompat = Need(FindTmCompatibility(rom), "the TM compatibility table");
            var (trainers, trainerCount) = FindTrainers(rom);
            Need(trainers, "the trainers table");
            var (wild, wildCount) = FindWildHeaders(rom);
            Need(wild, "the wild Pokémon table");

            var moveCount = 1;
            while (Gen3Text.LooksLikeName(rom.Bytes.AsSpan(moveNames + (13 * moveCount)), 13) && moveCount < 1000)
            {
                moveCount++;
            }

            var abilityCount = 1;
            while (Gen3Text.LooksLikeName(rom.Bytes.AsSpan(abilityNames + (13 * abilityCount)), 13) && abilityCount < 300)
            {
                abilityCount++;
            }

            var items = masterBall - ItemSize;
            var itemCount = 1;
            // (unused item slots keep id 0, so the name decides)
            while (itemCount < 1000 && items + (ItemSize * (itemCount + 1)) <= rom.Length
                && rom.U16(items + (ItemSize * itemCount) + 14) is var id && (id == itemCount || id == 0)
                && Gen3Text.LooksLikeName(rom.Bytes.AsSpan(items + (ItemSize * itemCount)), 14))
            {
                itemCount++;
            }

            var (classNames, classCount) = FindClassNames(rom, trainers, trainerCount);
            var mapNames = FindMapNames(rom, out var pointerList, out var firstSection);
            return new Gen3Tables
            {
                BaseStats = bulba - BaseStatsSize,
                SpeciesNames = names - 11,
                Moves = pound - MoveSize,
                MoveCount = moveCount,
                MoveNames = moveNames,
                AbilityNames = abilityNames,
                AbilityCount = abilityCount,
                Items = items,
                ItemCount = itemCount,
                Evolutions = evolutions,
                LevelUpPointers = learnsetTable,
                NationalDex = natDex,
                TmMoves = tmMoves,
                TmCompatibility = tmCompat,
                Trainers = trainers,
                TrainerCount = trainerCount,
                ClassNames = classNames,
                ClassCount = classCount,
                WildHeaders = wild,
                WildCount = wildCount,
                MoveDescriptions = FindMoveDescriptions(rom),
                MapGroups = FindMapGroups(rom),
                MapNames = mapNames,
                MapNamesArePointerList = pointerList,
                MapNamesFirstSection = firstSection,
            };
        }
        catch (InvalidDataException ex)
        {
            problem = $"Couldn't find {ex.Message} in this ROM. It may be a hack that moved or changed it.";
            return null;
        }
    }

    /// <summary>412 entries of 8 bytes; entry 0 empty; Bulbasaur and Ivysaur share their TM list (Toxic, Bullet Seed…).</summary>
    private static int FindTmCompatibility(RomImage rom)
    {
        ulong tms = 0;
        foreach (var tm in new[] { 6, 9, 10, 11, 17, 19, 21, 22, 27, 32, 36, 42, 43, 44, 45 })
        {
            tms |= 1UL << (tm - 1);
        }

        const ulong TmMask = (1UL << 50) - 1;
        for (var at = 0; at < rom.Length - 32; at += 4)
        {
            if (rom.U32(at) != 0 || rom.U32(at + 4) != 0)
            {
                continue;
            }

            var bulba = (ulong)rom.U32(at + 8) | ((ulong)rom.U32(at + 12) << 32);
            var ivy = (ulong)rom.U32(at + 16) | ((ulong)rom.U32(at + 20) << 32);
            if ((bulba & TmMask) == tms && bulba == ivy)
            {
                return at;
            }
        }

        return -1;
    }

    private static bool LooksLikeTrainer(RomImage rom, int at)
    {
        var size = rom.U8(at + 32);
        var party = rom.U32(at + 36);
        var name = rom.Bytes.AsSpan(at + 4, 12);
        return size <= 6 && rom.IsPointer(party) && Gen3Text.LooksLikeName(name, 12, allowEmpty: true);
    }

    /// <summary>40-byte records: an empty first trainer, then 50 in a row that look right.</summary>
    private static (int Start, int Count) FindTrainers(RomImage rom)
    {
        for (var at = 0; at < rom.Length - (TrainerSize * 60); at += 4)
        {
            if (rom.U8(at + 4) != Gen3Text.End || rom.U8(at + 32) != 0)
            {
                continue;
            }

            var ok = true;
            for (var i = 1; i <= 50 && ok; i++)
            {
                var rec = at + (TrainerSize * i);
                ok = LooksLikeTrainer(rom, rec) && rom.U8(rec + 32) > 0;
            }

            // (FireRed starts with unused, unnamed trainers: count names over a longer stretch)
            var named = 0;
            for (var i = 1; i <= 400 && ok && at + (TrainerSize * (i + 1)) <= rom.Length; i++)
            {
                named += rom.U8(at + (TrainerSize * i) + 4) != Gen3Text.End && LooksLikeTrainer(rom, at + (TrainerSize * i)) ? 1 : 0;
            }

            if (!ok || named < 100)
            {
                continue;
            }

            var count = 1;
            while (at + (TrainerSize * (count + 1)) <= rom.Length && LooksLikeTrainer(rom, at + (TrainerSize * count)) && count < 2000)
            {
                count++;
            }

            return (at, count);
        }

        return (-1, 0);
    }

    /// <summary>13-byte names right before the trainers table.</summary>
    private static (int Start, int Count) FindClassNames(RomImage rom, int trainers, int trainerCount)
    {
        var maxClass = 0;
        for (var i = 0; i < trainerCount; i++)
        {
            maxClass = Math.Max(maxClass, rom.U8(trainers + (TrainerSize * i) + 1));
        }

        // the right alignment is the one where each name follows the previous one's end marker or padding
        var best = (Start: -1, Count: 0, Score: -1);
        for (var pad = 0; pad < 4; pad++)
        {
            var end = trainers - pad;
            var count = 0;
            var score = 0;
            while (end - (13 * (count + 1)) > 0 && Gen3Text.LooksLikeName(rom.Bytes.AsSpan(end - (13 * (count + 1))), 13, allowEmpty: true) && count < 300)
            {
                count++;
                score += rom.U8(end - (13 * count) - 1) is 0x00 or 0xFF ? 1 : 0;
            }

            if (count > maxClass && score > best.Score)
            {
                best = (end - (13 * count), count, score);
            }
        }

        if (best.Count <= maxClass)
        {
            return (-1, 0);
        }

        // the run may start with unrelated text: keep the last (maxClass + 1) names
        var classes = maxClass + 1;
        return (best.Start + (13 * (best.Count - classes)), classes);
    }

    /// <summary>20-byte headers (group, number, then four pointers), ended by group 0xFF; checked through the land tables.</summary>
    private static (int Start, int Count) FindWildHeaders(RomImage rom)
    {
        bool Header(int at)
        {
            if (rom.U16(at + 2) != 0)
            {
                return false;
            }

            for (var k = 0; k < 4; k++)
            {
                var p = rom.U32(at + 4 + (4 * k));
                if (p != 0 && !rom.IsPointer(p))
                {
                    return false;
                }
            }

            return true;
        }

        bool LandTable(int at)
        {
            var land = rom.Offset(at + 4);
            if (land < 0 || rom.U8(land) is 0 or > 100)
            {
                return false;
            }

            var mons = rom.Offset(land + 4);
            if (mons < 0)
            {
                return false;
            }

            for (var k = 0; k < 12; k++)
            {
                var min = rom.U8(mons + (4 * k));
                var max = rom.U8(mons + (4 * k) + 1);
                var sp = rom.U16(mons + (4 * k) + 2);
                if (min is 0 or > 100 || max < min || max > 100 || sp is 0 or >= SpeciesCount)
                {
                    return false;
                }
            }

            return true;
        }

        for (var at = 0; at < rom.Length - 400; at += 4)
        {
            var ok = true;
            for (var i = 0; i < 8 && ok; i++)
            {
                ok = Header(at + (20 * i)) && LandTable(at + (20 * i));
            }

            if (!ok)
            {
                continue;
            }

            var count = 0;
            while (rom.U8(at + (20 * count)) != 0xFF && count < 1000)
            {
                count++;
            }

            return (at, count);
        }

        return (-1, 0);
    }

    /// <summary>Pointers to move descriptions; the first describes Pound.</summary>
    private static int FindMoveDescriptions(RomImage rom)
    {
        var text = rom.Find(Gen3Text.Write("Pounds the foe with\nforelegs or tail."));
        return text < 0 ? -1 : rom.PointersTo(text).DefaultIfEmpty(-1).First();
    }

    /// <summary>The map groups table: pointers to lists of pointers to map headers (each header has four pointers first).</summary>
    private static int FindMapGroups(RomImage rom)
    {
        bool HeaderAt(int header) => header >= 0 && header + 28 <= rom.Length
            && rom.IsPointer(rom.U32(header)) && rom.IsPointer(rom.U32(header + 4)) && rom.IsPointer(rom.U32(header + 8))
            && (rom.U32(header + 12) == 0 || rom.IsPointer(rom.U32(header + 12)));

        for (var at = 0; at < rom.Length - 64; at += 4)
        {
            var ok = true;
            for (var g = 0; g < 10 && ok; g++)
            {
                var list = rom.Offset(at + (4 * g));
                ok = list >= 0 && HeaderAt(rom.Offset(list)) && HeaderAt(rom.Offset(list + 4));
            }

            if (ok)
            {
                return at;
            }
        }

        return -1;
    }

    /// <summary>Region map names: Emerald/Ruby keep 8-byte entries (x, y, width, height, name pointer); FireRed a list of name pointers.</summary>
    private static int FindMapNames(RomImage rom, out bool pointerList, out int firstSection)
    {
        pointerList = false;
        firstSection = 0;
        // Emerald / Ruby: 8 entries in a row of (x, y, width, height, name pointer), then back to the first one
        bool Entry(int e) => e >= 0 && rom.U8(e) < 40 && rom.U8(e + 1) < 30 && rom.U8(e + 2) is >= 1 and <= 6 && rom.U8(e + 3) is >= 1 and <= 6
            && rom.Offset(e + 4) is var n and >= 0 && Gen3Text.LooksLikeName(rom.Bytes.AsSpan(n), 24);
        string NameAt(int e) => rom.Offset(e + 4) is var n and >= 0 ? Gen3Text.Read(rom.Bytes.AsSpan(n, Math.Min(24, rom.Length - n))) : "";
        {
            for (var at = 0; at < rom.Length - 80; at += 4)
            {
                var ok = true;
                for (var k = 0; k < 8 && ok; k++)
                {
                    ok = Entry(at + (8 * k));
                }

                if (ok)
                {
                    var start = at;
                    while (Entry(start - 8))
                    {
                        start -= 8;
                    }

                    // Hoenn's list starts with its towns and routes
                    if (Enumerable.Range(0, 30).Any(k => NameAt(start + (8 * k)) is "ROUTE 101" or "LITTLEROOT TOWN"))
                    {
                        return start;
                    }

                    at = start + (8 * 8);
                }
            }
        }

        var palletText = Gen3Text.Write("PALLET TOWN");
        for (var pallet = rom.Find(palletText); pallet >= 0; pallet = rom.Find(palletText, pallet + 1))
        {
            foreach (var at in rom.PointersTo(pallet))
            {
                if (rom.Offset(at + 4) is var next and >= 0 && Gen3Text.Read(rom.Bytes.AsSpan(next, 20)) == "VIRIDIAN CITY")
                {
                    pointerList = true;
                    firstSection = 0x58; // MAPSEC_PALLET_TOWN in FireRed / LeafGreen
                    return at;
                }
            }
        }

        return -1;
    }
}
