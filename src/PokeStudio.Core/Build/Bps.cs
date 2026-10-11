namespace PokeStudio.Core.Build;

/// <summary>
/// BPS patches (the format Floating IPS and the online ROM patchers use), written from the format's public description.
/// A patch holds only the changes: anything that also exists in the player's own unmodified game, even if it moved, is
/// copied from their file, so a patch never carries the original game's data.
/// </summary>
public static class Bps
{
    private const int SourceRead = 0, TargetRead = 1, SourceCopy = 2;
    private const int HashBits = 22, Window = 8, MaxTries = 32, MinRead = 4, MinCopy = 8;

    /// <summary>Makes a patch that turns <paramref name="source"/> (the unmodified game) into <paramref name="target"/>.</summary>
    public static byte[] Create(ReadOnlySpan<byte> source, ReadOnlySpan<byte> target)
    {
        var patch = new List<byte>(1 << 16) { (byte)'B', (byte)'P', (byte)'S', (byte)'1' };
        Number(patch, (ulong)source.Length);
        Number(patch, (ulong)target.Length);
        Number(patch, 0); // no metadata

        // where each 8-byte run starts in the source, newest first through `next`
        var head = new int[1 << HashBits];
        Array.Fill(head, -1);
        var next = new int[Math.Max(0, source.Length)];
        for (var p = 0; p + Window <= source.Length; p++)
        {
            var h = Hash(source, p);
            next[p] = head[h];
            head[h] = p;
        }

        long sourceOffset = 0;
        var literalStart = -1;
        void Flush(int upTo, ReadOnlySpan<byte> t)
        {
            if (literalStart < 0)
            {
                return;
            }

            Number(patch, ((ulong)(upTo - literalStart - 1) << 2) | TargetRead);
            for (var k = literalStart; k < upTo; k++)
            {
                patch.Add(t[k]);
            }

            literalStart = -1;
        }

        var i = 0;
        while (i < target.Length)
        {
            var read = i < source.Length ? Match(source, i, target, i) : 0;
            int bestLength = read, bestAt = -1;
            if (read < 64 && i + Window <= target.Length)
            {
                var tries = 0;
                for (var p = head[Hash(target, i)]; p >= 0 && tries < MaxTries; p = next[p], tries++)
                {
                    var length = Match(source, p, target, i);
                    if (length > bestLength)
                    {
                        bestLength = length;
                        bestAt = p;
                    }
                }
            }

            if (bestAt < 0 && read >= MinRead)
            {
                Flush(i, target);
                Number(patch, ((ulong)(read - 1) << 2) | SourceRead);
                i += read;
            }
            else if (bestAt >= 0 && bestLength >= MinCopy)
            {
                Flush(i, target);
                Number(patch, ((ulong)(bestLength - 1) << 2) | SourceCopy);
                var delta = bestAt - sourceOffset;
                Number(patch, ((ulong)Math.Abs(delta) << 1) | (delta < 0 ? 1UL : 0UL));
                sourceOffset = bestAt + bestLength;
                i += bestLength;
            }
            else
            {
                if (literalStart < 0)
                {
                    literalStart = i;
                }

                i++;
            }
        }

        Flush(target.Length, target);
        AddU32(patch, Crc32(source));
        AddU32(patch, Crc32(target));
        AddU32(patch, Crc32(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(patch)));
        return [.. patch];
    }

    /// <summary>Applies a patch; throws if the patch is damaged or made for a different game file.</summary>
    public static byte[] Apply(ReadOnlySpan<byte> source, ReadOnlySpan<byte> patch)
    {
        if (patch.Length < 16 || patch[0] != 'B' || patch[1] != 'P' || patch[2] != 'S' || patch[3] != '1')
        {
            throw new InvalidDataException("This isn't a BPS patch.");
        }

        var end = patch.Length - 12;
        if (Crc32(patch[..^4]) != U32(patch, patch.Length - 4))
        {
            throw new InvalidDataException("The patch file is damaged.");
        }

        if (Crc32(source) != U32(patch, end))
        {
            throw new InvalidDataException("This patch was made for a different game file.");
        }

        var at = 4;
        var sourceSize = (long)ReadNumber(patch, ref at);
        var target = new byte[(int)ReadNumber(patch, ref at)];
        var metadata = (int)ReadNumber(patch, ref at);
        at += metadata;
        if (sourceSize != source.Length)
        {
            throw new InvalidDataException("This patch was made for a different game file.");
        }

        long output = 0, sourceOffset = 0, targetOffset = 0;
        while (at < end)
        {
            var data = ReadNumber(patch, ref at);
            var length = (long)(data >> 2) + 1;
            switch ((int)(data & 3))
            {
                case SourceRead:
                    source.Slice((int)output, (int)length).CopyTo(target.AsSpan((int)output));
                    output += length;
                    break;
                case TargetRead:
                    patch.Slice(at, (int)length).CopyTo(target.AsSpan((int)output));
                    at += (int)length;
                    output += length;
                    break;
                case SourceCopy:
                    sourceOffset += Signed(ReadNumber(patch, ref at));
                    source.Slice((int)sourceOffset, (int)length).CopyTo(target.AsSpan((int)output));
                    sourceOffset += length;
                    output += length;
                    break;
                default: // target copy: byte by byte, the copy may overlap what it writes
                    targetOffset += Signed(ReadNumber(patch, ref at));
                    for (var k = 0; k < length; k++)
                    {
                        target[output++] = target[targetOffset++];
                    }

                    break;
            }
        }

        if (Crc32(target) != U32(patch, end + 4))
        {
            throw new InvalidDataException("The patched game doesn't match what the patch expected.");
        }

        return target;
    }

    private static int Match(ReadOnlySpan<byte> source, int s, ReadOnlySpan<byte> target, int t)
    {
        var length = Math.Min(source.Length - s, target.Length - t);
        return length <= 0 ? 0 : source.Slice(s, length).CommonPrefixLength(target.Slice(t, length));
    }

    private static int Hash(ReadOnlySpan<byte> data, int at) =>
        (int)((BitConverter.ToUInt64(data.Slice(at, Window)) * 0x9E3779B97F4A7C15UL) >> (64 - HashBits));

    private static void Number(List<byte> output, ulong data)
    {
        while (true)
        {
            var x = (byte)(data & 0x7F);
            data >>= 7;
            if (data == 0)
            {
                output.Add((byte)(0x80 | x));
                return;
            }

            output.Add(x);
            data--;
        }
    }

    private static ulong ReadNumber(ReadOnlySpan<byte> patch, ref int at)
    {
        ulong data = 0, shift = 1;
        while (true)
        {
            var x = patch[at++];
            data += (ulong)(x & 0x7F) * shift;
            if ((x & 0x80) != 0)
            {
                return data;
            }

            shift <<= 7;
            data += shift;
        }
    }

    private static long Signed(ulong data) => (data & 1) != 0 ? -(long)(data >> 1) : (long)(data >> 1);

    private static void AddU32(List<byte> output, uint value)
    {
        for (var k = 0; k < 4; k++)
        {
            output.Add((byte)(value >> (8 * k)));
        }
    }

    private static uint U32(ReadOnlySpan<byte> data, int at) => BitConverter.ToUInt32(data.Slice(at, 4));

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (var k = 0; k < 8; k++)
        {
            c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        }

        return c;
    }).ToArray();

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }
}
