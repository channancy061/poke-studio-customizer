using System.Buffers.Binary;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Modules.Gen3Rom;

/// <summary>
/// The adaptive boss slot in an unmodified FireRed (US 1.0) or Emerald (US) ROM: our own compiled code (Inject/adaptive.c,
/// position-independent) and a table of bosses and candidates go into free space, and 8 bytes where the game has just
/// created a trainer's team jump to it. Other versions and already-modified ROMs aren't touched.
/// </summary>
internal static class Gen3Adaptive
{
    private sealed record Version(string GameCode, byte Revision, string Resource, int Hook, byte[] Original);

    // where CB2_InitBattleInternal creates the trainer's team, and the 8 original bytes there (from matching pret builds)
    private static readonly Version[] Versions =
    [
        new("BPRE", 0, "adaptive_firered.bin", 0x0000FF90, [0x01, 0xF0, 0xA6, 0xF9, 0x34, 0xF0, 0x2E, 0xFA]),
        new("BPEE", 0, "adaptive_emerald.bin", 0x000369C4, [0x01, 0xF0, 0x10, 0xFE, 0x28, 0x68, 0x80, 0x21]),
    ];

    private const uint Placeholder = 0xDEADBEEF;
    private const int CandidateSize = 14; // species 2, level 1, IV 1, item 2, moves 4 × 2

    private static Version? VersionOf(RomImage rom) =>
        Versions.FirstOrDefault(v => v.GameCode == rom.GameCode && v.Revision == rom.Revision
            && rom.Length > v.Hook + 8 && (rom.Bytes.AsSpan(v.Hook, 8).SequenceEqual(v.Original) || IsOurHook(rom, v)));

    // our hook: ldr r3, [pc, #0]; bx r3; .word code | 1
    private static bool IsOurHook(RomImage rom, Version v) =>
        rom.U16(v.Hook) == 0x4B00 && rom.U16(v.Hook + 2) == 0x4718 && rom.Offset(v.Hook + 4) is >= 0;

    public static bool Supported(RomImage rom) => VersionOf(rom) is not null;

    public static string WhyNot(RomImage rom) =>
        $"The adaptive boss slot can be added to unmodified Pokémon FireRed (US 1.0) and Emerald (US) ROMs. This ROM ({rom.GameCode} rev {rom.Revision}) "
        + "is another version or was already changed where the slot's code goes; use a decomp project for it.";

    private static byte[] Code(Version v)
    {
        using var s = typeof(Gen3Adaptive).Assembly.GetManifestResourceStream($"PokeStudio.Modules.Gen3Rom.Inject.{v.Resource}")
            ?? throw new InvalidOperationException($"The adaptive slot code for {v.GameCode} is missing from the app.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Reads the bosses' slots back from a ROM the app already patched.</summary>
    public static void Read(Gen3RomModule.State state, GameData data)
    {
        var rom = state.Rom;
        if (VersionOf(rom) is not { } v || !IsOurHook(rom, v) || Table(rom, v) is not { } table)
        {
            return;
        }

        var speciesKeys = state.SpeciesIndex.ToDictionary(kv => kv.Value, kv => kv.Key);
        var itemKeys = state.ItemIndex.ToDictionary(kv => kv.Value, kv => kv.Key);
        var moveKeys = state.MoveIndex.ToDictionary(kv => kv.Value, kv => kv.Key);
        var count = (int)rom.U32(table);
        var at = table + 4;
        for (var e = 0; e < count && at + 4 <= rom.Length; e++)
        {
            int id = rom.U16(at), flex = rom.U8(at + 2), n = rom.U8(at + 3);
            var candidates = new List<TrainerPokemon>();
            for (var k = 0; k < n; k++)
            {
                var c = at + 4 + (CandidateSize * k);
                var moves = Enumerable.Range(0, 4).Select(m => rom.U16(c + 6 + (2 * m))).Where(m => m > 0).Select(m => moveKeys.GetValueOrDefault(m, "MOVE_NONE")).ToList();
                var iv = rom.U8(c + 3);
                candidates.Add(new TrainerPokemon
                {
                    Species = speciesKeys.GetValueOrDefault(rom.U16(c), "SPECIES_NONE"),
                    Level = rom.U8(c + 2),
                    HeldItem = rom.U16(c + 4) is var it and > 0 ? itemKeys.GetValueOrDefault(it) : null,
                    Moves = moves,
                    Ivs = new BaseStats(iv, iv, iv, iv, iv, iv),
                });
            }

            at += 4 + (CandidateSize * n);
            var i = data.Trainers.FindIndex(t => t.Id == id);
            if (i >= 0 && flex < data.Trainers[i].Party.Count)
            {
                var updated = data.Trainers[i] with { FlexIndex = flex, FlexCandidates = candidates };
                data.Trainers[i] = updated;
                if (state.Trainers.TryGetValue(updated.Key, out var entry))
                {
                    state.Trainers[updated.Key] = (updated, entry.Index);
                }
            }
        }
    }

    /// <summary>Offset of the table our code uses, or null.</summary>
    private static int? Table(RomImage rom, Version v)
    {
        var code = rom.Offset(v.Hook + 4) is var c and >= 0 ? c & ~1 : -1;
        var length = Code(v).Length;
        if (code < 0 || code + length > rom.Length)
        {
            return null;
        }

        var placeholderAt = Code(v).AsSpan().IndexOf(BitConverter.GetBytes(Placeholder));
        return placeholderAt < 0 ? null : rom.Offset(code + placeholderAt) is var t and >= 0 ? t : null;
    }

    private static byte[] BuildTable(Gen3RomModule.State state, IReadOnlyList<Trainer> bosses)
    {
        var bytes = new List<byte>();
        void U16(int v) { bytes.Add((byte)v); bytes.Add((byte)(v >> 8)); }
        bytes.AddRange(BitConverter.GetBytes((uint)bosses.Count));
        foreach (var t in bosses)
        {
            var candidates = t.FlexCandidates.Where(c => state.SpeciesIndex.ContainsKey(c.Species)).Take(255).ToList();
            U16(t.Id);
            bytes.Add((byte)t.FlexIndex!.Value);
            bytes.Add((byte)candidates.Count);
            foreach (var c in candidates)
            {
                U16(state.SpeciesIndex[c.Species]);
                bytes.Add((byte)Math.Clamp(c.Level, 1, 100));
                bytes.Add((byte)Math.Clamp(c.Ivs?.Hp ?? t.Party[t.FlexIndex.Value].Ivs?.Hp ?? 0, 0, 31));
                U16(c.HeldItem is { } h && state.ItemIndex.TryGetValue(h, out var item) ? item : 0);
                for (var m = 0; m < 4; m++)
                {
                    U16(m < c.Moves.Count && state.MoveIndex.TryGetValue(c.Moves[m], out var move) ? move : 0);
                }
            }
        }

        return [.. bytes];
    }

    /// <summary>
    /// Writes the slots: the code and table into free space (replacing ours from an earlier save) and the hook; with no
    /// slots left, the game's original bytes come back.
    /// </summary>
    public static void Write(Gen3RomModule.State state, GameData data)
    {
        var rom = state.Rom;
        if (VersionOf(rom) is not { } v)
        {
            return;
        }

        var bosses = data.Trainers.Where(t => t.HasAdaptiveSlot && t.LockedReason is null && t.FlexCandidates.Any(c => state.SpeciesIndex.ContainsKey(c.Species))).ToList();
        var code = Code(v);

        // free what an earlier save wrote (its code and table), so saving again doesn't use up free space
        if (IsOurHook(rom, v) && Table(rom, v) is { } oldTable)
        {
            var oldCode = rom.Offset(v.Hook + 4) & ~1;
            var oldTableLength = 4 + Enumerable.Range(0, (int)rom.U32(oldTable)).Aggregate(0, (sum, _) =>
            {
                var at = oldTable + 4 + sum;
                return sum + 4 + (CandidateSize * rom.U8(at + 3));
            });
            rom.Bytes.AsSpan(oldCode, code.Length).Fill(0xFF);
            rom.Bytes.AsSpan(oldTable, oldTableLength).Fill(0xFF);
        }

        if (bosses.Count == 0)
        {
            v.Original.CopyTo(rom.Bytes, v.Hook);
            return;
        }

        var table = BuildTable(state, bosses);
        var total = ((code.Length + 3) & ~3) + table.Length;
        var at = rom.FindFree(total);
        if (at < 0)
        {
            throw new InvalidOperationException("The ROM has no free space left for the adaptive boss slots.");
        }

        var tableAt = at + ((code.Length + 3) & ~3);
        var placeholderAt = code.AsSpan().IndexOf(BitConverter.GetBytes(Placeholder));
        BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(placeholderAt), RomImage.Base + (uint)tableAt);
        code.CopyTo(rom.Bytes, at);
        table.CopyTo(rom.Bytes, tableAt);

        // ldr r3, [pc, #0]; bx r3; .word code | 1 (Thumb)
        BinaryPrimitives.WriteUInt16LittleEndian(rom.Bytes.AsSpan(v.Hook), 0x4B00);
        BinaryPrimitives.WriteUInt16LittleEndian(rom.Bytes.AsSpan(v.Hook + 2), 0x4718);
        BinaryPrimitives.WriteUInt32LittleEndian(rom.Bytes.AsSpan(v.Hook + 4), RomImage.Base + (uint)at + 1);
    }
}
