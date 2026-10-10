using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Progression;
using PokeStudio.Core.Validation;

namespace PokeStudio.Core.Tests;

public class WorldDataTests
{
    private static async Task<(PokeStudio.Modules.Expansion.ExpansionModule Module, GameData Data, ModuleSaveContext Ctx, string Root)> Load(TempDir tmp)
    {
        var root = ExpansionMovesTests.CopyFixture(tmp);
        var (m, d, c) = await ExpansionMovesTests.LoadAsync(root);
        return (m, d, c, root);
    }

    private static List<string> Diff(string a, string b) =>
        a.Split('\n').Zip(b.Split('\n')).Where(p => p.First != p.Second).Select(p => p.Second.Trim()).ToList();

    [Fact]
    public async Task Reads_encounters_items_marts_text_and_caps()
    {
        using var tmp = new TempDir();
        var (module, data, _, _) = await Load(tmp);

        Assert.True(module.Capabilities.HasFlag(ModuleCapabilities.Encounters));
        var meadow = data.Encounters.Single(e => e.Key == "gMeadowRoute");
        Assert.Equal("MAP_MEADOW_ROUTE", meadow.Map);
        var land = Assert.Single(meadow.Methods);
        Assert.Equal(("land_mons", 20, 4), (land.Type, land.Rate, land.Slots.Count));
        Assert.Equal(new EncounterSlot("SPECIES_EMBERPUP", 3, 4), land.Slots[1]);
        Assert.Equal([40, 30, 20, 10], data.EncounterChances.Single(c => c.Type == "land_mons").Percent);
        Assert.Equal([0, 1], data.EncounterChances.Single(c => c.Type == "fishing_mons").Groups["old_rod"]);

        var sun = data.Items.Single(i => i.Key == "ITEM_SUN_STONE");
        Assert.Equal(("Sun Stone", 3000), (sun.Name, sun.Price));
        Assert.Contains("Price", sun.SettingDependentFields.Keys);
        Assert.Equal(80, data.Items.Single(i => i.Key == "ITEM_ORAN_BERRY").Price); // #if branch for I_PRICE
        Assert.Equal(20, data.Items.Single(i => i.Key == "ITEM_MIRACLE_SEED").EffectAmount);

        var mart = Assert.Single(data.Marts);
        Assert.Equal(("Meadow Town Mart", 2), (mart.Place, mart.Items.Count));

        var welcome = data.Texts.Single(t => t.Key == "MeadowTown_Mart_Text_Welcome");
        Assert.Equal("Welcome to the meadow!\\nWe sell berries and stones.\\pHave a nice day!", welcome.Text);
        Assert.Contains(data.Texts, t => t.Key == "gText_MeadowSign" && t.Place == "Sign Posts");

        Assert.Equal([12, 18, 25, 40], data.Rules!.LevelCaps.Select(c => c.Level));
        Assert.Equal(["Gym 1", "Gym 2", "Gym 3", "Champion"], data.Rules.LevelCaps.Select(c => c.Label));
        Assert.False(data.Rules.CapsEnabled);
        Assert.True(data.Rules.PhysicalSpecialSplit);
    }

    [Fact]
    public async Task Saving_without_changes_keeps_every_file_identical()
    {
        using var tmp = new TempDir();
        var (module, data, ctx, root) = await Load(tmp);
        var files = module.ManagedFiles(root).Where(f => File.Exists(Path.Combine([root, .. f.Split('/')]))).ToDictionary(f => f, f => ExpansionMovesTests.Read(root, f));

        await module.SaveDataAsync(data, ctx);

        foreach (var (f, text) in files)
        {
            Assert.Equal(text, ExpansionMovesTests.Read(root, f));
        }
    }

    [Fact]
    public async Task Edits_change_only_their_own_lines()
    {
        using var tmp = new TempDir();
        var (module, data, ctx, root) = await Load(tmp);
        var json = ExpansionMovesTests.Read(root, "src/data/wild_encounters.json");
        var items = ExpansionMovesTests.Read(root, "src/data/items.h");
        var caps = ExpansionMovesTests.Read(root, "src/caps.c");
        var capsCfg = ExpansionMovesTests.Read(root, "include/config/caps.h");

        var e = data.Encounters.Single(x => x.Key == "gMeadowRoute");
        data.Encounters[data.Encounters.IndexOf(e)] = e with { Methods = [e.Methods[0] with { Slots = [e.Methods[0].Slots[0] with { Species = "SPECIES_BLOOMTLE" }, .. e.Methods[0].Slots.Skip(1)] }] };
        var sun = data.Items.Single(i => i.Key == "ITEM_SUN_STONE");
        data.Items[data.Items.IndexOf(sun)] = sun with { Price = 2500, Name = "Sunny Stone" };
        var berry = data.Items.Single(i => i.Key == "ITEM_ORAN_BERRY");
        data.Items[data.Items.IndexOf(berry)] = berry with { Price = 1 };
        data.Rules = data.Rules! with
        {
            LevelCaps = [.. data.Rules.LevelCaps.SkipLast(1), data.Rules.LevelCaps[^1] with { Level = 95 }],
            CapsEnabled = true,
            ExpCap = ExpCapKind.Hard,
            RareCandyCap = true,
            PhysicalSpecialSplit = false,
        };
        await module.SaveDataAsync(data, ctx);

        Assert.Equal(["\"species\": \"SPECIES_BLOOMTLE\""], Diff(json, ExpansionMovesTests.Read(root, "src/data/wild_encounters.json")));
        Assert.Equal(
            [".name = ITEM_NAME(\"Sunny Stone\"),", ".price = (I_PRICE >= GEN_7) ? 2500 : 2100,", ".price = 1,"],
            Diff(items, ExpansionMovesTests.Read(root, "src/data/items.h")));
        Assert.Equal(["{FLAG_IS_CHAMPION, 95},"], Diff(caps, ExpansionMovesTests.Read(root, "src/caps.c")));
        Assert.Equal(3, Diff(capsCfg, ExpansionMovesTests.Read(root, "include/config/caps.h")).Count);
        Assert.Contains("#define B_PHYSICAL_SPECIAL_SPLIT    GEN_3 //", ExpansionMovesTests.Read(root, "include/config/battle.h"), StringComparison.Ordinal);

        var (_, again, _) = await ExpansionMovesTests.LoadAsync(root);
        Assert.True(again.Rules!.CapsEnabled);
        Assert.Equal(ExpCapKind.Hard, again.Rules.ExpCap);
        Assert.False(again.Rules.PhysicalSpecialSplit);
    }

    [Fact]
    public async Task Text_edits_keep_the_games_line_codes()
    {
        using var tmp = new TempDir();
        var (module, data, ctx, root) = await Load(tmp);
        var welcome = data.Texts.Single(t => t.Key == "MeadowTown_Mart_Text_Welcome");
        var editable = PokeStudio.Modules.Expansion.TextScripts.ToEditable(welcome.Text);
        Assert.Equal("Welcome to the meadow!\nWe sell berries and stones.\n\nHave a nice day!", editable);

        var newText = PokeStudio.Modules.Expansion.TextScripts.FromEditable("Welcome!\nBerries, stones\nand more.\n\nCome again!");
        Assert.Equal("Welcome!\\nBerries, stones\\land more.\\pCome again!", newText);
        data.Texts[data.Texts.IndexOf(welcome)] = welcome with { Text = newText };
        await module.SaveDataAsync(data, ctx);

        Assert.Contains(
            "MeadowTown_Mart_Text_Welcome:\n\t.string \"Welcome!\\n\"\n\t.string \"Berries, stones\\l\"\n\t.string \"and more.\\p\"\n\t.string \"Come again!$\"\n",
            ExpansionMovesTests.Read(root, "data/maps/MeadowTown_Mart/scripts.inc"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Badge_tiers_generate_extra_shops_and_can_be_removed()
    {
        using var tmp = new TempDir();
        var (module, data, ctx, root) = await Load(tmp);
        var file = "data/maps/MeadowTown_Mart/scripts.inc";
        var original = ExpansionMovesTests.Read(root, file);
        var mart = data.Marts[0];
        data.Marts[0] = mart with
        {
            BadgeTiers = new Dictionary<int, IReadOnlyList<string>> { [2] = ["ITEM_MIRACLE_SEED"], [3] = ["ITEM_ORAN_BERRY", "ITEM_SUN_STONE"] },
        };
        await module.SaveDataAsync(data, ctx);

        var text = ExpansionMovesTests.Read(root, file);
        Assert.Contains(
            "\tgoto_if_set FLAG_BADGE03_GET, MeadowTown_Mart_Pokemart_PokeStudioTier3 @ Poke Studio badge tier 3\n" +
            "\tgoto_if_set FLAG_BADGE02_GET, MeadowTown_Mart_Pokemart_PokeStudioTier2 @ Poke Studio badge tier 2\n" +
            "\tpokemart MeadowTown_Mart_Pokemart\n", text, StringComparison.Ordinal);
        Assert.Contains(
            "MeadowTown_Mart_Pokemart_PokeStudioTier2::\n" +
            "\tpokemart MeadowTown_Mart_Pokemart_PokeStudioTier2_Items\n" +
            "\tmsgbox gText_PleaseComeAgain, MSGBOX_DEFAULT\n\trelease\n\tend\n\n" +
            "\t.align 2\nMeadowTown_Mart_Pokemart_PokeStudioTier2_Items:\n" +
            "\t.2byte ITEM_ORAN_BERRY\n\t.2byte ITEM_SUN_STONE\n\t.2byte ITEM_MIRACLE_SEED\n\tpokemartlistend\n", text, StringComparison.Ordinal);

        var (module2, again, ctx2) = await ExpansionMovesTests.LoadAsync(root);
        var reread = Assert.Single(again.Marts);
        Assert.Equal(["ITEM_MIRACLE_SEED"], reread.BadgeTiers[2]);
        Assert.Empty(reread.BadgeTiers[3]); // already sold from the start
        again.Marts[0] = reread with { BadgeTiers = new Dictionary<int, IReadOnlyList<string>>() };
        await module2.SaveDataAsync(again, ctx2);
        Assert.Equal(original, ExpansionMovesTests.Read(root, file));
    }

    [Fact]
    public async Task Checks_cover_wild_slots_items_shops_text_and_caps()
    {
        using var tmp = new TempDir();
        var (module, data, _, _) = await Load(tmp);
        var e = data.Encounters.Single(x => x.Key == "gMeadowRoute");
        data.Encounters[data.Encounters.IndexOf(e)] = e with
        {
            Methods = [e.Methods[0] with { Slots = [new EncounterSlot("SPECIES_GHOSTLING", 9, 5), .. e.Methods[0].Slots.Skip(1)] }],
        };
        data.Marts[0] = data.Marts[0] with { Items = [] };
        var sign = data.Texts.Single(t => t.Key == "gText_LongSign");
        data.Texts[data.Texts.IndexOf(sign)] = sign with { Text = sign.Text + " Really." }; // only changed text is checked
        data.Rules = data.Rules! with { LevelCaps = [data.Rules.LevelCaps[0] with { Level = 30 }, .. data.Rules.LevelCaps.Skip(1)] };

        var issues = GameValidator.Run(data, module);

        Assert.Contains(issues, i => i.Area == "Encounter" && i.Message.Contains("switched off", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Area == "Encounter" && i.Message.Contains("lowest level must come first", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Area == "Mart" && i.Message.Contains("sells nothing", StringComparison.Ordinal));
        Assert.Contains(issues, i => i.Area == "Text" && i.TargetKey == "gText_LongSign");
        Assert.DoesNotContain(issues, i => i.Area == "Text" && i.TargetKey == "gText_MeadowSign");
        Assert.Contains(issues, i => i.Area == "Level caps" && i.Message.Contains("lower than the one before", StringComparison.Ordinal));
    }
}

public class ProgressionTests
{
    private static readonly CurvePoint[] Curve = [new(15, 20), new(46, 70), new(58, 95)];

    [Theory]
    [InlineData(1, 1)]
    [InlineData(15, 20)]
    [InlineData(46, 70)]
    [InlineData(58, 95)]
    [InlineData(30, 44)] // between milestones: same relative position (15→46 maps onto 20→70)
    [InlineData(8, 11)]
    [InlineData(62, 100)] // beyond the last milestone the last slope continues, capped at 100
    public void Curve_keeps_order_between_milestones(int level, int expected) =>
        Assert.Equal(expected, LevelCurve.Map(level, Curve, 100));

    [Fact]
    public async Task Applying_the_curve_changes_trainers_wild_and_caps_in_one_step()
    {
        using var tmp = new TempDir();
        var (module, data, _) = await ExpansionMovesTests.LoadAsync(ExpansionMovesTests.CopyFixture(tmp));
        var targets = new[] { 20, 30, 45, 90 };
        var curve = LevelCurve.FromCaps(data.Rules!.LevelCaps, targets);

        var change = LevelCurve.Apply(data, curve, targets, new LevelCurve.Options(EvolveTrainers: true, ItemEvolutionLevel: 30), module.Limits, "Champion at 90");
        Assert.NotNull(change);
        var undo = new UndoRedoStack();
        undo.Execute(change);

        Assert.Equal(targets, data.Rules!.LevelCaps.Select(c => c.Level));
        var rick = data.Trainers.Single(t => t.Key == "TRAINER_RICK_1");
        Assert.Equal([20, 22], rick.Party.Select(p => p.Level)); // 12 → 20 (Gym 1 cap), 13 → 22 (a sixth of the way to 18 → 30)
        Assert.Equal("SPECIES_BLOOMTLE", rick.Party[0].Species); // Sproutle evolves at 16 in the fixture, so a Lv 20 one is Bloomtle
        Assert.Equal(1, undo.UnsavedCount);
        var meadow = data.Encounters.Single(e => e.Key == "gMeadowRoute").Methods[0].Slots[0];
        Assert.Equal((4, 4), (meadow.MinLevel, meadow.MaxLevel)); // 3 → 4 (2/11 of the way from 1 → 1 to 12 → 20)

        undo.Undo();
        Assert.Equal([12, 18, 25, 40], data.Rules!.LevelCaps.Select(c => c.Level));
        Assert.Equal("SPECIES_SPROUTLE", data.Trainers.Single(t => t.Key == "TRAINER_RICK_1").Party[0].Species);
    }

    [Fact]
    public async Task Wild_tools_raise_levels_and_replace_pokemon_everywhere()
    {
        using var tmp = new TempDir();
        var (module, data, _) = await ExpansionMovesTests.LoadAsync(ExpansionMovesTests.CopyFixture(tmp));

        var raise = WildTools.ChangeLevels(data, data.Encounters.Select(e => e.Key), 5, module.Limits)!;
        new UndoRedoStack().Execute(raise);
        Assert.Equal((8, 8), (data.Encounters[0].Methods[0].Slots[0].MinLevel, data.Encounters[0].Methods[0].Slots[0].MaxLevel));

        var replace = WildTools.Replace(data, "SPECIES_EMBERPUP", "SPECIES_BLOOMTLE", trainers: true, "Emberpup", "Bloomtle")!;
        new UndoRedoStack().Execute(replace);
        Assert.Equal("Emberpup → Bloomtle (3 places)", replace.Description); // 2 wild slots + Rick (the pool trainer is locked)
        Assert.DoesNotContain(data.Encounters.SelectMany(e => e.Methods).SelectMany(m => m.Slots), s => s.Species == "SPECIES_EMBERPUP");
        Assert.Equal("SPECIES_BLOOMTLE", data.Trainers.Single(t => t.Key == "TRAINER_RICK_1").Party[1].Species);
    }

    [Fact]
    public async Task Shop_presets_only_list_items_the_game_has()
    {
        using var tmp = new TempDir();
        var (_, data, _) = await ExpansionMovesTests.LoadAsync(ExpansionMovesTests.CopyFixture(tmp));

        var presets = MartPresets.For(data.Catalog);

        var berries = Assert.Single(presets, p => p.Id == "berries");
        Assert.Equal(["ITEM_ORAN_BERRY"], berries.Items);
        Assert.DoesNotContain(presets, p => p.Id == "competitive"); // the fixture has none of those items
    }
}
