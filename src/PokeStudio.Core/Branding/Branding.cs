using PokeStudio.Core.Graphics;

namespace PokeStudio.Core.Branding;

/// <summary>
/// The hack's own name and look: the ROM title emulators show, the built file's name, the name the game uses for
/// itself in text (the credits), the title-screen logo, and whether the expansion's "Powered by" screen plays.
/// Every value is optional: null leaves the game as it is (or puts back what it was).
/// </summary>
public sealed record BrandingSettings
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The 12-character title in the ROM header (A–Z, 0–9 and spaces), e.g. "CROSSOVER RD".</summary>
    public string? RomTitle { get; init; }

    /// <summary>The built game's file name without ".gba", e.g. "crossover-red".</summary>
    public string? FileName { get; init; }

    /// <summary>The name the game uses for itself in text, e.g. "Crossover Red" (the credits title).</summary>
    public string? VersionName { get; init; }

    /// <summary>Whether the expansion's "Powered by" intro plays before the title screen (null: as it is).</summary>
    public bool? ShowExpansionIntro { get; init; }

    /// <summary>The title-screen logo, a PNG (base64) at the size the game needs.</summary>
    public string? LogoPng { get; init; }

    /// <summary>Emerald's "Version" line under the logo, a PNG (base64), or null to keep it.</summary>
    public string? SubtitlePng { get; init; }

    public bool IsEmpty => RomTitle is null && FileName is null && VersionName is null && ShowExpansionIntro is null && LogoPng is null && SubtitlePng is null;
}

/// <summary>What a game can change and how big its pictures have to be.</summary>
/// <param name="CurrentTitle">The title the ROM has now.</param>
/// <param name="CurrentFileName">The built file's name now (without ".gba").</param>
/// <param name="CurrentVersionName">The name in the credits now.</param>
/// <param name="IntroShown">Whether the "Powered by" intro plays now (null: the game has none).</param>
/// <param name="Logo">The logo's size and colours.</param>
/// <param name="Subtitle">The size and colours of a separate "Version" line (null: the game has none).</param>
public sealed record BrandingSupport(
    string CurrentTitle,
    string CurrentFileName,
    string CurrentVersionName,
    bool? IntroShown,
    ImageSlot Logo,
    ImageSlot? Subtitle);

/// <summary>A picture the game needs at an exact size with at most <paramref name="Colors"/> colours (plus see-through).</summary>
/// <param name="EmptyCorner">The top-left 8×8 block has to stay see-through (the game fills the rest of the screen with it).</param>
/// <param name="MaxBlocks">At most this many different 8×8 blocks (the rest of the video memory belongs to other pictures).</param>
public sealed record ImageSlot(int Width, int Height, int Colors, string Hint, bool EmptyCorner = false, int MaxBlocks = int.MaxValue);

public static class Brand
{
    public const int TitleLength = 12;
    public const int VersionNameLength = 16;

    /// <summary>A ROM title the header can hold: capitals, digits and spaces, at most 12.</summary>
    public static string? CheckTitle(string title) =>
        title.Length is 0 or > TitleLength ? $"The ROM title needs 1 to {TitleLength} characters."
        : title.Any(c => c is not (>= 'A' and <= 'Z' or >= '0' and <= '9' or ' ')) ? "The ROM title can use capital letters, digits and spaces only."
        : null;

    /// <summary>A file name that works everywhere: letters, digits, '-' and '_'.</summary>
    public static string? CheckFileName(string name) =>
        name.Length is 0 or > 40 ? "The file name needs 1 to 40 characters."
        : name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) ? "The file name can use letters, digits, '-' and '_' only."
        : null;

    public static string? CheckVersionName(string name) =>
        name.Length is 0 or > VersionNameLength ? $"The game's name needs 1 to {VersionNameLength} characters."
        : name.Any(c => c is '"' or '\\' or '{' or '}' or '$') ? "The game's name can't use \" \\ { } or $."
        : null;

    /// <summary>
    /// A picture for a slot: the exact size, see-through pixels (or the top-left colour) as index 0, and at most
    /// <see cref="ImageSlot.Colors"/> GBA colours. Returns the problem instead when it doesn't fit.
    /// </summary>
    public static (IndexedImage? Image, string? Problem) Convert(byte[] png, ImageSlot slot, string what)
    {
        RgbaImage rgba;
        try
        {
            rgba = Png.Read(png) switch
            {
                RgbaImage r => r,
                IndexedImage i => ToRgba(i),
                _ => throw new InvalidDataException(),
            };
        }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            return (null, $"The {what} isn't a PNG the app can read.");
        }

        if (rgba.Width != slot.Width || rgba.Height != slot.Height)
        {
            return (null, $"The {what} has to be exactly {slot.Width}×{slot.Height} pixels (this one is {rgba.Width}×{rgba.Height}).");
        }

        var image = Quantizer.ToIndexed(rgba, slot.Colors + 1);
        if (slot.EmptyCorner && Enumerable.Range(0, 8).Any(y => Enumerable.Range(0, 8).Any(x => image.Pixels[(y * image.Width) + x] != 0)))
        {
            return (null, $"Keep the top-left 8×8 corner of the {what} empty (see-through or background colour): the game fills the rest of the screen with it.");
        }

        var blocks = DistinctBlocks(image);
        if (blocks > slot.MaxBlocks)
        {
            return (null, $"The {what} has {blocks} different 8×8 blocks; the game has room for {slot.MaxBlocks}. Leave more of it empty or plain.");
        }

        return (image, null);
    }

    /// <summary>How many different 8×8 blocks a picture has (the empty one included).</summary>
    public static int DistinctBlocks(IndexedImage image) => Blocks(image).Select(b => System.Convert.ToBase64String(b)).Distinct().Count();

    /// <summary>The picture's 8×8 blocks, left to right, top to bottom.</summary>
    public static IEnumerable<byte[]> Blocks(IndexedImage image)
    {
        for (var by = 0; by < image.Height / 8; by++)
        {
            for (var bx = 0; bx < image.Width / 8; bx++)
            {
                var block = new byte[64];
                for (var y = 0; y < 8; y++)
                {
                    Array.Copy(image.Pixels, ((by * 8) + y) * image.Width + (bx * 8), block, y * 8, 8);
                }

                yield return block;
            }
        }
    }

    private static RgbaImage ToRgba(IndexedImage i)
    {
        var rgba = new byte[i.Width * i.Height * 4];
        for (var p = 0; p < i.Pixels.Length; p++)
        {
            var c = i.Palette[Math.Min(i.Pixels[p], i.Palette.Count - 1)];
            (rgba[p * 4], rgba[(p * 4) + 1], rgba[(p * 4) + 2], rgba[(p * 4) + 3]) = (c.R, c.G, c.B, 255);
        }

        return new RgbaImage(i.Width, i.Height, rgba);
    }
}
