using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Randomizer;
using PokeStudio.Core.Safety;
using PokeStudio.Modules.Gen3Rom;

namespace PokeStudio.Core.Tests;

public class RandomizerTests
{
    private static readonly RandomizerOptions Everything = new()
    {
        Seed = "123456",
        Wild = WildRandomizing.SimilarStrength,
        Trainers = TrainerRandomizing.KeepTypes,
        Abilities = true,
        Types = true,
        LevelUpMoves = true,
        TmCompatibility = true,
    };

    private static async Task<(GameData Data, Gen3RomModule Module, TempDir Tmp, string Rom)?> Emerald()
    {
        var real = Environment.GetEnvironmentVariable("POKESTUDIO_EMERALD_ROM");
        if (string.IsNullOrEmpty(real) || !File.Exists(real))
        {
            return null;
        }

        var tmp = new TempDir();
        var rom = tmp.Combine("emerald.gba");
        File.Copy(real, rom);
        var module = new Gen3RomModule();
        return (await module.LoadDataAsync(rom, tmp.Combine("data")), module, tmp, rom);
    }

    [Fact]
    public void Seeds_are_stable_numbers()
    {
        Assert.Equal(Randomizer.Randomizer.SeedNumber("abc"), Randomizer.Randomizer.SeedNumber("abc"));
        Assert.NotEqual(Randomizer.Randomizer.SeedNumber("abc"), Randomizer.Randomizer.SeedNumber("abd"));
        Assert.True(Randomizer.Randomizer.SeedNumber("") >= 0);
    }

    [Fact]
    public async Task Real_emerald_randomizes_the_same_way_for_the_same_seed_when_available()
    {
        if (await Emerald() is not var (a, _, tmpA, _) || await Emerald() is not var (b, _, tmpB, _))
        {
            return;
        }

        using (tmpA)
        using (tmpB)
        {
            var (changeA, summaryA) = Randomizer.Randomizer.Build(a, Everything);
            var (changeB, summaryB) = Randomizer.Randomizer.Build(b, Everything);
            changeA!.Apply();
            changeB!.Apply();
            Assert.Equal(summaryA, summaryB);
            Assert.True(summaryA.WildSlots > 300 && summaryA.TrainerPokemon > 500, summaryA.ToString());
            Assert.Equal(a.Encounters.SelectMany(e => e.Methods).SelectMany(m => m.Slots), b.Encounters.SelectMany(e => e.Methods).SelectMany(m => m.Slots));
            Assert.Equal(a.Trainers.SelectMany(t => t.Party).Select(p => p.Species), b.Trainers.SelectMany(t => t.Party).Select(p => p.Species));

            var (other, _) = Randomizer.Randomizer.Build(b, Everything with { Seed = "654321" });
            other!.Apply();
            Assert.NotEqual(a.Trainers.SelectMany(t => t.Party).Select(p => p.Species), b.Trainers.SelectMany(t => t.Party).Select(p => p.Species));
        }
    }

    [Fact]
    public async Task Real_emerald_randomizing_follows_the_rules_undoes_and_saves_when_available()
    {
        if (await Emerald() is not var (data, module, tmp, rom))
        {
            return;
        }

        using (tmp)
        {
            var before = data.Trainers.ToDictionary(t => t.Key, t => t.Party.Select(p => p.Species).ToList());
            var beforeSpecies = data.Species.ToList();
            var (change, _) = Randomizer.Randomizer.Build(data, Everything with { Types = false });
            change!.Apply();
            var byKey = data.Species.ToDictionary(s => s.Key);

            // no legendaries in the wild; trainers keep a type in common with the Pokémon each replaces
            var legendary = SpeciesTags.RestrictedLegendary | SpeciesTags.SubLegendary | SpeciesTags.Mythical;
            Assert.DoesNotContain(data.Encounters.SelectMany(e => e.Methods).SelectMany(m => m.Slots), s => byKey.TryGetValue(s.Species, out var sp) && (sp.Tags & legendary) != 0);
            foreach (var t in data.Trainers.Where(t => t.LockedReason is null && before[t.Key].Count == t.Party.Count))
            {
                for (var i = 0; i < t.Party.Count; i++)
                {
                    if (byKey.TryGetValue(before[t.Key][i], out var old) && byKey.TryGetValue(t.Party[i].Species, out var now))
                    {
                        Assert.True(old.Types.Intersect(now.Types).Any(), $"{t.Key}: {old.Key} → {now.Key}");
                    }
                }
            }

            Assert.DoesNotContain(module.Validate(data), v => v.Severity == Validation.IssueSeverity.Error);
            change.Revert();
            Assert.Equal(beforeSpecies, data.Species);
            Assert.Equal(before, data.Trainers.ToDictionary(t => t.Key, t => t.Party.Select(p => p.Species).ToList()));

            // random types: a whole evolution family shares them
            var (typed, _) = Randomizer.Randomizer.Build(data, Everything);
            typed!.Apply();
            byKey = data.Species.ToDictionary(s => s.Key);
            Assert.Equal(byKey["SPECIES_BULBASAUR"].Types, byKey["SPECIES_VENUSAUR"].Types);
            Assert.DoesNotContain(module.Validate(data), v => v.Severity == Validation.IssueSeverity.Error);

            // and the result saves into the ROM
            await module.SaveDataAsync(data, new ModuleSaveContext(new SafeFileWriter(tmp.Path), tmp.Combine("data"), rom));
            var reloaded = await module.LoadDataAsync(rom, tmp.Combine("data"));
            Assert.Equal(data.Trainers.SelectMany(t => t.Party).Select(p => p.Species), reloaded.Trainers.SelectMany(t => t.Party).Select(p => p.Species));
            Assert.Equal(byKey["SPECIES_PIKACHU"].Types, reloaded.Species.Single(s => s.Key == "SPECIES_PIKACHU").Types);
        }
    }
}
