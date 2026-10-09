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
    public List<EvolutionMethod> Evolutions { get; } = [];
    public List<Trainer> Trainers { get; } = [];
    public List<Encounter> Encounters { get; } = [];
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

    Task<GameData> LoadDataAsync(string workingSourcePath, string projectDataDir, CancellationToken cancellationToken = default);

    Task SaveDataAsync(GameData data, ModuleSaveContext context, CancellationToken cancellationToken = default);
}
