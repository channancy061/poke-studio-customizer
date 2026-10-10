using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.GameOptions;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Safety;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

public class AdaptiveSlotTests
{
    private const string Party = """
        === TRAINER_LEADER_X ===
        Name: X
        Class: Leader
        Pic: Leader Roxanne
        Gender: Female
        Music: Female
        Double Battle: No
        Party Size: 3
        Pool Pick Functions: Poke Studio Adaptive

        Geodude
        Level: 12

        Nosepass
        Level: 14
        Tags: Tag7

        Onix
        Level: 15

        Lileep
        Level: 14
        Tags: Tag7

        Anorith
        Level: 14
        Tags: Tag7

        """;

    private static string Species(string name) => TrainerParty.SpeciesConstant(name);

    [Fact]
    public void A_pool_with_our_pick_function_reads_as_team_plus_candidates_and_writes_back_the_same()
    {
        var text = Party.ReplaceLineEndings("\n");
        var (block, t) = TrainerParty.Parse(text, Species).Single();
        Assert.Null(t.LockedReason);
        Assert.Equal(["SPECIES_GEODUDE", "SPECIES_NOSEPASS", "SPECIES_ONIX"], t.Party.Select(p => p.Species));
        Assert.Equal(1, t.FlexIndex);
        Assert.Equal(["SPECIES_LILEEP", "SPECIES_ANORITH"], t.FlexCandidates.Select(p => p.Species));
        Assert.All(t.Party.Concat(t.FlexCandidates), p => Assert.Empty(p.Extra)); // the tag is ours, not shown as an extra field
        Assert.True(t.HasAdaptiveSlot);

        // an unrelated edit leaves the pool exactly as it was
        var renamed = t with { Name = "Y" };
        var after = Core.SourceCode.TextEdits.Apply(text, TrainerParty.EditsFor(text, block, t, renamed, new GameCatalog(), new Dictionary<string, string>()));
        Assert.Equal(text.Replace("Name: X", "Name: Y", StringComparison.Ordinal), after);

        // removing the slot drops the pool lines and the candidates
        var plain = t with { FlexIndex = null, FlexCandidates = [] };
        var removed = Core.SourceCode.TextEdits.Apply(text, TrainerParty.EditsFor(text, block, t, plain, new GameCatalog(), new Dictionary<string, string>()));
        Assert.DoesNotContain("Party Size", removed, StringComparison.Ordinal);
        Assert.DoesNotContain("Pool Pick Functions", removed, StringComparison.Ordinal);
        Assert.DoesNotContain("Lileep", removed, StringComparison.Ordinal);
        Assert.DoesNotContain("Tag7", removed, StringComparison.Ordinal);
        var (_, back) = TrainerParty.Parse(removed, Species).Single();
        Assert.False(back.HasAdaptiveSlot);
        Assert.Equal(3, back.Party.Count);

        // and adding it again restores the same text
        var (block2, again) = TrainerParty.Parse(removed, Species).Single();
        var readded = Core.SourceCode.TextEdits.Apply(removed, TrainerParty.EditsFor(removed, block2, again, again with { FlexIndex = 1, FlexCandidates = t.FlexCandidates }, new GameCatalog(), new Dictionary<string, string>()));
        var (_, roundTrip) = TrainerParty.Parse(readded, Species).Single();
        Assert.Equal(1, roundTrip.FlexIndex);
        Assert.Equal(["SPECIES_LILEEP", "SPECIES_ANORITH"], roundTrip.FlexCandidates.Select(p => p.Species));
        Assert.Contains("Pool Pick Functions: Poke Studio Adaptive", readded, StringComparison.Ordinal);
    }

    [Fact]
    public void Other_pools_stay_locked()
    {
        var other = Party.Replace("Pool Pick Functions: Poke Studio Adaptive", "Pool Rules: Weather Doubles", StringComparison.Ordinal);
        Assert.NotNull(TrainerParty.Parse(other, Species).Single().Trainer.LockedReason);
    }

    [Fact]
    public void The_helper_script_waits_after_choosing_a_pokemon_only_where_the_game_needs_it()
    {
        var ids = new GameOptionsWriter.Ids(new Dictionary<string, string>(), new Dictionary<string, string>());
        var o = new GameOptionsSettings { CenterHelper = new CenterHelperSettings { Enabled = true, IvPrice = 1500 } };
        var fireRed = GameOptionsWriter.BuildScript(o, ids, fireRed: true);
        var emerald = GameOptionsWriter.BuildScript(o, ids, fireRed: false);
        Assert.Contains("\tspecial ChoosePartyMon\n\twaitstate\n", fireRed, StringComparison.Ordinal);
        Assert.DoesNotContain("waitstate", emerald, StringComparison.Ordinal);
        Assert.Contains("\tcheckmoney 1500\n", emerald, StringComparison.Ordinal);
        Assert.Contains("\tcallnative PokeStudio_MaxIvs\n", emerald, StringComparison.Ordinal);
        Assert.Contains("\tspecial SetHiddenNature\n", emerald, StringComparison.Ordinal);
        Assert.Contains("PokeStudio_Text_NatureAdamant:\n\t.string \"Adamant +Atk -SpA$\"", emerald, StringComparison.Ordinal);
        Assert.Contains("Perfect IVs (¥1500)", emerald, StringComparison.Ordinal);
    }

    private static async Task RealProject(string? real)
    {
        if (string.IsNullOrEmpty(real) || !Directory.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var root = tmp.Combine("game");
        foreach (var dir in new[] { "include", "src" })
        {
            ProjectService.CopyDirectory(Path.Combine(real, dir), Path.Combine(root, dir));
        }

        File.Copy(Path.Combine(real, "Makefile"), Path.Combine(root, "Makefile"));
        var module = new ExpansionModule();
        var data = await module.LoadDataAsync(root, tmp.Combine("data"));
        var lab = new LabSettings();
        var (change, count) = AdaptiveSlots.SuggestForAll(data, lab);
        Assert.True(count >= 8, $"only {count} bosses got a slot");
        change!.Apply();
        Assert.DoesNotContain(data.Trainers, t => t.HasAdaptiveSlot && Lab.GroupOf(data, lab, t) == TrainerGroup.Champion);
        var slots = data.Trainers.Where(t => t.HasAdaptiveSlot).ToDictionary(t => t.Key, t => t.FlexCandidates.Select(c => c.Species).ToList());
        Assert.All(data.Trainers.Where(t => t.HasAdaptiveSlot), t =>
            Assert.DoesNotContain(t.FlexCandidates, c => t.Party.Any(p => p.Species == c.Species)));

        var ctx = new ModuleSaveContext(new SafeFileWriter(root, allowedFiles: module.ManagedFiles(root)), root, root);
        await module.SaveDataAsync(data, ctx);
        var party = await File.ReadAllTextAsync(Path.Combine(root, "src", "data", "trainers.party"));
        Assert.Contains("Pool Pick Functions: Poke Studio Adaptive", party, StringComparison.Ordinal);
        Assert.Contains("Tags: Tag7", party, StringComparison.Ordinal);

        var reloaded = await module.LoadDataAsync(root, tmp.Combine("data"));
        foreach (var (key, candidates) in slots)
        {
            var t = reloaded.Trainers.Single(x => x.Key == key);
            Assert.Null(t.LockedReason);
            Assert.Equal(candidates, t.FlexCandidates.Select(c => c.Species));
        }
    }

    [Fact]
    public Task Real_emerald_bosses_get_adaptive_slots_when_available() => RealProject(Environment.GetEnvironmentVariable("POKESTUDIO_EXPANSION_DIR"));

    [Fact]
    public Task Real_firered_bosses_get_adaptive_slots_when_available() => RealProject(Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR"));
}
