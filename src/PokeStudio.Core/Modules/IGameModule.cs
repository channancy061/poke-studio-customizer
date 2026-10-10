using PokeStudio.Core.Models;

namespace PokeStudio.Core.Modules;

/// <summary>Features a module can offer. The UI shows a screen only when its capability is present.</summary>
[Flags]
public enum ModuleCapabilities
{
    None = 0,
    Species = 1 << 0,
    Moves = 1 << 1,
    Learnsets = 1 << 2,
    Evolutions = 1 << 3,
    Trainers = 1 << 4,
    TrainerAi = 1 << 5,
    Graphics = 1 << 6,
    Models3d = 1 << 7,
    Encounters = 1 << 8,
    ItemsAndMarts = 1 << 9,
    Text = 1 << 10,
    NewGameWizard = 1 << 11,
    Randomizer = 1 << 12,
    Build = 1 << 13,
}

public enum SupportLevel
{
    Supported,
    /// <summary>Works with restrictions the user should know about.</summary>
    Limited,
    /// <summary>Planned; shown locked with the reason.</summary>
    NotYet,
    /// <summary>The game format can never support it; hidden and counted.</summary>
    Never,
}

/// <summary>Plain-English description of one feature for the current game.</summary>
public sealed record FeatureSupport(ModuleCapabilities Feature, SupportLevel Level, string? Reason = null);

public enum GameSourceKind
{
    RomFile,
    DecompProject,
    ExtractedRomFs,
    /// <summary>Test/demo data used to exercise the pipeline.</summary>
    DemoFile,
}

/// <summary>Something the user pointed us at: a file or folder they own.</summary>
public sealed record GameSource(string Path, GameSourceKind Kind);

public sealed record DetectionResult
{
    public static DetectionResult NoMatch { get; } = new() { IsMatch = false };

    public bool IsMatch { get; init; }

    /// <summary>0..1; the registry picks the highest-confidence match.</summary>
    public double Confidence { get; init; }

    public string GameTitle { get; init; } = "";
    public string? Region { get; init; }
    public string? Version { get; init; }
    public GameSourceKind SourceKind { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Everything a module loaded, in storage-independent form.</summary>
public sealed class GameData
{
    public List<Species> Species { get; } = [];
    public List<Move> Moves { get; } = [];
    public List<Learnset> Learnsets { get; } = [];
    public List<EvolutionSet> Evolutions { get; } = [];
    public List<Trainer> Trainers { get; } = [];
    public List<EncounterTable> Encounters { get; } = [];
    public List<EncounterSlotChances> EncounterChances { get; } = [];
    public List<Item> Items { get; } = [];
    public List<Mart> Marts { get; } = [];
    public List<TextEntry> Texts { get; } = [];

    /// <summary>Level caps and related settings, or null when the game has none.</summary>
    public GameRules? Rules { get; set; }
    public GameCatalog Catalog { get; } = new();

    public Learnset? LearnsetFor(Species species) =>
        species.LearnsetKey is { } key ? Learnsets.FirstOrDefault(l => l.Key == key) : null;
}

/// <summary>The game's limits, so editors and validation can explain them in plain words.</summary>
public sealed record GameLimits
{
    public int SpeciesNameLength { get; init; } = 10;
    public int CategoryNameLength { get; init; } = 12;
    public int MaxLevel { get; init; } = 100;
    public int MovesKnown { get; init; } = 4;
    public int DexEntryMaxLines { get; init; } = 4;
    public int DexEntryLineLength { get; init; } = 42;
    public int MaxEvolutionsPerSpecies { get; init; } = 10;
    public int MoveNameLength { get; init; } = 12;
    public int MaxMovePower { get; init; } = 255;
    public int MaxMovePp { get; init; } = 64;
    public int MinMovePriority { get; init; } = -7;
    public int MaxMovePriority { get; init; } = 7;
    public int MoveDescriptionMaxLines { get; init; } = 4;
    public int TrainerNameLength { get; init; } = 10;
    public int MaxPartySize { get; init; } = 6;
    public int MaxTrainerItems { get; init; } = 4;
    public int NicknameLength { get; init; } = 10;
    public int ItemNameLength { get; init; } = 14;
    public int MaxPrice { get; init; } = 999_999;

    /// <summary>Width of a dialogue line in pixels (null = not checked).</summary>
    public int? TextLineWidth { get; init; }
    public int TextLinesPerBox { get; init; } = 2;
}

/// <summary>Context handed to a module when saving: where it may write.</summary>
public sealed record ModuleSaveContext(Safety.SafeFileWriter Writer, string ProjectDataDir, string WorkingSourceDir);

/// <summary>A plug-in that understands one platform or game family.</summary>
public interface IGameModule
{
    /// <summary>Stable id stored in project files, e.g. "gba.emerald.decomp".</summary>
    string Id { get; }

    string DisplayName { get; }
    GamePlatform Platform { get; }
    ModuleCapabilities Capabilities { get; }

    /// <summary>Per-feature support with plain-English reasons, including unsupported ones.</summary>
    IReadOnlyList<FeatureSupport> DescribeFeatures();

    DetectionResult Detect(GameSource source);

    GameLimits Limits => new();

    /// <summary>Width in pixels of one line of dialogue in the game's font, or null if the module can't measure it.</summary>
    int? MeasureText(string line) => null;

    /// <summary>Generations whose movesets this game can switch to (empty = Modernize isn't available).</summary>
    IReadOnlyList<Modernize.GenerationChoice> ModernizeGenerations(GameData data) => [];

    /// <summary>Learnsets (and optionally stats, types, abilities) as they are in <paramref name="generation"/>'s games.</summary>
    Task<Modernize.ModernizeReference?> LoadModernizeReferenceAsync(GameData data, int generation, CancellationToken cancellationToken = default) =>
        Task.FromResult<Modernize.ModernizeReference?>(null);

    /// <summary>How to turn the project into a playable game, or null when this game type can't be built.</summary>
    PokeStudio.Core.Build.BuildDefinition? Build => null;

    /// <summary>The build for one project (e.g. its ROM name differs between Emerald and FireRed bases).</summary>
    PokeStudio.Core.Build.BuildDefinition? BuildFor(string workingSourcePath) => Build;

    /// <summary>
    /// True when the module edits the user's folder directly (decomp projects) instead of a copy.
    /// Such modules must list every file they may write in <see cref="ManagedFiles"/>.
    /// </summary>
    bool WorksInPlace => false;

    /// <summary>Paths (relative to the working source) this module may write; backed up before each save.</summary>
    IReadOnlyList<string> ManagedFiles(string workingSourcePath) => [];

    /// <summary>Game-specific checks run before saving. General checks live in <see cref="Validation.GameValidator"/>.</summary>
    IReadOnlyList<Validation.ValidationIssue> Validate(GameData data) => [];

    Task<GameData> LoadDataAsync(string workingSourcePath, string projectDataDir, CancellationToken cancellationToken = default);

    Task SaveDataAsync(GameData data, ModuleSaveContext context, CancellationToken cancellationToken = default);
}
