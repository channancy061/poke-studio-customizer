using PokeStudio.Core.Graphics;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Safety;
using PokeStudio.Modules.Expansion;
using PokeStudio.Modules.Gen3Rom;

namespace PokeStudio.Core.Tests;

public class GraphicsTests
{
    private static readonly Rgb[] Colors = [.. Enumerable.Range(0, 16).Select(i => new Rgb((byte)(i * 16), (byte)(255 - (i * 16)), (byte)(i * 8)))];

    private static IndexedImage Sample(int width, int height) =>
        new(width, height, [.. Enumerable.Range(0, width * height).Select(i => (byte)((i / 7) % 16))], Colors);

    [Fact]
    public void Png_round_trips_paletted_images_at_4_and_8_bits()
    {
        foreach (var depth in new[] { 4, 8 })
        {
            var image = Sample(64, 64);
            var back = Assert.IsType<IndexedImage>(Png.Read(Png.Write(image, depth)));
            Assert.Equal(image.Pixels, back.Pixels);
            Assert.Equal(Colors, back.Palette.Take(16));
        }
    }

    [Fact]
    public void Lz77_and_4bpp_tiles_round_trip()
    {
        var image = Sample(64, 128);
        var tiles = Tiles4bpp.FromImage(image);
        Assert.Equal(64 * 128 / 2, tiles.Length);
        var packed = Lz77.Compress(tiles);
        Assert.True(packed.Length < tiles.Length);
        var unpacked = Lz77.Decompress(packed, 0);
        Assert.Equal(tiles, unpacked);
        Assert.Equal(image.Pixels, Tiles4bpp.ToImage(unpacked!, 64, Colors).Pixels);
        var gba = Colors.Select(c => c.ToGba()).ToList();
        Assert.Equal(gba, Bgr555.Read(Bgr555.Write(Colors)));
    }

    [Fact]
    public void Pictures_are_fitted_to_the_sprite_rules()
    {
        var rgba = new RgbaImage(64, 64, Sample(64, 64).ToRgba(transparentZero: false));
        var rules = new SpriteRules(64, 128, 2, 16, true);
        var fitted = SpriteEditing.Prepare(rgba, rules, null, out var problem);
        Assert.Null(problem);
        Assert.Equal(128, fitted!.Height); // one frame repeated
        Assert.True(fitted.Palette.Count <= 16);
        Assert.Null(SpriteEditing.Prepare(new RgbaImage(32, 32, new byte[32 * 32 * 4]), rules, null, out problem));
        Assert.Contains("64×64", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Jasc_palettes_round_trip()
    {
        Assert.Equal(Colors, JascPalette.Read(JascPalette.Write(Colors, "\n")));
    }

    [Fact]
    public async Task Real_emerald_rom_sprites_load_replace_and_save_when_available()
    {
        var real = Environment.GetEnvironmentVariable("POKESTUDIO_EMERALD_ROM");
        if (string.IsNullOrEmpty(real) || !File.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var rom = tmp.Combine("game.gba");
        File.Copy(real, rom);
        var module = new Gen3RomModule();
        Assert.True(module.Capabilities.HasFlag(ModuleCapabilities.Graphics));
        var data = await module.LoadDataAsync(rom, tmp.Combine("data"));
        var species = data.Species.First(s => s.Key != "SPECIES_NONE").Key;

        var rules = module.Rules(data, species, SpriteKind.Front)!;
        var front = module.Load(data, species, SpriteKind.Front)!;
        Assert.Equal((rules.Width, rules.Height), (front.Width, front.Height));
        Assert.Contains(front.Pixels, p => p != 0);
        Assert.Equal(16, module.LoadPalette(data, species, true)!.Count);
        var back = module.Load(data, species, SpriteKind.Back)!;
        Assert.Equal(64, back.Height);

        var mirrored = new byte[front.Pixels.Length];
        for (var y = 0; y < front.Height; y++)
        {
            for (var x = 0; x < front.Width; x++)
            {
                mirrored[(y * front.Width) + x] = front[front.Width - 1 - x, y];
            }
        }

        var newColors = Colors.Select(c => c.ToGba()).ToList();
        SpriteEditing.Replace(data, species, SpriteKind.Front, new IndexedImage(front.Width, front.Height, mirrored, newColors)).Apply();
        SpriteEditing.SetPalette(data, species, true, [.. newColors.AsEnumerable().Reverse()]).Apply();
        await module.SaveDataAsync(data, new ModuleSaveContext(new SafeFileWriter(tmp.Path), tmp.Combine("data"), rom));
        Assert.True(data.Graphics.IsEmpty);

        var reloaded = await module.LoadDataAsync(rom, tmp.Combine("data"));
        var key = reloaded.Species.Single(s => s.Key == species).Key;
        Assert.Equal(mirrored, module.Load(reloaded, key, SpriteKind.Front)!.Pixels);
        Assert.Equal(newColors, module.LoadPalette(reloaded, key, false));
        Assert.Equal(newColors.AsEnumerable().Reverse(), module.LoadPalette(reloaded, key, true));
        Assert.Equal(back.Pixels, module.Load(reloaded, key, SpriteKind.Back)!.Pixels); // untouched
    }

    [Fact]
    public async Task Real_expansion_sprites_load_replace_and_save_when_available()
    {
        var real = Environment.GetEnvironmentVariable("POKESTUDIO_EXPANSION_DIR");
        if (string.IsNullOrEmpty(real) || !Directory.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var root = tmp.Combine("game");
        foreach (var dir in new[] { "include", "src", "graphics/pokemon/bulbasaur", "graphics/pokemon/icon_palettes" })
        {
            ProjectService.CopyDirectory(Path.Combine(real, dir), Path.Combine(root, dir));
        }

        File.Copy(Path.Combine(real, "Makefile"), Path.Combine(root, "Makefile"));
        var module = new ExpansionModule();
        var data = await module.LoadDataAsync(root, tmp.Combine("data"));
        const string Bulbasaur = "SPECIES_BULBASAUR";

        var front = module.Load(data, Bulbasaur, SpriteKind.Front)!;
        Assert.Equal(64, front.Width);
        Assert.Equal(16, module.LoadPalette(data, Bulbasaur, false)!.Count);
        Assert.NotNull(module.Load(data, Bulbasaur, SpriteKind.Back));
        var icon = module.Load(data, Bulbasaur, SpriteKind.Icon)!;
        Assert.Equal(32, icon.Width);
        Assert.False(module.Rules(data, Bulbasaur, SpriteKind.Icon)!.OwnPalette);

        var rules = module.Rules(data, Bulbasaur, SpriteKind.Front)!;
        var picture = new RgbaImage(rules.Width, rules.FrameHeight, Sample(rules.Width, rules.FrameHeight).ToRgba(transparentZero: false));
        var fitted = SpriteEditing.Prepare(picture, rules, null, out var problem)!;
        Assert.Null(problem);
        SpriteEditing.Replace(data, Bulbasaur, SpriteKind.Front, fitted).Apply();

        // in place, the writer may only touch the managed files: the edited sprite files must be among them
        var managed = module.ManagedFiles(root, data);
        Assert.Contains(managed, f => f.EndsWith("bulbasaur/normal.pal", StringComparison.Ordinal) || f.EndsWith("bulbasaur/normal_gba.pal", StringComparison.Ordinal));
        await module.SaveDataAsync(data, new ModuleSaveContext(new SafeFileWriter(root, allowedFiles: managed), tmp.Combine("data"), root));

        var reloaded = await module.LoadDataAsync(root, tmp.Combine("data"));
        Assert.Equal(fitted.Pixels, module.Load(reloaded, Bulbasaur, SpriteKind.Front)!.Pixels);
        Assert.Equal(fitted.Palette.Take(16).Select(c => c.ToGba()), module.LoadPalette(reloaded, Bulbasaur, false)!.Select(c => c.ToGba()).Take(fitted.Palette.Count));
        Assert.Equal(icon.Pixels, module.Load(reloaded, Bulbasaur, SpriteKind.Icon)!.Pixels);
    }
}
