using PokeStudio.Core.Build;

namespace PokeStudio.Core.Tests;

/// <summary>BPS patches: they round-trip, stay small and don't carry the original game's data.</summary>
public class BpsTests
{
    private static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void Patch_round_trips_edits_in_place_and_a_longer_game()
    {
        var source = Random(1 << 20, 1);
        var target = new byte[source.Length + 5000];
        source.CopyTo(target, 0);
        target[100] ^= 0xFF;
        Random(300, 2).CopyTo(target, 50_000);
        Random(5000, 3).CopyTo(target, source.Length);

        var patch = Bps.Create(source, target);
        Assert.Equal(target, Bps.Apply(source, patch));
        Assert.True(patch.Length < 7000, $"patch is {patch.Length} bytes");
    }

    [Fact]
    public void Moved_data_is_copied_from_the_players_game_not_stored_in_the_patch()
    {
        // like a decomp build: new data pushes the original game's data further along
        var source = Random(1 << 20, 4);
        var inserted = Random(1000, 5);
        var target = inserted.Concat(source).ToArray();

        var patch = Bps.Create(source, target);
        Assert.Equal(target, Bps.Apply(source, patch));
        Assert.True(patch.Length < 1200, $"patch is {patch.Length} bytes");
        Assert.True(patch.AsSpan().IndexOf(source.AsSpan(500_000, 32)) < 0, "the patch holds a piece of the original game");
    }

    [Fact]
    public void Patch_round_trips_a_shorter_game()
    {
        var source = Random(100_000, 6);
        var target = source[20_000..90_000];
        Assert.Equal(target, Bps.Apply(source, Bps.Create(source, target)));
    }

    [Fact]
    public void Patch_refuses_a_different_game_file()
    {
        var source = Random(10_000, 7);
        var target = (byte[])source.Clone();
        target[5] ^= 1;
        var patch = Bps.Create(source, target);
        var other = (byte[])source.Clone();
        other[9] ^= 1;
        Assert.Throws<InvalidDataException>(() => Bps.Apply(other, patch));
    }

    [Fact]
    public void Smallest_patch_matches_the_format()
    {
        // "abcdef" to "abcdef": header, sizes 6 / 6 / no metadata, one source read of 6, then three CRC32s
        var patch = Bps.Create("abcdef"u8, "abcdef"u8);
        Assert.Equal("BPS1"u8.ToArray().Concat(new byte[] { 0x86, 0x86, 0x80, 0x94 }), patch[..8]);
        Assert.Equal(20, patch.Length);
        Assert.Equal(0x352441C2u, Bps.Crc32("abc"u8));
    }

    [Fact]
    public async Task Real_firered_patch_is_small_when_available()
    {
        var real = Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_ROM");
        if (string.IsNullOrEmpty(real) || !File.Exists(real))
        {
            return;
        }

        var source = await File.ReadAllBytesAsync(real);
        var target = (byte[])source.Clone();
        Random(2000, 8).CopyTo(target, 0x00800000 - 4000); // new data in free space
        target[0x1000] ^= 0x55;
        var patch = Bps.Create(source, target);
        Assert.Equal(target, Bps.Apply(source, patch));
        Assert.True(patch.Length < 4000, $"patch is {patch.Length} bytes");
    }
}
