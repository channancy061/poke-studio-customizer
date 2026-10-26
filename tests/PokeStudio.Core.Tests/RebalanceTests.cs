using PokeStudio.Core.Models;
using PokeStudio.Core.Modernize;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

public class RebalanceTests
{
    [Theory]
    [InlineData(40, 5)]
    [InlineData(50, 5)]
    [InlineData(75, 21)]
    [InlineData(150, 61)]
    public void Added_moves_come_at_a_level_that_fits_their_power(int power, int level) => Assert.Equal(level, Rebalance.FairLevel(power));

    [Fact]
    public async Task Real_firered_rebalance_fixes_slaking_and_gives_ledian_its_punches_when_available()
    {
        var real = Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR");
        if (string.IsNullOrEmpty(real) || !Directory.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var data = await new ExpansionModule().LoadDataAsync(real, tmp.Combine("data"));
        var rows = Rebalance.Rows(data);
        var slakingRow = Assert.Single(rows, r => r.Change.Species == "SPECIES_SLAKING");
        Assert.Contains("Truant", slakingRow.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(rows, r => r.Change.Species == "SPECIES_REGIGIGAS");
        var before = data.Species.ToList();
        var learnsetsBefore = data.Learnsets.ToList();

        var change = Rebalance.Build(data, rows.Where(r => r.Change.Species is "SPECIES_SLAKING" or "SPECIES_GOLURK" or "SPECIES_LEDIAN").Select(r => r.Change))!;
        change.Apply();

        var slaking = data.Species.Single(s => s.Key == "SPECIES_SLAKING");
        Assert.Equal(new BaseStats(150, 140, 90, 75, 65, 80), slaking.Stats);
        Assert.Equal(["ABILITY_GUTS", "ABILITY_SCRAPPY", "ABILITY_MOXIE"], slaking.Abilities);
        Assert.DoesNotContain("ABILITY_KLUTZ", data.Species.Single(s => s.Key == "SPECIES_GOLURK").Abilities);
        var ledian = data.Species.Single(s => s.Key == "SPECIES_LEDIAN");
        Assert.Contains("TYPE_FIGHTING", ledian.Types);
        var punches = data.LearnsetFor(ledian)!;
        Assert.Contains(punches.LevelUp.Select(m => m.Move).Concat(punches.Teachable), m => m is "MOVE_FIRE_PUNCH" or "MOVE_ICE_PUNCH" or "MOVE_THUNDER_PUNCH");
        Assert.Equal(punches.LevelUp.OrderBy(m => m.Level).Select(m => m.Level), punches.LevelUp.Select(m => m.Level)); // still in level order

        change.Revert();
        Assert.Equal(before, data.Species);
        Assert.Equal(learnsetsBefore, data.Learnsets);

        // every Pokémon can learn every HM, and Undo takes it back
        var hms = Rebalance.HmsForEveryone(data)!;
        hms.Apply();
        Assert.Equal(0, Rebalance.MissingHms(data));
        Assert.Contains("MOVE_SURF", data.LearnsetFor(data.Species.Single(s => s.Key == "SPECIES_MAGIKARP"))!.Teachable);
        hms.Revert();
        Assert.Equal(learnsetsBefore, data.Learnsets);
    }
}
