using System.Runtime.CompilerServices;
using System.Text.Json;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Storage;
using PokeStudio.Core.Validation;

namespace PokeStudio.Modules.Nds;

/// <summary>
/// NDS ROM mode for Diamond, Pearl, Platinum, HeartGold, SoulSilver, Black and White (Black 2 / White 2 untested):
/// reads and writes the game's own archives inside a copy of the user's ROM.
/// </summary>
public sealed class NdsModule : IGameModule
{
    public const string ModuleId = "nds.gen45.rom";
    public const string TablesFileName = "nds-tables.json";

    private readonly ConditionalWeakTable<GameData, NdsState> _states = new();

    public string Id => ModuleId;
    public string DisplayName => "DS ROM (Diamond, Pearl, Platinum, HeartGold, SoulSilver, Black, White)";
    public GamePlatform Platform => GamePlatform.Nds;

    public ModuleCapabilities Capabilities => ModuleCapabilities.Species | ModuleCapabilities.Moves | ModuleCapabilities.Learnsets
        | ModuleCapabilities.Evolutions | ModuleCapabilities.Trainers | ModuleCapabilities.TrainerAi | ModuleCapabilities.Encounters | ModuleCapabilities.ItemsAndMarts | ModuleCapabilities.Text;

    public GameLimits Limits => new()
    {
        SpeciesNameLength = 10,
        MoveNameLength = 16,
        TrainerNameLength = 12,
        ItemNameLength = 16,
        MaxEvolutionsPerSpecies = 7,
        MaxPrice = 65_535,
        MaxTrainerItems = 4,
        GbaTextBreaks = false,
    };

    public IReadOnlyList<FeatureSupport> DescribeFeatures() =>
    [
        new(ModuleCapabilities.Species, SupportLevel.Supported),
        new(ModuleCapabilities.Moves, SupportLevel.Limited, "Descriptions can't change yet."),
        new(ModuleCapabilities.Learnsets, SupportLevel.Limited, "Level-up moves, TM/HM lists and egg moves (Diamond/Pearl/Platinum: within the game's current room); tutors not yet."),
        new(ModuleCapabilities.Evolutions, SupportLevel.Limited, "Up to 7 per Pokémon, using the methods these games know."),
        new(ModuleCapabilities.Trainers, SupportLevel.Supported),
        new(ModuleCapabilities.TrainerAi, SupportLevel.Supported),
        new(ModuleCapabilities.Encounters, SupportLevel.Limited, "Areas are numbered (their names live in the game's program). The number of slots is fixed."),
        new(ModuleCapabilities.ItemsAndMarts, SupportLevel.Limited, "Item names and prices; shops not yet."),
        new(ModuleCapabilities.Text, SupportLevel.Limited, "Every message file (Black/White: the story text). Places show as file numbers; {CMD …} codes insert names and numbers and must stay."),
        new(ModuleCapabilities.Graphics, SupportLevel.NotYet, "The graphics studio comes in a later phase."),
        new(ModuleCapabilities.Models3d, SupportLevel.Never, "These games use sprites, not 3D models."),
    ];

    public DetectionResult Detect(GameSource source)
    {
        if (!File.Exists(source.Path) || !source.Path.EndsWith(".nds", StringComparison.OrdinalIgnoreCase) || new FileInfo(source.Path).Length < 0x200)
        {
            return DetectionResult.NoMatch;
        }

        var header = new byte[0x20];
        using (var f = File.OpenRead(source.Path))
        {
            f.ReadExactly(header);
        }

        var code = System.Text.Encoding.ASCII.GetString(header, 0x0C, 4);
        if (NdsGame.For(code) is not { } game)
        {
            return DetectionResult.NoMatch;
        }

        var notes = new List<string> { "Edits a copy of your ROM; your original file is never changed." };
        if (!game.Tested)
        {
            notes.Add("This game hasn't been tested with Poke Studio yet: check your changes in an emulator.");
        }

        if ((header[0x12] & 2) != 0)
        {
            notes.Add("DSi-enhanced game: edited copies play in DS mode (emulators and DS consoles); DSi-mode checks may refuse them.");
        }

        return new DetectionResult
        {
            IsMatch = true,
            Confidence = 0.9,
            GameTitle = game.Title,
            Region = code[3] switch { 'E' or 'O' => "English (US)", 'P' => "English (Europe)", 'J' => "Japanese", _ => code[3].ToString() },
            SourceKind = GameSourceKind.RomFile,
            Notes = notes,
        };
    }

    public Task<GameData> LoadDataAsync(string workingSourcePath, string projectDataDir, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var rom = new NdsRom(File.ReadAllBytes(workingSourcePath));
            var game = NdsGame.For(rom.GameCode) ?? throw new InvalidDataException("This DS game isn't supported.");
            var tables = LoadTables(rom, game, projectDataDir);
            var (data, state) = NdsReader.Read(rom, game, tables);
            NdsEggMoves.Read(state, data);
            NdsDialogue.Load(state, data);
            _states.AddOrUpdate(data, state);
            return data;
        }, cancellationToken);

    public async Task SaveDataAsync(GameData data, ModuleSaveContext context, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue(data, out var state))
        {
            throw new InvalidOperationException("This data wasn't loaded by the DS ROM module.");
        }

        var errors = Validate(data).Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(errors[0].Message);
        }

        NdsWriter.Write(state, data);
        NdsEggMoves.Write(state, data);
        NdsDialogue.Save(state, data);
        await context.Writer.WriteAllBytesAsync(context.WorkingSourceDir, state.Rom.Bytes, cancellationToken);
        var (freshData, fresh) = NdsReader.Read(state.Rom, state.Game, state.Tables);
        NdsEggMoves.Read(fresh, freshData);
        NdsDialogue.Load(fresh, new GameData()); // the edited text stays in data
        _states.AddOrUpdate(data, fresh);
    }

    public IReadOnlyList<ValidationIssue> Validate(GameData data) =>
        _states.TryGetValue(data, out var state) ? [.. NdsWriter.Check(state, data, Limits), .. NdsEggMoves.Check(state, data), .. NdsDialogue.Check(state, data)] : [];

    /// <summary>Which text files hold which names (found by content once, then remembered: renames would hide them).</summary>
    private static NdsTables LoadTables(NdsRom rom, NdsGame game, string projectDataDir)
    {
        var file = Path.Combine(projectDataDir, TablesFileName);
        if (File.Exists(file))
        {
            try
            {
                if (JsonSerializer.Deserialize<NdsTables>(File.ReadAllText(file), JsonStore.Options) is { } saved && saved.GameCode == rom.GameCode)
                {
                    if (!saved.EggMovesLocated)
                    {
                        // a project from before egg move support
                        saved = saved.WithEggMoves(rom, game);
                        File.WriteAllText(file, JsonSerializer.Serialize(saved, JsonStore.Options));
                    }

                    return saved;
                }
            }
            catch (JsonException)
            {
                // found again below
            }
        }

        var tables = NdsTables.Locate(rom, game);
        Directory.CreateDirectory(projectDataDir);
        File.WriteAllText(file, JsonSerializer.Serialize(tables, JsonStore.Options));
        return tables;
    }
}

/// <summary>Text files of the names and the TM list, per game.</summary>
internal sealed record NdsTables
{
    public required string GameCode { get; init; }
    public required int SpeciesNames { get; init; }
    public required int MoveNames { get; init; }
    public int MoveNamesUpper { get; init; } = -1;
    public required int AbilityNames { get; init; }
    public int AbilityNamesUpper { get; init; } = -1;
    public required int ItemNames { get; init; }
    public int ItemNamesUpper { get; init; } = -1;
    public int TrainerNames { get; init; } = -1;
    public int ClassNames { get; init; } = -1;
    public IReadOnlyList<int> TmMoves { get; init; } = [];

    /// <summary>Diamond/Pearl/Platinum: the overlay and offset of the egg move table (-1 = elsewhere or not found).</summary>
    public int EggMoveOverlay { get; init; } = -1;

    public int EggMoveOffset { get; init; } = -1;

    public bool EggMovesLocated { get; init; }

    public static NdsTables Locate(NdsRom rom, NdsGame game)
    {
        var text = new Narc(rom.Read(game.Text));
        var lists = new List<List<string>?>();
        for (var i = 0; i < text.Members.Count; i++)
        {
            try
            {
                lists.Add(game.Generation == 4 ? Gen4Text.ReadFile(text.Members[i], out _) : Gen5Text.ReadFile(text.Members[i]));
            }
            catch (ArgumentOutOfRangeException)
            {
                lists.Add(null);
            }
        }

        int Find(string second, int minCount) => lists.FindIndex(l => l is { Count: var c } && c >= minCount && l[1] == second);
        var species = Find("BULBASAUR", game.SpeciesCount) is var s4 and >= 0 ? s4 : Find("Bulbasaur", game.SpeciesCount);
        var moves = Find("Pound", 400);
        var abilities = Find("Stench", 100);
        var items = Find("Master Ball", 400);
        if (species < 0 || moves < 0 || abilities < 0 || items < 0)
        {
            throw new InvalidDataException("Couldn't find the name lists in this game's text. It may be a hack that changed them.");
        }

        var trainers = new Narc(rom.Read(game.TrainerData)).Members;
        var trainerCount = trainers.Count;
        var maxClass = trainers.Max(t => t.Length > 1 ? t[1] : 0);
        var trainerNames = lists.FindIndex(l => l is { Count: var c } && c == trainerCount && l.Count(n => n.Length > 1 && char.IsLetter(n[0])) > trainerCount / 2);
        string[] bosses = ["Leader", "Gym Leader", "Elite Four", "Champion"];
        // the class list has an entry per class (a few spare), with boss titles among them
        var classNames = lists.Select((l, i) => (l, i))
            .Where(x => x.l is { Count: var c } && c > maxClass && c < maxClass + 40 && x.l.Count(n => bosses.Contains(n, StringComparer.OrdinalIgnoreCase)) >= 2)
            .OrderBy(x => x.l!.Count - maxClass).Select(x => x.i).DefaultIfEmpty(-1).First();

        // the TM list is in the main program (or, in some games, one of its overlays)
        var arm9 = Arm9.Read(rom);
        var tm = Arm9.FindTmList(arm9);
        if (tm < 0)
        {
            foreach (var overlay in Arm9.Overlays(rom))
            {
                tm = Arm9.FindTmList(overlay);
                if (tm >= 0)
                {
                    arm9 = overlay;
                    break;
                }
            }
        }

        var tmCount = game.Generation == 4 ? 100 : 101;
        var tmMoves = tm < 0 ? [] : Enumerable.Range(0, tmCount).Select(i => (int)BitConverter.ToUInt16(arm9, tm + (2 * i))).ToList();
        return new NdsTables
        {
            GameCode = rom.GameCode,
            SpeciesNames = species,
            MoveNames = moves,
            MoveNamesUpper = Find("POUND", 400),
            AbilityNames = abilities,
            AbilityNamesUpper = Find("STENCH", 100),
            ItemNames = items,
            ItemNamesUpper = Find("MASTER BALL", 400),
            TrainerNames = trainerNames,
            ClassNames = classNames,
            TmMoves = tmMoves,
        }.WithEggMoves(rom, game);
    }

    /// <summary>Finds the egg move table when it lives in an overlay (Diamond/Pearl/Platinum).</summary>
    public NdsTables WithEggMoves(NdsRom rom, NdsGame game)
    {
        if (game.Generation != 4 || rom.Exists("a/2/2/9"))
        {
            return this with { EggMovesLocated = true };
        }

        var moves = new Narc(rom.Read(game.Moves)).Members.Count;
        var (overlay, offset) = NdsEggMoves.LocateOverlayTable(rom, moves);
        return this with { EggMoveOverlay = overlay, EggMoveOffset = offset, EggMovesLocated = true };
    }
}

internal sealed class NdsState
{
    public required NdsRom Rom { get; init; }
    public required NdsGame Game { get; init; }
    public required NdsTables Tables { get; init; }
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
    public Dictionary<string, (EncounterTable Table, int Index)> Encounters { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Item> Items { get; } = new(StringComparer.Ordinal);
    public List<string> TmMoves { get; } = [];
    public Dictionary<string, TextEntry> Texts { get; } = new(StringComparer.Ordinal);
    public bool EggMovesFound { get; set; }
}
