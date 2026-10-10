using PokeStudio.Core.Graphics;
using PokeStudio.Core.Modules;

namespace PokeStudio.Modules.Gen3Rom;

/// <summary>Pokémon front/back sprites and palettes in a Gen 3 ROM (LZ77-compressed 4bpp tiles; new data goes to free space).</summary>
internal static class Gen3Sprites
{
    public static SpriteRules? Rules(Gen3RomModule.State s, SpriteKind kind)
    {
        var t = s.Tables;
        return kind switch
        {
            SpriteKind.Front when t.AnimFrontPics >= 0 => new SpriteRules(64, 128, 2, 16, true, "Two frames stacked: the second is the animation frame."),
            SpriteKind.Front when t.FrontPics >= 0 => new SpriteRules(64, 64, 1, 16, true),
            SpriteKind.Back when t.BackPics >= 0 => new SpriteRules(64, 64, 1, 16, true),
            SpriteKind.Icon when t.IconTable >= 0 => new SpriteRules(32, 64, 2, 16, false, "Icons share one of six palettes; your colours are matched to it."),
            _ => null,
        };
    }

    private static int Table(Gen3RomModule.State s, SpriteKind kind) => kind switch
    {
        SpriteKind.Front => s.Tables.AnimFrontPics >= 0 ? s.Tables.AnimFrontPics : s.Tables.FrontPics,
        SpriteKind.Back => s.Tables.BackPics,
        _ => -1,
    };

    private static IReadOnlyList<Rgb>? IconPalette(Gen3RomModule.State s, int i)
    {
        var t = s.Tables;
        if (t.IconTable < 0 || i >= t.IconCount)
        {
            return null;
        }

        var p = s.Rom.Offset(t.IconPalettes + (8 * s.Rom.U8(t.IconPaletteIndices + i)));
        return p < 0 ? null : Bgr555.Read(s.Rom.Bytes.AsSpan(p, 32).ToArray());
    }

    public static IndexedImage? Load(Gen3RomModule.State s, string species, SpriteKind kind)
    {
        if (kind == SpriteKind.Icon)
        {
            if (s.Tables.IconTable < 0 || !s.SpeciesIndex.TryGetValue(species, out var n) || n >= s.Tables.IconCount || s.Rom.Offset(s.Tables.IconTable + (4 * n)) is not (var at and >= 0))
            {
                return null;
            }

            return Tiles4bpp.ToImage(s.Rom.Bytes.AsSpan(at, 0x400).ToArray(), 32, IconPalette(s, n)!);
        }

        var table = Table(s, kind);
        if (table < 0 || !s.SpeciesIndex.TryGetValue(species, out var i))
        {
            return null;
        }

        var tiles = Lz77.Decompress(s.Rom.Bytes, s.Rom.Offset(table + (8 * i)));
        return tiles is null ? null : Tiles4bpp.ToImage(tiles, 64, Palette(s, species, false) ?? Enumerable.Repeat(default(Rgb), 16).ToList());
    }

    public static IReadOnlyList<Rgb>? Palette(Gen3RomModule.State s, string species, bool shiny)
    {
        var table = shiny ? s.Tables.ShinyPalettes : s.Tables.Palettes;
        if (table < 0 || !s.SpeciesIndex.TryGetValue(species, out var i))
        {
            return null;
        }

        var data = Lz77.Decompress(s.Rom.Bytes, s.Rom.Offset(table + (8 * i)));
        return data is null ? null : Bgr555.Read(data);
    }

    /// <summary>Writes pending sprite and palette edits (compressed into free space; the table entries are repointed).</summary>
    public static void Write(Gen3RomModule.State s, GameData data)
    {
        var rom = s.Rom;
        void Put(int table, int index, byte[] raw)
        {
            if (table < 0)
            {
                return;
            }

            var packed = Lz77.Compress(raw);
            var at = rom.FindFree(packed.Length);
            if (at < 0)
            {
                throw new InvalidOperationException("The ROM has no free space left for the new graphics.");
            }

            packed.CopyTo(rom.Bytes, at);
            rom.WritePointer(table + (8 * index), at);
        }

        foreach (var (key, image) in data.Graphics.Images)
        {
            var parts = key.Split('|');
            if (!s.SpeciesIndex.TryGetValue(parts[0], out var i) || !Enum.TryParse<SpriteKind>(parts[1], out var kind))
            {
                continue;
            }

            var tiles = Tiles4bpp.FromImage(image);
            if (kind == SpriteKind.Front)
            {
                Put(s.Tables.AnimFrontPics, i, tiles);
                Put(s.Tables.FrontPics, i, tiles.AsSpan(0, Math.Min(tiles.Length, 0x800)).ToArray()); // the still picture is the first frame
            }
            else if (kind == SpriteKind.Back)
            {
                Put(s.Tables.BackPics, i, tiles);
            }
            else if (kind == SpriteKind.Icon && s.Tables.IconTable >= 0 && i < s.Tables.IconCount)
            {
                // icons aren't compressed: overwrite in place, unless other species share this picture
                var entry = s.Tables.IconTable + (4 * i);
                var at = rom.Offset(entry);
                var shared = Enumerable.Range(0, s.Tables.IconCount).Count(k => rom.U32(s.Tables.IconTable + (4 * k)) == rom.U32(entry)) > 1;
                if (shared)
                {
                    at = rom.FindFree(0x400);
                    if (at < 0)
                    {
                        throw new InvalidOperationException("The ROM has no free space left for the new icon.");
                    }

                    rom.WritePointer(entry, at);
                }

                tiles.AsSpan(0, Math.Min(tiles.Length, 0x400)).CopyTo(rom.Bytes.AsSpan(at));
            }
        }

        foreach (var (key, palette) in data.Graphics.Palettes)
        {
            var parts = key.Split('|');
            if (s.SpeciesIndex.TryGetValue(parts[0], out var i))
            {
                Put(parts[1] == "Shiny" ? s.Tables.ShinyPalettes : s.Tables.Palettes, i, Bgr555.Write(palette));
            }
        }

        data.Graphics.Clear();
    }
}
