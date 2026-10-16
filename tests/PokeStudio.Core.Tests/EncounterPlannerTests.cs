using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Models;
using PokeStudio.Core.Progression;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

/// <summary>Wild Pokémon planned for the story, on the user's real projects.</summary>
public class EncounterPlannerTests
{
    private static async Task Plan(string? root, string file)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        using var tmp = new TempDir();
        var module = new ExpansionModule();
        var data = await module.LoadDataAsync(root, tmp.Combine("data"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var rows = EncounterPlanner.Plan(data, new LabSettings(), new EncounterPlanner.Options());
        var took = watch.Elapsed;
        var species = data.Species.ToDictionary(s => s.Key);
        var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "planner");
        Directory.CreateDirectory(outDir);
        await File.WriteAllLinesAsync(Path.Combine(outDir, file), [$"{rows.Count} areas in {took.TotalSeconds:0.0}s",
            .. rows.Select(r => $"{r.Title} (before {r.Before}; bosses: {string.Join("/", r.Threats.Select(t => t.Replace("TYPE_", "")))}): "
                + string.Join("; ", r.Swaps.Select(s => $"[{s.Method}] {s.From.Replace("SPECIES_", "")} -> {s.To.Replace("SPECIES_", "")} ({s.Why})")))]);

        Assert.NotEmpty(rows);
        Assert.All(rows.SelectMany(r => r.Swaps), s => Assert.StartsWith("hits ", s.Why, StringComparison.Ordinal)); // every new Pokémon is a real answer
        Assert.True(took.TotalSeconds < 60, $"took {took.TotalSeconds:0}s");
        var swaps = rows.SelectMany(r => r.Swaps).ToList();
        Assert.All(swaps, s => Assert.Equal(0, (int)(species[s.To].Tags & (SpeciesTags.RestrictedLegendary | SpeciesTags.SubLegendary | SpeciesTags.Mythical))));
        Assert.All(swaps.Where(s => s.Method.Contains("water", StringComparison.Ordinal) || s.Method.Contains("fish", StringComparison.Ordinal)),
            s => Assert.Contains("TYPE_WATER", species[s.To].Types));

        // one undo step; nothing else in the tables changes
        var change = EncounterPlanner.ToChange(data, rows)!;
        var before = data.Encounters.ToList();
        change.Apply();
        Assert.All(rows, r => Assert.All(r.Swaps, s => Assert.Contains(data.Encounters.Single(t => t.Key == r.TableKeys[0]).Methods.SelectMany(m => m.Slots), x => x.Species == s.To)));
        change.Revert();
        Assert.Equal(before, data.Encounters);
    }

    [Fact]
    public Task Real_firered_wild_pokemon_fit_the_story_when_available() => Plan(Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR"), "firered.txt");

    [Fact]
    public async Task Real_firered_placeholder_trainers_are_never_fought_when_available()
    {
        var root = Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        using var tmp = new TempDir();
        var data = await new ExpansionModule().LoadDataAsync(root, tmp.Combine("data"));
        Assert.True(data.Trainers.Single(t => t.Key == "TRAINER_PLAYER_BRENDAN").NeverFought); // only defined, never battled
        Assert.False(data.Trainers.Single(t => t.Key == "TRAINER_LEADER_BROCK").NeverFought);

        // so the first gym's Rock types lead what Route 1 is planned against (not four lone Ekans)
        var route1 = Assert.Single(EncounterPlanner.Plan(data, new LabSettings(), new EncounterPlanner.Options()), r => r.TableKeys.Any(k => k.StartsWith("sRoute1_", StringComparison.Ordinal)));
        Assert.Contains("TYPE_ROCK", route1.Threats.Take(2)); // Brock: Rock/Ground
    }

    [Fact]
    public async Task Real_firered_follows_the_playthrough_and_every_gym_gets_answers_when_available()
    {
        var root = Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        using var tmp = new TempDir();
        var data = await new ExpansionModule().LoadDataAsync(root, tmp.Combine("data"));
        var caps = data.Rules!.LevelCaps;
        int Gym(int n) => caps.Select((c, i) => (c, i)).First(x => x.c.Milestone == $"FLAG_BADGE0{n}_GET").i;
        var next = EncounterPlanner.NextBattles(data);

        // Route 3 is after Pewter City: it leads to Misty, not Brock, even though its levels are low
        Assert.Equal(Gym(1), next("MAP_ROUTE1", 3, "land_mons", 0));
        Assert.Equal(Gym(2), next("MAP_ROUTE3", 6, "land_mons", 0));

        // surfing and fishing open up later: Pallet Town's water only after the 5th badge, the Old Rod in Vermilion
        Assert.True(next("MAP_PALLET_TOWN", 5, "water_mons", 0) > Gym(5) - 1);
        Assert.True(next("MAP_PALLET_TOWN", 5, "fishing_mons", 0) >= Gym(3));
        Assert.True(next("MAP_PALLET_TOWN", 5, "fishing_mons", 6) > next("MAP_PALLET_TOWN", 5, "fishing_mons", 0)); // the Super Rod

        // the check finds the original game's gap (only Mankey answers Brock before Pewter), and the plan fills it
        var lab = new LabSettings();
        Assert.False(EncounterPlanner.Check(data, lab).Single(r => r.Index == Gym(1)).Ready);
        EncounterPlanner.ToChange(data, EncounterPlanner.Plan(data, lab, new EncounterPlanner.Options()))!.Apply();
        var after = EncounterPlanner.Check(data, lab);
        Assert.All(after, r => Assert.True(r.Ready, $"{r.Label}: {r.Counters.Count} counters"));
        Assert.Equal("Route 1", StoryOrder.AreaName("MAP_ROUTE1"));
        Assert.Equal("S.S. Anne Exterior", StoryOrder.AreaName("MAP_SSANNE_EXTERIOR"));
    }

    [Fact]
    public Task Real_emerald_wild_pokemon_fit_the_story_when_available() => Plan(Environment.GetEnvironmentVariable("POKESTUDIO_EXPANSION_DIR"), "emerald.txt");
}
