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
    public Task Real_emerald_wild_pokemon_fit_the_story_when_available() => Plan(Environment.GetEnvironmentVariable("POKESTUDIO_EXPANSION_DIR"), "emerald.txt");
}
