using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Modules.Demo;

namespace PokeStudio.Core.Tests;

public class ModuleTests
{
    private sealed class FakeModule(string id, double confidence, bool throws = false) : IGameModule
    {
        public string Id => id;
        public string DisplayName => id;
        public GamePlatform Platform => GamePlatform.Gba;
        public ModuleCapabilities Capabilities => ModuleCapabilities.Species;
        public IReadOnlyList<FeatureSupport> DescribeFeatures() => [];

        public DetectionResult Detect(GameSource source) => throws
            ? throw new IOException("unreadable")
            : confidence > 0 ? new DetectionResult { IsMatch = true, Confidence = confidence, GameTitle = id } : DetectionResult.NoMatch;

        public Task<GameData> LoadDataAsync(string w, string d, CancellationToken c = default) => Task.FromResult(new GameData());
        public Task SaveDataAsync(GameData data, ModuleSaveContext context, CancellationToken c = default) => Task.CompletedTask;
    }

    [Fact]
    public void Registry_picks_highest_confidence_and_ignores_failing_modules()
    {
        var registry = new ModuleRegistry()
            .Register(new FakeModule("low", 0.3))
            .Register(new FakeModule("broken", 0, throws: true))
            .Register(new FakeModule("high", 0.9))
            .Register(new FakeModule("none", 0));

        var match = registry.Detect(new GameSource("x", GameSourceKind.RomFile));

        Assert.NotNull(match);
        Assert.Equal("high", match.Value.Module.Id);
    }

    [Fact]
    public void Registry_returns_null_when_nothing_matches()
    {
        var registry = new ModuleRegistry().Register(new FakeModule("none", 0));
        Assert.Null(registry.Detect(new GameSource("x", GameSourceKind.RomFile)));
    }

    [Fact]
    public void Registry_rejects_duplicate_ids()
    {
        var registry = new ModuleRegistry().Register(new FakeModule("a", 1));
        Assert.Throws<InvalidOperationException>(() => registry.Register(new FakeModule("a", 1)));
    }

    [Fact]
    public void Demo_module_detects_sample_and_rejects_other_files()
    {
        using var tmp = new TempDir();
        var module = new DemoGameModule();

        var hit = module.Detect(new GameSource(Fixtures.DemoGameFile, GameSourceKind.DemoFile));
        Assert.True(hit.IsMatch);
        Assert.Contains("Demo Valley", hit.GameTitle, StringComparison.Ordinal);

        var other = tmp.Combine("other.demogame.json");
        File.WriteAllText(other, """{ "format": "something-else" }""");
        Assert.False(module.Detect(new GameSource(other, GameSourceKind.DemoFile)).IsMatch);

        var notJson = tmp.Combine("broken.demogame.json");
        File.WriteAllText(notJson, "not json");
        Assert.False(module.Detect(new GameSource(notJson, GameSourceKind.DemoFile)).IsMatch);
    }

    [Fact]
    public void Demo_module_capabilities_match_its_feature_descriptions()
    {
        var module = new DemoGameModule();
        var supported = module.DescribeFeatures()
            .Where(f => f.Level is SupportLevel.Supported or SupportLevel.Limited)
            .Aggregate(ModuleCapabilities.None, (acc, f) => acc | f.Feature);

        Assert.Equal(module.Capabilities, supported);
        Assert.All(module.DescribeFeatures().Where(f => f.Level != SupportLevel.Supported), f => Assert.False(string.IsNullOrWhiteSpace(f.Reason)));
    }

    [Fact]
    public async Task Demo_module_round_trips_edits_through_project_data()
    {
        using var tmp = new TempDir();
        var (project, _) = await Fixtures.CreateDemoProjectAsync(tmp);
        var module = new DemoGameModule();

        var data = await module.LoadDataAsync(project.WorkingSourcePath, project.DataDir);
        var leafling = data.Species[0];
        data.Species[0] = leafling with { Stats = leafling.Stats with { Speed = 90 } };
        await module.SaveDataAsync(data, project.CreateSaveContext());

        var reloaded = await module.LoadDataAsync(project.WorkingSourcePath, project.DataDir);
        Assert.Equal(90, reloaded.Species[0].Stats.Speed);
        Assert.Equal(375, reloaded.Species[0].Stats.Total);
    }
}
