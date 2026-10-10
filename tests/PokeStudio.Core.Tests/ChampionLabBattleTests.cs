using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Tests;

public class BattleMathTests
{
    [Theory]
    [InlineData("TYPE_ELECTRIC", new[] { "TYPE_WATER", "TYPE_GROUND" }, 0)]
    [InlineData("TYPE_ICE", new[] { "TYPE_DRAGON", "TYPE_FLYING" }, 4)]
    [InlineData("TYPE_FIRE", new[] { "TYPE_WATER" }, 0.5)]
    [InlineData("TYPE_FAIRY", new[] { "TYPE_DRAGON", "TYPE_DARK" }, 4)]
    [InlineData("TYPE_STELLAR", new[] { "TYPE_WATER" }, 1)] // unknown types are neutral
    public void Type_chart(string attack, string[] defender, double expected) =>
        Assert.Equal(expected, TypeChart.Multiplier(attack, defender));

    [Fact]
    public void Damage_follows_the_main_series_formula()
    {
        // level 50, 80 power, 100 Attack vs 100 Defense, neutral: base 37, rolls 31–37
        Assert.Equal((31, 37), Calc.Damage(50, 80, 100, 100, stab: false, effectiveness: 1));
        Assert.Equal((46, 55), Calc.Damage(50, 80, 100, 100, stab: true, effectiveness: 1));
        Assert.Equal((0, 0), Calc.Damage(50, 80, 100, 100, stab: true, effectiveness: 0));
        Assert.Equal((4, 3), Calc.HitsToKo((31, 37), 110));
    }

    [Fact]
    public void Stats_use_level_ivs_evs_and_nature()
    {
        // a base-100 stat at level 50 with 31 IVs, 252 EVs and a boosting nature is 167 (the usual competitive number)
        Assert.Equal(167, Calc.Stat(100, 50, 31, 252, 1.1));
        Assert.Equal(207, Calc.Hp(100, 50, 31, 252));
        Assert.Equal(175, Calc.Hp(100, 50, 31, 0));
        Assert.Equal(1.1, Natures.Factor("NATURE_JOLLY", 5));
        Assert.Equal(0.9, Natures.Factor("NATURE_JOLLY", 3));
        Assert.Equal("NATURE_MODEST", Natures.For(3, 1));
    }
}

public class TeamBuilderTests
{
    private static async Task<(PokeStudio.Modules.Expansion.ExpansionModule Module, GameData Data, ModuleSaveContext Ctx, string Root)> Load(TempDir tmp)
    {
        var root = ExpansionMovesTests.CopyFixture(tmp);
        var (m, d, c) = await ExpansionMovesTests.LoadAsync(root);
        return (m, d, c, root);
    }

    [Fact]
    public async Task Suggestions_follow_the_theme_and_explain_every_pick()
    {
        using var tmp = new TempDir();
        var (_, data, _, _) = await Load(tmp);
        var lab = new LabSettings { Themes = new Dictionary<string, TypeTheme> { ["TRAINER_LEADER_FERN"] = new("TYPE_GRASS") } };
        var fern = data.Trainers.Single(t => t.Key == "TRAINER_LEADER_FERN");

        var team = TeamBuilder.Suggest(data, lab, fern, new TeamBuilder.Options(Difficulty.Hard, TeamSize: 3, Level: 40));

        Assert.NotEmpty(team.Members);
        Assert.All(team.Members, m => Assert.Contains("TYPE_GRASS", data.Species.Single(s => s.Key == m.Pokemon.Species).Types));
        Assert.Equal(team.Members.Count, team.Members.Select(m => m.Pokemon.Species).Distinct().Count()); // Species Clause
        Assert.All(team.Members, m => Assert.NotEmpty(m.Reasons));
        Assert.All(team.Members, m => Assert.Equal(new BaseStats(31, 31, 31, 31, 31, 31), m.Pokemon.Ivs)); // Hard: perfect IVs
        Assert.DoesNotContain(team.Members, m => m.Pokemon.Species == "SPECIES_SPROUTLE"); // at level 40 it would have evolved
        Assert.StartsWith("Ace", team.Members[^1].Role, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Legendary_rules_are_respected_while_building()
    {
        using var tmp = new TempDir();
        var (_, data, _, _) = await Load(tmp);
        var i = data.Species.FindIndex(s => s.Key == "SPECIES_BLOOMTLE");
        data.Species[i] = data.Species[i] with { Tags = SpeciesTags.RestrictedLegendary };
        var fern = data.Trainers.Single(t => t.Key == "TRAINER_LEADER_FERN");

        var team = TeamBuilder.Suggest(data, new LabSettings(), fern, new TeamBuilder.Options(Difficulty.Normal, TeamSize: 3, Level: 40));

        Assert.DoesNotContain(team.Members, m => m.Pokemon.Species == "SPECIES_BLOOMTLE"); // gyms allow no restricted legendaries
    }

    [Fact]
    public async Task Report_card_scores_and_suggests_fixes()
    {
        using var tmp = new TempDir();
        var (_, data, _, _) = await Load(tmp);
        var fern = data.Trainers.Single(t => t.Key == "TRAINER_LEADER_FERN");

        var report = ReportCard.For(data, new LabSettings(), fern);

        Assert.InRange(report.Score, 1, 10);
        Assert.Equal(2, report.Members.Count);
        Assert.Contains("Fire", report.SharedWeaknesses); // both are Grass
        Assert.Contains(report.Fixes, f => f.Contains("weak to Fire", StringComparison.Ordinal));
        Assert.True(report.ThreatCount > 0);
    }

    [Fact]
    public async Task Difficulty_versions_are_generated_saved_and_read_back()
    {
        using var tmp = new TempDir();
        var (module, data, ctx, root) = await Load(tmp);
        var lab = new LabSettings();

        var change = DifficultyVersions.Generate(data, lab, ["TRAINER_LEADER_FERN"], [Difficulty.Easy, Difficulty.Hard, Difficulty.ExtraHard]);
        Assert.NotNull(change);
        var undo = new UndoRedoStack();
        undo.Execute(change);

        var easy = data.Trainers.Single(t => t.Key == "TRAINER_LEADER_FERN@Easy");
        Assert.All(easy.Party, p => Assert.Equal(new BaseStats(0, 0, 0, 0, 0, 0), p.Ivs));
        Assert.True(easy.Party[0].Level < data.Trainers.Single(t => t.Key == "TRAINER_LEADER_FERN").Party[0].Level);
        var extra = data.Trainers.Single(t => t.Key == "TRAINER_LEADER_FERN@Extra Hard");
        Assert.Contains("AI_FLAG_DOUBLE_ACE_POKEMON", extra.AiFlags); // Fern fights doubles

        await module.SaveDataAsync(data, ctx);
        var text = ExpansionMovesTests.Read(root, "src/data/trainers.party");
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Count(text, "=== TRAINER_LEADER_FERN ==="));
        Assert.Contains("Difficulty: Extra Hard", text, StringComparison.Ordinal);
        Assert.Contains("DIFFICULTY_HARD,\n    DIFFICULTY_EXTRA_HARD, // added by Poke Studio", ExpansionMovesTests.Read(root, "include/constants/difficulty.h"), StringComparison.Ordinal);

        var (module2, again, ctx2) = await ExpansionMovesTests.LoadAsync(root);
        Assert.Contains(again.Trainers, t => t.Key == "TRAINER_LEADER_FERN@Hard");
        Assert.Equal(Difficulty.ExtraHard, Lab.DifficultyOf(again.Trainers.Single(t => t.Key == "TRAINER_LEADER_FERN@Extra Hard")));

        // removing a version deletes its block; the rest of the file is untouched
        again.Trainers.RemoveAll(t => t.Key == "TRAINER_LEADER_FERN@Easy");
        await module2.SaveDataAsync(again, ctx2);
        var after = ExpansionMovesTests.Read(root, "src/data/trainers.party");
        Assert.DoesNotContain("Difficulty: Easy", after, StringComparison.Ordinal);
        Assert.Contains("Difficulty: Hard", after, StringComparison.Ordinal);
        Assert.Contains("=== TRAINER_TWINS_POOL ===", after, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_settings_are_read_and_written()
    {
        using var tmp = new TempDir();
        var (module, data, ctx, root) = await Load(tmp);

        var loses = data.AiSettings.Single(s => s.Key == "SHOULD_SWITCH_LOSES_1V1_PERCENTAGE");
        Assert.Equal(("Switch out when: loses 1v1", 0, true), (loses.Label, loses.Value, loses.IsPercentage));
        Assert.Contains(data.AiSettings, s => s.Key == "STAY_IN_STATS_RAISED" && !s.IsPercentage);

        data.AiSettings[data.AiSettings.IndexOf(loses)] = loses with { Value = 75 };
        await module.SaveDataAsync(data, ctx);
        Assert.Contains("#define SHOULD_SWITCH_LOSES_1V1_PERCENTAGE                          75 // Keeping", ExpansionMovesTests.Read(root, "include/config/ai.h"), StringComparison.Ordinal);
    }
}
