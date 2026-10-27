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

    [Fact]
    public void Real_shop_and_ai_fixes_go_in_and_come_out_again_when_available()
    {
        var fr = Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR");
        if (fr is null || !Directory.Exists(fr))
        {
            return;
        }

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

    [Fact]
    public void Real_firered_marts_get_the_specialist_behind_the_counter_when_available() => RealProject(Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR"));

    [Fact]
    public void Real_emerald_marts_get_the_specialist_behind_the_counter_when_available() => RealProject(Environment.GetEnvironmentVariable("POKESTUDIO_EXPANSION_DIR"));
}
