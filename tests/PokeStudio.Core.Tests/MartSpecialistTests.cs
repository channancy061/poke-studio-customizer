using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

public class MartSpecialistTests
{
    private static void RealProject(string? root)
    {
        if (root is null || !Directory.Exists(root))
        {
            return;
        }

        string? Read(string rel) => File.Exists(Path.Combine(root, rel)) ? File.ReadAllText(Path.Combine(root, rel)) : null;
        byte[]? ReadBytes(string rel) => File.Exists(Path.Combine(root, rel)) ? File.ReadAllBytes(Path.Combine(root, rel)) : null;
        var report = new List<string>();
        var behind = 0;
        var marts = CenterPlacement.MartMaps(root).ToList();
        foreach (var mart in marts)
        {
            var json = Read(mart)!;
            var layout = System.Text.RegularExpressions.Regex.Match(json, "\"layout\"\\s*:\\s*\"(\\w+)\"").Groups[1].Value;
            var grid = CenterPlacement.LoadGrid(Read, ReadBytes, layout);
            Assert.NotNull(grid);
            var spot = CenterPlacement.Find(json, grid, g => g.Contains("CLERK", StringComparison.Ordinal) || g.Contains("MART_EMPLOYEE", StringComparison.Ordinal));
            behind += spot?.BehindCounter == true ? 1 : 0;
            report.Add($"{mart}: {(spot is null ? "no spot" : $"({spot.X},{spot.Y}) {(spot.BehindCounter ? "behind the counter" : "on the floor")} facing {spot.Facing}")}");
        }

        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"specialist-{Path.GetFileName(root)}.txt"), string.Join("\n", report));
        // behind the counter in every Mart but one (Vermilion, Verdanturf: someone stands where a customer would talk across it)
        Assert.True(behind >= marts.Count - 1, string.Join("\n", report));
    }

    [Fact]
    public void Bag_sizes_are_set_and_put_back()
    {
        const string global = "#define PC_ITEMS_COUNT      30\n#define BAG_ITEMS_COUNT     42\n";
        var bigger = GameOptionsWriter.WithSize(GameOptionsWriter.WithSize(global, "BAG_ITEMS_COUNT", 80), "PC_ITEMS_COUNT", 50);
        Assert.Contains("#define BAG_ITEMS_COUNT     80 // Poke Studio: size (was 42)", bigger, StringComparison.Ordinal);
        Assert.Contains("#define PC_ITEMS_COUNT      50 // Poke Studio: size (was 30)", bigger, StringComparison.Ordinal);
        Assert.Contains("BAG_ITEMS_COUNT     100 // Poke Studio: size (was 42)", GameOptionsWriter.WithSize(bigger, "BAG_ITEMS_COUNT", 100), StringComparison.Ordinal);
        Assert.Equal(global, GameOptionsWriter.WithSize(GameOptionsWriter.WithSize(bigger, "BAG_ITEMS_COUNT", 0), "PC_ITEMS_COUNT", 0));
    }

    private static void RealBag(string? root, bool hasFiller)
    {
        if (root is null || !Directory.Exists(root))
        {
            return;
        }

        foreach (var file in BiggerBag.Files)
        {
            var text = File.ReadAllText(Path.Combine(root, file));
            var original = BiggerBag.Clean(text);
            var bigger = BiggerBag.Apply(file, original, 150);
            Assert.NotEqual(original, bigger);
            Assert.Equal(bigger, BiggerBag.Apply(file, bigger, 150)); // the same when saved again
            Assert.Equal(original, BiggerBag.Apply(file, bigger, 0));
            switch (file)
            {
                case BiggerBag.Constants:
                    Assert.Contains("#define PS_BAG_ITEMS_COUNT 150 // Poke Studio: bigger bag", bigger, StringComparison.Ordinal);
                    break;
                case BiggerBag.Global:
                    Assert.Contains("u32 psBagItems[PS_BAG_ITEMS_COUNT];", bigger, StringComparison.Ordinal);
                    Assert.Equal(hasFiller, bigger.Contains("- sizeof(u32) * PS_BAG_ITEMS_COUNT]", StringComparison.Ordinal));
                    break;
                case BiggerBag.Item:
                    Assert.Contains("gBagPockets[POCKET_ITEMS].itemSlots = (struct ItemSlot *)gSaveBlock3Ptr->psBagItems;", bigger, StringComparison.Ordinal);
                    Assert.Contains("CpuFastFill(0, gSaveBlock3Ptr->psBagItems", bigger, StringComparison.Ordinal);
                    break;
                case BiggerBag.ItemMenu:
                    Assert.DoesNotContain("gSaveBlock1Ptr->bag.items", bigger, StringComparison.Ordinal);
                    Assert.Contains("max(PS_BAG_ITEMS_COUNT,", bigger, StringComparison.Ordinal);
                    break;
            }
        }
    }

    [Fact]
    public void Real_firered_bag_moves_to_save_block_3_when_available() => RealBag(Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR"), hasFiller: true);

    [Fact]
    public void Real_emerald_bag_moves_to_save_block_3_when_available() => RealBag(Environment.GetEnvironmentVariable("POKESTUDIO_EXPANSION_DIR"), hasFiller: false);

    [Fact]
    public void Real_shop_and_ai_fixes_go_in_and_come_out_again_when_available()
    {
        var fr = Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR");
        if (fr is null || !Directory.Exists(fr))
        {
            return;
        }

        // Oak's aides count every Pokémon and ask for a set number; off puts them back
        var aide = File.ReadAllText(Path.Combine(fr, "data", "maps", "Route2_EastBuilding", "scripts.inc"));
        var counting = AideWriter.Apply(aide, true, 15);
        Assert.Contains("setvar VAR_0x8004, 1 @ Poke Studio: aides count every Pokémon", counting, StringComparison.Ordinal);
        Assert.Contains(".equ REQUIRED_SEEN_MONS, 15 @ Poke Studio: aide's number (was 10)", counting, StringComparison.Ordinal);
        Assert.Equal(counting, AideWriter.Apply(counting, true, 15));
        Assert.Equal(aide, AideWriter.Apply(counting, false, 0));
        Assert.Equal(5, AideWriter.Files(fr).Count());

        // in-game trades: every requested line of a trade changes (Nidoran has one per game version), and comes back
        var trades = File.ReadAllText(Path.Combine(fr, "src", "data", "ingame_trades.h"));
        var asked = TradeWriter.Apply(trades, new Dictionary<string, string> { ["INGAME_TRADE_MR_MIME"] = "SPECIES_RALTS", ["INGAME_TRADE_NIDORAN"] = "SPECIES_BUDEW" }, true);
        Assert.Contains(".requestedSpecies = SPECIES_RALTS // Poke Studio: trade (was SPECIES_ABRA)", asked, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(asked, "SPECIES_BUDEW"));
        Assert.Equal(trades, TradeWriter.Apply(asked, new Dictionary<string, string>(), false));

        // night tables go in after their day table, marked, and come out again; the time-of-day switch too
        var wild = File.ReadAllText(Path.Combine(fr, "src", "data", "wild_encounters.json"));
        var night = new Core.Models.EncounterTable
        {
            Key = "sRoute1_FireRed_Night", Map = "MAP_ROUTE1",
            Methods = [new Core.Models.EncounterMethodTable { Type = "land_mons", Rate = 21, Slots = [new Core.Models.EncounterSlot("SPECIES_HOOTHOOT", 2, 3)] }],
        };
        var withNight = NightWriter.Encounters(wild, [night], true);
        Assert.Contains("\"base_label\": \"sRoute1_FireRed_Night\"", withNight, StringComparison.Ordinal);
        Assert.Contains("\"poke_studio\": \"night\"", withNight, StringComparison.Ordinal);
        Assert.Equal(withNight, NightWriter.Encounters(withNight, [night], true));
        Assert.Equal(wild, NightWriter.Encounters(withNight, [], false));
        var ow = File.ReadAllText(Path.Combine(fr, "include", "config", "overworld.h"));
        Assert.Matches(@"#define OW_TIME_OF_DAY_ENCOUNTERS\s+TRUE", NightWriter.Config(ow, true));
        Assert.Equal(ow, NightWriter.Config(NightWriter.Config(ow, true), false));

        // the DexNav: on with our flags and variables by number, and back as it was
        var dexNav = File.ReadAllText(Path.Combine(fr, "include", "config", "dexnav.h"));
        var ids = new GameOptionsWriter.Ids(
            new Dictionary<string, string> { ["FLAG_PS_DEXNAV_SEARCHING"] = "0x500", ["FLAG_PS_DEXNAV_DETECTOR"] = "0x501" },
            new Dictionary<string, string> { ["VAR_PS_DEXNAV_SPECIES"] = "0x40F0", ["VAR_PS_DEXNAV_STEPS"] = "0x40F1" });
        var on = GameOptionsWriter.WithDexNav(dexNav, ids);
        Assert.Matches(@"#define DEXNAV_ENABLED\s+TRUE .*// Poke Studio: DexNav \(was FALSE\)", on);
        Assert.Matches(@"#define DN_FLAG_DEXNAV_GET\s+FLAG_SYS_POKEDEX_GET", on);
        Assert.Matches(@"#define DN_VAR_STEP_COUNTER\s+0x40F1", on);
        Assert.Equal(on, GameOptionsWriter.WithDexNav(on, ids));
        Assert.Equal(dexNav, GameOptionsWriter.WithDexNav(on, null));

        var shop = File.ReadAllText(Path.Combine(fr, "src", "shop.c"));
        var patched = ShopsJson.OneOfEachPatch(shop)!;
        Assert.Contains("sText_PsAlreadyHave", patched, StringComparison.Ordinal);
        Assert.Null(ShopsJson.OneOfEachPatch(patched)); // once only
        Assert.Equal(shop, ShopsJson.WithoutOneOfEach(patched));

        // FireRed's naming screen stays quiet before the battle's "add to your party?" question
        var naming = File.ReadAllText(Path.Combine(fr, "src", "naming_screen.c"));
        var quiet = GameOptionsWriter.QuietNaming(naming, true);
        Assert.Contains("&& B_CATCH_SWAP_INTO_PARTY < GEN_7 // Poke Studio: the battle asks first", quiet, StringComparison.Ordinal);
        Assert.Equal(naming, GameOptionsWriter.QuietNaming(quiet, false));

        // the expansion's debug menus are off for players, and come back as they were
        var debug = File.ReadAllText(Path.Combine(fr, "include", "config", "debug.h"));
        var off = GameOptionsWriter.WithDebugMenus(debug, false);
        Assert.Matches(@"#define DEBUG_OVERWORLD_MENU\s+FALSE // Poke Studio: debug menus off \(was DISABLED_ON_RELEASE\)", off);
        Assert.Matches(@"#define DEBUG_BATTLE_MENU\s+FALSE", off);
        Assert.Equal(debug, GameOptionsWriter.WithDebugMenus(off, true));

        var ai = File.ReadAllText(Path.Combine(fr, "src", "battle_ai_main.c"));
        var fixedAi = GameOptionsWriter.AiMemoryFix(ai)!;
        Assert.Contains("ClearBattlerAbilityHistory(battler); // Poke Studio: forget the Pokémon that left", fixedAi, StringComparison.Ordinal);
        Assert.Equal(ai, System.Text.RegularExpressions.Regex.Replace(fixedAi, @"^[^\n]*Poke Studio: forget the Pokémon that left[^\n]*\r?\n", "", System.Text.RegularExpressions.RegexOptions.Multiline));
    }

    [Theory]
    [InlineData("POKESTUDIO_FIRERED_DIR")]
    [InlineData("POKESTUDIO_EXPANSION_DIR")]
    public void Real_start_menu_gets_the_tools_and_loses_them_again_when_available(string variable)
    {
        var root = Environment.GetEnvironmentVariable(variable);
        if (root is null || !Directory.Exists(root))
        {
            return;
        }

        var menu = File.ReadAllText(Path.Combine(root, "src", "start_menu.c"));
        var tools = ToolsWriter.Patch(menu, true);
        foreach (var piece in new[] { "    MENU_ACTION_PS_TOOLS,", "[MENU_ACTION_PS_TOOLS] =", "AddStartMenuAction(MENU_ACTION_PS_TOOLS);", "&& gMenuCallback != StartMenuPsToolsCallback", "ScriptContext_SetupScript(PokeStudio_EventScript_Tools);" })
        {
            Assert.Contains(piece, tools, StringComparison.Ordinal);
        }

        Assert.Equal(tools, ToolsWriter.Patch(tools, true));
        Assert.Equal(menu, ToolsWriter.Patch(tools, false));
    }

    [Fact]
    public void Real_firered_marts_get_the_specialist_behind_the_counter_when_available() => RealProject(Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR"));

    [Fact]
    public void Real_emerald_marts_get_the_specialist_behind_the_counter_when_available() => RealProject(Environment.GetEnvironmentVariable("POKESTUDIO_EXPANSION_DIR"));
}
