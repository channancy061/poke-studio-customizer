using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Safety;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

/// <summary>Runs against a real pokefirered-expansion clone when POKESTUDIO_FIRERED_DIR is set (on a copy).</summary>
public class FireRedRealTests
{
    private static string Read(string root, string rel) => File.ReadAllText(Path.Combine([root, .. rel.Split('/')]));

    private static List<(string First, string Second)> Diff(string a, string b) =>
        a.Split('\n').Zip(b.Split('\n')).Where(p => p.First != p.Second).ToList();

    [Fact]
    public async Task Real_firered_project_reads_round_trips_and_edits_minimally_when_available()
    {
        var real = Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR");
        if (string.IsNullOrEmpty(real) || !Directory.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var root = tmp.Combine("firered");
        foreach (var dir in new[] { "include", "src", "data/maps", "data/text" })
        {
            ProjectService.CopyDirectory(Path.Combine([real, .. dir.Split('/')]), Path.Combine([root, .. dir.Split('/')]));
        }

        foreach (var f in new[] { "Makefile", "charmap.txt" })
        {
            File.Copy(Path.Combine(real, f), Path.Combine(root, f));
        }

        var module = new ExpansionModule();
        var detected = module.Detect(new GameSource(root, GameSourceKind.DecompProject));
        Assert.True(detected.IsMatch);
        Assert.StartsWith("pokefirered-expansion", detected.GameTitle, StringComparison.Ordinal);
        Assert.Equal("pokefirered.gba", module.BuildFor(root).OutputFile);

        var files = module.ManagedFiles(root).Where(f => File.Exists(Path.Combine([root, .. f.Split('/')]))).ToDictionary(f => f, f => Read(root, f));
        var data = await module.LoadDataAsync(root, tmp.Combine("data"));
        var ctx = new ModuleSaveContext(new SafeFileWriter(root, allowedFiles: module.ManagedFiles(root)), root, root);

        Assert.True(data.Species.Count > 1000, $"only {data.Species.Count} species");
        Assert.True(data.Moves.Count > 900, $"only {data.Moves.Count} moves");
        Assert.True(data.Trainers.Count > 300, $"only {data.Trainers.Count} trainers");
        Assert.True(data.Encounters.Count > 100, $"only {data.Encounters.Count} encounter tables");
        Assert.All(data.Encounters, e => Assert.Null(e.LockedReason));
        Assert.True(data.Items.Count > 500, $"only {data.Items.Count} items");
        Assert.True(data.Marts.Count >= 15, $"only {data.Marts.Count} shops");
        Assert.Contains(data.Marts, m => m.Key == "SHOP_ID_VIRIDIAN_CITY_MART" && m.LockedReason is null);
        Assert.Contains(data.Marts, m => m.TiersLockedReason is not null); // Two Island grows with the story
        Assert.True(data.Texts.Count > 2000, $"only {data.Texts.Count} texts");
        Assert.Equal("Champion", data.Rules!.LevelCaps[^1].Label);
        Assert.Equal(208, module.Limits.TextLineWidth);
        var speciesKeys = data.Species.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var unknown = data.Trainers.SelectMany(t => t.Party).Select(p => p.Species).Where(s => !speciesKeys.Contains(s)).Distinct().ToList();
        Assert.True(unknown.Count == 0, "unknown trainer species: " + string.Join(", ", unknown.Take(10)));
        // unchanged original text is never reported; FireRed's own towns fit the 208-pixel box
        Assert.DoesNotContain(Validation.GameValidator.Run(data, module), i => i.Area == "Text");
        var townLines = data.Texts.Where(t => t.Place is "Pallet Town" or "Viridian City" or "Pewter City" or "Cerulean City")
            .SelectMany(t => Validation.GameValidator.TextLines(t.Text)).ToList();
        Assert.True(townLines.Count > 50);
        Assert.All(townLines, l => Assert.True(module.MeasureText(l) <= 208, $"“{l}” measures {module.MeasureText(l)} px"));

        // Champion Lab: FireRed's starters and its rival convention (the suffix is the rival's own starter)
        Assert.Equal(["SPECIES_BULBASAUR", "SPECIES_SQUIRTLE", "SPECIES_CHARMANDER"], data.Starters);
        var lab = new ChampionLab.LabSettings();
        var rivalIssues = ChampionLab.LabChecks.RivalChecks(data, lab, new ChampionLab.StarterSet(data.Starters)).ToList();
        Assert.True(rivalIssues.Count == 0, string.Join(" | ", rivalIssues.Take(10).Select(i => i.Subject + " " + i.Message)));
        Assert.Equal(ChampionLab.TrainerGroup.Rival, ChampionLab.Lab.GroupOf(data, lab, data.Trainers.Single(t => t.Key == "TRAINER_RIVAL_OAKS_LAB_SQUIRTLE")));

        // saving with no changes keeps every file identical
        await module.SaveDataAsync(data, ctx);
        foreach (var (f, text) in files)
        {
            Assert.Equal(text, Read(root, f));
        }

        // one move edit = one line
        var movesText = Read(root, "src/data/moves_info.h");
        var bolt = data.Moves.FindIndex(m => m.Key == "MOVE_THUNDERBOLT");
        data.Moves[bolt] = data.Moves[bolt] with { Power = 85 };
        await module.SaveDataAsync(data, ctx);
        Assert.Single(Diff(movesText, Read(root, "src/data/moves_info.h")));

        // a shop in shops.json: one item added = one line; a badge tier adds stages and the badge checks in shop.c
        var shopsText = Read(root, "src/data/shops.json");
        var shopC = Read(root, "src/shop.c");
        var vi = data.Marts.FindIndex(m => m.Key == "SHOP_ID_VIRIDIAN_CITY_MART");
        data.Marts[vi] = data.Marts[vi] with { Items = [.. data.Marts[vi].Items, "ITEM_RARE_CANDY"] };
        await module.SaveDataAsync(data, ctx);
        var shopsAfter = Read(root, "src/data/shops.json");
        Assert.Equal(shopsText.Split('\n').Length + 1, shopsAfter.Split('\n').Length);
        Assert.Contains("\"ITEM_RARE_CANDY\"", shopsAfter, StringComparison.Ordinal);

        var reread = await new ExpansionModule().LoadDataAsync(root, tmp.Combine("data2"));
        var module2 = new ExpansionModule();
        var again = await module2.LoadDataAsync(root, tmp.Combine("data3"));
        var ctx2 = new ModuleSaveContext(new SafeFileWriter(root, allowedFiles: module2.ManagedFiles(root)), root, root);
        Assert.Contains("ITEM_RARE_CANDY", reread.Marts.Single(m => m.Key == "SHOP_ID_VIRIDIAN_CITY_MART").Items);
        var vi2 = again.Marts.FindIndex(m => m.Key == "SHOP_ID_VIRIDIAN_CITY_MART");
        again.Marts[vi2] = again.Marts[vi2] with { BadgeTiers = new Dictionary<int, IReadOnlyList<string>> { [2] = ["ITEM_GREAT_BALL"] } };
        await module2.SaveDataAsync(again, ctx2);
        Assert.Contains("\"isUnlockedFunc\": \"PokeStudio_HasBadge2\"", Read(root, "src/data/shops.json"), StringComparison.Ordinal);
        var shopCAfter = Read(root, "src/shop.c");
        Assert.Contains("static bool32 __attribute__((unused)) PokeStudio_HasBadge2(void) { return FlagGet(FLAG_BADGE02_GET); }", shopCAfter, StringComparison.Ordinal);
        Assert.True(shopCAfter.IndexOf("PokeStudio_HasBadge1", StringComparison.Ordinal) < shopCAfter.IndexOf("#include \"data/shops.h\"", StringComparison.Ordinal));
        Assert.Equal(shopC.Split('\n').Length + 11, shopCAfter.Split('\n').Length); // marker lines + 8 checks + blank line

        var third = await new ExpansionModule().LoadDataAsync(root, tmp.Combine("data4"));
        Assert.Equal(["ITEM_GREAT_BALL"], third.Marts.Single(m => m.Key == "SHOP_ID_VIRIDIAN_CITY_MART").BadgeTiers[2]);
    }
}
