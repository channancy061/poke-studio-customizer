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
        // in-game trades then ask for a Pokémon met before the trader
        var metBy = EncounterPlanner.Met(data);
        var asks = TradePlanner.Requests(data, [new InGameTrade("T1", "Route 2 House", "SPECIES_MAGIKARP", "SPECIES_MR_MIME"), new InGameTrade("T2", "Cerulean City House3", "SPECIES_POLIWHIRL", "SPECIES_JYNX")]);
        foreach (var (id, ask) in asks)
        {
            var place = id == "T1" ? "Route 2 House" : "Cerulean City House3";
            var by = StoryOrder.MilestoneOfPlace(data, place)!.Value;
            Assert.Contains(metBy, m => m.From <= by && Core.ChampionLab.TeamBuilder.FamilyRoot(data, m.Species) == Core.ChampionLab.TeamBuilder.FamilyRoot(data, ask));
        }

        // route trainers then use those wild Pokémon, with fair moves (no Pidgey spamming Sand Attack)
        var (trainers, updated) = RouteTrainers.Refresh(data, lab);
        Assert.True(updated > 100);
        trainers!.Apply();
        var wild = EncounterPlanner.Met(data).Select(m => m.Species).ToHashSet();
        var rick = data.Trainers.Single(t => t.Key == "TRAINER_BUG_CATCHER_RICK");
        Assert.All(rick.Party, p => Assert.True(wild.Any(w => Core.ChampionLab.TeamBuilder.FamilyRoot(data, w) == Core.ChampionLab.TeamBuilder.FamilyRoot(data, p.Species)), p.Species));
        // the type spread: before Erika no type is on more than about a third of the route trainers' Pokémon (it was Grass,
        // Bug and Poison almost everywhere)
        var species = data.Species.ToDictionary(s => s.Key);
        var early = data.Trainers.Where(t => !t.Key.Contains('@') && !t.NeverFought && Core.ChampionLab.Lab.GroupOf(data, lab, t) == Core.ChampionLab.TrainerGroup.Regular
            && Core.ChampionLab.Lab.MilestoneOf(data, t) < Gym(4)).SelectMany(t => t.Party).ToList();
        var shares = early.SelectMany(p => species[p.Species].Types.Distinct()).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count() / (double)early.Count);
        Assert.True(shares.Values.Max() < 0.35, string.Join(", ", shares.OrderByDescending(x => x.Value).Take(5).Select(x => $"{x.Key} {x.Value:P0}")));

        // Nightmare: six fully trained Pokémon with items and the Expert AI
        var (nightmare, nightmareCount) = RouteTrainers.ExtraHardTeams(data, lab);
        Assert.True(nightmareCount > 100);
        nightmare!.Apply();
        var hard = data.Trainers.Where(t => t.Key.EndsWith("@Extra Hard", StringComparison.Ordinal)).ToList();
        Assert.All(hard, t => Assert.Equal(6, t.Party.Count));
        Assert.True(hard.SelectMany(t => t.Party).Count(p => p.Ivs?.Attack == 31 && p.Evs is not null) > hard.Count * 5);
        Assert.True(hard.SelectMany(t => t.Party).Count(p => p.HeldItem is not null) > hard.Count * 3);
        Assert.All(hard, t => Assert.Contains("AI_FLAG_PREDICTION", t.AiFlags));
        string[] unfair = ["MOVE_SAND_ATTACK", "MOVE_DOUBLE_TEAM", "MOVE_FISSURE", "MOVE_SMOKESCREEN"];
        Assert.DoesNotContain(data.Trainers.Where(t => !t.Key.Contains('@') && !t.NeverFought && Core.ChampionLab.Lab.GroupOf(data, lab, t) == Core.ChampionLab.TrainerGroup.Regular)
            .SelectMany(t => t.Party).SelectMany(p => p.Moves), unfair.Contains);

        // the redesign gives every boss a team that grows with the badges, and early ones nothing too strong for their level
        var (bosses, redesigned) = Core.ChampionLab.TeamBuilder.RedesignBosses(data, lab);
        Assert.True(redesigned >= 8);
        bosses!.Apply();
        var brock = data.Trainers.Single(t => t.Key == "TRAINER_LEADER_BROCK");
        Assert.Equal(3, brock.Party.Count);
        Assert.All(brock.Party, p => Assert.True(data.Species.Single(s => s.Key == p.Species).Stats.Total <= Core.ChampionLab.TeamBuilder.MaxStatTotal(14, Core.ChampionLab.Difficulty.Normal), p.Species));
        Assert.Equal(6, data.Trainers.Single(t => t.Key == "TRAINER_LEADER_GIOVANNI").Party.Count);
        Assert.Empty(data.Trainers.Where(t => !t.Key.Contains('@') && !t.NeverFought && t.Party.Any(p => p.Moves.Any(unfair.Contains)))
            .Select(t => $"{t.Key} ({Core.ChampionLab.Lab.GroupOf(data, lab, t)}): {string.Join(",", t.Party.SelectMany(p => p.Moves).Where(unfair.Contains))}"));
        Assert.Equal("Route 1", StoryOrder.AreaName("MAP_ROUTE1"));
        Assert.Equal("S.S. Anne Exterior", StoryOrder.AreaName("MAP_SSANNE_EXTERIOR"));
    }

    [Fact]
    public Task Real_emerald_wild_pokemon_fit_the_story_when_available() => Plan(Environment.GetEnvironmentVariable("POKESTUDIO_EXPANSION_DIR"), "emerald.txt");
}
