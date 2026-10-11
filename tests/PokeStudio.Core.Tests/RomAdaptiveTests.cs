using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Safety;
using PokeStudio.Core.Validation;
using PokeStudio.Modules.Gen3Rom;

namespace PokeStudio.Core.Tests;

/// <summary>The adaptive boss slot injected into unmodified FireRed / Emerald ROMs (on copies of the user's own ROMs).</summary>
public class RomAdaptiveTests
{
    private static async Task Inject(string? real, string file, int hook, string original)
    {
        if (string.IsNullOrEmpty(real) || !File.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var rom = tmp.Combine(file);
        File.Copy(real, rom);
        var before = await File.ReadAllBytesAsync(rom);
        Assert.Equal(original, Convert.ToHexString(before, hook, 8));
        var module = new Gen3RomModule();
        var data = await module.LoadDataAsync(rom, tmp.Combine("data"));
        Assert.True(data.AdaptiveSlotsAlwaysOn);

        var (change, count) = AdaptiveSlots.SuggestForAll(data, new LabSettings());
        change!.Apply();
        var slots = data.Trainers.Where(t => t.HasAdaptiveSlot).ToDictionary(t => t.Key, t => (t.FlexIndex, Candidates: t.FlexCandidates.Select(c => c.Species).ToList()));
        Assert.Equal(count, slots.Count);
        var ctx = new ModuleSaveContext(new SafeFileWriter(tmp.Path), tmp.Combine("data"), rom);
        await module.SaveDataAsync(data, ctx);

        // the hook: ldr r3, [pc, #0]; bx r3; .word code | 1 — into the ROM's free space
        var saved = await File.ReadAllBytesAsync(rom);
        Assert.Equal("004B1847", Convert.ToHexString(saved, hook, 4));
        var code = (int)(BitConverter.ToUInt32(saved, hook + 4) - 0x08000000);
        Assert.Equal(1, code & 1);
        Assert.True(code > 0x00800000 * 0 + 0x00400000, "code goes to free space past the game's own code");

        // read back on open
        var reloaded = await module.LoadDataAsync(rom, tmp.Combine("data"));
        var back = reloaded.Trainers.Where(t => t.HasAdaptiveSlot).ToDictionary(t => t.Key, t => (t.FlexIndex, Candidates: t.FlexCandidates.Select(c => c.Species).ToList()));
        Assert.Equal(slots.Keys.Order(), back.Keys.Order());
        foreach (var (key, slot) in slots)
        {
            Assert.Equal(slot.FlexIndex, back[key].FlexIndex);
            Assert.Equal(slot.Candidates, back[key].Candidates);
        }

        // saving again reuses the space (what the last save wrote is freed first)
        reloaded.Trainers[reloaded.Trainers.FindIndex(t => t.HasAdaptiveSlot)] = reloaded.Trainers.First(t => t.HasAdaptiveSlot) with { Name = reloaded.Trainers.First(t => t.HasAdaptiveSlot).Name };
        await module.SaveDataAsync(reloaded, ctx);
        var again = await File.ReadAllBytesAsync(rom);
        Assert.Equal(BitConverter.ToUInt32(saved, hook + 4), BitConverter.ToUInt32(again, hook + 4));

        // no slots left: the game's own bytes come back
        var plain = await module.LoadDataAsync(rom, tmp.Combine("data"));
        for (var i = 0; i < plain.Trainers.Count; i++)
        {
            if (plain.Trainers[i].HasAdaptiveSlot)
            {
                AdaptiveSlots.Remove(plain, plain.Trainers[i].Key)!.Apply();
            }
        }

        await module.SaveDataAsync(plain, ctx);
        Assert.Equal(original, Convert.ToHexString(await File.ReadAllBytesAsync(rom), hook, 8));
    }

    [Fact]
    public Task Real_firered_gets_the_adaptive_slot_when_available() =>
        Inject(Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_ROM"), "firered.gba", 0x0000FF90, "01F0A6F934F02EFA");

    [Fact]
    public Task Real_emerald_gets_the_adaptive_slot_when_available() =>
        Inject(Environment.GetEnvironmentVariable("POKESTUDIO_EMERALD_ROM"), "emerald.gba", 0x000369C4, "01F010FE28688021");

    [Fact]
    public async Task Other_roms_keep_the_warning_when_available()
    {
        var real = Environment.GetEnvironmentVariable("POKESTUDIO_RUBY_ROM");
        if (string.IsNullOrEmpty(real) || !File.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var rom = tmp.Combine("ruby.gba");
        File.Copy(real, rom);
        var module = new Gen3RomModule();
        var data = await module.LoadDataAsync(rom, tmp.Combine("data"));
        Assert.False(module.SupportsAdaptiveSlots(data));
        var (change, _) = AdaptiveSlots.SuggestForAll(data, new LabSettings());
        change?.Apply();
        if (data.Trainers.Any(t => t.HasAdaptiveSlot))
        {
            Assert.All(module.Validate(data).Where(i => i.Message.Contains("adaptive boss slot", StringComparison.Ordinal)), i => Assert.Equal(IssueSeverity.Warning, i.Severity));
        }
    }
}
