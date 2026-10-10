using System.Buffers.Binary;

namespace PokeStudio.Modules.Nds;

/// <summary>Where one DS game keeps its data (file paths inside the ROM) and which record formats it uses.</summary>
internal sealed record NdsGame
{
    public required string Title { get; init; }
    public required int Generation { get; init; }
    public required string Personal { get; init; }
    public required string Learnsets { get; init; }
    public required string Evolutions { get; init; }
    public required string Moves { get; init; }
    public required string TrainerData { get; init; }
    public required string TrainerParties { get; init; }
    public required string Items { get; init; }
    public required string Text { get; init; }

    /// <summary>Where the story dialogue is when it isn't with the other text (Black/White keep it in a second archive).</summary>
    public string? StoryText { get; init; }
    public required string Encounters { get; init; }

    /// <summary>"dppt" (424-byte areas), "hgss" (196) or "bw" (232 per season).</summary>
    public required string EncounterFormat { get; init; }

    public int SpeciesCount => Generation == 4 ? 494 : 650;
    public int PersonalSize => Generation == 4 ? 44 : 60;
    public int TmBitsOffset => Generation == 4 ? 28 : 40;
    public int EvolutionsPerSpecies => 7;
    public int EvolutionSize => 6;
    public int MoveSize => Generation == 4 ? 16 : 36;
    public bool Tested { get; init; } = true;

    /// <summary>Platinum and HeartGold/SoulSilver add 2 bytes (ball capsule) after each trainer Pokémon; Diamond/Pearl don't.</summary>
    public bool TrainerMonExtra { get; init; } = true;

    private static readonly NdsGame Dp = new()
    {
        Title = "Pokémon Diamond", Generation = 4, Personal = "poketool/personal/personal.narc", Learnsets = "poketool/personal/wotbl.narc",
        Evolutions = "poketool/personal/evo.narc", Moves = "poketool/waza/waza_tbl.narc", TrainerData = "poketool/trainer/trdata.narc",
        TrainerParties = "poketool/trainer/trpoke.narc", Items = "itemtool/itemdata/item_data.narc", Text = "msgdata/msg.narc",
        Encounters = "fielddata/encountdata/d_enc_data.narc", EncounterFormat = "dppt", TrainerMonExtra = false,
    };

    private static readonly NdsGame Hgss = new()
    {
        Title = "Pokémon HeartGold", Generation = 4, Personal = "a/0/0/2", Learnsets = "a/0/3/3", Evolutions = "a/0/3/4", Moves = "a/0/1/1",
        TrainerData = "a/0/5/5", TrainerParties = "a/0/5/6", Items = "a/0/1/7", Text = "a/0/2/7", Encounters = "a/0/3/7", EncounterFormat = "hgss",
    };

    private static readonly NdsGame Bw = new()
    {
        Title = "Pokémon Black", Generation = 5, Personal = "a/0/1/6", Learnsets = "a/0/1/8", Evolutions = "a/0/1/9", Moves = "a/0/2/1",
        TrainerData = "a/0/9/2", TrainerParties = "a/0/9/3", Items = "a/0/2/4", Text = "a/0/0/2", StoryText = "a/0/0/3", Encounters = "a/1/2/6", EncounterFormat = "bw",
    };

    public static NdsGame? For(string gameCode) => gameCode[..3] switch
    {
        "ADA" => Dp,
        "APA" => Dp with { Title = "Pokémon Pearl", Encounters = "fielddata/encountdata/p_enc_data.narc" },
        "CPU" => Dp with
        {
            TrainerMonExtra = true,
            Title = "Pokémon Platinum", Personal = "poketool/personal/pl_personal.narc", Moves = "poketool/waza/pl_waza_tbl.narc",
            Items = "itemtool/itemdata/pl_item_data.narc", Text = "msgdata/pl_msg.narc", Encounters = "fielddata/encountdata/pl_enc_data.narc",
        },
        "IPK" => Hgss,
        "IPG" => Hgss with { Title = "Pokémon SoulSilver" },
        "IRB" => Bw,
        "IRA" => Bw with { Title = "Pokémon White" },
        "IRE" => Bw with { Title = "Pokémon Black 2", TrainerData = "a/0/9/1", TrainerParties = "a/0/9/2", Encounters = "a/1/2/7", Tested = false },
        "IRD" => Bw with { Title = "Pokémon White 2", TrainerData = "a/0/9/1", TrainerParties = "a/0/9/2", Encounters = "a/1/2/7", Tested = false },
        _ => null,
    };
}

/// <summary>Reads the main program (arm9) — decompressing it when the game stores it compressed — to find tables there.</summary>
internal static class Arm9
{
    public static byte[] Read(NdsRom rom)
    {
        var bytes = rom.Bytes;
        var offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x20));
        var ram = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x28));
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x2C));
        var arm9 = bytes.AsSpan(offset, size).ToArray();

        // the module parameters (marked by the nitrocode) say where the compressed part ends, or 0 when it isn't compressed
        ReadOnlySpan<byte> nitro = [0x21, 0x06, 0xC0, 0xDE, 0xDE, 0xC0, 0x06, 0x21];
        var marker = arm9.AsSpan().IndexOf(nitro);
        if (marker < 0x1C)
        {
            return arm9;
        }

        var compressedEnd = BinaryPrimitives.ReadUInt32LittleEndian(arm9.AsSpan(marker - 0x1C + 0x14));
        if (compressedEnd == 0 || compressedEnd < ram || compressedEnd - ram > (uint)arm9.Length)
        {
            return arm9;
        }

        return DecompressBlz(arm9, (int)(compressedEnd - ram));
    }

    private static int OverlayTable(NdsRom rom) => (int)BinaryPrimitives.ReadUInt32LittleEndian(rom.Bytes.AsSpan(0x50));

    public static int OverlayCount(NdsRom rom) => (int)BinaryPrimitives.ReadUInt32LittleEndian(rom.Bytes.AsSpan(0x54)) / 32;

    /// <summary>One overlay, decompressed, or null when it can't be read.</summary>
    public static byte[]? Overlay(NdsRom rom, int index)
    {
        var at = OverlayTable(rom) + (32 * index);
        var fileId = (int)BinaryPrimitives.ReadUInt32LittleEndian(rom.Bytes.AsSpan(at + 0x18));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(rom.Bytes.AsSpan(at + 0x1C));
        byte[] data;
        try
        {
            data = rom.Read(fileId);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        if ((flags & 0x01000000) == 0 || data.Length <= 8)
        {
            return data;
        }

        var compressed = (int)(flags & 0xFFFFFF);
        try
        {
            return DecompressBlz(data, Math.Min(compressed == 0 ? data.Length : compressed, data.Length));
        }
        catch (IndexOutOfRangeException)
        {
            return data;
        }
    }

    /// <summary>Stores an overlay uncompressed (the game loads either kind) and marks it so in the overlay table.</summary>
    public static void WriteOverlay(NdsRom rom, int index, byte[] data)
    {
        var at = OverlayTable(rom) + (32 * index);
        var fileId = (int)BinaryPrimitives.ReadUInt32LittleEndian(rom.Bytes.AsSpan(at + 0x18));
        rom.Write(fileId, data);
        at = OverlayTable(rom) + (32 * index); // the table doesn't move, but read it again in case the header changed
        BinaryPrimitives.WriteUInt32LittleEndian(rom.Bytes.AsSpan(at + 0x1C), 0);
    }

    /// <summary>The program's overlays (arm9 overlay table at header 0x50), decompressed where they're stored compressed.</summary>
    public static IEnumerable<byte[]> Overlays(NdsRom rom)
    {
        var bytes = rom.Bytes;
        var table = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x50));
        var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x54));
        for (var at = table; at + 32 <= table + size; at += 32)
        {
            var fileId = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 0x18));
            var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 0x1C));
            byte[] data;
            try
            {
                data = rom.Read(fileId);
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            if ((flags & 0x01000000) != 0 && data.Length > 8)
            {
                var compressed = (int)(flags & 0xFFFFFF);
                byte[]? unpacked = null;
                try
                {
                    unpacked = DecompressBlz(data, Math.Min(compressed == 0 ? data.Length : compressed, data.Length));
                }
                catch (IndexOutOfRangeException)
                {
                    // not compressed after all
                }

                if (unpacked is not null)
                {
                    data = unpacked;
                }
            }

            yield return data;
        }
    }

    /// <summary>"Backwards LZ" used by DS programs: decompressed from the end towards the start.</summary>
    public static byte[] DecompressBlz(byte[] data, int end)
    {
        var footer = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(end - 8));
        var extra = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(end - 4));
        var compressedLength = (int)(footer & 0xFFFFFF);
        var headerLength = (int)(footer >> 24);
        var output = new byte[end + extra + (data.Length - end)];
        data.AsSpan(0, end).CopyTo(output);
        data.AsSpan(end).CopyTo(output.AsSpan(end + extra));
        var src = end - headerLength;
        var dst = end + extra;
        var stop = end - compressedLength;
        while (src > stop)
        {
            var flags = data[--src];
            for (var bit = 0; bit < 8 && src > stop; bit++, flags <<= 1)
            {
                if ((flags & 0x80) == 0)
                {
                    output[--dst] = data[--src];
                    continue;
                }

                var b1 = data[--src];
                var b2 = data[--src];
                var length = (b1 >> 4) + 3;
                var distance = (((b1 & 0x0F) << 8) | b2) + 3;
                for (var i = 0; i < length; i++)
                {
                    dst--;
                    output[dst] = output[dst + distance];
                }
            }
        }

        return output;
    }

    /// <summary>
    /// The TM/HM move list: Gen 4 starts with Focus Punch, Dragon Claw, Water Pulse, Calm Mind; Gen 5 with Hone Claws,
    /// Dragon Claw, Psyshock, Calm Mind.
    /// </summary>
    public static int FindTmList(byte[] arm9)
    {
        var gen4 = FindTmList(arm9, [0x08, 0x01, 0x51, 0x01, 0x60, 0x01, 0x5B, 0x01]);
        return gen4 >= 0 ? gen4 : FindTmList(arm9, [0xD4, 0x01, 0x51, 0x01, 0xD9, 0x01, 0x5B, 0x01]);
    }

    private static int FindTmList(byte[] arm9, ReadOnlySpan<byte> start)
    {
        for (var at = arm9.AsSpan().IndexOf(start); at >= 0; )
        {
            if (at % 2 == 0)
            {
                return at;
            }

            var next = arm9.AsSpan(at + 1).IndexOf(start);
            at = next < 0 ? -1 : at + 1 + next;
        }

        return -1;
    }
}
