using PokeStudio.Core.Branding;
using PokeStudio.Core.Graphics;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

/// <summary>The hack's name, logo and intro: Makefile and C lines that can be put back, and logos on the user's real projects.</summary>
public class BrandingTests
{
    private const string Make = "GAME_VERSION ?= FIRERED\nifeq ($(GAME_VERSION), FIRERED)\nTITLE       := POKEMON FIRE\nGAME_CODE   := BPRE\nBUILD_NAME  := firered\nendif\nFILE_NAME := poke$(BUILD_NAME)\nROM_NAME := $(FILE_NAME).gba\n";

    [Fact]
    public void Makefile_title_and_file_name_are_set_without_trailing_spaces_and_put_back()
    {
        var set = BrandingWriter.MakefileText(Make, "CROSSOVER RD", "crossover-red");
        Assert.Contains("# Poke Studio (was: TITLE       := POKEMON FIRE)\nTITLE       := CROSSOVER RD\n", set, StringComparison.Ordinal);
        Assert.Contains("# Poke Studio (was: FILE_NAME := poke$(BUILD_NAME))\nFILE_NAME := crossover-red\nROM_NAME", set, StringComparison.Ordinal);
        Assert.Equal(set, BrandingWriter.MakefileText(set, "CROSSOVER RD", "crossover-red"));
        Assert.Equal(Make, BrandingWriter.MakefileText(set, null, null));
        Assert.Contains("TITLE       := NEW NAME\n", BrandingWriter.MakefileText(set, "NEW NAME", null), StringComparison.Ordinal);
    }

    [Fact]
    public void C_lines_are_replaced_with_the_original_kept_above_and_put_back()
    {
        const string c = "#include \"global.h\"\nconst u8 gText_ExpandedPlaceholder_Emerald[] = _(\"EMERALD\");\nconst u8 gText_Other[] = _(\"X\");\n";
        var set = BrandingWriter.SetLine(c, "gText_ExpandedPlaceholder_Emerald[]", "const u8 gText_ExpandedPlaceholder_Emerald[] = _(\"CROSSOVER\");");
        Assert.Contains("// Poke Studio (was: const u8 gText_ExpandedPlaceholder_Emerald[] = _(\"EMERALD\");)\nconst u8 gText_ExpandedPlaceholder_Emerald[] = _(\"CROSSOVER\");\nconst u8 gText_Other", set, StringComparison.Ordinal);
        Assert.Equal(set, BrandingWriter.SetLine(set, "gText_ExpandedPlaceholder_Emerald[]", "const u8 gText_ExpandedPlaceholder_Emerald[] = _(\"CROSSOVER\");"));
        Assert.Equal(c, BrandingWriter.SetLine(set, "gText_ExpandedPlaceholder_Emerald[]", null));
    }

    [Fact]
    public void Names_and_pictures_are_checked()
    {
        Assert.Null(Brand.CheckTitle("CROSSOVER RD"));
        Assert.NotNull(Brand.CheckTitle("CROSSOVER RED")); // 13
        Assert.NotNull(Brand.CheckTitle("crossover"));
        Assert.Null(Brand.CheckFileName("crossover-red"));
        Assert.NotNull(Brand.CheckFileName("crossover red"));
        Assert.NotNull(Brand.CheckVersionName("Bad \"name\""));

        var slot = new ImageSlot(16, 16, 15, "", EmptyCorner: true, MaxBlocks: 3);
        Assert.Contains("exactly 16×16", Brand.Convert(Logo(8, 8, 2), slot, "logo").Problem, StringComparison.Ordinal);
        Assert.Contains("top-left", Brand.Convert(Logo(16, 16, 4, cornerEmpty: false), slot, "logo").Problem, StringComparison.Ordinal);
        Assert.Contains("8×8 blocks", Brand.Convert(Logo(16, 16, 4), slot with { MaxBlocks = 2 }, "logo").Problem, StringComparison.Ordinal);
        Assert.Null(Brand.Convert(Logo(16, 16, 4), slot, "logo").Problem);
    }

    [Fact]
    public void A_detailed_background_is_made_to_fit_the_block_limit_with_few_pixels_changed()
    {
        // 240×160 of noise-like detail: 600 different blocks for 512 places
        var rnd = new Random(3);
        var palette = Enumerable.Range(0, 15).Select(i => new Rgb((byte)(i * 17), (byte)(255 - (i * 13)), (byte)(i * 7))).ToList();
        var pixels = new byte[240 * 160];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)((i / 240 / 8 % 3 == 0) ? rnd.Next(15) : (i % 240 / 8 % 15));
        }

        var png = Png.Write(new IndexedImage(240, 160, pixels, palette), 8);
        var slot = new ImageSlot(240, 160, 15, "", MaxBlocks: 512, Opaque: true);
        var (image, problem) = Brand.Convert(png, slot, "background");
        Assert.Null(problem);
        Assert.True(Brand.DistinctBlocks(image!) <= 512);
        Assert.DoesNotContain((byte)0, image!.Pixels); // nothing see-through in a background
    }

    /// <summary>A test logo: stripes of <paramref name="colors"/> colours, each 8×8 block different, the corner empty.</summary>
    private static byte[] Logo(int width, int height, int colors, bool cornerEmpty = true)
    {
        var palette = new List<Rgb> { new(255, 0, 255) };
        palette.AddRange(Enumerable.Range(0, colors).Select(i => new Rgb((byte)(40 + (i * 13 % 200)), (byte)(i * 37 % 256), (byte)(255 - (i * 7 % 200)))));
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var block = ((y / 8) * (width / 8)) + (x / 8);
                pixels[(y * width) + x] = cornerEmpty && x < 8 && y < 8 ? (byte)0 : (byte)(1 + ((block + (x % 8 == y % 8 ? 1 : 0) + (y * 3 / 8) + x) % colors));
            }
        }

        return Png.Write(new IndexedImage(width, height, pixels, palette), 8);
    }

    private static string? Dir(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static void RealProject(string? root, string logoFile, int width, int height)
    {
        if (root is null)
        {
            return;
        }

        var layout = new ExpansionLayout(root);
        var now = BrandingWriter.Describe(layout);
        Assert.Equal((width, height), (now.Logo.Width, now.Logo.Height));
        Assert.Equal($"poke{layout.BuildName}", now.CurrentFileName);
        Assert.StartsWith("POKEMON ", now.CurrentTitle, StringComparison.Ordinal);
        Assert.NotNull(now.IntroShown);

        string? Read(string rel) => File.Exists(layout.Full(rel)) ? File.ReadAllText(layout.Full(rel)) : null;
        byte[]? ReadBytes(string rel) => File.Exists(layout.Full(rel)) ? File.ReadAllBytes(layout.Full(rel)) : null;
        var settings = new BrandingSettings
        {
            RomTitle = "CROSSOVER RD",
            FileName = "crossover",
            VersionName = "Crossover",
            ShowExpansionIntro = false,
            LogoPng = Convert.ToBase64String(Logo(width, height, 60)),
            SubtitlePng = now.Subtitle is { } sub ? Convert.ToBase64String(Logo(sub.Width, sub.Height, 6, cornerEmpty: false)) : null,
        };
        var (text, binary, problems) = BrandingWriter.Plan(layout, settings, Read, ReadBytes, _ => null);
        Assert.Empty(problems);
        Assert.Contains("TITLE", text[BrandingWriter.Makefile], StringComparison.Ordinal);
        Assert.Contains("FILE_NAME := crossover\n", text[BrandingWriter.Makefile], StringComparison.Ordinal);
        Assert.Matches(@"#define EXPANSION_INTRO\s+FALSE", text[BrandingWriter.General]);
        var named = text.Where(t => t.Key.StartsWith("src/", StringComparison.Ordinal)).ToList();
        Assert.Equal(layout.IsFireRedBase ? 1 : 2, named.Count); // FireRed: the credits; Emerald: the credits and the version word
        Assert.All(named, t => Assert.Contains("Poke Studio (was: ", t.Value, StringComparison.Ordinal));
        Assert.All(named, t => Assert.Contains("CROSSOVER", t.Value.ToUpperInvariant(), StringComparison.Ordinal));

        // the logo: an 8bpp picture with the game's 256-colour palette (FireRed: also a 32×20 map)
        var png = Assert.IsType<IndexedImage>(Png.Read(binary[logoFile + ".png"]));
        Assert.Equal(256, png.Width);
        Assert.Equal(256, JascPalette.Read(System.Text.Encoding.ASCII.GetString(binary[logoFile + ".pal"])).Count);
        if (binary.TryGetValue(logoFile + ".bin", out var map))
        {
            Assert.Equal(32 * 20 * 2, map.Length);
            Assert.True(png.Height / 8 * 32 <= 256, "at most 256 tiles");
        }
        else
        {
            Assert.Equal(64, png.Height);
        }

        // cleared settings put every line back; pictures come back from the kept originals
        var applied = new Dictionary<string, string>(text);
        var (back, _, _) = BrandingWriter.Plan(layout, new BrandingSettings(), f => applied.TryGetValue(f, out var t) ? t : Read(f), ReadBytes, _ => null);
        foreach (var (file, original) in back)
        {
            Assert.Equal(Read(file), original);
        }

        Assert.Equal(applied.Keys.Where(k => k != BrandingWriter.General).OrderBy(k => k), back.Keys.OrderBy(k => k));
    }

    [Fact]
    public void Real_firered_gets_its_name_logo_and_intro_when_available() =>
        RealProject(Dir("POKESTUDIO_FIRERED_DIR"), BrandingWriter.FireRedLogo, 176, 96);

    [Fact]
    public void Real_emerald_gets_its_name_logo_and_intro_when_available() =>
        RealProject(Dir("POKESTUDIO_EXPANSION_DIR"), BrandingWriter.EmeraldLogo, 256, 64);

    [Fact]
    public void Real_emerald_background_takes_rayquazas_place_and_is_put_back_when_available()
    {
        if (Dir("POKESTUDIO_EXPANSION_DIR") is not { } root)
        {
            return;
        }

        var layout = new ExpansionLayout(root);
        var slot = Assert.IsType<ImageSlot>(BrandingWriter.Describe(layout).Background);
        Assert.Equal((240, 160), (slot.Width, slot.Height));
        string? Read(string rel) => File.Exists(layout.Full(rel)) ? File.ReadAllText(layout.Full(rel)) : null;
        byte[]? ReadBytes(string rel) => File.Exists(layout.Full(rel)) ? File.ReadAllBytes(layout.Full(rel)) : null;
        var settings = new BrandingSettings { BackgroundPng = Convert.ToBase64String(Logo(240, 160, 15, cornerEmpty: false)) };
        var (text, binary, problems) = BrandingWriter.Plan(layout, settings, Read, ReadBytes, _ => null);
        Assert.Empty(problems);
        var code = text[BrandingWriter.EmeraldTitleCode];
        Assert.Contains($"INCGFX_U32(\"{BrandingWriter.EmeraldBackground}.png\", \".4bpp.lz\")", code, StringComparison.Ordinal);
        Assert.Contains($"INCGFX_U32(\"{BrandingWriter.EmeraldBackground}.bin\", \".lz\")", code, StringComparison.Ordinal);
        Assert.Contains("LoadPalette(sPokeStudioBackgroundPal, BG_PLTT_ID(14), PLTT_SIZE_4BPP);", code, StringComparison.Ordinal);
        Assert.Matches(@"(?m)^// Poke Studio \(was: \s*DecompressDataWithHeaderVram\(sTitleScreenCloudsGfx", code);
        Assert.Matches(@"(?m)^// Poke Studio \(was: \s*UpdateLegendaryMarkingColor", code);
        var tiles = Assert.IsType<IndexedImage>(Png.Read(binary[BrandingWriter.EmeraldBackground + ".png"]));
        Assert.True(tiles.Height / 8 * 32 <= 512, "at most 512 tiles (the clouds' tiles start after them)");
        Assert.Equal(32 * 20 * 2, binary[BrandingWriter.EmeraldBackground + ".bin"].Length);

        // without a background the title screen code is as it was
        var (back, _, _) = BrandingWriter.Plan(layout, new BrandingSettings(), f => f == BrandingWriter.EmeraldTitleCode ? code : Read(f), ReadBytes, _ => null);
        Assert.Equal(Read(BrandingWriter.EmeraldTitleCode), back[BrandingWriter.EmeraldTitleCode]);
    }
}
