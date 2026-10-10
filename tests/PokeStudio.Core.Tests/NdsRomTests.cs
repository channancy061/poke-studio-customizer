using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Progression;
using PokeStudio.Core.Safety;
using PokeStudio.Modules.Nds;

namespace PokeStudio.Core.Tests;

/// <summary>Runs against the user's own DS ROMs when POKESTUDIO_PLATINUM_ROM / _DIAMOND_ROM / _SOULSILVER_ROM / _BLACK_ROM are set (on copies).</summary>
public class NdsRomTests
{
    [Fact]
    public void Gen4_and_gen5_text_files_round_trip()
    {
        var names = new List<string> { "BULBASAUR", "NIDORAN♀", "FARFETCH’D", "{0001}raw" };
        var g4 = Gen4Text.WriteFile(names, 0x1234);
        Assert.Equal(names, Gen4Text.ReadFile(g4, out var seed));
        Assert.Equal(0x1234, seed);

        var original = Gen5Text.WriteFile(["x"], new byte[16]);
        var g5 = Gen5Text.WriteFile(["Bulbasaur", "Nidoran♀", "Line\nbreak", "{F000}x"], original);
        Assert.Equal(["Bulbasaur", "Nidoran♀", "Line\nbreak", "{F000}x"], Gen5Text.ReadFile(g5));
    }

    [Fact]
    public void Header_checksum_matches_the_known_algorithm()
    {
        Assert.Equal(0x4B37, NdsRom.Crc16("123456789"u8)); // CRC-16/MODBUS check value
    }

    [Fact]
    public void Evolutions_convert_both_ways_for_both_generations()
    {
        foreach (var gen in new[] { 4, 5 })
        {
            var methods = gen == 4 ? Enumerable.Range(1, 26) : Enumerable.Range(1, 27);
            foreach (var m in methods)
            {
                var param = m is 4 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 22 or 23 ? 30 : m is 15 ? 170 : 5;
                if (gen == 5 && m is 9 or 10 or 11 or 12 or 13 or 14 or 15 or 23 or 24)
                {
                    param = 30;
                }

                if (gen == 5 && m == 16)
                {
                    param = 170;
                }

                var e = NdsConst.ToEvolution(gen, m, param, "SPECIES_X", i => $"ITEM_{i}", i => $"MOVE_{i}", i => $"SPECIES_{i}");
                var back = NdsWriter.FromEvolution(gen, e, k => int.Parse(k[5..]), k => int.Parse(k[5..]), k => int.Parse(k[8..]));
                Assert.True(back is not null, $"gen {gen} method {m}: {e.Method} {string.Join(",", e.Conditions.Select(c => c.Kind))}");
                Assert.Equal(m, back!.Value.Method);
            }
        }
    }

    private static async Task RealRom(string? path, string code, int trainers, int speciesInPersonal)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        using var tmp = new TempDir();
        var rom = tmp.Combine("game.nds");
        File.Copy(path, rom);
        var original = await File.ReadAllBytesAsync(rom);
        var module = new NdsModule();
        Assert.True(module.Detect(new GameSource(rom, GameSourceKind.RomFile)).IsMatch);
        var data = await module.LoadDataAsync(rom, tmp.Combine("data"));
        Assert.Equal(code, System.Text.Encoding.ASCII.GetString(original, 0x0C, 4));

        var bulba = data.Species.Single(s => s.NationalDex == 1);
        Assert.Equal(new BaseStats(45, 49, 49, 65, 65, 45), bulba.Stats);
        Assert.Equal(["TYPE_GRASS", "TYPE_POISON"], bulba.Types);
        Assert.Equal(speciesInPersonal - 1, data.Species.Count);
        Assert.Equal(40, data.Moves.Single(m => m.Id == 1).Power);
        Assert.Equal(MoveCategory.Physical, data.Moves.Single(m => m.Id == 1).Category);
        Assert.Equal(trainers - 1, data.Trainers.Count);
        Assert.True(data.Encounters.Count > 40, $"only {data.Encounters.Count} areas");
        Assert.True(data.Catalog.Machines.Count >= 100, $"{data.Catalog.Machines.Count} TMs");
        Assert.Equal("MOVE_TOXIC", data.Catalog.Machines[5].Move);
        Assert.Contains(data.Learnsets.Single(l => l.Key == bulba.Key).Teachable, m => m == "MOVE_TOXIC");
        Assert.Contains(data.Items, i => i.Key == "ITEM_POTION" && i.Price == 300);
        Assert.Contains(data.Trainers, t => data.Catalog.NameOf(t.TrainerClass) is "Elite Four" or "Champion");
        Assert.All(data.Trainers.Take(50), t => Assert.All(t.Party, p => Assert.InRange(p.Level, 1, 100)));
        Assert.Empty(module.Validate(data));

        // edits that need bigger archives: a longer learnset, a bigger team with items and moves, a renamed Pokémon
        var bi = data.Species.IndexOf(bulba);
        data.Species[bi] = bulba with { Stats = bulba.Stats with { Attack = 99 }, Name = bulba.Name.Length > 0 && char.IsUpper(bulba.Name[^1]) ? "BULBY" : "Bulby" };
        var li = data.Learnsets.FindIndex(l => l.Key == bulba.Key);
        data.Learnsets[li] = data.Learnsets[li] with { LevelUp = [.. data.Learnsets[li].LevelUp, new LevelUpMove(60, "MOVE_GIGA_DRAIN"), new LevelUpMove(61, "MOVE_TOXIC")] };
        var ti = data.Trainers.FindIndex(t => t.Party.Count == 1);
        var trainer = data.Trainers[ti];
        data.Trainers[ti] = trainer with { Party = [.. trainer.Party, new TrainerPokemon { Species = data.Species.Single(s => s.NationalDex == 25).Key, Level = 20, HeldItem = "ITEM_POTION", Moves = ["MOVE_THUNDERBOLT"] }] };
        var fixes = EvolutionFixer.Find(data, trades: true, timeOfDay: true);
        EvolutionFixer.Build(data, fixes.Select(f => (f, f.Suggested))).Change?.Apply();
        Assert.Empty(module.Validate(data));

        var ctx = new ModuleSaveContext(new SafeFileWriter(tmp.Path), tmp.Combine("data"), rom);
        await module.SaveDataAsync(data, ctx);
        var reloaded = await module.LoadDataAsync(rom, tmp.Combine("data"));
        var b2 = reloaded.Species.Single(s => s.NationalDex == 1);
        Assert.Equal(99, b2.Stats.Attack);
        Assert.StartsWith("Bulby", b2.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(new LevelUpMove(61, "MOVE_TOXIC"), reloaded.Learnsets.Single(l => l.Key == b2.Key).LevelUp);
        var t2 = reloaded.Trainers.Single(t => t.Id == trainer.Id);
        Assert.Equal(trainer.Party.Count + 1, t2.Party.Count);
        Assert.Equal("ITEM_POTION", t2.Party[^1].HeldItem);
        Assert.Equal(["MOVE_THUNDERBOLT"], t2.Party[^1].Moves);
        Assert.DoesNotContain(EvolutionFixer.Find(reloaded, trades: true, timeOfDay: false), f => f.Problem != EvolutionProblem.TimeOfDay && f.Suggested != EvolutionFix.Leave);
        Assert.Equal(reloaded.Species.Count, data.Species.Count);
        Assert.Equal(reloaded.Trainers.Count, data.Trainers.Count);

        // the header checksum is valid and the unchanged files are byte-for-byte the same
        var edited = await File.ReadAllBytesAsync(rom);
        Assert.Equal(NdsRom.Crc16(edited.AsSpan(0, 0x15E)), BitConverter.ToUInt16(edited, 0x15E));
        var before = new NdsRom(original);
        var after = new NdsRom(edited);
        var changed = before.Paths.Where(p => !before.Read(p).AsSpan().SequenceEqual(after.Read(p))).ToList();
        Assert.True(changed.Count is >= 4 and <= 8, string.Join(", ", changed));
    }

    [Fact]
    public Task Real_platinum_rom_loads_edits_and_saves_when_available() => RealRom(Environment.GetEnvironmentVariable("POKESTUDIO_PLATINUM_ROM"), "CPUE", 928, 494);

    [Fact]
    public Task Real_diamond_rom_loads_edits_and_saves_when_available() => RealRom(Environment.GetEnvironmentVariable("POKESTUDIO_DIAMOND_ROM"), "ADAE", 850, 494);

    [Fact]
    public Task Real_soulsilver_rom_loads_edits_and_saves_when_available() => RealRom(Environment.GetEnvironmentVariable("POKESTUDIO_SOULSILVER_ROM"), "IPGE", 738, 494);

    [Fact]
    public Task Real_black_rom_loads_edits_and_saves_when_available() => RealRom(Environment.GetEnvironmentVariable("POKESTUDIO_BLACK_ROM"), "IRBO", 616, 650);
}
