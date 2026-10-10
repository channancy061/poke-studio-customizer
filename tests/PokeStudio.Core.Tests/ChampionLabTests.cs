using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Validation;

namespace PokeStudio.Core.Tests;

public class ChampionLabTests
{
    private static async Task<(PokeStudio.Modules.Expansion.ExpansionModule Module, GameData Data)> Load(TempDir tmp)
    {
        var (m, d, _) = await ExpansionMovesTests.LoadAsync(ExpansionMovesTests.CopyFixture(tmp));
        return (m, d);
    }

    private static void Tag(GameData d, string key, SpeciesTags tags)
    {
        var i = d.Species.FindIndex(s => s.Key == key);
        d.Species[i] = d.Species[i] with { Tags = tags };
    }

    private static List<ValidationIssue> Check(GameData d, LabSettings lab, StarterSet? starters = null) => LabChecks.Run(d, lab, starters).ToList();

    [Fact]
    public void Defaults_match_the_agreed_rules()
    {
        var lab = new LabSettings();
        var normal = lab.RulesFor(TrainerGroup.Champion, Difficulty.Normal);
        var hard = lab.RulesFor(TrainerGroup.Champion, Difficulty.Hard);
        var extra = lab.RulesFor(TrainerGroup.Champion, Difficulty.ExtraHard);

        Assert.Equal((0, 1), (normal.MaxRestricted, normal.MaxSubLegendary));
        Assert.Equal((1, 2, true), (hard.MaxRestricted, hard.MaxSubLegendary, hard.RestrictedOrSub));
        Assert.Equal(2, extra.MaxRestricted);
        Assert.Equal(0, lab.RulesFor(TrainerGroup.EarlyGym, Difficulty.ExtraHard).MaxSubLegendary);
        Assert.Equal(1, lab.RulesFor(TrainerGroup.LateGym, Difficulty.ExtraHard).MaxSubLegendary);
        Assert.All(lab.Rules, r => Assert.True(r.SpeciesClause));
        Assert.All(lab.Rules, r => Assert.Equal(1, r.MaxMega));
        Assert.Equal(0, lab.RulesFor(TrainerGroup.Champion, Difficulty.Normal).MaxMythical); // mythicals off unless allowed
    }

    [Fact]
    public async Task Groups_are_guessed_and_can_be_overridden()
    {
        using var tmp = new TempDir();
        var (_, data) = await Load(tmp);
        var lab = new LabSettings();
        TrainerGroup G(string key, LabSettings l) => Lab.GroupOf(data, l, data.Trainers.Single(t => t.Key == key));

        Assert.Equal(TrainerGroup.EarlyGym, G("TRAINER_LEADER_FERN", lab));
        Assert.Equal(TrainerGroup.Regular, G("TRAINER_RICK_1", lab));
        var moved = lab with { GroupOverrides = new Dictionary<string, TrainerGroup> { ["TRAINER_RICK_1"] = TrainerGroup.Rival } };
        Assert.Equal(TrainerGroup.Rival, G("TRAINER_RICK_1", moved));
        Assert.Equal("TYPE_GRASS", Lab.SuggestTheme(data, data.Trainers.Single(t => t.Key == "TRAINER_LEADER_FERN")));
    }

    [Fact]
    public async Task Type_themes_flag_off_type_members_and_repeated_type_combos()
    {
        using var tmp = new TempDir();
        var (_, data) = await Load(tmp);
        var themed = new LabSettings { Themes = new Dictionary<string, TypeTheme> { ["TRAINER_LEADER_FERN"] = new("TYPE_GRASS") } };
        Assert.DoesNotContain(Check(data, themed), i => i.TargetKey == "TRAINER_LEADER_FERN"); // Bloomtle and Sproutle (Frost) are both Grass

        var fire = themed with { Themes = new Dictionary<string, TypeTheme> { ["TRAINER_LEADER_FERN"] = new("TYPE_FIRE") } };
        var issue = Assert.Single(Check(data, fire), i => i.TargetKey == "TRAINER_LEADER_FERN");
        Assert.Contains("Fire-type specialist", issue.Message, StringComparison.Ordinal);
        Assert.Contains("Bloomtle, Sproutle (Frost) aren't Fire type", issue.Message, StringComparison.Ordinal);

        var fern = data.Trainers.Single(t => t.Key == "TRAINER_LEADER_FERN");
        data.Trainers[data.Trainers.IndexOf(fern)] = fern with { Party = [fern.Party[0], fern.Party[0] with { Level = 20 }] };
        var issues = Check(data, themed).Where(i => i.TargetKey == "TRAINER_LEADER_FERN").ToList();
        Assert.Contains(issues, i => i.Message.Contains("Species Clause", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("more than one Grass", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Legendary_limits_depend_on_group_and_difficulty()
    {
        using var tmp = new TempDir();
        var (_, data) = await Load(tmp);
        Tag(data, "SPECIES_BLOOMTLE", SpeciesTags.RestrictedLegendary);
        var lab = new LabSettings();

        var gym = Assert.Single(Check(data, lab), i => i.TargetKey == "TRAINER_LEADER_FERN");
        Assert.Contains("has 1 restricted legendary Pokémon; Gyms 1–5, Normal allows 0.", gym.Message, StringComparison.Ordinal);

        // the same team as a Hard Champion: one restricted is fine, but then no sub-legendaries
        var champ = lab with { GroupOverrides = new Dictionary<string, TrainerGroup> { ["TRAINER_LEADER_FERN"] = TrainerGroup.Champion } };
        var fern = data.Trainers.Single(t => t.Key == "TRAINER_LEADER_FERN");
        data.Trainers.Add(fern with { Key = "TRAINER_LEADER_FERN@Hard", Party = [.. fern.Party, fern.Party[0] with { Species = "SPECIES_EMBERPUP", HeldItem = "ITEM_ORAN_BERRY" }] });
        Tag(data, "SPECIES_EMBERPUP", SpeciesTags.SubLegendary);
        var hard = Check(data, champ).Where(i => i.TargetKey == "TRAINER_LEADER_FERN@Hard").ToList();
        Assert.Contains(hard, i => i.Message.Contains("one restricted or 2 sub-legendaries", StringComparison.Ordinal));
        Assert.Contains(hard, i => i.Message.Contains("Item Clause", StringComparison.Ordinal)); // two Oran Berries on Hard
    }

    [Fact]
    public async Task Story_locks_and_fair_play()
    {
        using var tmp = new TempDir();
        var (_, data) = await Load(tmp);
        var lab = new LabSettings { StoryLocks = new Dictionary<string, int> { ["SPECIES_BLOOMTLE"] = 3 } }; // not before the Champion

        var locked = Check(data, lab).Where(i => i.Message.Contains("when the story introduces it", StringComparison.Ordinal)).ToList();
        Assert.Contains(locked, i => i.TargetKey == "TRAINER_LEADER_FERN");

        Tag(data, "SPECIES_SPROUTLE_FROST", SpeciesTags.SubLegendary);
        var fair = lab with
        {
            StoryLocks = new Dictionary<string, int>(),
            Rules = [.. lab.Rules.Where(r => r.Group != TrainerGroup.EarlyGym), new GroupRules { Group = TrainerGroup.EarlyGym, Difficulty = Difficulty.Normal, MaxSubLegendary = 1 }],
        };
        var issues = Check(data, fair).Where(i => i.TargetKey == "TRAINER_LEADER_FERN").ToList();
        Assert.DoesNotContain(issues, i => i.Message.Contains("can catch only", StringComparison.Ordinal)); // Sproutle (Frost) lives in Frost Lake, reachable by then
        var lake = data.Encounters.Single(e => e.Key == "gFrostLake");
        data.Encounters[data.Encounters.IndexOf(lake)] = lake with { Methods = [.. lake.Methods.Select(m => m with { Slots = m.Slots.Select(s => s with { MinLevel = 50, MaxLevel = 50 }).ToList() })] };
        Assert.Contains(Check(data, fair), i => i.TargetKey == "TRAINER_LEADER_FERN" && i.Message.Contains("can catch only 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Scouting_grows_with_each_milestone()
    {
        using var tmp = new TempDir();
        var (_, data) = await Load(tmp);

        var scout = Scouting.Run(data);

        Assert.Equal(["Gym 1", "Gym 2", "Gym 3", "Champion"], scout.Select(s => s.Milestone));
        Assert.DoesNotContain("SPECIES_SPROUTLE_FROST", scout[0].Species); // Frost Lake goes up to 18
        Assert.Contains("SPECIES_SPROUTLE_FROST", scout[1].Species);
        Assert.Contains("TYPE_ICE", scout[1].Types);
        Assert.Contains("SPECIES_BLOOMTLE", scout[0].Species);
    }

    [Fact]
    public async Task Legendary_labels_are_written_back_and_removed()
    {
        using var tmp = new TempDir();
        var root = ExpansionMovesTests.CopyFixture(tmp);
        var (module, data, ctx) = await ExpansionMovesTests.LoadAsync(root);
        var file = "src/data/pokemon/species_info/gen_1_families.h";

        Tag(data, "SPECIES_BLOOMTLE", SpeciesTags.Mythical);
        await module.SaveDataAsync(data, ctx);
        Assert.Contains(".isMythical = TRUE,", ExpansionMovesTests.Read(root, file), StringComparison.Ordinal);

        var (module2, again, ctx2) = await ExpansionMovesTests.LoadAsync(root);
        Assert.Equal(SpeciesTags.Mythical, again.Species.Single(s => s.Key == "SPECIES_BLOOMTLE").Tags);
        Tag(again, "SPECIES_BLOOMTLE", SpeciesTags.None);
        await module2.SaveDataAsync(again, ctx2);
        Assert.DoesNotContain("isMythical", ExpansionMovesTests.Read(root, file), StringComparison.Ordinal);
    }
}

/// <summary>The rival rule on small invented data, in both conventions the real projects use.</summary>
public class RivalRuleTests
{
    private static GameData World(bool suffixIsPlayersPick)
    {
        var d = new GameData();
        void S(string key, string type) => d.Species.Add(new Species { Id = d.Species.Count, Key = key, Name = key[8..], Stats = new BaseStats(50, 50, 50, 50, 50, 50), Types = [type], IsEnabled = true });
        S("SPECIES_LEAFY", "TYPE_GRASS");
        S("SPECIES_LEAFIER", "TYPE_GRASS");
        S("SPECIES_FLAMY", "TYPE_FIRE");
        S("SPECIES_DROPPY", "TYPE_WATER");
        d.Evolutions.Add(new EvolutionSet { FromSpecies = "SPECIES_LEAFY", Evolutions = [new Evolution("EVO_LEVEL", "16", "SPECIES_LEAFIER")] });
        d.Catalog.TrainerClasses.Add(new CatalogEntry("TRAINER_CLASS_RIVAL", "Rival"));
        Trainer R(string suffix, string species, int level) => new()
        {
            Id = d.Trainers.Count, Key = "TRAINER_RIVAL_ROUTE_1_" + suffix, Name = "BLUE", TrainerClass = "TRAINER_CLASS_RIVAL",
            Party = [new TrainerPokemon { Species = species, Level = level }],
        };

        if (suffixIsPlayersPick)
        {
            // player picked Droppy (water) → rival has Leafy (grass beats water), like Emerald's May
            d.Trainers.Add(R("DROPPY", "SPECIES_LEAFY", 5));
            d.Trainers.Add(R("LEAFY", "SPECIES_FLAMY", 5));
        }
        else
        {
            // the suffix is the rival's own starter, like FireRed
            d.Trainers.Add(R("LEAFY", "SPECIES_LEAFY", 5));
            d.Trainers.Add(R("FLAMY", "SPECIES_FLAMY", 5));
        }

        return d;
    }

    private static readonly StarterSet Starters3 = new(["SPECIES_LEAFY", "SPECIES_FLAMY", "SPECIES_DROPPY"]);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Original_teams_pass_in_either_convention(bool suffixIsPlayersPick) =>
        Assert.Empty(LabChecks.RivalChecks(World(suffixIsPlayersPick), new LabSettings(), Starters3));

    [Fact]
    public void A_rival_without_the_starter_or_with_it_unevolved_is_flagged()
    {
        var d = World(suffixIsPlayersPick: false);
        d.Trainers.Add(d.Trainers[0] with { Key = "TRAINER_RIVAL_ROUTE_9_LEAFY", Party = [new TrainerPokemon { Species = "SPECIES_DROPPY", Level = 30 }] });
        d.Trainers.Add(d.Trainers[0] with { Key = "TRAINER_RIVAL_ROUTE_12_LEAFY", Party = [new TrainerPokemon { Species = "SPECIES_LEAFY", Level = 25 }] });

        var issues = LabChecks.RivalChecks(d, new LabSettings(), Starters3).ToList();

        Assert.Contains(issues, i => i.TargetKey == "TRAINER_RIVAL_ROUTE_9_LEAFY" && i.Message.Contains("doesn't have the LEAFY line, their starter", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.TargetKey == "TRAINER_RIVAL_ROUTE_12_LEAFY" && i.Message.Contains("would have evolved into LEAFIER", StringComparison.Ordinal));
        Assert.Equal("SPECIES_FLAMY", Core.ChampionLab.Starters.Counter(d, Starters3, "SPECIES_LEAFY"));
    }
}
