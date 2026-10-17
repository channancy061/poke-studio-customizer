using System.Text;
using System.Text.RegularExpressions;
using PokeStudio.Core.Branding;
using PokeStudio.Core.Graphics;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// The hack's name, logo and intro in a pokeemerald-expansion / pokefirered-expansion project: the Makefile's TITLE and
/// FILE_NAME, EXPANSION_INTRO in include/config/general.h, the credits title (and Emerald's "EMERALD" word), and the
/// title-screen logo pictures. Every line it changes keeps the original in a comment above it, so clearing a setting
/// puts the line back; the original pictures are kept in the project's data folder.
/// </summary>
internal static partial class BrandingWriter
{
    private const string Was = "Poke Studio (was: ";
    public const string Makefile = "Makefile";
    public const string General = "include/config/general.h";
    public const string FireRedCredits = "src/credits.c";
    public const string EmeraldCredits = "src/data/credits.h";
    public const string EmeraldStrings = "src/strings.c";
    public const string FireRedLogo = "graphics/title_screen/firered/game_title_logo";
    public const string EmeraldLogo = "graphics/title_screen/pokemon_logo";
    public const string EmeraldSubtitle = "graphics/title_screen/emerald_version.png";
    public const string FireRedTitleCode = "src/title_screen.c";
    public const string FireRedBoxArtMap = "graphics/title_screen/firered/box_art_mon.bin";
    public const string FireRedBackground = "graphics/title_screen/firered/ps_background";

    // FireRed's back layer (BG3): its own 16 KB of tiles (512) and a 32×20 map, drawn with BG palette 14
    private static readonly ImageSlot FireRedBackgroundSlot = new(240, 160, 15, "240×160 pixels, up to 15 colours, nothing see-through. It sits behind the logo, the box-art Pokémon and the flames.", MaxBlocks: 512, Opaque: true);

    // FireRed: an 8bpp background, 22×12 tiles at the top left of a 32×20 map, BG palettes 0–12 (207 colours + see-through)
    private const int FireRedMapWidth = 32, FireRedMapHeight = 20;
    private static readonly ImageSlot FireRedSlot = new(176, 96, 207, "176×96 pixels, up to 207 colours. See-through pixels (or the top-left pixel's colour) show the title screen behind it.", MaxBlocks: 256);

    // Emerald: a 256×64 8bpp picture laid out tile by tile (palettes 0–13: 223 colours); tile 0 fills the rest of the screen
    private static readonly ImageSlot EmeraldSlot = new(256, 64, 223, "256×64 pixels, up to 223 colours. Keep the top-left 8×8 corner empty. See-through pixels (or the top-left pixel's colour) show the title screen behind it.", EmptyCorner: true);
    private static readonly ImageSlot EmeraldSubtitleSlot = new(128, 32, 15, "The line under the logo (\"Emerald Version\"): 128×32 pixels, up to 15 colours.");

    public static IReadOnlyList<string> Files(ExpansionLayout layout) => layout.IsFireRedBase
        ? [Makefile, General, FireRedCredits, FireRedLogo + ".png", FireRedLogo + ".pal", FireRedLogo + ".bin", FireRedTitleCode,
            FireRedBackground + ".png", FireRedBackground + ".pal", FireRedBackground + ".bin", FireRedBoxArtMap]
        : [Makefile, General, EmeraldCredits, EmeraldStrings, EmeraldLogo + ".png", EmeraldLogo + ".pal", EmeraldSubtitle];

    public static BrandingSupport Describe(ExpansionLayout layout)
    {
        string? Read(string rel) => File.Exists(layout.Full(rel)) ? File.ReadAllText(layout.Full(rel)) : null;
        var make = Read(Makefile) ?? "";
        var title = TitleLine().Match(make) is { Success: true } t ? t.Groups[2].Value.Trim() : "";
        var intro = Read(General) is { } general && IntroLine().Match(general) is { Success: true } i ? i.Groups[2].Value == "TRUE" : (bool?)null;
        var fireRed = layout.IsFireRedBase;
        var version = fireRed
            ? Regex.Match(Read(FireRedCredits) ?? "", @"sString_PokemonFireRed_Staff\[\] = _\(""Pokémon (.*?) Version\\nStaff""\)") is { Success: true } f ? f.Groups[1].Value : "FireRed"
            : Regex.Match(Read(EmeraldStrings) ?? "", @"gText_ExpandedPlaceholder_Emerald\[\] = _\(""(.*?)""\)") is { Success: true } e ? e.Groups[1].Value : "EMERALD";
        return new BrandingSupport(title, layout.FileName, version, intro, fireRed ? FireRedSlot : EmeraldSlot, fireRed ? null : EmeraldSubtitleSlot,
            fireRed && Read(FireRedTitleCode)?.Contains("sBorderBgTiles[]", StringComparison.Ordinal) == true ? FireRedBackgroundSlot : null,
            CanHideLayers: fireRed);
    }

    /// <summary>
    /// The new text files and pictures. <paramref name="original"/> gives a file as the game had it before the app first
    /// changed it (kept in the project's data folder), or null when the app never changed it.
    /// </summary>
    public static (Dictionary<string, string> Text, Dictionary<string, byte[]> Binary, List<string> Problems) Plan(
        ExpansionLayout layout, BrandingSettings b, Func<string, string?> read, Func<string, byte[]?> readBytes, Func<string, byte[]?> original)
    {
        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        var binary = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var problems = new List<string>();
        void Put(string file, string? before, string after)
        {
            if (before is not null && before != after)
            {
                text[file] = after;
            }
        }

        void PutBytes(string file, byte[] after)
        {
            if (readBytes(file) is not { } before || !before.AsSpan().SequenceEqual(after))
            {
                binary[file] = after;
            }
        }

        var fireRed = layout.IsFireRedBase;
        if (read(Makefile) is { } make)
        {
            Put(Makefile, make, MakefileText(make, b.RomTitle, b.FileName));
        }

        if (b.ShowExpansionIntro is { } show && read(General) is { } general)
        {
            Put(General, general, IntroLine().Replace(general, m => m.Groups[1].Value + (show ? "TRUE" : "FALSE") + m.Groups[3].Value, 1));
        }

        // the game's name in text
        var name = b.VersionName;
        if (fireRed)
        {
            if (read(FireRedCredits) is { } credits)
            {
                Put(FireRedCredits, credits, SetLine(credits, "sString_PokemonFireRed_Staff[]", name is null ? null : $"static const u8 sString_PokemonFireRed_Staff[] = _(\"Pokémon {name} Version\\nStaff\");"));
            }
        }
        else
        {
            if (read(EmeraldCredits) is { } credits)
            {
                Put(EmeraldCredits, credits, SetLine(credits, "sCreditsText_PkmnEmeraldVersion[]", name is null ? null : $"static const u8 sCreditsText_PkmnEmeraldVersion[]             = _(\"POKéMON {Upper(name)} VERSION\");"));
            }

            if (read(EmeraldStrings) is { } strings)
            {
                Put(EmeraldStrings, strings, SetLine(strings, "gText_ExpandedPlaceholder_Emerald[]", name is null ? null : $"const u8 gText_ExpandedPlaceholder_Emerald[] = _(\"{Upper(name)}\");"));
            }
        }

        // the logo: from the player's picture, or back to the original
        var logoBase = fireRed ? FireRedLogo : EmeraldLogo;
        var logoFiles = fireRed ? new[] { logoBase + ".png", logoBase + ".pal", logoBase + ".bin" } : [logoBase + ".png", logoBase + ".pal"];
        // FireRed: a hidden logo or box-art Pokémon is a map of the blank first tile (the originals come back when shown)
        if (fireRed)
        {
            if (b.HideBoxArt == true)
            {
                PutBytes(FireRedBoxArtMap, new byte[FireRedMapWidth * FireRedMapHeight * 2]);
            }
            else if (original(FireRedBoxArtMap) is { } boxArt)
            {
                PutBytes(FireRedBoxArtMap, boxArt);
            }
        }

        if (fireRed && b.HideLogo == true)
        {
            PutBytes(FireRedLogo + ".bin", new byte[FireRedMapWidth * FireRedMapHeight * 2]);
        }
        else if (b.LogoPng is { } logo)
        {
            var (image, problem) = Brand.Convert(Convert.FromBase64String(logo), fireRed ? FireRedSlot : EmeraldSlot, "logo");
            var pal = (original(logoBase + ".pal") ?? readBytes(logoBase + ".pal")) is { } palBytes ? JascPalette.Read(Encoding.ASCII.GetString(palBytes)) : null;
            if (image is null || pal is null)
            {
                problems.Add(problem ?? "The game's logo palette wasn't found.");
            }
            else
            {
                var palette = MergePalette(pal, image.Palette);
                PutBytes(logoBase + ".pal", Encoding.ASCII.GetBytes(JascPalette.Write(palette)));
                if (fireRed)
                {
                    var (tiles, map) = FireRedTiles(image);
                    PutBytes(logoBase + ".png", Png.Write(tiles with { Palette = palette }, 8));
                    PutBytes(logoBase + ".bin", map);
                }
                else
                {
                    PutBytes(logoBase + ".png", Png.Write(image with { Palette = palette }, 8));
                }
            }
        }
        else
        {
            foreach (var f in logoFiles)
            {
                if (original(f) is { } back)
                {
                    PutBytes(f, back);
                }
            }
        }

        // FireRed's background: new tiles, map and palette for the back layer, and the title screen pointed at them
        if (fireRed && read(FireRedTitleCode) is { } titleCode)
        {
            var code = Restore(titleCode, "//");
            if (b.BackgroundPng is { } background)
            {
                var (image, problem) = Brand.Convert(Convert.FromBase64String(background), FireRedBackgroundSlot, "background");
                if (image is null)
                {
                    problems.Add(problem!);
                }
                else
                {
                    var (tiles, map) = BackgroundTiles(image, 14);
                    PutBytes(FireRedBackground + ".png", Png.Write(tiles, 4));
                    PutBytes(FireRedBackground + ".pal", Encoding.ASCII.GetBytes(JascPalette.Write(tiles.Palette)));
                    PutBytes(FireRedBackground + ".bin", map);
                    code = SetLine(code, "sBorderBgTiles[]",
                        $"static const u8 sBorderBgTiles[] = INCBIN_U8(\"{FireRedBackground}.4bpp.smol\"); static const u16 sPokeStudioBackgroundPal[] = INCBIN_U16(\"{FireRedBackground}.gbapal\");", restore: false);
                    code = SetLine(code, "sBorderBgMap[]", $"static const u8 sBorderBgMap[] = INCBIN_U8(\"{FireRedBackground}.bin.smolTM\");", restore: false);
                    code = Regex.Replace(code, @"^([ \t]*)LoadPalette\(gGraphics_TitleScreen_BackgroundPals, BG_PLTT_ID\(14\)[^\n]*$",
                        m => $"{m.Groups[1].Value}// {Was}{m.Value.Trim()})\n{m.Groups[1].Value}LoadPalette(sPokeStudioBackgroundPal, BG_PLTT_ID(14), PLTT_SIZE_4BPP);", RegexOptions.Multiline);
                }
            }

            Put(FireRedTitleCode, titleCode, code);
        }

        if (!fireRed)
        {
            if (b.SubtitlePng is { } subtitle)
            {
                var (image, problem) = Brand.Convert(Convert.FromBase64String(subtitle), EmeraldSubtitleSlot, "version line");
                if (image is null)
                {
                    problems.Add(problem!);
                }
                else
                {
                    var palette = image.Palette.Concat(Enumerable.Repeat(new Rgb(0, 0, 0), 16)).Take(16).ToList();
                    PutBytes(EmeraldSubtitle, Png.Write(image with { Palette = palette }, 4));
                }
            }
            else if (original(EmeraldSubtitle) is { } back)
            {
                PutBytes(EmeraldSubtitle, back);
            }
        }

        return (text, binary, problems);
    }

    private static string Upper(string name) => name.ToUpperInvariant().Replace("É", "é", StringComparison.Ordinal);

    /// <summary>The game's 256-colour palette with the picture's colours in 1…n (index 0 and the rest stay as they were).</summary>
    internal static List<Rgb> MergePalette(IReadOnlyList<Rgb> original, IReadOnlyList<Rgb> colours)
    {
        var palette = original.Concat(Enumerable.Repeat(new Rgb(0, 0, 0), Math.Max(0, 256 - original.Count))).Take(256).ToList();
        for (var i = 1; i < colours.Count && i < palette.Count; i++)
        {
            palette[i] = colours[i];
        }

        return palette;
    }

    /// <summary>A full-screen background: each different 8×8 block once (4bpp), and a 32×20 map using one palette.</summary>
    internal static (IndexedImage Tiles, byte[] Map) BackgroundTiles(IndexedImage image, int palette)
    {
        var blocks = new List<byte[]>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var map = new byte[32 * 20 * 2];
        var columns = image.Width / 8;
        var i = 0;
        foreach (var block in Brand.Blocks(image))
        {
            var key = Convert.ToBase64String(block);
            if (!index.TryGetValue(key, out var tile))
            {
                index[key] = tile = blocks.Count;
                blocks.Add(block);
            }

            var cell = ((i / columns) * 32) + (i % columns);
            var entry = tile | (palette << 12);
            map[cell * 2] = (byte)(entry & 0xFF);
            map[(cell * 2) + 1] = (byte)(entry >> 8);
            i++;
        }

        var rows = (blocks.Count + 31) / 32;
        var pixels = new byte[256 * rows * 8];
        for (var t = 0; t < blocks.Count; t++)
        {
            for (var y = 0; y < 8; y++)
            {
                Array.Copy(blocks[t], y * 8, pixels, ((((t / 32) * 8) + y) * 256) + ((t % 32) * 8), 8);
            }
        }

        return (new IndexedImage(256, rows * 8, pixels, image.Palette.Concat(Enumerable.Repeat(new Rgb(0, 0, 0), 16)).Take(16).ToList()), map);
    }

    /// <summary>FireRed's logo: each different 8×8 block once (the empty block first), and a 32×20 map placing them.</summary>
    internal static (IndexedImage Tiles, byte[] Map) FireRedTiles(IndexedImage image)
    {
        var blocks = new List<byte[]> { new byte[64] };
        var index = new Dictionary<string, int>(StringComparer.Ordinal) { [Convert.ToBase64String(new byte[64])] = 0 };
        var map = new byte[FireRedMapWidth * FireRedMapHeight * 2];
        var columns = image.Width / 8;
        var i = 0;
        foreach (var block in Brand.Blocks(image))
        {
            var key = Convert.ToBase64String(block);
            if (!index.TryGetValue(key, out var tile))
            {
                index[key] = tile = blocks.Count;
                blocks.Add(block);
            }

            var cell = ((i / columns) * FireRedMapWidth) + (i % columns);
            map[cell * 2] = (byte)(tile & 0xFF);
            map[(cell * 2) + 1] = (byte)((tile >> 8) & 0x03);
            i++;
        }

        // the tiles as a 256-pixel-wide picture, 32 per row
        var rows = (blocks.Count + 31) / 32;
        var pixels = new byte[256 * rows * 8];
        for (var t = 0; t < blocks.Count; t++)
        {
            var (bx, by) = (t % 32, t / 32);
            for (var y = 0; y < 8; y++)
            {
                Array.Copy(blocks[t], y * 8, pixels, (((by * 8) + y) * 256) + (bx * 8), 8);
            }
        }

        return (new IndexedImage(256, rows * 8, pixels, image.Palette), map);
    }

    /// <summary>The Makefile with the base game's TITLE and FILE_NAME set (or put back when null).</summary>
    internal static string MakefileText(string make, string? title, string? fileName)
    {
        var text = Restore(make, "#");
        if (title is not null && TitleLine().Match(text) is { Success: true } t)
        {
            text = text.Remove(t.Index, t.Length).Insert(t.Index, $"# {Was}{t.Value.TrimEnd('\r')})\n{t.Groups[1].Value}{title}");
        }

        if (fileName is not null && FileNameLine().Match(text) is { Success: true } f)
        {
            text = text.Remove(f.Index, f.Length).Insert(f.Index, $"# {Was}{f.Value.TrimEnd('\r')})\n{f.Groups[1].Value}{fileName}");
        }

        return text;
    }

    /// <summary>A C file with the line that defines <paramref name="name"/> replaced (the original kept above it), or put back.</summary>
    internal static string SetLine(string c, string name, string? line, bool restore = true)
    {
        var text = restore ? Restore(c, "//") : c;
        if (line is null)
        {
            return text;
        }

        var at = Regex.Match(text, $@"^[^\n]*{Regex.Escape(name)}\s*= [^\n]*", RegexOptions.Multiline);
        return at.Success ? text.Remove(at.Index, at.Length).Insert(at.Index, $"// {Was}{at.Value.TrimEnd('\r')})\n{line}") : text;
    }

    /// <summary>Puts back every line the app changed: "&lt;comment&gt; Poke Studio (was: original)" and the line after it.</summary>
    private static string Restore(string text, string comment) =>
        Regex.Replace(text, $@"^{Regex.Escape(comment)} {Regex.Escape(Was)}(.*)\)\r?\n[^\n]*", m => m.Groups[1].Value, RegexOptions.Multiline);

    [GeneratedRegex(@"^(TITLE\s*\??:?=[ \t]*)([^\n#]*)", RegexOptions.Multiline)]
    private static partial Regex TitleLine();

    [GeneratedRegex(@"^(FILE_NAME\s*:=[ \t]*)([^\n#]*)", RegexOptions.Multiline)]
    private static partial Regex FileNameLine();

    [GeneratedRegex(@"^(#define EXPANSION_INTRO\s+)(TRUE|FALSE)(.*)$", RegexOptions.Multiline)]
    private static partial Regex IntroLine();
}
