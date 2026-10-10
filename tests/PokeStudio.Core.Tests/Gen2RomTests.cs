using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Progression;
using PokeStudio.Core.Safety;
using PokeStudio.Modules.Gen2Rom;

namespace PokeStudio.Core.Tests;

/// <summary>Runs with the user's own Crystal ROM when POKESTUDIO_CRYSTAL_ROM is set (on a copy).</summary>
public class Gen2RomTests
{
    [Fact]
    public void Gen2_text_round_trips()
    {
        Assert.Equal("NIDORAN♀", Gen2Text.Read(Gen2Text.Write("NIDORAN♀")));
        Assert.Equal("FARFETCH'D", Gen2Text.Read(Gen2Text.Write("FARFETCH'D")));
        Assert.Equal("MR_MIME", Gen2Text.Constant("MR.MIME"));
    }

    [Fact]
    public async Task Real_crystal_rom_loads_edits_and_saves_when_available()
    {
        var path = Environment.GetEnvironmentVariable("POKESTUDIO_CRYSTAL_ROM");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        using var tmp = new TempDir();
        var rom = tmp.Combine("game.gbc");
        File.Copy(path, rom);
        var original = await File.ReadAllBytesAsync(rom);
        var module = new Gen2RomModule();
        Assert.Equal("Pokémon Crystal", module.Detect(new GameSource(rom, GameSourceKind.RomFile)).GameTitle);
        var data = await module.LoadDataAsync(rom, tmp.Combine("data"));
        var bulba = data.Species.Single(s => s.Key == "SPECIES_BULBASAUR");
        Assert.Equal(new BaseStats(45, 49, 49, 65, 65, 45), bulba.Stats);
        Assert.Equal(["TYPE_GRASS", "TYPE_POISON"], bulba.Types);
        Assert.Equal(251, data.Species.Count);
        Assert.Equal(251, data.Moves.Count);
        Assert.Equal(40, data.Moves.Single(m => m.Key == "MOVE_POUND").Power);
        Assert.Equal(100, data.Moves.Single(m => m.Key == "MOVE_POUND").Accuracy);
        Assert.Equal(("TM01", "MOVE_DYNAMICPUNCH"), data.Catalog.Machines[0]);
        Assert.Contains(new LevelUpMove(7, "MOVE_LEECH_SEED"), data.Learnsets.Single(l => l.Key == bulba.Key).LevelUp);
        Assert.Contains(data.Evolutions.Single(e => e.FromSpecies == "SPECIES_EEVEE").Evolutions, e => e.Conditions.Any(c => c.Value == "TIME_NIGHT"));
        Assert.Contains(data.Trainers, t => t.Name == "FALKNER" && t.Party.Any(p => p.Species == "SPECIES_PIDGEOTTO"));
        Assert.Contains(data.Catalog.TrainerClasses, c => c.Name == "Leader");
        Assert.True(data.Trainers.Count > 400, $"{data.Trainers.Count} trainers");
        Assert.True(data.Encounters.Count > 100, $"{data.Encounters.Count} wild areas");
        Assert.True(data.Items.Any(i => i.Name.EndsWith("BALL", StringComparison.Ordinal) && i.Price == 200), string.Join(", ", data.Items.Take(6).Select(i => $"{i.Name}={i.Price}")));
        Assert.Empty(module.Validate(data));

        var bi = data.Species.IndexOf(bulba);
        data.Species[bi] = bulba with { Stats = bulba.Stats with { Attack = 99 }, Name = "BULBY" };
        var li = data.Learnsets.FindIndex(l => l.Key == bulba.Key);
        data.Learnsets[li] = data.Learnsets[li] with { LevelUp = [.. data.Learnsets[li].LevelUp, new LevelUpMove(50, "MOVE_GIGA_DRAIN"), new LevelUpMove(55, "MOVE_SLUDGE_BOMB")] };
        var fi = data.Trainers.FindIndex(t => t.Name == "FALKNER");
        var falkner = data.Trainers[fi];
        data.Trainers[fi] = falkner with { Party = [.. falkner.Party, new TrainerPokemon { Species = "SPECIES_PIKACHU", Level = 12, HeldItem = "ITEM_BERRY", Moves = ["MOVE_THUNDERSHOCK"] }] };
        var fixes = EvolutionFixer.Find(data);
        Assert.DoesNotContain(fixes, f => f.Choices.Contains(EvolutionFix.HoldItem));
        EvolutionFixer.Build(data, fixes.Select(f => (f, f.Suggested))).Change?.Apply();
        Assert.Empty(module.Validate(data));

        await module.SaveDataAsync(data, new ModuleSaveContext(new SafeFileWriter(tmp.Path), tmp.Combine("data"), rom));
        var reloaded = await module.LoadDataAsync(rom, tmp.Combine("data"));
        var b2 = reloaded.Species.Single(s => s.Key == "SPECIES_BULBY");
        Assert.Equal(99, b2.Stats.Attack);
        Assert.Contains(new LevelUpMove(55, "MOVE_SLUDGE_BOMB"), reloaded.Learnsets.Single(l => l.Key == b2.Key).LevelUp);
        var f2 = reloaded.Trainers.Single(t => t.Name == "FALKNER");
        Assert.Equal(falkner.Party.Count + 1, f2.Party.Count);
        Assert.Equal("ITEM_BERRY", f2.Party[^1].HeldItem);
        Assert.Equal(["MOVE_THUNDERSHOCK"], f2.Party[^1].Moves);
        Assert.Equal(reloaded.Trainers.Count, data.Trainers.Count);
        Assert.DoesNotContain(reloaded.Evolutions.Single(e => e.FromSpecies == "SPECIES_KADABRA").Evolutions, e => e.Method == "EVO_TRADE"); // (some copies are already patched to level 37)

        var edited = await File.ReadAllBytesAsync(rom);
        Assert.Equal(original.Length, edited.Length);
        Assert.InRange(original.Zip(edited).Count(p => p.First != p.Second), 10, 400);
        var sum = edited.Select((x, i) => i is 0x14E or 0x14F ? 0 : (int)x).Sum();
        Assert.Equal((ushort)sum, (ushort)((edited[0x14E] << 8) | edited[0x14F]));
    }
}
