using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace PokeStudio.Core.Graphics;

public readonly record struct Rgb(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>The nearest colour the GBA/DS can show (5 bits per channel).</summary>
    public Rgb ToGba() => new((byte)((R >> 3) << 3), (byte)((G >> 3) << 3), (byte)((B >> 3) << 3));

    public static Rgb? Parse(string text)
    {
        var t = text.Trim().TrimStart('#');
        return t.Length == 6 && int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)
            ? new Rgb((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : null;
    }

    public int DistanceTo(Rgb o) => ((R - o.R) * (R - o.R) * 3) + ((G - o.G) * (G - o.G) * 4) + ((B - o.B) * (B - o.B) * 2);
}

/// <summary>A paletted image (each pixel is an index into <see cref="Palette"/>); index 0 is see-through in game sprites.</summary>
public sealed record IndexedImage(int Width, int Height, byte[] Pixels, IReadOnlyList<Rgb> Palette)
{
    public byte this[int x, int y] => Pixels[(y * Width) + x];

    public IndexedImage WithPalette(IReadOnlyList<Rgb> palette) => this with { Palette = palette };

    /// <summary>RGBA bytes for display (index 0 transparent when <paramref name="transparentZero"/>).</summary>
    public byte[] ToRgba(bool transparentZero = true)
    {
        var rgba = new byte[Width * Height * 4];
        for (var i = 0; i < Pixels.Length; i++)
        {
            var c = Pixels[i] < Palette.Count ? Palette[Pixels[i]] : default;
            rgba[i * 4] = c.R;
            rgba[(i * 4) + 1] = c.G;
            rgba[(i * 4) + 2] = c.B;
            rgba[(i * 4) + 3] = (byte)(transparentZero && Pixels[i] == 0 ? 0 : 255);
        }

        return rgba;
    }
}

/// <summary>A full-colour image with alpha (what users usually draw sprites in).</summary>
public sealed record RgbaImage(int Width, int Height, byte[] Rgba);

/// <summary>PNG reading (8-bit RGB/RGBA/grey, 1/2/4/8-bit paletted) and paletted PNG writing; built on .NET's zlib.</summary>
public static class Png
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>Reads a PNG; paletted files come back as <see cref="IndexedImage"/>, others as <see cref="RgbaImage"/>.</summary>
    public static object Read(byte[] data)
    {
        if (data.Length < 33 || !data.AsSpan(0, 8).SequenceEqual(Signature))
        {
            throw new InvalidDataException("This isn't a PNG image.");
        }

        int width = 0, height = 0, depth = 0, colorType = 0;
        var palette = new List<Rgb>();
        using var idat = new MemoryStream();
        for (var p = 8; p + 8 <= data.Length;)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p));
            var type = Encoding.ASCII.GetString(data, p + 4, 4);
            var body = data.AsSpan(p + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = (int)BinaryPrimitives.ReadUInt32BigEndian(body);
                    height = (int)BinaryPrimitives.ReadUInt32BigEndian(body[4..]);
                    depth = body[8];
                    colorType = body[9];
                    if (body[12] != 0)
                    {
                        throw new InvalidDataException("Interlaced PNGs aren't supported; save it without interlacing.");
                    }

                    break;
                case "PLTE":
                    for (var i = 0; i + 2 < length; i += 3)
                    {
                        palette.Add(new Rgb(body[i], body[i + 1], body[i + 2]));
                    }

                    break;
                case "IDAT":
                    idat.Write(body);
                    break;
            }

            p += 12 + length;
        }

        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new InvalidDataException("Unsupported PNG colour type.") };
        if (colorType != 3 && depth != 8)
        {
            throw new InvalidDataException("Only 8-bit colour PNGs (or paletted PNGs) are supported.");
        }

        var bitsPerPixel = channels * depth;
        var stride = ((width * bitsPerPixel) + 7) / 8;
        var raw = Inflate(idat.ToArray());
        var pixels = Unfilter(raw, height, stride, Math.Max(1, bitsPerPixel / 8));
        if (colorType == 3)
        {
            var indices = new byte[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var bit = x * depth;
                    var b = pixels[(y * stride) + (bit / 8)];
                    indices[(y * width) + x] = depth == 8 ? b : (byte)((b >> (8 - depth - (bit % 8))) & ((1 << depth) - 1));
                }
            }

            return new IndexedImage(width, height, indices, palette);
        }

        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var s = i * channels;
            var (r, g, b, a) = colorType switch
            {
                0 => (pixels[s], pixels[s], pixels[s], (byte)255),
                4 => (pixels[s], pixels[s], pixels[s], pixels[s + 1]),
                2 => (pixels[s], pixels[s + 1], pixels[s + 2], (byte)255),
                _ => (pixels[s], pixels[s + 1], pixels[s + 2], pixels[s + 3]),
            };
            rgba[i * 4] = r;
            rgba[(i * 4) + 1] = g;
            rgba[(i * 4) + 2] = b;
            rgba[(i * 4) + 3] = a;
        }

        return new RgbaImage(width, height, rgba);
    }

    /// <summary>Writes a paletted PNG (4-bit when there are at most 16 colours, else 8-bit).</summary>
    public static byte[] Write(IndexedImage image, int? bitDepth = null)
    {
        var depth = bitDepth ?? (image.Palette.Count <= 16 ? 4 : 8);
        var stride = ((image.Width * depth) + 7) / 8;
        var raw = new byte[(stride + 1) * image.Height];
        for (var y = 0; y < image.Height; y++)
        {
            var row = y * (stride + 1);
            for (var x = 0; x < image.Width; x++)
            {
                var v = image[x, y];
                if (depth == 8)
                {
                    raw[row + 1 + x] = v;
                }
                else
                {
                    var bit = x * 4;
                    raw[row + 1 + (bit / 8)] |= (byte)((v & 0xF) << (4 - (bit % 8)));
                }
            }
        }

        using var ms = new MemoryStream();
        ms.Write(Signature);
        void Chunk(string type, ReadOnlySpan<byte> body)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)body.Length);
            ms.Write(len);
            var typeBytes = Encoding.ASCII.GetBytes(type);
            ms.Write(typeBytes);
            ms.Write(body);
            var crc = Crc32(typeBytes, body);
            BinaryPrimitives.WriteUInt32BigEndian(len, crc);
            ms.Write(len);
        }

        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)image.Width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)image.Height);
        ihdr[8] = (byte)depth;
        ihdr[9] = 3;
        Chunk("IHDR", ihdr);
        var count = depth == 4 ? 16 : Math.Max(image.Palette.Count, 1);
        var plte = new byte[count * 3];
        for (var i = 0; i < count; i++)
        {
            var c = i < image.Palette.Count ? image.Palette[i] : default;
            plte[i * 3] = c.R;
            plte[(i * 3) + 1] = c.G;
            plte[(i * 3) + 2] = c.B;
        }

        Chunk("PLTE", plte);
        using (var z = new MemoryStream())
        {
            using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
            {
                zs.Write(raw);
            }

            Chunk("IDAT", z.ToArray());
        }

        Chunk("IEND", []);
        return ms.ToArray();
    }

    private static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] Unfilter(byte[] raw, int height, int stride, int bpp)
    {
        var output = new byte[height * stride];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            for (var x = 0; x < stride; x++)
            {
                var v = raw[(y * (stride + 1)) + 1 + x];
                var a = x >= bpp ? output[(y * stride) + x - bpp] : 0;
                var b = y > 0 ? output[((y - 1) * stride) + x] : 0;
                var c = x >= bpp && y > 0 ? output[((y - 1) * stride) + x - bpp] : 0;
                output[(y * stride) + x] = filter switch
                {
                    1 => (byte)(v + a),
                    2 => (byte)(v + b),
                    3 => (byte)(v + ((a + b) / 2)),
                    4 => (byte)(v + Paeth(a, b, c)),
                    _ => v,
                };
            }
        }

        return output;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> body)
    {
        var crc = 0xFFFFFFFFu;
        void Add(ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }
        }

        Add(type);
        Add(body);
        return ~crc;
    }
}

/// <summary>Turns a full-colour picture into a game sprite: up to 15 colours plus a see-through colour at index 0.</summary>
public static class Quantizer
{
    /// <summary>
    /// The background (transparent pixels, or the top-left pixel's colour) becomes index 0; the rest is reduced to
    /// <paramref name="colors"/> − 1 colours by median cut, snapped to what the GBA/DS can show.
    /// </summary>
    public static IndexedImage ToIndexed(RgbaImage image, int colors = 16, bool gbaColors = true)
    {
        var count = image.Width * image.Height;
        var px = new Rgb[count];
        var transparent = new bool[count];
        var corner = new Rgb(image.Rgba[0], image.Rgba[1], image.Rgba[2]);
        for (var i = 0; i < count; i++)
        {
            // sprites are drawn on a plain background: that colour (or see-through pixels) becomes index 0
            px[i] = new Rgb(image.Rgba[i * 4], image.Rgba[(i * 4) + 1], image.Rgba[(i * 4) + 2]);
            transparent[i] = image.Rgba[(i * 4) + 3] < 128 || px[i] == corner;
        }

        var opaque = px.Where((_, i) => !transparent[i]).Select(c => gbaColors ? c.ToGba() : c).ToList();
        var palette = new List<Rgb> { gbaColors ? corner.ToGba() : corner };
        palette.AddRange(MedianCut(opaque, colors - 1));
        var indices = new byte[count];
        for (var i = 0; i < count; i++)
        {
            indices[i] = transparent[i] ? (byte)0 : Nearest(palette, gbaColors ? px[i].ToGba() : px[i], skipZero: true);
        }

        return new IndexedImage(image.Width, image.Height, indices, palette);
    }

    /// <summary>Maps an image onto a fixed palette (e.g. a shared icon palette); index 0 stays the background.</summary>
    public static IndexedImage ToPalette(RgbaImage image, IReadOnlyList<Rgb> palette)
    {
        var corner = new Rgb(image.Rgba[0], image.Rgba[1], image.Rgba[2]);
        var indices = new byte[image.Width * image.Height];
        for (var i = 0; i < indices.Length; i++)
        {
            var c = new Rgb(image.Rgba[i * 4], image.Rgba[(i * 4) + 1], image.Rgba[(i * 4) + 2]);
            indices[i] = image.Rgba[(i * 4) + 3] < 128 || c == corner ? (byte)0 : Nearest(palette, c, skipZero: true);
        }

        return new IndexedImage(image.Width, image.Height, indices, palette);
    }

    public static byte Nearest(IReadOnlyList<Rgb> palette, Rgb c, bool skipZero)
    {
        var best = skipZero && palette.Count > 1 ? 1 : 0;
        for (var i = best; i < palette.Count; i++)
        {
            if (palette[i].DistanceTo(c) < palette[best].DistanceTo(c))
            {
                best = i;
            }
        }

        return (byte)best;
    }

    private static List<Rgb> MedianCut(List<Rgb> colors, int target)
    {
        var distinct = colors.Distinct().ToList();
        if (distinct.Count <= target)
        {
            return distinct;
        }

        var boxes = new List<List<Rgb>> { colors };
        while (boxes.Count < target)
        {
            var box = boxes.Where(b => b.Distinct().Count() > 1).OrderByDescending(Range).FirstOrDefault();
            if (box is null)
            {
                break;
            }

            boxes.Remove(box);
            var (r, g, b) = (box.Max(c => c.R) - box.Min(c => c.R), box.Max(c => c.G) - box.Min(c => c.G), box.Max(c => c.B) - box.Min(c => c.B));
            var sorted = r >= g && r >= b ? box.OrderBy(c => c.R).ToList() : g >= b ? box.OrderBy(c => c.G).ToList() : box.OrderBy(c => c.B).ToList();
            boxes.Add(sorted[..(sorted.Count / 2)]);
            boxes.Add(sorted[(sorted.Count / 2)..]);
        }

        return boxes.Select(b => new Rgb((byte)b.Average(c => c.R), (byte)b.Average(c => c.G), (byte)b.Average(c => c.B)).ToGba()).Distinct().ToList();
    }

    private static int Range(List<Rgb> box) =>
        Math.Max(box.Max(c => c.R) - box.Min(c => c.R), Math.Max(box.Max(c => c.G) - box.Min(c => c.G), box.Max(c => c.B) - box.Min(c => c.B))) * box.Count;
}

/// <summary>JASC-PAL palette files (used by pokeemerald-expansion's graphics).</summary>
public static class JascPalette
{
    public static List<Rgb> Read(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 3 || lines[0] != "JASC-PAL")
        {
            throw new InvalidDataException("Not a JASC palette.");
        }

        return lines.Skip(3).Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 3)
            .Select(p => new Rgb(byte.Parse(p[0], CultureInfo.InvariantCulture), byte.Parse(p[1], CultureInfo.InvariantCulture), byte.Parse(p[2], CultureInfo.InvariantCulture)))
            .ToList();
    }

    public static string Write(IReadOnlyList<Rgb> palette, string newline = "\r\n")
    {
        var sb = new StringBuilder();
        sb.Append("JASC-PAL").Append(newline).Append("0100").Append(newline).Append(palette.Count.ToString(CultureInfo.InvariantCulture)).Append(newline);
        foreach (var c in palette)
        {
            sb.Append(CultureInfo.InvariantCulture, $"{c.R} {c.G} {c.B}").Append(newline);
        }

        return sb.ToString();
    }
}
