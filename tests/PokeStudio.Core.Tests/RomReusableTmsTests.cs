using PokeStudio.Core.Modules;
using PokeStudio.Core.Safety;
using PokeStudio.Modules.Gen3Rom;

namespace PokeStudio.Core.Tests;

/// <summary>Reusable TMs in unmodified FireRed / Emerald ROMs, with the user's own ROMs when their variables are set.</summary>
public class RomReusableTmsTests
{
    private static async Task Switch(string? real, string file, int call, string original)
    {
        if (string.IsNullOrEmpty(real) || !File.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var rom = tmp.Combine(file);
        File.Copy(real, rom);
        var before = await File.ReadAllBytesAsync(rom);
        Assert.Equal(original, Convert.ToHexString(before, call, 4)); // bl RemoveBagItem in Task_LearnedMove
        var module = new Gen3RomModule();
        var data = await module.LoadDataAsync(rom, tmp.Combine("data"));
        Assert.False(data.Rules!.ReusableTms);

        // on: the call becomes two "do nothing" instructions, and nothing else changes
        data.Rules = data.Rules with { ReusableTms = true };
        var ctx = new ModuleSaveContext(new SafeFileWriter(tmp.Path), tmp.Combine("data"), rom);
        await module.SaveDataAsync(data, ctx);
        var saved = await File.ReadAllBytesAsync(rom);
        Assert.Equal("C046C046", Convert.ToHexString(saved, call, 4));
        Assert.Equal(before.Length, saved.Length);
        Assert.Equal(4, Enumerable.Range(0, before.Length).Count(i => before[i] != saved[i]));
        Assert.True((await module.LoadDataAsync(rom, tmp.Combine("data"))).Rules!.ReusableTms);

        // off again: the game's own bytes come back
        data.Rules = data.Rules with { ReusableTms = false };
        await module.SaveDataAsync(data, ctx);
        Assert.Equal(before, await File.ReadAllBytesAsync(rom));
    }

    [Fact]
    public Task Real_firered_tms_can_be_made_reusable_when_available() =>
        Switch(Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_ROM"), "firered.gba", 0x125C80, "74F7AAFA");

    [Fact]
    public Task Real_emerald_tms_can_be_made_reusable_when_available() =>
        Switch(Environment.GetEnvironmentVariable("POKESTUDIO_EMERALD_ROM"), "emerald.gba", 0x1B6EEC, "1FF7DAFD");

    [Fact]
    public async Task Other_roms_have_no_switch_when_available()
    {
        var real = Environment.GetEnvironmentVariable("POKESTUDIO_RUBY_ROM");
        if (string.IsNullOrEmpty(real) || !File.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var rom = tmp.Combine("ruby.gba");
        File.Copy(real, rom);
        var data = await new Gen3RomModule().LoadDataAsync(rom, tmp.Combine("data"));
        Assert.Null(data.Rules?.ReusableTms);
    }
}
