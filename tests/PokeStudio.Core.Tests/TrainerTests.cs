using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Trainers;
using PokeStudio.Core.Validation;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

public class ExpansionTrainerTests
{
    private const string PartyFile = "src/data/trainers.party";

    private static Trainer Get(GameData d, string key) => d.Trainers.Single(t => t.Key == key);

    private static void Set(GameData d, Trainer t) => d.Trainers[d.Trainers.FindIndex(x => x.Key == t.Key)] = t;

    private static List<string> ChangedLines(string before, string after)
    {
        var a = before.Split('\n');
        var b = after.Split('\n');
        return a.Length != b.Length ? ["<line count changed>"] : a.Zip(b).Where(p => p.First != p.Second).Select(p => p.Second.Trim()).ToList();
    }

    [Fact]
    public async Task Reads_trainers_and_their_teams()
    {
        using var tmp = new TempDir();
        var (module, data, _) = await ExpansionMovesTests.LoadAsync(ExpansionMovesTests.CopyFixture(tmp));

        Assert.True(module.Capabilities.HasFlag(ModuleCapabilities.Trainers));
        Assert.Equal(["TRAINER_NONE", "TRAINER_RICK_1", "TRAINER_MIA", "TRAINER_LEADER_FERN", "TRAINER_TWINS_POOL"], data.Trainers.Select(t => t.Key)); // the one inside a comment is ignored

        var mia = Get(data, "TRAINER_MIA");
        Assert.Equal("MIA", mia.Name);
        Assert.Equal("TRAINER_CLASS_LASS", mia.TrainerClass);
        Assert.Equal("TRAINER_ENCOUNTER_MUSIC_FEMALE", mia.Music);
        Assert.Equal(["ITEM_ORAN_BERRY"], mia.Items);
        Assert.Equal(["AI_FLAG_BASIC_TRAINER"], mia.AiFlags);
        Assert.Equal(2, mia.Party.Count);
        var bud = mia.Party[0];
        Assert.Equal(("SPECIES_SPROUTLE", "Bud", "F", "ITEM_MIRACLE_SEED"), (bud.Species, bud.Nickname, bud.Gender, bud.HeldItem));
        Assert.Equal("ABILITY_CHLOROPHYLL", bud.Ability);
        Assert.Equal("NATURE_MODEST", bud.Nature);
        Assert.Equal(new BaseStats(4, 0, 0, 252, 0, 0), bud.Evs);
        Assert.Equal(["MOVE_VINE_LASH", "MOVE_GROWL"], bud.Moves);
        Assert.Null(mia.Party[1].Ivs); // not set = the game's default

        var fern = Get(data, "TRAINER_LEADER_FERN");
        Assert.Equal(BattleType.Double, fern.Battle);
        Assert.Equal(4, fern.AiFlags.Count);
        Assert.True(fern.Party[0].Shiny);
        Assert.Equal("TYPE_FAIRY", fern.Party[0].TeraType);
        Assert.Equal("SPECIES_SPROUTLE_FROST", fern.Party[1].Species);

        Assert.NotNull(Get(data, "TRAINER_TWINS_POOL").LockedReason);
        Assert.Contains(data.Catalog.AiFlags, f => f.Key == "AI_FLAG_SMART_TRAINER" && f.Description!.StartsWith("Preset:", StringComparison.Ordinal));
        Assert.Equal("Leader Fern", data.Catalog.NameOf("TRAINER_PIC_LEADER_FERN"));
        Assert.DoesNotContain(data.Catalog.TrainerPics, p => p.Key == "TRAINER_PIC_BACK_BRENDAN");
    }

    [Fact]
    public async Task Saving_without_changes_keeps_the_file_identical()
    {
        using var tmp = new TempDir();
        var root = ExpansionMovesTests.CopyFixture(tmp);
        var (module, data, ctx) = await ExpansionMovesTests.LoadAsync(root);
        var before = ExpansionMovesTests.Read(root, PartyFile);

        await module.SaveDataAsync(data, ctx);

        Assert.Equal(before, ExpansionMovesTests.Read(root, PartyFile));
    }

    [Fact]
    public async Task Header_and_level_edits_change_only_their_lines()
    {
        using var tmp = new TempDir();
        var root = ExpansionMovesTests.CopyFixture(tmp);
        var (module, data, ctx) = await ExpansionMovesTests.LoadAsync(root);
        var before = ExpansionMovesTests.Read(root, PartyFile);

        var rick = Get(data, "TRAINER_RICK_1");
        Set(data, rick with
        {
            Name = "RICKY",
            AiFlags = ["AI_FLAG_SMART_TRAINER", "AI_FLAG_PREDICTION"],
            Party = [rick.Party[0], rick.Party[1] with { Level = 15 }],
        });
        await module.SaveDataAsync(data, ctx);

        // The second Pokémon is rewritten in the standard layout; the first stays exactly as it was.
        Assert.Equal(["Name: RICKY", "AI: Smart Trainer / Prediction", "Level: 15"], ChangedLines(before, ExpansionMovesTests.Read(root, PartyFile)));
    }

    [Fact]
    public async Task Team_changes_keep_comments_and_untouched_pokemon()
    {
        using var tmp = new TempDir();
        var root = ExpansionMovesTests.CopyFixture(tmp);
        var (module, data, ctx) = await ExpansionMovesTests.LoadAsync(root);

        var mia = Get(data, "TRAINER_MIA");
        var added = new TrainerPokemon { Species = "SPECIES_EMBERPUP", Level = 17, Moves = ["MOVE_EMBER_SNAP"], HeldItem = "ITEM_ORAN_BERRY", Nature = "NATURE_TIMID" };
        Set(data, mia with { Items = [], Battle = BattleType.Double, Party = [mia.Party[0], mia.Party[1], added] });
        await module.SaveDataAsync(data, ctx);

        var text = ExpansionMovesTests.Read(root, PartyFile);
        Assert.DoesNotContain("Items: Oran Berry\nDouble Battle: No\nAI: Basic Trainer", text, StringComparison.Ordinal);
        Assert.Contains("Double Battle: Yes\nAI: Basic Trainer\n\nBud (Sproutle) (F) @ Miracle Seed", text, StringComparison.Ordinal);
        Assert.Contains("/*Her second Pokémon changes in the remake.*/\n\nBloomtle\nLevel: 16\n\nEmberpup @ Oran Berry\nLevel: 17\nNature: Timid\n- Ember Snap\n\n=== TRAINER_LEADER_FERN", text, StringComparison.Ordinal);

        var (_, again, _) = await ExpansionMovesTests.LoadAsync(root);
        var reread = Get(again, "TRAINER_MIA");
        Assert.Empty(reread.Items);
        Assert.Equal(3, reread.Party.Count);
        Assert.True(reread.Party[2].Equivalent(added));
    }

    [Fact]
    public async Task Removing_pokemon_and_writing_constants_when_names_dont_round_trip()
    {
        using var tmp = new TempDir();
        var root = ExpansionMovesTests.CopyFixture(tmp);
        var (module, data, ctx) = await ExpansionMovesTests.LoadAsync(root);

        var fern = Get(data, "TRAINER_LEADER_FERN");
        Set(data, fern with { Party = [fern.Party[1] with { Nickname = null, Level = 30 }] });
        await module.SaveDataAsync(data, ctx);

        var text = ExpansionMovesTests.Read(root, PartyFile);
        Assert.Contains("AI: Check Bad Move / Try To Faint / Check Viability / Ace Pokemon\n\nSPECIES_SPROUTLE_FROST\nLevel: 30\n- Vine Lash\n\n=== TRAINER_TWINS_POOL", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Trainer_checks_explain_problems()
    {
        using var tmp = new TempDir();
        var (module, data, _) = await ExpansionMovesTests.LoadAsync(ExpansionMovesTests.CopyFixture(tmp));
        var rick = Get(data, "TRAINER_RICK_1");
        Set(data, rick with
        {
            Name = "RICKASTLEYX",
            Battle = BattleType.Double,
            Party = [rick.Party[0] with { Level = 120, Moves = ["MOVE_FROST_BREATH"], Ability = "ABILITY_BLAZE", Evs = new BaseStats(252, 252, 252, 0, 0, 0) }],
        });

        var issues = GameValidator.Run(data, module).Where(i => i.Area == "Trainer" && i.TargetKey == "TRAINER_RICK_1").ToList();

        Assert.Contains(issues, i => i.Message.Contains("allows 10", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Message.Contains("double battle but has only one", StringComparison.Ordinal) && i.Fix is not null);
        Assert.Contains(issues, i => i.Message.Contains("level 120", StringComparison.Ordinal) && i.Fix is not null);
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("can't learn normally", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("can't normally have", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("756 EVs", StringComparison.Ordinal));

        var undo = new UndoRedoStack();
        undo.Execute(issues.First(i => i.Message.Contains("level 120", StringComparison.Ordinal)).Fix!(data));
        Assert.Equal(100, Get(data, "TRAINER_RICK_1").Party[0].Level);
    }

    [Theory]
    [InlineData("Mr. Mime", "SPECIES_MR_MIME")]
    [InlineData("Nidoran♀", "SPECIES_NIDORAN_F")]
    [InlineData("Farfetch’d", "SPECIES_FARFETCHD")]
    [InlineData("Flabébé", "SPECIES_FLABEBE")]
    [InlineData("Type: Null", "SPECIES_TYPE_NULL")]
    public void Species_names_convert_like_the_projects_tool(string name, string constant) =>
        Assert.Equal(constant, TrainerParty.SpeciesConstant(name));

    [Theory]
    [InlineData("MOVE", "Will-O-Wisp", "MOVE_WILL_O_WISP")]
    [InlineData("ITEM", "King's Rock", "ITEM_KINGS_ROCK")]
    [InlineData("TRAINER_PIC", "Aqua Grunt M", "TRAINER_PIC_AQUA_GRUNT_M")]
    [InlineData("ITEM", "ITEM_ORAN_BERRY", "ITEM_ORAN_BERRY")]
    public void Names_convert_like_the_projects_tool(string prefix, string name, string constant) =>
        Assert.Equal(constant, TrainerParty.ToConstant(prefix, name));
}

public class TrainerToolsTests
{
    [Theory]
    [InlineData(10, LevelChangeMode.AddLevels, 5, 15)]
    [InlineData(98, LevelChangeMode.AddLevels, 5, 100)]
    [InlineData(3, LevelChangeMode.AddLevels, -5, 1)]
    [InlineData(20, LevelChangeMode.Percent, 125, 25)]
    [InlineData(15, LevelChangeMode.Percent, 110, 17)] // 16.5 rounds up
    public void New_levels_are_clamped_and_rounded(int level, LevelChangeMode mode, int amount, int expected) =>
        Assert.Equal(expected, TrainerTools.NewLevel(level, mode, amount, 100));

    [Fact]
    public async Task Level_changes_apply_to_chosen_trainers_as_one_undo_step()
    {
        using var tmp = new TempDir();
        var (module, data, _) = await ExpansionMovesTests.LoadAsync(ExpansionMovesTests.CopyFixture(tmp));

        var change = TrainerTools.ChangeLevels(data, ["TRAINER_RICK_1", "TRAINER_MIA", "TRAINER_TWINS_POOL"], LevelChangeMode.Percent, 150, module.Limits);
        Assert.NotNull(change);
        var undo = new UndoRedoStack();
        undo.Execute(change);

        Assert.Equal("2 trainers · levels × 150%", change.Description); // the pool trainer is locked
        Assert.Equal([18, 20], data.Trainers.Single(t => t.Key == "TRAINER_RICK_1").Party.Select(p => p.Level));
        Assert.Equal(10, data.Trainers.Single(t => t.Key == "TRAINER_TWINS_POOL").Party[0].Level);
        undo.Undo();
        Assert.Equal([12, 13], data.Trainers.Single(t => t.Key == "TRAINER_RICK_1").Party.Select(p => p.Level));
    }

    [Fact]
    public async Task Ai_presets_use_the_games_flags_and_are_recognised()
    {
        using var tmp = new TempDir();
        var (_, data, _) = await ExpansionMovesTests.LoadAsync(ExpansionMovesTests.CopyFixture(tmp));

        var presets = AiPresets.For(data.Catalog);
        Assert.Equal(["none", "beginner", "basic", "smart", "expert", "champion", "double-ace"], presets.Select(p => p.Id));
        Assert.Equal("basic", AiPresets.Match(data.Trainers.Single(t => t.Key == "TRAINER_MIA").AiFlags, presets)!.Id);
        Assert.Null(AiPresets.Match(data.Trainers.Single(t => t.Key == "TRAINER_LEADER_FERN").AiFlags, presets)); // custom

        var expert = presets.Single(p => p.Id == "expert");
        new UndoRedoStack().Execute(TrainerTools.SetAi(data, data.Trainers.Select(t => t.Key), expert.Flags, expert.Name)!);
        Assert.All(data.Trainers.Where(t => t.LockedReason is null), t => Assert.Equal(expert.Flags, t.AiFlags));
    }
}
