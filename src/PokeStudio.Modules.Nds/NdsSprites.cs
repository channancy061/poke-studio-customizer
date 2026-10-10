using System.Buffers.Binary;
using PokeStudio.Core.Graphics;
using PokeStudio.Core.Modules;

namespace PokeStudio.Modules.Nds;

/// <summary>
/// Pokémon sprites in Diamond/Pearl/Platinum/HeartGold/SoulSilver: six files per species (female back, male back,
/// female front, male front, normal palette, shiny palette). Pictures are 160×80 (two 80×80 frames side by side),
/// 4 bits per pixel in rows, lightly scrambled with a number sequence; this app shows the frames stacked (80×160).
/// </summary>
internal static class NdsSprites
{
    private const int PixelData = 0x30;
    private const int PixelBytes = 160 * 80 / 2;
    private const int PaletteData = 0x28;
    private const int PerSpecies = 6;

    /// <summary>The scrambling runs forwards from the first word (Platinum, HeartGold/SoulSilver) or backwards from the last (Diamond/Pearl).</summary>
    public static bool Forward(NdsState s)
    {
        if (s.SpriteForward is { } known)
        {
            return known;
        }

        var narc = new Narc(s.Rom.Read(s.Game.Sprites!));
        var raw = narc.Members[PerSpecies + 3];
        int Zeros(bool forward) => Decode(raw, forward).Count(p => p == 0);
        s.SpriteForward = Zeros(true) >= Zeros(false);
        return s.SpriteForward.Value;
    }

    private static byte[] Decode(byte[] file, bool forward)
    {
        var words = new ushort[PixelBytes / 2];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(PixelData + (2 * i)));
        }

        uint seed = forward ? words[0] : words[^1];
        for (var k = 0; k < words.Length; k++)
        {
            var i = forward ? k : words.Length - 1 - k;
            words[i] ^= (ushort)seed;
            seed = (seed * 1103515245) + 24691;
        }

        var px = new byte[160 * 80];
        for (var i = 0; i < words.Length; i++)
        {
            for (var q = 0; q < 4; q++)
            {
                px[(i * 4) + q] = (byte)((words[i] >> (4 * q)) & 0xF);
            }
        }

        return px;
    }

    /// <summary>Scrambles pixels back. The word the sequence starts from always reads back as 0, so those 4 pixels are made see-through.</summary>
    private static void Encode(byte[] file, byte[] px, bool forward)
    {
        var words = new ushort[PixelBytes / 2];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = (ushort)(px[i * 4] | (px[(i * 4) + 1] << 4) | (px[(i * 4) + 2] << 8) | (px[(i * 4) + 3] << 12));
        }

        var first = forward ? 0 : words.Length - 1;
        words[first] = 0;
        uint seed = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(PixelData + (2 * first)));
        if (seed == 0)
        {
            seed = 0x1234;
        }

        for (var k = 0; k < words.Length; k++)
        {
            var i = forward ? k : words.Length - 1 - k;
            BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(PixelData + (2 * i)), (ushort)(words[i] ^ (ushort)seed));
            seed = (seed * 1103515245) + 24691;
        }
    }

    // 160×80 side by side ↔ 80×160 stacked
    private static byte[] Stack(byte[] wide)
    {
        var tall = new byte[80 * 160];
        for (var y = 0; y < 80; y++)
        {
            for (var x = 0; x < 160; x++)
            {
                var frame = x / 80;
                tall[(((frame * 80) + y) * 80) + (x % 80)] = wide[(y * 160) + x];
            }
        }

        return tall;
    }

    private static byte[] Unstack(byte[] tall)
    {
        var wide = new byte[160 * 80];
        for (var y = 0; y < 160; y++)
        {
            for (var x = 0; x < 80; x++)
            {
                wide[((y % 80) * 160) + ((y / 80) * 80) + x] = tall[(y * 80) + x];
            }
        }

        return wide;
    }

    private static int? Member(NdsState s, string species, SpriteKind kind) =>
        s.Game.Sprites is null || !s.SpeciesIndex.TryGetValue(species, out var i) ? null
        : kind switch { SpriteKind.Front => (i * PerSpecies) + 3, SpriteKind.Back => (i * PerSpecies) + 1, _ => null };

    public static SpriteRules? Rules(NdsState s, string species, SpriteKind kind) =>
        Member(s, species, kind) is null ? null : new SpriteRules(80, 160, 2, 16, true, "Two frames stacked: the second is the animation frame.");

    public static IndexedImage? Load(NdsState s, string species, SpriteKind kind)
    {
        if (Member(s, species, kind) is not { } m)
        {
            return null;
        }

        var narc = new Narc(s.Rom.Read(s.Game.Sprites!));
        if (m >= narc.Members.Count || narc.Members[m].Length < PixelData + PixelBytes)
        {
            return null;
        }

        return new IndexedImage(80, 160, Stack(Decode(narc.Members[m], Forward(s))), Palette(s, species, false) ?? Enumerable.Repeat(default(Rgb), 16).ToList());
    }

    public static IReadOnlyList<Rgb>? Palette(NdsState s, string species, bool shiny)
    {
        if (s.Game.Sprites is null || !s.SpeciesIndex.TryGetValue(species, out var i))
        {
            return null;
        }

        var narc = new Narc(s.Rom.Read(s.Game.Sprites));
        var m = (i * PerSpecies) + (shiny ? 5 : 4);
        return m < narc.Members.Count && narc.Members[m].Length >= PaletteData + 32 ? Bgr555.Read(narc.Members[m].AsSpan(PaletteData, 32).ToArray()) : null;
    }

    public static void Write(NdsState s, GameData data)
    {
        if (s.Game.Sprites is null || data.Graphics.IsEmpty)
        {
            return;
        }

        var forward = Forward(s);
        var narc = new Narc(s.Rom.Read(s.Game.Sprites));
        foreach (var (key, image) in data.Graphics.Images)
        {
            var parts = key.Split('|');
            if (!Enum.TryParse<SpriteKind>(parts[1], out var kind) || Member(s, parts[0], kind) is not { } male || male >= narc.Members.Count)
            {
                continue;
            }

            // the female picture follows when it was the same as the male one (no gender difference)
            var female = male - 1;
            var sameForFemale = narc.Members[female].AsSpan().SequenceEqual(narc.Members[male]);
            var file = (byte[])narc.Members[male].Clone();
            Encode(file, Unstack(image.Pixels), forward);
            narc.Members[male] = file;
            if (sameForFemale)
            {
                narc.Members[female] = (byte[])file.Clone();
            }
        }

        foreach (var (key, palette) in data.Graphics.Palettes)
        {
            var parts = key.Split('|');
            if (!s.SpeciesIndex.TryGetValue(parts[0], out var i))
            {
                continue;
            }

            var m = (i * PerSpecies) + (parts[1] == "Shiny" ? 5 : 4);
            var file = (byte[])narc.Members[m].Clone();
            Bgr555.Write(palette).CopyTo(file, PaletteData);
            narc.Members[m] = file;
        }

        s.Rom.Write(s.Game.Sprites, narc.Build());
        data.Graphics.Clear();
    }
}
