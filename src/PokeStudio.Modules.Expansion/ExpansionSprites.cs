using System.Text.RegularExpressions;
using PokeStudio.Core.Graphics;
using PokeStudio.Core.Modules;
using PokeStudio.Core.SourceCode;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// Pokémon sprites in a pokeemerald-expansion project: the PNG/PAL files each species' frontPic, backPic, palette,
/// shinyPalette and iconSprite point to (through src/data/graphics/pokemon.h, honouring the GBA-style settings).
/// </summary>
internal static partial class ExpansionSprites
{
    public const string GraphicsHeader = "src/data/graphics/pokemon.h";

    [GeneratedRegex(@"\b(gMon\w+)\[\]\s*=\s*INCGFX_U\d+\(""([^""]+)""")]
    private static partial Regex Incgfx();

    /// <summary>Symbol → file, for the definitions the project's settings actually use.</summary>
    public static Dictionary<string, string> Paths(ExpansionLayout layout)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(layout.Full(GraphicsHeader)))
        {
            return result;
        }

        var file = SpeciesInfo.LoadFile(layout, GraphicsHeader);
        foreach (Match m in Incgfx().Matches(file.Text))
        {
            if (file.Map.IsActive(m.Index))
            {
                result[m.Groups[1].Value] = m.Groups[2].Value;
            }
        }

        return result;
    }

    /// <summary>The project-relative files of one species: front, back, icon, normal palette, shiny palette, icon palette.</summary>
    public static (string? Front, string? Back, string? Icon, string? Normal, string? Shiny, string? IconPalette) Files(ExpansionModule.LoadedState state, string species)
    {
        if (!state.Blocks.TryGetValue(species, out var block) || !state.Files.TryGetValue(block.File, out var file))
        {
            return default;
        }

        state.GraphicsPaths ??= Paths(state.Layout);
        string? PathOf(string field) => block.Get(field) is { } f && state.GraphicsPaths.TryGetValue(SpeciesInfo.Raw(file, f).Trim(), out var p) ? p : null;
        string? iconPalette = null;
        if (block.Get("iconPalIndex") is { } ip && file.Macros.TryEvaluate(SpeciesInfo.Resolve(SpeciesInfo.Raw(file, ip), file.Macros, out _)) is { } index)
        {
            iconPalette = $"graphics/pokemon/icon_palettes/pal{index}.pal";
        }

        return (PathOf("frontPic"), PathOf("backPic"), PathOf("iconSprite"), PathOf("palette"), PathOf("shinyPalette"), iconPalette);
    }

    private static string? FileFor(ExpansionModule.LoadedState state, string species, SpriteKind kind)
    {
        var f = Files(state, species);
        return kind switch { SpriteKind.Front => f.Front, SpriteKind.Back => f.Back, _ => f.Icon };
    }

    public static SpriteRules? Rules(ExpansionModule.LoadedState state, string species, SpriteKind kind)
    {
        if (FileFor(state, species, kind) is not { } rel || !File.Exists(state.Layout.Full(rel)))
        {
            return null;
        }

        var (w, h) = PngSize(File.ReadAllBytes(state.Layout.Full(rel)));
        return kind switch
        {
            SpriteKind.Icon => new SpriteRules(w, h, Math.Max(1, h / w), 16, false, "Icons share one of six palettes; your colours are matched to it."),
            SpriteKind.Front => new SpriteRules(w, h, Math.Max(1, h / w), 16, true, h > w ? "Two frames stacked: the second is the animation frame." : null),
            _ => new SpriteRules(w, h, 1, 16, true),
        };
    }

    private static (int Width, int Height) PngSize(byte[] png) =>
        png.Length > 24 ? ((png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19], (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]) : (0, 0);

    public static IndexedImage? Load(ExpansionModule.LoadedState state, string species, SpriteKind kind)
    {
        if (FileFor(state, species, kind) is not { } rel || !File.Exists(state.Layout.Full(rel)))
        {
            return null;
        }

        if (Png.Read(File.ReadAllBytes(state.Layout.Full(rel))) is not IndexedImage image)
        {
            return null;
        }

        var palette = kind == SpriteKind.Icon
            ? Files(state, species).IconPalette is { } ip && File.Exists(state.Layout.Full(ip)) ? JascPalette.Read(File.ReadAllText(state.Layout.Full(ip))) : null
            : Palette(state, species, false);
        return palette is null ? image : image.WithPalette(palette);
    }

    public static IReadOnlyList<Rgb>? Palette(ExpansionModule.LoadedState state, string species, bool shiny)
    {
        var f = Files(state, species);
        var rel = shiny ? f.Shiny : f.Normal;
        return rel is not null && File.Exists(state.Layout.Full(rel)) ? JascPalette.Read(File.ReadAllText(state.Layout.Full(rel))) : null;
    }

    /// <summary>Files the pending graphics edits will write.</summary>
    public static IEnumerable<string> PendingFiles(ExpansionModule.LoadedState state, GraphicsEdits edits)
    {
        foreach (var key in edits.Images.Keys.Concat(edits.Palettes.Keys))
        {
            var f = Files(state, key.Split('|')[0]);
            foreach (var p in new[] { f.Front, f.Back, f.Icon, f.Normal, f.Shiny })
            {
                if (p is not null)
                {
                    yield return p;
                }
            }
        }
    }

    /// <summary>Writes the pending sprites (paletted PNGs) and palettes (JASC files), then clears them.</summary>
    public static async Task WriteAsync(ExpansionModule.LoadedState state, GameData data, ModuleSaveContext context, CancellationToken cancellationToken)
    {
        foreach (var (key, image) in data.Graphics.Images)
        {
            var parts = key.Split('|');
            var kind = Enum.Parse<SpriteKind>(parts[1]);
            if (FileFor(state, parts[0], kind) is not { } rel)
            {
                continue;
            }

            var depth = File.Exists(state.Layout.Full(rel)) && File.ReadAllBytes(state.Layout.Full(rel)) is { Length: > 24 } old ? old[24] : 4;
            var palette = kind == SpriteKind.Icon ? image.Palette : data.Graphics.Palettes.GetValueOrDefault(GraphicsEdits.PaletteKey(parts[0], false)) ?? image.Palette;
            await context.Writer.WriteAllBytesAsync(state.Layout.Full(rel), Png.Write(image.WithPalette(palette), depth == 8 ? 8 : 4), cancellationToken);
        }

        foreach (var (key, palette) in data.Graphics.Palettes)
        {
            var parts = key.Split('|');
            var f = Files(state, parts[0]);
            var rel = parts[1] == "Shiny" ? f.Shiny : f.Normal;
            if (rel is null)
            {
                continue;
            }

            var newline = File.Exists(state.Layout.Full(rel)) ? CScanner.LineEnding(File.ReadAllText(state.Layout.Full(rel))) : "\r\n";
            var colors = palette.Concat(Enumerable.Repeat(default(Rgb), Math.Max(0, 16 - palette.Count))).Take(16).ToList();
            await context.Writer.WriteAllTextAsync(state.Layout.Full(rel), JascPalette.Write(colors, newline), cancellationToken);
        }

        data.Graphics.Clear();
    }
}
