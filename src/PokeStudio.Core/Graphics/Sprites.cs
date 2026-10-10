using PokeStudio.Core.Changes;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Graphics;

public enum SpriteKind
{
    Front,
    Back,
    Icon,
}

/// <summary>What a sprite must look like in this game (the editor checks replacements against it).</summary>
public sealed record SpriteRules(int Width, int Height, int Frames, int Colors, bool OwnPalette, string? Note = null)
{
    /// <summary>Height of one frame (animated front sprites stack two frames).</summary>
    public int FrameHeight => Height / Math.Max(1, Frames);
}

/// <summary>Modules that can show and replace Pokémon sprites implement this.</summary>
public interface ISpriteSource
{
    SpriteRules? Rules(GameData data, string species, SpriteKind kind);

    /// <summary>The sprite as it is in the game (ignoring pending edits), or null when there is none.</summary>
    IndexedImage? Load(GameData data, string species, SpriteKind kind);

    /// <summary>The normal or shiny palette as it is in the game.</summary>
    IReadOnlyList<Rgb>? LoadPalette(GameData data, string species, bool shiny);
}

/// <summary>Pending graphics changes, keyed "SPECIES_X|Front" (images) and "SPECIES_X|Normal" / "|Shiny" (palettes); written on save.</summary>
public sealed class GraphicsEdits
{
    public Dictionary<string, IndexedImage> Images { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, IReadOnlyList<Rgb>> Palettes { get; } = new(StringComparer.Ordinal);

    public static string ImageKey(string species, SpriteKind kind) => $"{species}|{kind}";

    public static string PaletteKey(string species, bool shiny) => $"{species}|{(shiny ? "Shiny" : "Normal")}";

    public bool IsEmpty => Images.Count == 0 && Palettes.Count == 0;

    public void Clear()
    {
        Images.Clear();
        Palettes.Clear();
    }
}

/// <summary>Graphics edits as undoable changes, and the current picture (the edit if any, else the game's).</summary>
public static class SpriteEditing
{
    public static IndexedImage? Current(ISpriteSource source, GameData data, string species, SpriteKind kind, bool shiny)
    {
        var image = data.Graphics.Images.TryGetValue(GraphicsEdits.ImageKey(species, kind), out var edited) ? edited : source.Load(data, species, kind);
        if (image is null || kind == SpriteKind.Icon)
        {
            return image;
        }

        return CurrentPalette(source, data, species, shiny) is { } palette ? image.WithPalette(palette) : image;
    }

    public static IReadOnlyList<Rgb>? CurrentPalette(ISpriteSource source, GameData data, string species, bool shiny) =>
        data.Graphics.Palettes.TryGetValue(GraphicsEdits.PaletteKey(species, shiny), out var p) ? p : source.LoadPalette(data, species, shiny);

    /// <summary>
    /// Replaces a sprite. Front/back sprites bring their own colours: the normal palette becomes the new one and the shiny
    /// palette starts as a copy (edit it afterwards). One undo step.
    /// </summary>
    public static IChange Replace(GameData data, string species, SpriteKind kind, IndexedImage image)
    {
        var parts = new List<IChange> { Set(data.Graphics.Images, GraphicsEdits.ImageKey(species, kind), image, $"{kind} sprite") };
        if (kind != SpriteKind.Icon)
        {
            parts.Add(Set(data.Graphics.Palettes, GraphicsEdits.PaletteKey(species, false), image.Palette, "palette"));
            parts.Add(Set(data.Graphics.Palettes, GraphicsEdits.PaletteKey(species, true), image.Palette, "shiny palette"));
        }

        return new CompositeChange("Graphics", $"{species}: new {kind.ToString().ToLowerInvariant()} sprite", parts);
    }

    public static IChange SetPalette(GameData data, string species, bool shiny, IReadOnlyList<Rgb> palette) =>
        Set(data.Graphics.Palettes, GraphicsEdits.PaletteKey(species, shiny), palette, shiny ? $"{species}: shiny colours" : $"{species}: colours");

    private static IChange Set<T>(Dictionary<string, T> map, string key, T value, string description)
        where T : class
    {
        var had = map.TryGetValue(key, out var before);
        return new ValueChange<T?>("Graphics", description, v =>
        {
            if (v is null)
            {
                map.Remove(key);
            }
            else
            {
                map[key] = v;
            }
        }, had ? before : null, value);
    }

    /// <summary>Fits a picture to the rules: checks the size (a single frame is repeated for two-frame sprites) and reduces colours.</summary>
    public static IndexedImage? Prepare(RgbaImage picture, SpriteRules rules, IReadOnlyList<Rgb>? fixedPalette, out string? problem)
    {
        problem = null;
        var frameHeight = rules.FrameHeight;
        if (picture.Width != rules.Width || (picture.Height != rules.Height && picture.Height != frameHeight))
        {
            problem = $"The picture must be {rules.Width}×{frameHeight} pixels{(rules.Frames > 1 ? $" (or {rules.Width}×{rules.Height} with {rules.Frames} frames)" : "")}; this one is {picture.Width}×{picture.Height}.";
            return null;
        }

        if (picture.Height == frameHeight && rules.Frames > 1)
        {
            var stacked = new byte[rules.Width * rules.Height * 4];
            for (var f = 0; f < rules.Frames; f++)
            {
                picture.Rgba.CopyTo(stacked, f * picture.Rgba.Length);
            }

            picture = new RgbaImage(rules.Width, rules.Height, stacked);
        }

        return fixedPalette is not null && !rules.OwnPalette ? Quantizer.ToPalette(picture, fixedPalette) : Quantizer.ToIndexed(picture, rules.Colors);
    }

    /// <summary>Any picture file → full colour (paletted PNGs are expanded).</summary>
    public static RgbaImage ToRgba(object png) => png switch
    {
        RgbaImage rgba => rgba,
        IndexedImage indexed => new RgbaImage(indexed.Width, indexed.Height, indexed.ToRgba(transparentZero: false)),
        _ => throw new InvalidDataException("Unsupported picture."),
    };
}
