using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Guide;
using PokeStudio.Core.Validation;
using PokeStudio.Modules.Gen3Rom;

namespace PokeStudio.Core.Tests;

/// <summary>The Player's Guide and boss tools on GBA ROMs, with the user's own ROMs when their variables are set.</summary>
public class RomGuideTests
{
    private static async Task GuideFor(string? real, string file, string realBoss, string placeholder)
    {
        if (string.IsNullOrEmpty(real) || !File.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var rom = tmp.Combine(file);
        File.Copy(real, rom);
        var module = new Gen3RomModule();
        var data = await module.LoadDataAsync(rom, tmp.Combine("data"));

        // trainers no map battles (FireRed's leftover Ruby/Sapphire placeholders) are marked and stay out of the guide
        var places = module.TrainerPlaces(data);
        Assert.True(places.Count > 100, $"only {places.Count} trainers placed");

        // scripts reached by a jump from another map keep their own map's name (FireRed's Giovanni was on the S.S. Anne)
        var giovanni = data.Trainers.Where(t => t.Name.Equals("Giovanni", StringComparison.OrdinalIgnoreCase) && places.ContainsKey(t.Key))
            .Select(t => places[t.Key]).ToList();
        Assert.DoesNotContain(giovanni, p => p.Contains("ANNE", StringComparison.OrdinalIgnoreCase));
        var lab = new LabSettings();
        var doc = PlayersGuide.Build(data, lab, places, new GuideOptions { Title = "Test" });
        var html = PlayersGuide.ToHtml(doc);
        var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "guides");
        Directory.CreateDirectory(outDir);
        await File.WriteAllTextAsync(Path.Combine(outDir, Path.ChangeExtension(file, ".html")), html);
        Assert.Contains(realBoss, html, StringComparison.OrdinalIgnoreCase);
        if (placeholder.Length > 0)
        {
            Assert.DoesNotContain(placeholder, html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(data.Trainers, t => t.NeverFought);
        }

        Assert.Contains("Where:", html, StringComparison.Ordinal);

        // the game's own choices fill the team table: abilities, last four level-up moves, random nature, no item
        var table = doc.Blocks.OfType<GuideTable>().First(t => t.Headers.Contains("Moves"));
        Assert.All(table.Rows, r => Assert.False(string.IsNullOrEmpty(r[3]), "ability"));
        Assert.All(table.Rows, r => Assert.False(string.IsNullOrEmpty(r[6]), "moves"));

        // unmodified FireRed / Emerald get the adaptive slot as injected code: no warning
        var (change, count) = AdaptiveSlots.SuggestForAll(data, lab);
        Assert.True(count > 0);
        change!.Apply();
        Assert.True(module.SupportsAdaptiveSlots(data));
        Assert.DoesNotContain(module.Validate(data), i => i.Message.Contains("adaptive boss slot", StringComparison.Ordinal));
    }

    [Fact]
    public Task Real_firered_guide_skips_placeholder_trainers_when_available() =>
        GuideFor(Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_ROM"), "firered.gba", "Brock", "Roxanne");

    [Fact]
    public Task Real_emerald_guide_lists_real_bosses_when_available() =>
        GuideFor(Environment.GetEnvironmentVariable("POKESTUDIO_EMERALD_ROM"), "emerald.gba", "Roxanne", "");
}
