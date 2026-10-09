using System.Text.Json;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Storage;

namespace PokeStudio.Modules.Demo;

/// <summary>File format of the invented sample game.</summary>
public sealed record DemoGameFile
{
    public const string FormatId = "poke-studio-demo";
    public const string Extension = ".demogame.json";

    public string Format { get; init; } = FormatId;
    public int Version { get; init; } = 1;
    public string Title { get; init; } = "";
    public List<Species> Species { get; init; } = [];
    public List<Learnset> Learnsets { get; init; } = [];
    public List<EvolutionSet> Evolutions { get; init; } = [];
}

public sealed class DemoGameModule : IGameModule
{
    public const string ModuleId = "demo.sample";
    public const string EditsFileName = "demo-edits.json";
    public const int MaxNameLength = 10;

    public string Id => ModuleId;
    public string DisplayName => "Demo sample game";
    public GamePlatform Platform => GamePlatform.Demo;

    public ModuleCapabilities Capabilities =>
        ModuleCapabilities.Species | ModuleCapabilities.Learnsets | ModuleCapabilities.Evolutions | ModuleCapabilities.Text;

    public GameLimits Limits => new() { SpeciesNameLength = MaxNameLength };

    public IReadOnlyList<FeatureSupport> DescribeFeatures() =>
    [
        new(ModuleCapabilities.Species, SupportLevel.Supported),
        new(ModuleCapabilities.Learnsets, SupportLevel.Supported),
        new(ModuleCapabilities.Evolutions, SupportLevel.Supported),
        new(ModuleCapabilities.Text, SupportLevel.Limited, $"Names can be up to {MaxNameLength} letters."),
        new(ModuleCapabilities.Moves, SupportLevel.NotYet, "The demo game has no move editor yet."),
        new(ModuleCapabilities.Trainers, SupportLevel.NotYet, "The demo game has no trainers yet."),
        new(ModuleCapabilities.TrainerAi, SupportLevel.NotYet, "Not available for this game yet."),
        new(ModuleCapabilities.Models3d, SupportLevel.Never, "The demo game has no 3D models."),
    ];

    public DetectionResult Detect(GameSource source)
    {
        if (!File.Exists(source.Path) || !source.Path.EndsWith(DemoGameFile.Extension, StringComparison.OrdinalIgnoreCase))
        {
            return DetectionResult.NoMatch;
        }

        try
        {
            using var stream = File.OpenRead(source.Path);
            var file = JsonSerializer.Deserialize<DemoGameFile>(stream, JsonStore.Options);
            if (file?.Format != DemoGameFile.FormatId)
            {
                return DetectionResult.NoMatch;
            }

            return new DetectionResult
            {
                IsMatch = true,
                Confidence = 1.0,
                GameTitle = file.Title,
                Version = $"v{file.Version}",
                SourceKind = GameSourceKind.DemoFile,
                Notes = ["This is an invented sample game for trying the app."],
            };
        }
        catch (JsonException)
        {
            return DetectionResult.NoMatch;
        }
    }

    public async Task<GameData> LoadDataAsync(string workingSourcePath, string projectDataDir, CancellationToken cancellationToken = default)
    {
        var edits = Path.Combine(projectDataDir, EditsFileName);
        var file = await JsonStore.ReadAsync<DemoGameFile>(File.Exists(edits) ? edits : workingSourcePath, cancellationToken)
            ?? throw new InvalidDataException("The demo game file is empty.");
        if (file.Species.Count == 0)
        {
            throw new InvalidDataException("The demo game file has no Pokémon.");
        }

        var data = new GameData();
        data.Species.AddRange(file.Species);
        data.Learnsets.AddRange(file.Learnsets);
        data.Evolutions.AddRange(file.Evolutions);
        FillCatalog(data.Catalog);
        return data;
    }

    public Task SaveDataAsync(GameData data, ModuleSaveContext context, CancellationToken cancellationToken = default)
    {
        var file = new DemoGameFile { Title = "Demo Valley (edited)", Species = data.Species, Learnsets = data.Learnsets, Evolutions = data.Evolutions };
        return context.Writer.WriteAllTextAsync(
            Path.Combine(context.ProjectDataDir, EditsFileName),
            JsonSerializer.Serialize(file, JsonStore.Options),
            cancellationToken);
    }

    private static void FillCatalog(GameCatalog c)
    {
        foreach (var t in Enum.GetNames<ElementType>())
        {
            c.Types.Add(new CatalogEntry("TYPE_" + t.ToUpperInvariant(), t));
        }

        foreach (var a in new[] { "NONE", "OVERGROW", "BLAZE", "TORRENT", "STATIC", "STURDY", "LEAF_GUARD", "CHLOROPHYLL" })
        {
            c.Abilities.Add(new CatalogEntry("ABILITY_" + a, a == "NONE" ? "None" : GameCatalog.Prettify("ABILITY_" + a)));
        }

        foreach (var m in new[] { "TACKLE", "GROWL", "VINE_WHIP", "SLEEP_POWDER", "SOLAR_BEAM", "CHARM", "RAZOR_LEAF", "EMBER", "WATER_GUN", "THUNDER_SHOCK" })
        {
            c.Moves.Add(new CatalogEntry("MOVE_" + m, GameCatalog.Prettify("MOVE_" + m)));
        }

        foreach (var g in new[] { "FAST", "MEDIUM_FAST", "MEDIUM_SLOW", "SLOW", "ERRATIC", "FLUCTUATING" })
        {
            c.GrowthRates.Add(new CatalogEntry("GROWTH_" + g, GameCatalog.Prettify("GROWTH_" + g)));
        }

        c.EvolutionMethods.Add(new CatalogEntry("EVO_LEVEL", "Reaches a level", "Evolves after levelling up to the level you choose."));
        c.EvolutionMethods.Add(new CatalogEntry("EVO_ITEM", "Uses an item", "Evolves when you use an item on it."));
        c.EvolutionMethods.Add(new CatalogEntry("EVO_TRADE", "Is traded", "Evolves when traded."));
    }
}
