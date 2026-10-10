using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Storage;
using PokeStudio.Core.Validation;

namespace PokeStudio.Modules.N3ds;

/// <summary>
/// 3DS mode for X, Y, Omega Ruby and Alpha Sapphire: reads the user's decrypted game in place and writes every change as
/// a LayeredFS mod — into the project's "romfs" folder and, when Citra is installed, its load/mods folder for the game.
/// </summary>
public sealed class N3dsModule : IGameModule
{
    public const string ModuleId = "3ds.gen6.rom";
    public const string TablesFileName = "3ds-tables.json";

    private static readonly Dictionary<ulong, string> Games = new()
    {
        [0x0004000000055D00] = "Pokémon X", [0x0004000000055E00] = "Pokémon Y",
        [0x000400000011C400] = "Pokémon Omega Ruby", [0x000400000011C500] = "Pokémon Alpha Sapphire",
    };

    private readonly ConditionalWeakTable<GameData, State> _states = new();

    /// <summary>Where Citra keeps mods (null = the usual %APPDATA%\Citra\load\mods); tests point it elsewhere.</summary>
    public string? CitraModsFolder { get; init; }

    public string Id => ModuleId;
    public string DisplayName => "3DS game (X, Y, Omega Ruby, Alpha Sapphire)";
    public GamePlatform Platform => GamePlatform.N3ds;
    public bool WorksInPlace => true;

    public ModuleCapabilities Capabilities => ModuleCapabilities.Species | ModuleCapabilities.Moves | ModuleCapabilities.Learnsets
        | ModuleCapabilities.Evolutions | ModuleCapabilities.Trainers | ModuleCapabilities.TrainerAi | ModuleCapabilities.ItemsAndMarts;

    public GameLimits Limits => new() { SpeciesNameLength = 12, MoveNameLength = 16, TrainerNameLength = 12, ItemNameLength = 16, MaxEvolutionsPerSpecies = 8, MaxPrice = 655_350, GbaTextBreaks = false };

    public IReadOnlyList<FeatureSupport> DescribeFeatures() =>
    [
        new(ModuleCapabilities.Species, SupportLevel.Supported),
        new(ModuleCapabilities.Moves, SupportLevel.Limited, "Descriptions can't change yet."),
        new(ModuleCapabilities.Learnsets, SupportLevel.Limited, "Level-up moves and TM/HM lists; egg moves and tutors not yet."),
        new(ModuleCapabilities.Evolutions, SupportLevel.Supported),
        new(ModuleCapabilities.Trainers, SupportLevel.Limited, "Teams, items and AI; single/double battle can't change yet."),
        new(ModuleCapabilities.TrainerAi, SupportLevel.Supported),
        new(ModuleCapabilities.ItemsAndMarts, SupportLevel.Limited, "Item names and prices; shops not yet."),
        new(ModuleCapabilities.Encounters, SupportLevel.NotYet, "Wild Pokémon in 3DS games come in a later update."),
        new(ModuleCapabilities.Text, SupportLevel.NotYet, "Dialogue editing for 3DS games comes later."),
        new(ModuleCapabilities.Models3d, SupportLevel.NotYet, "3D models come with the graphics studio."),
    ];

    public DetectionResult Detect(GameSource source)
    {
        if (!File.Exists(source.Path) || !source.Path.EndsWith(".3ds", StringComparison.OrdinalIgnoreCase))
        {
            return DetectionResult.NoMatch;
        }

        try
        {
            using var rom = new CtrRom(source.Path);
            if (!Games.TryGetValue(rom.TitleId, out var title))
            {
                return DetectionResult.NoMatch;
            }

            return new DetectionResult
            {
                IsMatch = true,
                Confidence = 0.95,
                GameTitle = title,
                SourceKind = GameSourceKind.RomFile,
                Notes =
                [
                    "Your game file is only read. Changes are saved as a mod (LayeredFS) that Citra loads automatically.",
                    $"Mod folder: load/mods/{rom.TitleIdHex}/romfs in Citra's user folder.",
                ],
            };
        }
        catch (InvalidDataException ex)
        {
            return new DetectionResult { IsMatch = false, Notes = [ex.Message] };
        }
    }

    public Task<GameData> LoadDataAsync(string workingSourcePath, string projectDataDir, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            using var rom = new CtrRom(workingSourcePath);
            var tables = LoadTables(rom, projectDataDir);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            byte[] Read(string path)
            {
                // the project's mod wins over the game file (earlier saves)
                var mod = Path.Combine(projectDataDir, "romfs", path.Replace('/', Path.DirectorySeparatorChar));
                return files.TryGetValue(path, out var b) ? b : files[path] = File.Exists(mod) ? File.ReadAllBytes(mod) : rom.ReadFile(path);
            }

            var state = new State { Tables = tables, ProjectDataDir = projectDataDir, TitleId = rom.TitleIdHex, Read = Read };
            var data = N3dsData.Read(state);
            _states.AddOrUpdate(data, state);
            return data;
        }, cancellationToken);

    public Task SaveDataAsync(GameData data, ModuleSaveContext context, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            if (!_states.TryGetValue(data, out var state))
            {
                throw new InvalidOperationException("This data wasn't loaded by the 3DS module.");
            }

            var errors = Validate(data).Where(i => i.Severity == IssueSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(errors[0].Message);
            }

            var changed = N3dsData.Write(state, data);
            var citra = CitraModsFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Citra", "load", "mods");
            var mirror = Directory.Exists(Path.GetDirectoryName(citra)) ? Path.Combine(citra, state.TitleId, "romfs") : null;
            foreach (var (path, bytes) in changed)
            {
                foreach (var root in new[] { Path.Combine(state.ProjectDataDir, "romfs"), mirror })
                {
                    if (root is null)
                    {
                        continue;
                    }

                    var target = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllBytes(target, bytes);
                }
            }

            // the next save compares against what was just written
            var fresh = new State { Tables = state.Tables, ProjectDataDir = state.ProjectDataDir, TitleId = state.TitleId, Read = state.Read };
            foreach (var kv in state.Overrides.Concat(changed))
            {
                fresh.Overrides[kv.Key] = kv.Value;
            }

            N3dsData.Read(fresh);
            _states.AddOrUpdate(data, fresh);
        }, cancellationToken);

    public IReadOnlyList<ValidationIssue> Validate(GameData data) => _states.TryGetValue(data, out var s) ? N3dsData.Check(s, data, Limits) : [];

    private static Tables LoadTables(CtrRom rom, string projectDataDir)
    {
        var file = Path.Combine(projectDataDir, TablesFileName);
        if (File.Exists(file))
        {
            try
            {
                if (JsonSerializer.Deserialize<Tables>(File.ReadAllText(file), JsonStore.Options) is { } saved && saved.TitleId == rom.TitleIdHex)
                {
                    return saved;
                }
            }
            catch (JsonException)
            {
                // found again below
            }
        }

        var tables = Tables.Locate(rom);
        Directory.CreateDirectory(projectDataDir);
        File.WriteAllText(file, JsonSerializer.Serialize(tables, JsonStore.Options));
        return tables;
    }

    /// <summary>Which archives hold what (found by content once per project).</summary>
    internal sealed record Tables
    {
        public required string TitleId { get; init; }
        public required string Personal { get; init; }
        public required string Evolutions { get; init; }
        public required string Learnsets { get; init; }
        public required string Moves { get; init; }
        public required string Items { get; init; }
        public required string TrainerData { get; init; }
        public required string TrainerParties { get; init; }
        public required string Text { get; init; }
        public required int SpeciesNames { get; init; }
        public required int MoveNames { get; init; }
        public required int ItemNames { get; init; }
        public required int AbilityNames { get; init; }
        public int TrainerNames { get; init; } = -1;
        public int ClassNames { get; init; } = -1;
        public IReadOnlyList<int> TmMoves { get; init; } = [];

        public static Tables Locate(CtrRom rom)
        {
            string? personal = null, evo = null, learn = null, moves = null, items = null, trData = null, trPoke = null;
            var garcs = new Dictionary<string, Garc>(StringComparer.Ordinal);
            foreach (var path in rom.Paths.Where(p => p.StartsWith("a/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                var bytes = rom.ReadFile(path);
                if (bytes.Length < 4 || Encoding.ASCII.GetString(bytes, 0, 4) != "CRAG")
                {
                    continue;
                }

                Garc g;
                try
                {
                    g = new Garc(bytes);
                }
                catch (ArgumentOutOfRangeException)
                {
                    continue;
                }

                if (g.Count < 2)
                {
                    if (g.Count == 1 && g[0].Length > 8 && g[0][0] == 'W' && g[0][1] == 'D')
                    {
                        moves ??= path;
                    }

                    continue;
                }

                var m1 = g[1];
                if (m1.Length >= 0x50 && m1.AsSpan(0, 8).SequenceEqual(new byte[] { 45, 49, 49, 45, 65, 65, 11, 3 }) && g.Count > 700)
                {
                    personal ??= path;
                }
                else if (m1.Length == 48 && m1.AsSpan(0, 8).SequenceEqual(new byte[] { 4, 0, 16, 0, 2, 0, 0, 0 }))
                {
                    evo ??= path;
                }
                else if (m1.Length >= 12 && m1.AsSpan(0, 8).SequenceEqual(new byte[] { 33, 0, 1, 0, 45, 0, 3, 0 }))
                {
                    learn ??= path;
                }
                else if (g.Count > 17 && g[17].Length >= 4 && g[17][0] == 30 && g[17][1] == 0 && g.Count > 700)
                {
                    items ??= path;
                }

                garcs[path] = g;
            }

            // trainers: 24-byte records, plus a team archive with as many entries whose sizes match each trainer's team
            foreach (var (path, td) in garcs.Where(kv => kv.Value.Count > 500 && kv.Value[1].Length == 24))
            {
                var teams = garcs.Where(kv => kv.Key != path && kv.Value.Count == td.Count)
                    .FirstOrDefault(kv => Enumerable.Range(1, Math.Min(50, td.Count - 1)).All(i =>
                        td[i].Length == 24 && kv.Value[i].Length == td[i][7] * (8 + ((td[i][0] & 2) != 0 ? 2 : 0) + ((td[i][0] & 1) != 0 ? 8 : 0)))).Key;
                if (teams is not null)
                {
                    (trData, trPoke) = (path, teams);
                    break;
                }
            }

            if (personal is null || evo is null || learn is null || moves is null)
            {
                throw new InvalidDataException("Couldn't find this game's Pokémon data. It may be a hack or an update-only copy.");
            }

            // the English text: species list with "Bulbasaur" and move list with "Pound"
            foreach (var (path, g) in garcs)
            {
                var lists = new List<List<string>?>();
                foreach (var (_, files) in g.Entries)
                {
                    try
                    {
                        lists.Add(Gen6Text.Read(files[0]));
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        lists.Add(null);
                    }
                }

                int Find(string second, int min) => lists.FindIndex(l => l is { Count: var c } && c >= min && l[1] == second);
                var moveNames = Find("Pound", 600);
                if (moveNames < 0)
                {
                    continue;
                }

                var trainers = trData is null ? 0 : garcs[trData].Count;
                var maxClass = trData is null ? 0 : Enumerable.Range(0, garcs[trData].Count).Max(i => garcs[trData][i].Length > 3 ? garcs[trData][i][2] : 0);
                string[] bosses = ["Leader", "Gym Leader", "Elite Four", "Champion"];
                var code = rom.ReadCode();
                var tm = FindTmList(code);
                return new Tables
                {
                    TitleId = rom.TitleIdHex,
                    Personal = personal, Evolutions = evo, Learnsets = learn, Moves = moves, Items = items ?? "", TrainerData = trData ?? "", TrainerParties = trPoke ?? "",
                    Text = path,
                    SpeciesNames = Find("Bulbasaur", 721),
                    MoveNames = moveNames,
                    ItemNames = Find("Master Ball", 600),
                    AbilityNames = Find("Stench", 180),
                    TrainerNames = lists.FindIndex(l => l is { Count: var c } && c == trainers && l.Count(n => n.Length > 1 && char.IsLetter(n[0])) > trainers / 2),
                    ClassNames = lists.Select((l, i) => (l, i)).Where(x => x.l is { Count: var c } && c > maxClass && c < maxClass + 60 && x.l.Count(n => bosses.Contains(n, StringComparer.OrdinalIgnoreCase)) >= 2)
                        .OrderBy(x => x.l!.Count).Select(x => x.i).DefaultIfEmpty(-1).First(),
                    TmMoves = tm,
                };
            }

            throw new InvalidDataException("Couldn't find this game's English text.");
        }

        /// <summary>Hone Claws, Dragon Claw, Psyshock, Calm Mind… then put in TM1–100, HM1–7 order (the code lists the HMs after TM92).</summary>
        private static List<int> FindTmList(byte[] code)
        {
            ReadOnlySpan<byte> start = [0xD4, 0x01, 0x51, 0x01, 0xD9, 0x01, 0x5B, 0x01];
            var at = code.AsSpan().IndexOf(start);
            while (at >= 0 && at % 2 != 0)
            {
                var next = code.AsSpan(at + 1).IndexOf(start);
                at = next < 0 ? -1 : at + 1 + next;
            }

            if (at < 0)
            {
                return [];
            }

            int Get(int i) => BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(at + (2 * i)));
            var oras = Get(97) == 249; // Rock Smash became HM06 in Omega Ruby / Alpha Sapphire
            var hms = oras ? 6 : 5;
            var list = Enumerable.Range(0, 92).Select(Get).ToList();
            list.AddRange(Enumerable.Range(92 + hms, 8).Select(Get)); // TM93–100
            list.AddRange(Enumerable.Range(92, hms).Select(Get)); // HM01–05/06
            if (oras)
            {
                list.Add(Get(92 + hms + 8)); // HM07 Dive
            }

            return list;
        }
    }

    internal sealed class State
    {
        public required Tables Tables { get; init; }
        public required string ProjectDataDir { get; init; }
        public required string TitleId { get; init; }
        public required Func<string, byte[]> Read { get; init; }
        public Dictionary<string, byte[]> Overrides { get; } = new(StringComparer.Ordinal);

        public byte[] File(string path) => Overrides.TryGetValue(path, out var b) ? b : Read(path);

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
        public Dictionary<string, Item> Items { get; } = new(StringComparer.Ordinal);
        public List<string> TmMoves { get; } = [];
    }
}
