using PokeStudio.Core.Modules;
using PokeStudio.Core.Safety;
using PokeStudio.Modules.Gen3Rom;

namespace PokeStudio.Core.Tests;

/// <summary>Shop editing in ROM mode, on copies of the user's own ROMs when their environment variables are set.</summary>
public class RomShopTests
{
    internal static async Task EditShops(IGameModule module, string? real, string file, int atLeast, string mustSell)
    {
        if (string.IsNullOrEmpty(real) || !File.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var rom = tmp.Combine(file);
        File.Copy(real, rom);
        var data = await module.LoadDataAsync(rom, tmp.Combine("data"));
        Assert.True(data.Marts.Count >= atLeast, $"only {data.Marts.Count} shops");
        Assert.Contains(data.Marts, m => m.Items.Contains(mustSell));
        Assert.All(data.Marts, m => Assert.All(m.Items, i => Assert.Contains(data.Items, it => it.Key == i)));
        var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "shops");
        Directory.CreateDirectory(outDir);
        await File.WriteAllLinesAsync(Path.Combine(outDir, Path.ChangeExtension(file, ".txt")),
            data.Marts.Select(m => $"[{m.Place}] {string.Join(", ", m.Items.Select(i => data.Items.First(it => it.Key == i).Name))}"));

        // one shop gets five more items (moves to free space), another loses its last item (stays in place)
        var extra = data.Items.Where(i => i.Price > 0).Select(i => i.Key).Except(data.Marts[0].Items).Take(5).ToList();
        var bigKey = data.Marts[0].Key;
        var big = data.Marts[0] with { Items = [.. data.Marts[0].Items, .. extra] };
        data.Marts[0] = big;
        var small = data.Marts.Skip(1).First(m => m.Items.Count > 2);
        var smallIndex = data.Marts.IndexOf(small);
        data.Marts[smallIndex] = small with { Items = small.Items.Take(small.Items.Count - 1).ToList() };
        Assert.DoesNotContain(module.Validate(data), v => v.Severity == Validation.IssueSeverity.Error);
        await module.SaveDataAsync(data, new ModuleSaveContext(new SafeFileWriter(tmp.Path), tmp.Combine("data"), rom));

        var reloaded = await module.LoadDataAsync(rom, tmp.Combine("data"));
        Assert.Equal(big.Items, reloaded.Marts.Single(m => m.Key == bigKey).Items);
        Assert.Equal(small.Items.Take(small.Items.Count - 1), reloaded.Marts.Single(m => m.Key == small.Key).Items);
        Assert.Equal(data.Marts.Count, reloaded.Marts.Count);
    }

    [Fact]
    public Task Real_emerald_shops_edit_when_available() =>
        EditShops(new Gen3RomModule(), Environment.GetEnvironmentVariable("POKESTUDIO_EMERALD_ROM"), "emerald.gba", 20, "ITEM_POKE_BALL");

    [Fact]
    public Task Real_firered_shops_edit_when_available() =>
        EditShops(new Gen3RomModule(), Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_ROM"), "firered.gba", 15, "ITEM_POKE_BALL");
}
