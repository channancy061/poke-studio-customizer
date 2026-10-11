using System.Globalization;
using System.Runtime.CompilerServices;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Validation;

namespace PokeStudio.Modules.Gen3Rom;

/// <summary>
/// GBA ROM mode for Ruby, Sapphire, FireRed, LeafGreen and Emerald: edits a copy of the user's ROM. Tables are found by
/// their contents; fixed-size data is changed in place and lists that grow (level-up moves, trainer teams) move to the
/// ROM's free space.
/// </summary>
public sealed class Gen3RomModule : IGameModule, PokeStudio.Core.Graphics.ISpriteSource
{
    public const string ModuleId = "gba.gen3.rom";

    private static readonly Dictionary<string, string> Games = new(StringComparer.Ordinal)
    {
        ["AXVE"] = "Pokémon Ruby", ["AXPE"] = "Pokémon Sapphire", ["BPRE"] = "Pokémon FireRed", ["BPGE"] = "Pokémon LeafGreen", ["BPEE"] = "Pokémon Emerald",
    };

    private readonly ConditionalWeakTable<GameData, State> _states = new();

    public string Id => ModuleId;
    public string DisplayName => "GBA ROM (Ruby, Sapphire, FireRed, LeafGreen, Emerald)";
    public GamePlatform Platform => GamePlatform.Gba;

    public ModuleCapabilities Capabilities => ModuleCapabilities.Species | ModuleCapabilities.Moves | ModuleCapabilities.Learnsets
        | ModuleCapabilities.Evolutions | ModuleCapabilities.Trainers | ModuleCapabilities.TrainerAi | ModuleCapabilities.Encounters | ModuleCapabilities.ItemsAndMarts
        | ModuleCapabilities.Graphics | ModuleCapabilities.Text;

    public GameLimits Limits => new()
    {
        SpeciesNameLength = 10,
        MoveNameLength = 12,
        TrainerNameLength = 11,
        ItemNameLength = 13,
        MaxEvolutionsPerSpecies = Gen3Tables.EvolutionsPerSpecies,
        MaxPrice = 65_535,
        MaxTrainerItems = 4,
        MaxMovePp = 64,
        MinMovePriority = -7,
        MaxMovePriority = 7,
    };

    public IReadOnlyList<FeatureSupport> DescribeFeatures() =>
    [
        new(ModuleCapabilities.Species, SupportLevel.Supported),
        new(ModuleCapabilities.Moves, SupportLevel.Limited, "Descriptions and physical/special can't change: in these games a move's type decides physical or special."),
        new(ModuleCapabilities.Learnsets, SupportLevel.Limited, "Level-up moves, TM/HM lists and egg moves (up to 10) can be edited; move tutors not yet."),
        new(ModuleCapabilities.Evolutions, SupportLevel.Limited, "Up to 5 per Pokémon, using the methods these games know."),
        new(ModuleCapabilities.Trainers, SupportLevel.Supported),
        new(ModuleCapabilities.TrainerAi, SupportLevel.Supported),
        new(ModuleCapabilities.Encounters, SupportLevel.Limited, "Pokémon and levels can change; the number of slots is fixed."),
        new(ModuleCapabilities.ItemsAndMarts, SupportLevel.Limited, "Item names, prices and what each shop sells. Stock that grows with badges needs a project (decomp)."),
        new(ModuleCapabilities.Text, SupportLevel.Limited, "What people, signs and trainers say on the maps. Menus and battle messages aren't included; use a project (decomp) for those."),
        new(ModuleCapabilities.Graphics, SupportLevel.Supported),
        new(ModuleCapabilities.Models3d, SupportLevel.Never, "GBA games have no 3D models."),
    ];

    public DetectionResult Detect(GameSource source)
    {
        if (!source.Path.EndsWith(".gba", StringComparison.OrdinalIgnoreCase))
        {
            return DetectionResult.NoMatch;
        }

        if (!File.Exists(source.Path) || new FileInfo(source.Path).Length is < 0x200 or > 0x2000000)
        {
            return DetectionResult.NoMatch;
        }

        var header = new byte[0xC0];
        using (var f = File.OpenRead(source.Path))
        {
            f.ReadExactly(header);
        }

        var code = System.Text.Encoding.ASCII.GetString(header, 0xAC, 4);
        if (!Games.TryGetValue(code, out var title))
        {
            return DetectionResult.NoMatch;
        }

        var size = new FileInfo(source.Path).Length;
        var notes = new List<string> { "Edits a copy of your ROM; your original file is never changed." };
        if (size > 0x1000000)
        {
            notes.Add("This ROM is larger than the original game, so it's probably a hack. If its data was moved, some parts may not load.");
        }

        return new DetectionResult
        {
            IsMatch = true,
            Confidence = size == 0x1000000 ? 0.9 : 0.6,
            GameTitle = title + (header[0xBC] > 0 ? $" (revision {header[0xBC]})" : ""),
            Region = "English",
            Version = header[0xBC].ToString(CultureInfo.InvariantCulture),
            SourceKind = GameSourceKind.RomFile,
            Notes = notes,
        };
    }

    public Task<GameData> LoadDataAsync(string workingSourcePath, string projectDataDir, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var rom = new RomImage(File.ReadAllBytes(workingSourcePath));
            var tables = LoadTables(rom, projectDataDir);
            var (data, state) = Gen3Reader.Read(rom, tables);
            state.Path = workingSourcePath;
            Gen3EggMoves.Read(state, data);
            Gen3Dialogue.Load(state, data);
            _states.AddOrUpdate(data, state);
            return data;
        }, cancellationToken);

    public async Task SaveDataAsync(GameData data, ModuleSaveContext context, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue(data, out var state))
        {
            throw new InvalidOperationException("This data wasn't loaded by the GBA ROM module.");
        }

        var issues = Validate(data).Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (issues.Count > 0)
        {
            throw new InvalidOperationException(issues[0].Message);
        }

        Gen3Writer.Write(state, data);
        Gen3EggMoves.Write(state, data);
        Gen3Sprites.Write(state, data);
        Gen3Dialogue.Save(state, data);
        Gen3Marts.Save(state, data);
        await context.Writer.WriteAllBytesAsync(context.WorkingSourceDir, state.Rom.Bytes, cancellationToken);
        var (freshData, fresh) = Gen3Reader.Read(state.Rom, state.Tables);
        fresh.Path = state.Path;
        Gen3EggMoves.Read(fresh, freshData);
        Gen3Dialogue.Load(fresh, freshData); // positions and places only: the edited text stays in data
        _states.AddOrUpdate(data, fresh);
    }

    public IReadOnlyList<ValidationIssue> Validate(GameData data)
    {
        var issues = Gen3Writer.Check(data, Limits).ToList();
        issues.AddRange(Gen3EggMoves.Check(data));
        _states.TryGetValue(data, out var state);
        foreach (var t in data.Texts)
        {
            if (state is not null && state.Texts.TryGetValue(t.Key, out var before) && before.Text == t.Text)
            {
                continue;
            }

            foreach (var problem in Gen3Dialogue.Check(t.Text))
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, "Text", t.Place, problem) { TargetKey = t.Key });
            }
        }

        return issues;
    }

    public PokeStudio.Core.Graphics.SpriteRules? Rules(GameData data, string species, PokeStudio.Core.Graphics.SpriteKind kind) =>
        _states.TryGetValue(data, out var s) ? Gen3Sprites.Rules(s, kind) : null;

    public PokeStudio.Core.Graphics.IndexedImage? Load(GameData data, string species, PokeStudio.Core.Graphics.SpriteKind kind) =>
        _states.TryGetValue(data, out var s) ? Gen3Sprites.Load(s, species, kind) : null;

    public IReadOnlyList<PokeStudio.Core.Graphics.Rgb>? LoadPalette(GameData data, string species, bool shiny) =>
        _states.TryGetValue(data, out var s) ? Gen3Sprites.Palette(s, species, shiny) : null;

    public const string TablesFileName = "gba-tables.json";

    private sealed record TablesFile(string GameCode, int Length, Gen3Tables Tables);

    /// <summary>
    /// The tables are found by their original contents the first time; their places are remembered in the project, because
    /// after edits (Bulbasaur's stats changed, say) the contents no longer match.
    /// </summary>
    private static Gen3Tables LoadTables(RomImage rom, string projectDataDir)
    {
        var file = Path.Combine(projectDataDir, TablesFileName);
        if (File.Exists(file))
        {
            try
            {
                var saved = System.Text.Json.JsonSerializer.Deserialize<TablesFile>(File.ReadAllText(file), PokeStudio.Core.Storage.JsonStore.Options);
                if (saved is not null && saved.GameCode == rom.GameCode && saved.Length == rom.Length)
                {
                    if (saved.Tables.SpritesLocated && saved.Tables.EggMovesLocated && saved.Tables.IconsLocated)
                    {
                        return saved.Tables;
                    }

                    // a project from before graphics or egg move support: find those tables too (their contents are still original)
                    var upgraded = saved.Tables;
                    if (!upgraded.SpritesLocated)
                    {
                        upgraded = upgraded.WithSprites(rom);
                    }

                    if (!upgraded.IconsLocated)
                    {
                        upgraded = upgraded.WithIcons(rom);
                    }

                    if (!upgraded.EggMovesLocated)
                    {
                        upgraded = upgraded with { EggMovePointers = Gen3EggMoves.Locate(rom, upgraded.MoveCount), EggMovesLocated = true };
                    }

                    File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(saved with { Tables = upgraded }, PokeStudio.Core.Storage.JsonStore.Options));
                    return upgraded;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // found again below
            }
        }

        var tables = (Gen3Tables.Locate(rom, out var problem) ?? throw new InvalidDataException(problem)).WithSprites(rom);
        tables = tables.WithIcons(rom) with { EggMovePointers = Gen3EggMoves.Locate(rom, tables.MoveCount), EggMovesLocated = true };
        Directory.CreateDirectory(projectDataDir);
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(new TablesFile(rom.GameCode, rom.Length, tables), PokeStudio.Core.Storage.JsonStore.Options));
        return tables;
    }

    /// <summary>Where each trainer is fought isn't stored with the trainer in a ROM.</summary>
    public IReadOnlyDictionary<string, string> TrainerPlaces(GameData data) =>
        _states.TryGetValue(data, out var s) ? new Dictionary<string, string>(s.TrainerPlaces) : new Dictionary<string, string>();

    internal sealed class State
    {
        public required RomImage Rom { get; init; }
        public required Gen3Tables Tables { get; init; }
        public string Path { get; set; } = "";
        public Dictionary<string, int> SpeciesIndex { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> MoveIndex { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ItemIndex { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> AbilityIndex { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ClassIndex { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Species> Species { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Move> Moves { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Learnset> Learnsets { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, EvolutionSet> Evolutions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (Trainer Trainer, int Index)> Trainers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (EncounterTable Table, int Header)> Encounters { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Item> Items { get; } = new(StringComparer.Ordinal);
        public List<string> TmMoves { get; } = [];
        public bool EggMovesFound { get; set; }
        public Dictionary<string, string> TrainerPlaces { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Gen3Dialogue.Message> Messages { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, TextEntry> Texts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Mart> Marts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Gen3Marts.Shop> Shops { get; } = new(StringComparer.Ordinal);
    }
}
