using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Progression;
using PokeStudio.Core.Safety;
using PokeStudio.Modules.Gen3Rom;

namespace PokeStudio.Core.Tests;

/// <summary>Runs against the user's own ROMs when POKESTUDIO_EMERALD_ROM / _FIRERED_ROM / _RUBY_ROM are set (always on a copy).</summary>
public class Gen3RomTests
{
    [Fact]
    public void Text_round_trips_and_names_become_constants()
    {
        Assert.Equal("NIDORAN♀", Gen3Text.Read(Gen3Text.Write("NIDORAN♀")));
        Assert.Equal("FARFETCH’D", Gen3Text.Read(Gen3Text.Write("FARFETCH'D")));
        Assert.Equal("NIDORAN_F", Gen3Text.Constant("NIDORAN♀"));
        Assert.Equal("MR_MIME", Gen3Text.Constant("MR. MIME"));
        Assert.Equal("HO_OH", Gen3Text.Constant("HO-OH"));
        Assert.False(Gen3Text.CanWrite("Pokémon ★"));
    }

    [Fact]
    public void Gen3_evolutions_convert_both_ways_and_unsupported_ones_are_refused()
    {
        string[] items = ["ITEM_NONE", "ITEM_MOON_STONE"];
        int Item(string k) => Array.IndexOf(items, k);
        foreach (var (method, param) in new[] { (4, 16), (1, 0), (2, 0), (3, 0), (5, 0), (6, 1), (7, 1), (8, 20), (11, 7), (13, 20), (14, 20), (15, 170) })
        {
            var e = Gen3Reader.ToEvolution(method, param, "SPECIES_X", items);
            Assert.Equal((method, param), Gen3Writer.FromEvolution(e, Item));
        }

        var held = new Evolution("EVO_LEVEL", "0", "SPECIES_X") { Conditions = [new EvolutionCondition("IF_HOLD_ITEM", "ITEM_MOON_STONE")] };
        Assert.Null(Gen3Writer.FromEvolution(held, Item));
    }

    private static async Task RealRom(string? path, string code, int trainers)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        using var tmp = new TempDir();
        var rom = tmp.Combine("game.gba");
        File.Copy(path, rom);
        var original = await File.ReadAllBytesAsync(rom);
        var module = new Gen3RomModule();
        var detected = module.Detect(new GameSource(rom, GameSourceKind.RomFile));
        Assert.True(detected.IsMatch);

        var data = await module.LoadDataAsync(rom, tmp.Combine("data"));
        Assert.Equal(code, System.Text.Encoding.ASCII.GetString(original, 0xAC, 4));
        var bulba = data.Species.Single(s => s.Key == "SPECIES_BULBASAUR");
        Assert.Equal(new BaseStats(45, 49, 49, 65, 65, 45), bulba.Stats);
        Assert.Equal(["TYPE_GRASS", "TYPE_POISON"], bulba.Types);
        Assert.Equal(1, bulba.NationalDex);
        Assert.Equal(252, data.Species.Single(s => s.Key == "SPECIES_TREECKO").NationalDex);
        Assert.Equal(SpeciesTags.RestrictedLegendary, data.Species.Single(s => s.Key == "SPECIES_MEWTWO").Tags);
        Assert.Equal(40, data.Moves.Single(m => m.Key == "MOVE_POUND").Power);
        Assert.Equal(354, data.Moves.Count);
        Assert.Contains(data.Items, i => i.Key == "ITEM_MASTER_BALL");
        Assert.Equal(trainers - 1, data.Trainers.Count);
        Assert.True(data.Encounters.Count > 80, $"only {data.Encounters.Count} wild areas");
        Assert.Contains(data.Encounters, e => e.Map.StartsWith("MAP_ROUTE_1", StringComparison.Ordinal));
        Assert.Contains(data.Learnsets.Single(l => l.Key == "SPECIES_BULBASAUR").LevelUp, m => m == new LevelUpMove(7, "MOVE_LEECH_SEED"));
        Assert.Contains("MOVE_SOLARBEAM", data.Learnsets.Single(l => l.Key == "SPECIES_BULBASAUR").Teachable);
        if (code != "BPRE") // (the FireRed copy used here has its own, renamed trainer classes)
        {
            Assert.Contains(data.Catalog.TrainerClasses, c => c.Name is "Leader" or "Gym Leader");
            Assert.Contains(data.Catalog.TrainerClasses, c => c.Name.Equals("Elite Four", StringComparison.OrdinalIgnoreCase));
        }
        var tradedKadabra = data.Evolutions.Single(e => e.FromSpecies == "SPECIES_KADABRA").Evolutions.Any(e => e.Method == "EVO_TRADE"); // some copies are already patched
        Assert.Empty(module.Validate(data));

        // edits: stats and name, a longer level-up list (moves to free space), a bigger team, the trade evolutions fixed
        var bi = data.Species.IndexOf(bulba);
        data.Species[bi] = bulba with { Stats = bulba.Stats with { Attack = 99 }, Name = "BULBY" };
        var li = data.Learnsets.FindIndex(l => l.Key == "SPECIES_BULBASAUR");
        data.Learnsets[li] = data.Learnsets[li] with { LevelUp = [.. data.Learnsets[li].LevelUp, new LevelUpMove(50, "MOVE_GIGA_DRAIN"), new LevelUpMove(55, "MOVE_SLUDGE_BOMB")] };
        var ti = data.Trainers.FindIndex(t => t.Party.Count == 1);
        var trainer = data.Trainers[ti];
        data.Trainers[ti] = trainer with { Party = [.. trainer.Party, new TrainerPokemon { Species = "SPECIES_PIKACHU", Level = 20, Moves = ["MOVE_THUNDERBOLT"], HeldItem = "ITEM_LIGHT_BALL" }] };
        var fixes = EvolutionFixer.Find(data, trades: true, timeOfDay: true);
        Assert.DoesNotContain(fixes, f => f.Choices.Contains(EvolutionFix.HoldItem)); // Gen 3 can't do it
        EvolutionFixer.Build(data, fixes.Select(f => (f, f.Suggested))).Change?.Apply();
        Assert.Empty(module.Validate(data));

        var ctx = new ModuleSaveContext(new SafeFileWriter(tmp.Path), tmp.Combine("data"), rom);
        await module.SaveDataAsync(data, ctx);

        var reloaded = await module.LoadDataAsync(rom, tmp.Combine("data"));
        var b2 = reloaded.Species.Single(s => s.Key == "SPECIES_BULBY");
        Assert.Equal(99, b2.Stats.Attack);
        Assert.Contains(new LevelUpMove(55, "MOVE_SLUDGE_BOMB"), reloaded.Learnsets.Single(l => l.Key == "SPECIES_BULBY").LevelUp);
        var t2 = reloaded.Trainers.Single(t => t.Id == trainer.Id);
        Assert.Equal(trainer.Party.Count + 1, t2.Party.Count);
        Assert.True(t2.Party[^1].HeldItem == "ITEM_LIGHT_BALL", $"{t2.Party[^1]} moves={string.Join(",", t2.Party[^1].Moves)} items={data.Items.Any(i => i.Key == "ITEM_LIGHT_BALL")}");
        Assert.Equal(["MOVE_THUNDERBOLT"], t2.Party[^1].Moves);
        if (tradedKadabra)
        {
            Assert.Contains(reloaded.Evolutions.Single(e => e.FromSpecies == "SPECIES_KADABRA").Evolutions, e => e.Method == "EVO_LEVEL" && e.Parameter == "36");
        }

        Assert.Empty(EvolutionFixer.Find(reloaded, trades: true, timeOfDay: true));

        // only the edited data changed
        var edited = await File.ReadAllBytesAsync(rom);
        Assert.Equal(original.Length, edited.Length);
        var changed = original.Zip(edited).Count(p => p.First != p.Second);
        Assert.InRange(changed, 20, 1500);
        Assert.Equal(original.AsSpan(0, 0xC0).ToArray(), edited.AsSpan(0, 0xC0).ToArray()); // header untouched
    }

    [Fact]
    public Task Real_emerald_rom_loads_edits_and_saves_when_available() => RealRom(Environment.GetEnvironmentVariable("POKESTUDIO_EMERALD_ROM"), "BPEE", 855);

    [Fact]
    public Task Real_firered_rom_loads_edits_and_saves_when_available() => RealRom(Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_ROM"), "BPRE", 743);

    [Fact]
    public Task Real_ruby_rom_loads_edits_and_saves_when_available() => RealRom(Environment.GetEnvironmentVariable("POKESTUDIO_RUBY_ROM"), "AXVE", 694);
}
