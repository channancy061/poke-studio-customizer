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
    public void Real_firered_marts_get_the_specialist_behind_the_counter_when_available() => RealProject(Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR"));

    [Fact]
    public void Real_emerald_marts_get_the_specialist_behind_the_counter_when_available() => RealProject(Environment.GetEnvironmentVariable("POKESTUDIO_EXPANSION_DIR"));
}
