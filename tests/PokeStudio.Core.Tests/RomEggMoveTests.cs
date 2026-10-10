using PokeStudio.Core.Modules;
using PokeStudio.Core.Safety;
using PokeStudio.Modules.Gen3Rom;
using PokeStudio.Modules.N3ds;
using PokeStudio.Modules.Nds;

namespace PokeStudio.Core.Tests;

/// <summary>Egg move editing in ROM mode, on copies of the user's own ROMs when their environment variables are set.</summary>
public class RomEggMoveTests
{
    private static async Task EditEggMoves(IGameModule module, string? real, string file, bool fixedRoom = false)
    {
        if (string.IsNullOrEmpty(real) || !File.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var rom = tmp.Combine(file);
        File.Copy(real, rom);
        var data = await module.LoadDataAsync(rom, tmp.Combine("data"));
        var bulba = data.Learnsets.Single(l => l.Key == "SPECIES_BULBASAUR");
        Assert.Contains("MOVE_SKULL_BASH", bulba.EggMoves);
        Assert.False(bulba.LockedParts.ContainsKey("egg"));
        Assert.True(data.Learnsets.Count(l => l.EggMoves.Count > 0) > 100);

        // Bulbasaur grows to 10 (the table no longer fits and moves), Charmander loses all of its egg moves
        // (a game with fixed room gets two more moves where Charmander loses all of its own)
        var charmanderCount = data.Learnsets.Single(l => l.Key == "SPECIES_CHARMANDER").EggMoves.Count;
        var grow = fixedRoom ? Math.Min(2, charmanderCount) : 10 - bulba.EggMoves.Count;
        var extra = data.Moves.Select(m => m.Key).Where(k => k != "MOVE_NONE").Except(bulba.EggMoves).Take(grow).ToList();
        var bi = data.Learnsets.IndexOf(bulba);
        data.Learnsets[bi] = bulba with { EggMoves = [.. bulba.EggMoves, .. extra] };
        var ci = data.Learnsets.FindIndex(l => l.Key == "SPECIES_CHARMANDER");
        data.Learnsets[ci] = data.Learnsets[ci] with { EggMoves = [] };
        Assert.DoesNotContain(module.Validate(data), v => v.Severity == Validation.IssueSeverity.Error);

        var tooMany = data.Learnsets[bi] with { EggMoves = [.. data.Moves.Select(m => m.Key).Where(k => k != "MOVE_NONE").Take(20)] };
        data.Learnsets[bi] = tooMany;
        Assert.Contains(module.Validate(data), v => v.Area == "Learnsets" && v.Message.Contains("egg moves", StringComparison.Ordinal));
        data.Learnsets[bi] = bulba with { EggMoves = [.. bulba.EggMoves, .. extra] };

        await module.SaveDataAsync(data, new ModuleSaveContext(new SafeFileWriter(tmp.Path), tmp.Combine("data"), rom));
        var reloaded = await module.LoadDataAsync(rom, tmp.Combine("data"));
        Assert.Equal([.. bulba.EggMoves, .. extra], reloaded.Learnsets.Single(l => l.Key == "SPECIES_BULBASAUR").EggMoves);
        Assert.Empty(reloaded.Learnsets.Single(l => l.Key == "SPECIES_CHARMANDER").EggMoves);
        Assert.Equal(data.Learnsets.Single(l => l.Key == "SPECIES_SQUIRTLE").EggMoves, reloaded.Learnsets.Single(l => l.Key == "SPECIES_SQUIRTLE").EggMoves);
    }

    [Fact]
    public Task Real_emerald_egg_moves_edit_when_available() =>
        EditEggMoves(new Gen3RomModule(), Environment.GetEnvironmentVariable("POKESTUDIO_EMERALD_ROM"), "emerald.gba");

    [Fact]
    public Task Real_firered_egg_moves_edit_when_available() =>
        EditEggMoves(new Gen3RomModule(), Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_ROM"), "firered.gba");

    [Fact]
    public Task Real_platinum_egg_moves_edit_when_available() =>
        EditEggMoves(new NdsModule(), Environment.GetEnvironmentVariable("POKESTUDIO_PLATINUM_ROM"), "platinum.nds", fixedRoom: true);

    [Fact]
    public Task Real_diamond_egg_moves_edit_when_available() =>
        EditEggMoves(new NdsModule(), Environment.GetEnvironmentVariable("POKESTUDIO_DIAMOND_ROM"), "diamond.nds", fixedRoom: true);

    [Fact]
    public Task Real_soulsilver_egg_moves_edit_when_available() =>
        EditEggMoves(new NdsModule(), Environment.GetEnvironmentVariable("POKESTUDIO_SOULSILVER_ROM"), "soulsilver.nds");

    [Fact]
    public Task Real_black_egg_moves_edit_when_available() =>
        EditEggMoves(new NdsModule(), Environment.GetEnvironmentVariable("POKESTUDIO_BLACK_ROM"), "black.nds");

    [Fact]
    public async Task Real_omega_ruby_egg_moves_edit_when_available()
    {
        var real = Environment.GetEnvironmentVariable("POKESTUDIO_OMEGARUBY_ROM");
        if (string.IsNullOrEmpty(real) || !File.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var module = new N3dsModule { CitraModsFolder = tmp.Combine("citra") };
        var data = await module.LoadDataAsync(real, tmp.Combine("data"));
        var bulba = data.Learnsets.Single(l => l.Key == "SPECIES_BULBASAUR");
        Assert.Contains("MOVE_SKULL_BASH", bulba.EggMoves);
        var bi = data.Learnsets.IndexOf(bulba);
        var edited = bulba.EggMoves.Take(3).Append("MOVE_POUND").ToList();
        data.Learnsets[bi] = bulba with { EggMoves = edited };
        Assert.Empty(module.Validate(data));
        await module.SaveDataAsync(data, new ModuleSaveContext(new SafeFileWriter(tmp.Path), tmp.Combine("data"), real));
        var reloaded = await module.LoadDataAsync(real, tmp.Combine("data"));
        Assert.Equal(edited, reloaded.Learnsets.Single(l => l.Key == "SPECIES_BULBASAUR").EggMoves);
        Assert.Equal(data.Learnsets.Single(l => l.Key == "SPECIES_SQUIRTLE").EggMoves, reloaded.Learnsets.Single(l => l.Key == "SPECIES_SQUIRTLE").EggMoves);
    }
}
