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
}

public sealed class DemoGameModule : IGameModule
{
    public const string ModuleId = "demo.sample";
    public const string EditsFileName = "demo-species.json";
    public const int MaxNameLength = 10;

    public string Id => ModuleId;
    public string DisplayName => "Demo sample game";
    public GamePlatform Platform => GamePlatform.Demo;
    public ModuleCapabilities Capabilities => ModuleCapabilities.Species | ModuleCapabilities.Text;

    public IReadOnlyList<FeatureSupport> DescribeFeatures() =>
    [
        new(ModuleCapabilities.Species, SupportLevel.Supported),
        new(ModuleCapabilities.Text, SupportLevel.Limited, $"Names can be up to {MaxNameLength} letters."),
        new(ModuleCapabilities.Moves, SupportLevel.NotYet, "The demo game has no moves yet."),
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
        var species = File.Exists(edits)
            ? await JsonStore.ReadAsync<List<Species>>(edits, cancellationToken)
            : (await JsonStore.ReadAsync<DemoGameFile>(workingSourcePath, cancellationToken))?.Species;

        var data = new GameData();
        data.Species.AddRange(species ?? throw new InvalidDataException("The demo game file has no creatures."));
        return data;
    }

    public Task SaveDataAsync(GameData data, ModuleSaveContext context, CancellationToken cancellationToken = default) =>
        context.Writer.WriteAllTextAsync(
            Path.Combine(context.ProjectDataDir, EditsFileName),
            JsonSerializer.Serialize(data.Species, JsonStore.Options),
            cancellationToken);
}
