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

        // adaptive slots can't run in a ROM: a warning with a fix, never a blocker
        var (change, count) = AdaptiveSlots.SuggestForAll(data, lab);
        Assert.True(count > 0);
        change!.Apply();
        var issues = module.Validate(data).Where(i => i.Message.Contains("adaptive boss slot", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(issues);
        Assert.All(issues, i => Assert.Equal(IssueSeverity.Warning, i.Severity));
        issues[0].Fix!(data).Apply();
        Assert.Equal(count - 1, data.Trainers.Count(t => t.HasAdaptiveSlot));
    }

    [Fact]
    public Task Real_firered_guide_skips_placeholder_trainers_when_available() =>
        GuideFor(Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_ROM"), "firered.gba", "Brock", "Roxanne");

    [Fact]
    public Task Real_emerald_guide_lists_real_bosses_when_available() =>
        GuideFor(Environment.GetEnvironmentVariable("POKESTUDIO_EMERALD_ROM"), "emerald.gba", "Roxanne", "");
}
