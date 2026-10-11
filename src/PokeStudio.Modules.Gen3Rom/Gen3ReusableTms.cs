namespace PokeStudio.Modules.Gen3Rom;

/// <summary>
/// Reusable TMs in an unmodified FireRed (US 1.0) or Emerald (US) ROM. After a Pokémon learns a move from a TM, the
/// game's Task_LearnedMove calls RemoveBagItem(item, 1) for TMs (HMs are already kept); replacing that one call with two
/// "do nothing" instructions keeps the TM. Offsets and bytes are from matching pret builds; other ROMs aren't touched.
/// </summary>
internal static class Gen3ReusableTms
{
    private sealed record Version(string GameCode, byte Revision, int Call, byte[] Original);

    private static readonly Version[] Versions =
    [
        new("BPRE", 0, 0x00125C80, [0x74, 0xF7, 0xAA, 0xFA]),
        new("BPEE", 0, 0x001B6EEC, [0x1F, 0xF7, 0xDA, 0xFD]),
    ];

    private static readonly byte[] Nops = [0xC0, 0x46, 0xC0, 0x46]; // mov r8, r8 (Thumb "nop"), twice

    private static Version? VersionOf(RomImage rom) =>
        Versions.FirstOrDefault(v => v.GameCode == rom.GameCode && v.Revision == rom.Revision && rom.Length > v.Call + 4
            && (rom.Bytes.AsSpan(v.Call, 4).SequenceEqual(v.Original) || rom.Bytes.AsSpan(v.Call, 4).SequenceEqual(Nops)));

    /// <summary>True when TMs are kept after use, false when they're used up, null when this ROM can't be switched.</summary>
    public static bool? Read(RomImage rom) => VersionOf(rom) is { } v ? rom.Bytes.AsSpan(v.Call, 4).SequenceEqual(Nops) : null;

    public static void Write(RomImage rom, bool reusable)
    {
        if (VersionOf(rom) is { } v)
        {
            (reusable ? Nops : v.Original).CopyTo(rom.Bytes, v.Call);
        }
    }
}
