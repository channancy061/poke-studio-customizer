using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.GameOptions;
using PokeStudio.Core.Guide;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Projects;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

public class GuideTests
{
    [Fact]
    public void Map_folders_become_place_names()
    {
        Assert.Equal("Rustboro City Gym", ExpansionModule.MapPlace("RustboroCity_Gym"));
        Assert.Equal("Route 104", ExpansionModule.MapPlace("Route104_Frlg"));
        Assert.Equal("Ever Grande City Pokemon League 1F", ExpansionModule.MapPlace("EverGrandeCity_PokemonLeague_1F"));
    }

    [Fact]
    public void Html_and_markdown_escape_text_and_keep_tables()
    {
        var doc = new GuideDocument("A & B", [("Bosses", "bosses")],
        [
            new GuideHeading(2, "Bosses", "bosses"),
            new GuideTip("Bring <Water>"),
            new GuideTable(["Pokémon", "Moves"], [["Geodude ?", "Tackle | Rock Throw"]]),
        ]);
        var html = PlayersGuide.ToHtml(doc);
        Assert.Contains("<title>A &amp; B</title>", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"tip\">Bring &lt;Water&gt;</div>", html, StringComparison.Ordinal);
        Assert.Contains("<td>Geodude ?</td>", html, StringComparison.Ordinal);
        var md = PlayersGuide.ToMarkdown(doc);
        Assert.Contains("| Geodude ? | Tackle \\| Rock Throw |", md, StringComparison.Ordinal);
        Assert.Contains("- [Bosses](#bosses)", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Real_emerald_guide_lists_bosses_with_places_and_adaptive_slots_when_available()
    {
        var real = Environment.GetEnvironmentVariable("POKESTUDIO_EXPANSION_DIR");
        if (string.IsNullOrEmpty(real) || !Directory.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var root = tmp.Combine("game");
        foreach (var dir in new[] { "include", "src", "data/maps" })
        {
            ProjectService.CopyDirectory(Path.Combine([real, .. dir.Split('/')]), Path.Combine([root, .. dir.Split('/')]));
        }

        File.Copy(Path.Combine(real, "Makefile"), Path.Combine(root, "Makefile"));
        var module = new ExpansionModule();
        var data = await module.LoadDataAsync(root, tmp.Combine("data"));
        var lab = new LabSettings();
        AdaptiveSlots.SuggestForAll(data, lab).Change!.Apply();
        data.GameOptions = new GameOptionsSettings { Nuzlocke = OptionMode.PlayerChooses, AdaptiveSlot = OptionMode.PlayerChooses, CenterHelper = new CenterHelperSettings { Enabled = true } };

        var places = module.TrainerPlaces(data);
        Assert.Equal("Rustboro City Gym", places["TRAINER_ROXANNE_1"]);
        var doc = PlayersGuide.Build(data, lab, places, new GuideOptions { Title = "Emerald test" });
        var html = PlayersGuide.ToHtml(doc);
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "guide-emerald.html"), html);

        Assert.Contains("Roxanne", html, StringComparison.Ordinal);
        Assert.Contains("Where: Rustboro City Gym.", html, StringComparison.Ordinal);
        Assert.Contains("Bring: ", html, StringComparison.Ordinal);
        Assert.Contains(" ?</td>", html, StringComparison.Ordinal);
        Assert.Contains("counters your team", html, StringComparison.Ordinal);
        Assert.Contains("don't keep pressing A", html, StringComparison.Ordinal);
        Assert.Contains("Where to catch Pokémon", html, StringComparison.Ordinal);
        Assert.Contains("How special Pokémon evolve", html, StringComparison.Ordinal);
        Assert.True(doc.Blocks.OfType<GuideHeading>().Count(h => h.Level == 3) > 50);

        // gym leaders come in badge order
        var order = doc.Blocks.OfType<GuideHeading>().Select(h => h.Text).ToList();
        Assert.True(order.FindIndex(t => t.Contains("Roxanne", StringComparison.Ordinal)) < order.FindIndex(t => t.Contains("Wallace", StringComparison.Ordinal) || t.Contains("Juan", StringComparison.Ordinal)));
    }
}
