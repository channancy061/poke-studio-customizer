using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Progression;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

public class EvolutionFixTests
{
    private static Evolution E(string method, string param, string target, params (string Kind, string Value)[] conditions) =>
        new(method, param, target) { Conditions = conditions.Select(c => new EvolutionCondition(c.Kind, c.Value)).ToList() };

    private static GameData Data()
    {
        var data = new GameData();
        data.Items.Add(new Item { Key = "ITEM_LINKING_CORD", Name = "Linking Cord" });
        data.Items.Add(new Item { Key = "ITEM_METAL_COAT", Name = "Metal Coat" });
        data.Items.Add(new Item { Key = "ITEM_MOON_STONE", Name = "Moon Stone" });
        data.Items.Add(new Item { Key = "ITEM_WATER_STONE", Name = "Water Stone" });
        data.Evolutions.Add(new EvolutionSet
        {
            FromSpecies = "SPECIES_EEVEE",
            Evolutions =
            [
                E("EVO_ITEM", "ITEM_WATER_STONE", "SPECIES_VAPOREON"),
                E("EVO_LEVEL", "0", "SPECIES_UMBREON", ("IF_MIN_FRIENDSHIP", "FRIENDSHIP_EVO_THRESHOLD"), ("IF_TIME", "TIME_NIGHT")),
                E("EVO_LEVEL", "0", "SPECIES_ESPEON", ("IF_MIN_FRIENDSHIP", "FRIENDSHIP_EVO_THRESHOLD"), ("IF_TIME", "TIME_DAY")),
            ],
        });
        data.Evolutions.Add(new EvolutionSet { FromSpecies = "SPECIES_KADABRA", Evolutions = [E("EVO_TRADE", "0", "SPECIES_ALAKAZAM")] });
        data.Evolutions.Add(new EvolutionSet { FromSpecies = "SPECIES_ONIX", Evolutions = [E("EVO_TRADE", "0", "SPECIES_STEELIX", ("IF_HOLD_ITEM", "ITEM_METAL_COAT"))] });
        data.Evolutions.Add(new EvolutionSet
        {
            FromSpecies = "SPECIES_YUNGOOS",
            Evolutions = [E("EVO_LEVEL", "20", "SPECIES_GUMSHOOS", ("IF_NOT_TIME", "TIME_NIGHT"))],
        });
        data.Evolutions.Add(new EvolutionSet
        {
            FromSpecies = "SPECIES_ROCKRUFF",
            Evolutions =
            [
                E("EVO_LEVEL", "25", "SPECIES_LYCANROC_MIDDAY", ("IF_NOT_TIME", "TIME_NIGHT")),
                E("EVO_LEVEL", "25", "SPECIES_LYCANROC_MIDNIGHT", ("IF_TIME", "TIME_NIGHT")),
            ],
        });
        data.Evolutions.Add(new EvolutionSet { FromSpecies = "SPECIES_MACHOKE", LockedReason = "settings", Evolutions = [E("EVO_TRADE", "0", "SPECIES_MACHAMP")] });
        return data;
    }

    [Fact]
    public void Suggestions_follow_the_rules()
    {
        var rows = EvolutionFixer.Find(Data());
        EvolutionFixRow Row(string target) => rows.Single(r => r.Original.TargetSpecies == target);

        Assert.DoesNotContain(rows, r => r.FromSpecies == "SPECIES_MACHOKE"); // locked
        Assert.DoesNotContain(rows, r => r.Original.TargetSpecies == "SPECIES_VAPOREON");
        Assert.Equal(EvolutionFix.Level, Row("SPECIES_ALAKAZAM").Suggested);
        Assert.Equal(EvolutionFix.HoldItem, Row("SPECIES_STEELIX").Suggested);
        Assert.Equal(EvolutionFix.Level, Row("SPECIES_GUMSHOOS").Suggested);

        // day keeps "any time"; the night branch would clash with it, so it gets a stone
        Assert.Equal(EvolutionFix.Level, Row("SPECIES_ESPEON").Suggested);
        Assert.Equal(EvolutionFix.Stone, Row("SPECIES_UMBREON").Suggested);
        Assert.Equal(EvolutionFix.Level, Row("SPECIES_LYCANROC_MIDDAY").Suggested);
        Assert.Equal(EvolutionFix.Stone, Row("SPECIES_LYCANROC_MIDNIGHT").Suggested);

        Assert.Equal(E("EVO_LEVEL", "36", "SPECIES_ALAKAZAM"), EvolutionFixer.Apply(Row("SPECIES_ALAKAZAM"), EvolutionFix.Level), Comparer);
        Assert.Equal(E("EVO_LEVEL", "40", "SPECIES_ALAKAZAM"), EvolutionFixer.Apply(Row("SPECIES_ALAKAZAM"), EvolutionFix.Level, 40), Comparer);
        Assert.Equal(E("EVO_ITEM", "ITEM_LINKING_CORD", "SPECIES_ALAKAZAM"), EvolutionFixer.Apply(Row("SPECIES_ALAKAZAM"), EvolutionFix.Stone), Comparer);
        Assert.Equal(E("EVO_LEVEL", "0", "SPECIES_STEELIX", ("IF_HOLD_ITEM", "ITEM_METAL_COAT")), EvolutionFixer.Apply(Row("SPECIES_STEELIX"), EvolutionFix.HoldItem), Comparer);
        Assert.Equal(E("EVO_LEVEL", "20", "SPECIES_GUMSHOOS"), EvolutionFixer.Apply(Row("SPECIES_GUMSHOOS"), EvolutionFix.Level), Comparer);
        Assert.Equal(E("EVO_LEVEL", "0", "SPECIES_ESPEON", ("IF_MIN_FRIENDSHIP", "FRIENDSHIP_EVO_THRESHOLD")), EvolutionFixer.Apply(Row("SPECIES_ESPEON"), EvolutionFix.Level), Comparer);
        Assert.Equal(E("EVO_ITEM", "ITEM_MOON_STONE", "SPECIES_UMBREON"), EvolutionFixer.Apply(Row("SPECIES_UMBREON"), EvolutionFix.Stone), Comparer);
        Assert.Equal(E("EVO_LEVEL", "25", "SPECIES_LYCANROC_MIDNIGHT", ("IF_HOLD_ITEM", "ITEM_MOON_STONE")), EvolutionFixer.Apply(Row("SPECIES_LYCANROC_MIDNIGHT"), EvolutionFix.HoldItem), Comparer);
        Assert.Equal("Level up with high friendship in the night", EvolutionFixer.Describe(Data(), Row("SPECIES_UMBREON").Original));
    }

    [Fact]
    public void Applying_is_one_undo_step_and_warns_about_clashes()
    {
        var data = Data();
        var rows = EvolutionFixer.Find(data);
        var (change, warnings) = EvolutionFixer.Build(data, rows.Select(r => (r, r.Suggested)));
        Assert.Empty(warnings);
        var undo = new UndoRedoStack();
        undo.Execute(change!);
        Assert.Empty(EvolutionFixer.Find(data));
        Assert.Contains(data.Evolutions.Single(s => s.FromSpecies == "SPECIES_EEVEE").Evolutions, e => e.Method == "EVO_ITEM" && e.Parameter == "ITEM_MOON_STONE");
        undo.Undo();
        Assert.Equal(rows.Count, EvolutionFixer.Find(data).Count);

        // both Lycanroc branches set to "any time" → the same trigger: warned
        var rockruff = rows.Where(r => r.FromSpecies == "SPECIES_ROCKRUFF").Select(r => (r, EvolutionFix.Level));
        var (_, clash) = EvolutionFixer.Build(data, rockruff);
        Assert.Single(clash);
        Assert.Contains("Lycanroc Midday", clash[0], StringComparison.Ordinal);
    }

    private static readonly EvolutionComparer Comparer = new();

    private sealed class EvolutionComparer : IEqualityComparer<Evolution>
    {
        public bool Equals(Evolution? x, Evolution? y) => x is not null && y is not null && x.Method == y.Method && x.Parameter == y.Parameter
            && x.TargetSpecies == y.TargetSpecies && x.Conditions.SequenceEqual(y.Conditions);

        public int GetHashCode(Evolution obj) => obj.TargetSpecies.GetHashCode(StringComparison.Ordinal);
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
            Projects.ProjectService.CopyDirectory(Path.Combine(real, dir), Path.Combine(root, dir));
        }

        File.Copy(Path.Combine(real, "Makefile"), Path.Combine(root, "Makefile"));
        var module = new ExpansionModule();
        var data = await module.LoadDataAsync(root, tmp.Combine("data"));
        var rows = EvolutionFixer.Find(data);
        Assert.Contains(rows, r => r.Original.TargetSpecies == "SPECIES_ALAKAZAM");
        Assert.Contains(rows, r => r.Original.TargetSpecies == "SPECIES_UMBREON" && r.Suggested == EvolutionFix.Stone);
        Assert.Contains(rows, r => r.Original.TargetSpecies == "SPECIES_ESPEON" && r.Suggested == EvolutionFix.Level);

        var report = string.Join("\n", rows.Select(r => $"{r.FromSpecies} -> {r.Original.TargetSpecies}: {EvolutionFixer.Describe(data, r.Original)} => {r.Suggested}: {EvolutionFixer.Describe(data, EvolutionFixer.Apply(r, r.Suggested))}"));
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, $"evolution-fixes-{Path.GetFileName(real)}.txt"), report);

        var (change, warnings) = EvolutionFixer.Build(data, rows.Select(r => (r, r.Suggested)));
        Assert.True(warnings.Count == 0, string.Join("\n", warnings));
        change!.Apply();
        var ctx = new ModuleSaveContext(new Safety.SafeFileWriter(root, allowedFiles: module.ManagedFiles(root)), root, root);
        await module.SaveDataAsync(data, ctx);

        // read back: nothing left to fix, and the Linking Cord / stones were written as real evolutions
        var reloaded = await module.LoadDataAsync(root, tmp.Combine("data"));
        Assert.Empty(EvolutionFixer.Find(reloaded));
        var kadabra = reloaded.Evolutions.Single(s => s.FromSpecies == "SPECIES_KADABRA");
        Assert.Contains(kadabra.Evolutions, e => e.Method == "EVO_LEVEL" && e.Parameter == "36" && e.TargetSpecies == "SPECIES_ALAKAZAM");
    }

    [Fact]
    public Task Real_emerald_project_evolutions_can_all_be_fixed_when_available() => RealProject(Environment.GetEnvironmentVariable("POKESTUDIO_EXPANSION_DIR"));

    [Fact]
    public Task Real_firered_project_evolutions_can_all_be_fixed_when_available() => RealProject(Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR"));
}
