namespace PokeStudio.Core.Graphics;

/// <summary>The GBA BIOS "LZ77" compression (type 0x10) used for sprites and palettes.</summary>
public static class Lz77
{
    /// <summary>Decompressed data at <paramref name="offset"/>, or null when there's no LZ77 data there.</summary>
    public static byte[]? Decompress(byte[] data, int offset)
    {
        if (offset < 0 || offset + 4 > data.Length || data[offset] != 0x10)
        {
            return null;
        }

        var size = data[offset + 1] | (data[offset + 2] << 8) | (data[offset + 3] << 16);
        if (size is 0 or > 0x40000)
        {
            return null;
        }

        var output = new byte[size];
        var o = 0;
        var p = offset + 4;
        while (o < size)
        {
            if (p >= data.Length)
            {
                return null;
            }

            var flags = data[p++];
            for (var bit = 0; bit < 8 && o < size; bit++, flags <<= 1)
            {
                if ((flags & 0x80) == 0)
                {
                    output[o++] = data[p++];
                    continue;
                }

                var length = (data[p] >> 4) + 3;
                var distance = (((data[p] & 0xF) << 8) | data[p + 1]) + 1;
                p += 2;
                if (distance > o)
                {
                    return null;
                }

                for (var k = 0; k < length && o < size; k++, o++)
                {
                    output[o] = output[o - distance];
                }
            }
        }

        return output;
    }

    /// <summary>Compresses (greedy matches up to 18 bytes, distance ≥ 2 so VRAM-safe), padded to 4 bytes.</summary>
    public static byte[] Compress(byte[] input)
    {
        var output = new List<byte> { 0x10, (byte)input.Length, (byte)(input.Length >> 8), (byte)(input.Length >> 16) };
        var p = 0;
        while (p < input.Length)
        {
            var flagAt = output.Count;
            output.Add(0);
            byte flags = 0;
            for (var bit = 0; bit < 8 && p < input.Length; bit++)
            {
                int bestLength = 0, bestDistance = 0;
                for (var distance = 2; distance <= Math.Min(4096, p); distance++)
                {
                    var length = 0;
                    while (length < 18 && p + length < input.Length && input[p + length] == input[p + length - distance])
                    {
                        length++;
                    }

                    if (length > bestLength)
                    {
                        (bestLength, bestDistance) = (length, distance);
                        if (length == 18)
                        {
                            break;
                        }
                    }
                }

                if (bestLength >= 3)
                {
                    flags |= (byte)(0x80 >> bit);
                    output.Add((byte)(((bestLength - 3) << 4) | ((bestDistance - 1) >> 8)));
                    output.Add((byte)(bestDistance - 1));
                    p += bestLength;
                }
                else
                {
                    output.Add(input[p++]);
                }
            }

            output[flagAt] = flags;
        }

        while (output.Count % 4 != 0)
        {
            output.Add(0);
        }

        return [.. output];
    }
}

/// <summary>4 bits per pixel, 8×8 tiles laid out left to right, top to bottom — how GBA sprites are stored.</summary>
public static class Tiles4bpp
{
    public static IndexedImage ToImage(byte[] tiles, int width, IReadOnlyList<Rgb> palette)
    {
        var tilesWide = width / 8;
        var tileCount = tiles.Length / 32;
        var height = (tileCount + tilesWide - 1) / tilesWide * 8;
        var pixels = new byte[width * height];
        for (var t = 0; t < tileCount; t++)
        {
            var tx = t % tilesWide * 8;
            var ty = t / tilesWide * 8;
            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 8; x++)
                {
                    var b = tiles[(t * 32) + (y * 4) + (x / 2)];
                    pixels[((ty + y) * width) + tx + x] = (byte)((x & 1) == 0 ? b & 0xF : b >> 4);
                }
            }
        }

        return new IndexedImage(width, height, pixels, palette);
    }

    public static byte[] FromImage(IndexedImage image)
    {
        var tilesWide = image.Width / 8;
        var tilesHigh = image.Height / 8;
        var tiles = new byte[tilesWide * tilesHigh * 32];
        for (var t = 0; t < tilesWide * tilesHigh; t++)
        {
            var tx = t % tilesWide * 8;
            var ty = t / tilesWide * 8;
            for (var y = 0; y < 8; y++)
            {
                for (var x = 0; x < 8; x += 2)
                {
                    tiles[(t * 32) + (y * 4) + (x / 2)] = (byte)((image[tx + x, ty + y] & 0xF) | ((image[tx + x + 1, ty + y] & 0xF) << 4));
                }
            }
        }

        return tiles;
    }
}

/// <summary>GBA/DS colours: 15-bit BGR (5 bits each).</summary>
public static class Bgr555
{
    public static List<Rgb> Read(byte[] data, int count = 16) =>
        Enumerable.Range(0, Math.Min(count, data.Length / 2)).Select(i =>
        {
            var v = data[2 * i] | (data[(2 * i) + 1] << 8);
            return new Rgb((byte)((v & 31) << 3), (byte)(((v >> 5) & 31) << 3), (byte)(((v >> 10) & 31) << 3));
        }).ToList();

    public static byte[] Write(IReadOnlyList<Rgb> palette, int count = 16)
    {
        var data = new byte[count * 2];
        for (var i = 0; i < Math.Min(count, palette.Count); i++)
        {
            var c = palette[i];
            var v = (c.R >> 3) | ((c.G >> 3) << 5) | ((c.B >> 3) << 10);
            data[2 * i] = (byte)v;
            data[(2 * i) + 1] = (byte)(v >> 8);
        }

        return data;
    }
}
