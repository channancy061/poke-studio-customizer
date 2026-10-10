using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Progression;
using PokeStudio.Core.Safety;
using PokeStudio.Modules.N3ds;

namespace PokeStudio.Core.Tests;

/// <summary>Runs with the user's own decrypted Omega Ruby when POKESTUDIO_OMEGARUBY_ROM is set (the game file is only read).</summary>
public class N3dsTests
{
    [Fact]
    public void Gen6_text_and_mini_packs_round_trip()
    {
        var original = Gen6Text.Write(["x"], new byte[16]);
        var bytes = Gen6Text.Write(["Egg", "Bulbasaur", "Nidoran♀", "{E07F}"], original);
        Assert.Equal(["Egg", "Bulbasaur", "Nidoran♀", "{E07F}"], Gen6Text.Read(bytes));
        var pack = MiniPack.Build([[1, 2, 3], [4, 5]], "WD"u8.ToArray());
        Assert.Equal([[1, 2, 3], [4, 5]], MiniPack.Read(pack));
    }

    [Fact]
    public void Gen6_evolutions_convert_both_ways()
    {
        for (var m = 1; m <= 34; m++)
        {
            var e = N3dsData.ToEvolution(m, m is 16 ? 170 : 30, "SPECIES_X", i => $"ITEM_{i}", i => $"MOVE_{i}", i => $"SPECIES_{i}");
            var back = N3dsData.FromEvolution(e, k => int.Parse(k[5..]), k => int.Parse(k[5..]), k => int.Parse(k[8..]));
            Assert.True(back?.Method == m, $"method {m}: {e.Method} [{string.Join(",", e.Conditions.Select(c => c.Kind))}] -> {back}");
        }
    }

    [Fact]
    public async Task Real_omega_ruby_loads_and_saves_a_layeredfs_mod_when_available()
    {
        var path = Environment.GetEnvironmentVariable("POKESTUDIO_OMEGARUBY_ROM");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        using var tmp = new TempDir();
        var citra = tmp.Combine("Citra", "load", "mods");
        Directory.CreateDirectory(Path.GetDirectoryName(citra)!);
        var module = new N3dsModule { CitraModsFolder = citra };
        var detected = module.Detect(new GameSource(path, GameSourceKind.RomFile));
        Assert.True(detected.IsMatch, string.Join(" ", detected.Notes));
        Assert.Equal("Pokémon Omega Ruby", detected.GameTitle);
        var stamp = File.GetLastWriteTimeUtc(path);

        var data = await module.LoadDataAsync(path, tmp.Combine("data"));
        var bulba = data.Species.Single(s => s.NationalDex == 1);
        Assert.Equal("Bulbasaur", bulba.Name);
        Assert.Equal(new BaseStats(45, 49, 49, 65, 65, 45), bulba.Stats);
        Assert.Equal(721, data.Species.Count);
        Assert.Contains(data.Species, s => s.NationalDex == 700 && s.Types.Contains("TYPE_FAIRY"));
        Assert.Equal(MoveCategory.Physical, data.Moves.Single(m => m.Key == "MOVE_POUND").Category);
        Assert.Equal(107, data.Catalog.Machines.Count);
        Assert.Equal(("TM06", "MOVE_TOXIC"), data.Catalog.Machines[5]);
        Assert.Equal(("HM01", "MOVE_CUT"), data.Catalog.Machines[100]);
        Assert.Contains("MOVE_TOXIC", data.Learnsets.Single(l => l.Key == bulba.Key).Teachable);
        Assert.Contains("MOVE_CUT", data.Learnsets.Single(l => l.Key == bulba.Key).Teachable);
        Assert.True(data.Trainers.Count > 900, $"{data.Trainers.Count} trainers");
        Assert.Contains(data.Trainers, t => data.Catalog.NameOf(t.TrainerClass) is "Leader" or "Gym Leader" or "Elite Four" or "Champion");
        Assert.Contains(data.Items, i => i.Key == "ITEM_POTION" && i.Price == 300);
        Assert.Empty(module.Validate(data));

        var bi = data.Species.IndexOf(bulba);
        data.Species[bi] = bulba with { Stats = bulba.Stats with { Attack = 99 }, Name = "Bulby" };
        var li = data.Learnsets.FindIndex(l => l.Key == bulba.Key);
        data.Learnsets[li] = data.Learnsets[li] with { LevelUp = [.. data.Learnsets[li].LevelUp, new LevelUpMove(60, "MOVE_GIGA_DRAIN")] };
        var ti = data.Trainers.FindIndex(t => t.Party.Count == 1);
        var trainer = data.Trainers[ti];
        data.Trainers[ti] = trainer with { Party = [.. trainer.Party, new TrainerPokemon { Species = data.Species.Single(s => s.NationalDex == 25).Key, Level = 20, HeldItem = "ITEM_POTION", Moves = ["MOVE_THUNDERBOLT"] }] };
        var pound = data.Moves.FindIndex(m => m.Key == "MOVE_POUND");
        data.Moves[pound] = data.Moves[pound] with { Power = 50 };
        var fixes = EvolutionFixer.Find(data);
        EvolutionFixer.Build(data, fixes.Select(f => (f, f.Suggested))).Change?.Apply();
        Assert.Empty(module.Validate(data));

        await module.SaveDataAsync(data, new ModuleSaveContext(new SafeFileWriter(tmp.Path), tmp.Combine("data"), path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path)); // the game file itself is never written
        var title = "000400000011C400";
        Assert.True(File.Exists(tmp.Combine("data", "romfs", "a", "1", "9", "5")));
        Assert.True(File.Exists(Path.Combine(citra, title, "romfs", "a", "1", "9", "5")));

        var reloaded = await module.LoadDataAsync(path, tmp.Combine("data"));
        var b2 = reloaded.Species.Single(s => s.NationalDex == 1);
        Assert.Equal(99, b2.Stats.Attack);
        Assert.Equal("Bulby", b2.Name);
        Assert.Contains(new LevelUpMove(60, "MOVE_GIGA_DRAIN"), reloaded.Learnsets.Single(l => l.Key == b2.Key).LevelUp);
        Assert.Equal(50, reloaded.Moves.Single(m => m.Key == "MOVE_POUND").Power);
        var t2 = reloaded.Trainers.Single(t => t.Id == trainer.Id);
        Assert.Equal(trainer.Party.Count + 1, t2.Party.Count);
        Assert.Equal("ITEM_POTION", t2.Party[^1].HeldItem);
        Assert.Equal(["MOVE_THUNDERBOLT"], t2.Party[^1].Moves);
        Assert.DoesNotContain(EvolutionFixer.Find(reloaded), f => f.Problem != EvolutionProblem.TimeOfDay && f.Suggested != EvolutionFix.Leave);
    }
}
