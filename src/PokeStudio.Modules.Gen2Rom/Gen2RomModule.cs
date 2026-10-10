using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Storage;
using PokeStudio.Core.Validation;

namespace PokeStudio.Modules.Gen2Rom;

/// <summary>GBC ROM mode for Gold, Silver and Crystal: edits a copy of the user's ROM.</summary>
public sealed class Gen2RomModule : IGameModule
{
    public const string ModuleId = "gbc.gen2.rom";
    public const string TablesFileName = "gbc-tables.json";

    private static readonly string?[] TypeIds = BuildTypes();
    private static readonly string[] Growth = ["MEDIUM_FAST", "SLIGHTLY_FAST", "SLIGHTLY_SLOW", "MEDIUM_SLOW", "FAST", "SLOW"];
    private static readonly string[] EggGroups =
        ["NONE", "MONSTER", "WATER_1", "BUG", "FLYING", "FIELD", "FAIRY", "GRASS", "HUMAN_LIKE", "WATER_3", "MINERAL", "AMORPHOUS", "WATER_2", "DITTO", "DRAGON", "UNDISCOVERED"];

    private readonly ConditionalWeakTable<GameData, State> _states = new();

    private static string?[] BuildTypes()
    {
        var t = new string?[28];
        string[] low = ["NORMAL", "FIGHTING", "FLYING", "POISON", "GROUND", "ROCK", "BIRD", "BUG", "GHOST", "STEEL"];
        string[] high = ["FIRE", "WATER", "GRASS", "ELECTRIC", "PSYCHIC", "ICE", "DRAGON", "DARK"];
        for (var i = 0; i < low.Length; i++)
        {
            t[i] = low[i];
        }

        t[19] = "MYSTERY";
        for (var i = 0; i < high.Length; i++)
        {
            t[20 + i] = high[i];
        }

        return t;
    }

    public string Id => ModuleId;
    public string DisplayName => "Game Boy Color ROM (Gold, Silver, Crystal)";
    public GamePlatform Platform => GamePlatform.Gb;

    public ModuleCapabilities Capabilities => ModuleCapabilities.Species | ModuleCapabilities.Moves | ModuleCapabilities.Learnsets
        | ModuleCapabilities.Evolutions | ModuleCapabilities.Trainers | ModuleCapabilities.Encounters | ModuleCapabilities.ItemsAndMarts | ModuleCapabilities.Text;

    public GameLimits Limits => new() { SpeciesNameLength = 10, MoveNameLength = 12, TrainerNameLength = 10, ItemNameLength = 12, MaxEvolutionsPerSpecies = 3, MaxPrice = 65_535, MaxMovePriority = 0, MinMovePriority = 0, TextLineWidth = 144 };

    public int? MeasureText(string line) => Gen2Dialogue.Measure(line);

    public IReadOnlyList<FeatureSupport> DescribeFeatures() =>
    [
        new(ModuleCapabilities.Species, SupportLevel.Limited, "No abilities or natures in these games."),
        new(ModuleCapabilities.Moves, SupportLevel.Limited, "A move's type decides physical or special; priority comes from its effect."),
        new(ModuleCapabilities.Learnsets, SupportLevel.Limited, "Level-up moves, TM/HM lists and egg moves."),
        new(ModuleCapabilities.Evolutions, SupportLevel.Supported),
        new(ModuleCapabilities.Trainers, SupportLevel.Supported),
        new(ModuleCapabilities.Encounters, SupportLevel.Limited, "Areas are numbered; the number of slots is fixed."),
        new(ModuleCapabilities.ItemsAndMarts, SupportLevel.Limited, "Item prices; shops not yet."),
        new(ModuleCapabilities.TrainerAi, SupportLevel.Never, "These games have no per-trainer AI settings."),
        new(ModuleCapabilities.Text, SupportLevel.Limited, "What people, signs and trainers say (the messages the maps link to). Places show as text banks; {CMD …} codes must stay."),
        new(ModuleCapabilities.Models3d, SupportLevel.Never, "Game Boy games have no 3D models."),
    ];

    public DetectionResult Detect(GameSource source)
    {
        if (!File.Exists(source.Path) || !(source.Path.EndsWith(".gbc", StringComparison.OrdinalIgnoreCase) || source.Path.EndsWith(".gb", StringComparison.OrdinalIgnoreCase))
            || new FileInfo(source.Path).Length < 0x8000)
        {
            return DetectionResult.NoMatch;
        }

        var header = new byte[0x150];
        using (var f = File.OpenRead(source.Path))
        {
            f.ReadExactly(header);
        }

        var title = System.Text.Encoding.ASCII.GetString(header, 0x134, 11).TrimEnd('\0');
        var game = title switch
        {
            "PM_CRYSTAL" => "Pokémon Crystal",
            "POKEMON_GLD" => "Pokémon Gold",
            "POKEMON_SLV" => "Pokémon Silver",
            _ => null,
        };
        return game is null
            ? DetectionResult.NoMatch
            : new DetectionResult { IsMatch = true, Confidence = 0.9, GameTitle = game, SourceKind = GameSourceKind.RomFile, Notes = ["Edits a copy of your ROM; your original file is never changed."] };
    }

    public Task<GameData> LoadDataAsync(string workingSourcePath, string projectDataDir, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var rom = new GbRom(File.ReadAllBytes(workingSourcePath));
            var tables = LoadTables(rom, projectDataDir);
            var (data, state) = Read(rom, tables);
            Gen2EggMoves.Read(state, data);
            Gen2Dialogue.Load(state, data);
            _states.AddOrUpdate(data, state);
            return data;
        }, cancellationToken);

    public async Task SaveDataAsync(GameData data, ModuleSaveContext context, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue(data, out var state))
        {
            throw new InvalidOperationException("This data wasn't loaded by the Game Boy module.");
        }

        var errors = Validate(data).Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(errors[0].Message);
        }

        Write(state, data);
        Gen2EggMoves.Write(state, data);
        Gen2Dialogue.Save(state, data);
        state.Rom.FixChecksum();
        await context.Writer.WriteAllBytesAsync(context.WorkingSourceDir, state.Rom.Bytes, cancellationToken);
        var (freshData, fresh) = Read(state.Rom, state.Tables);
        Gen2EggMoves.Read(fresh, freshData);
        Gen2Dialogue.Load(fresh, new GameData()); // the edited text stays in data
        _states.AddOrUpdate(data, fresh);
    }

    private static Gen2Tables LoadTables(GbRom rom, string projectDataDir)
    {
        var file = Path.Combine(projectDataDir, TablesFileName);
        if (File.Exists(file))
        {
            try
            {
                if (JsonSerializer.Deserialize<Gen2Tables>(File.ReadAllText(file), JsonStore.Options) is { } saved)
                {
                    if (!saved.EggMovesLocated)
                    {
                        // a project from before egg move support
                        saved = saved with { EggMoves = Gen2EggMoves.Locate(rom), EggMovesLocated = true };
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

        var tables = Gen2Tables.Locate(rom, out var problem) ?? throw new InvalidDataException(problem);
        tables = tables with { EggMoves = Gen2EggMoves.Locate(rom), EggMovesLocated = true };
        Directory.CreateDirectory(projectDataDir);
        File.WriteAllText(file, JsonSerializer.Serialize(tables, JsonStore.Options));
        return tables;
    }

    internal sealed class State
    {
        public required GbRom Rom { get; init; }
        public required Gen2Tables Tables { get; init; }
        public Dictionary<string, int> SpeciesIndex { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> MoveIndex { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ItemIndex { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ClassIndex { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Species> Species { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Move> Moves { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Learnset> Learnsets { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, EvolutionSet> Evolutions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Item> Items { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (EncounterTable Table, int Offset, string Kind)> Encounters { get; } = new(StringComparer.Ordinal);
        public List<(int Pointer, int Start, int End, List<Trainer> Trainers)> Groups { get; } = [];
        public Dictionary<string, Trainer> Trainers { get; } = new(StringComparer.Ordinal);
        public List<string> TmMoves { get; } = [];
        public (int Start, int Length) MoveNamesRegion { get; set; }
        public (int Start, int Length) ItemNamesRegion { get; set; }
        public Dictionary<string, (int Offset, int Length)> EvoRegions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Gen2Dialogue.Message> Messages { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, TextEntry> Texts { get; } = new(StringComparer.Ordinal);
    }

    // ---------- reading ----------

    private static List<string> NameList(GbRom rom, int start, int count, out int length)
    {
        var names = new List<string>();
        var p = start;
        for (var i = 0; i < count && p < rom.Length; i++)
        {
            var end = Array.IndexOf(rom.Bytes, Gen2Text.End, p);
            names.Add(Gen2Text.Read(rom.Bytes.AsSpan(p, end - p + 1)));
            p = end + 1;
        }

        length = p - start;
        return names;
    }

    internal static (GameData Data, State State) Read(GbRom rom, Gen2Tables t)
    {
        var s = new State { Rom = rom, Tables = t };
        var data = new GameData { HeldItemLevelEvolutions = false };
        var c = data.Catalog;
        foreach (var ty in TypeIds.Where(x => x is not null && x != "BIRD"))
        {
            c.Types.Add(new CatalogEntry("TYPE_" + ty, ty == "MYSTERY" ? "???" : GameCatalog.Prettify(ty!)));
        }

        foreach (var g in Growth)
        {
            c.GrowthRates.Add(new CatalogEntry("GROWTH_" + g, GameCatalog.Prettify(g)));
        }

        foreach (var g in EggGroups)
        {
            c.EggGroups.Add(new CatalogEntry("EGG_GROUP_" + g, g == "NONE" ? "None" : GameCatalog.Prettify(g)));
        }

        c.Abilities.Add(new CatalogEntry("ABILITY_NONE", "None"));
        foreach (var (key, name) in new[] { ("EVO_LEVEL", "Level up"), ("EVO_ITEM", "Use an item"), ("EVO_TRADE", "Trade") })
        {
            c.EvolutionMethods.Add(new CatalogEntry(key, name));
        }

        string Unique(string key, HashSet<string> used, int i) => used.Add(key) ? key : $"{key}_{i}";
        string Type(int id) => "TYPE_" + (id < TypeIds.Length && TypeIds[id] is { } n ? n : "NORMAL");
        var used = new HashSet<string>(StringComparer.Ordinal);

        // items
        var itemNames = NameList(rom, t.ItemNames, 255, out var itemLength);
        s.ItemNamesRegion = (t.ItemNames, itemLength);
        var itemKeys = new List<string> { "ITEM_NONE" };
        c.Items.Add(new CatalogEntry("ITEM_NONE", "None"));
        s.ItemIndex["ITEM_NONE"] = 0;
        for (var i = 0; i < itemNames.Count; i++)
        {
            var id = i + 1;
            var key = Unique(itemNames[i].Length == 0 || itemNames[i].All(ch => ch == '?') ? $"ITEM_UNUSED_{id}" : "ITEM_" + Gen2Text.Constant(itemNames[i]), used, id);
            itemKeys.Add(key);
            s.ItemIndex[key] = id;
            c.Items.Add(new CatalogEntry(key, itemNames[i]));
            var item = new Item
            {
                Key = key,
                Name = itemNames[i],
                Price = rom.U16(t.ItemAttributes + (7 * id)),
                LockedFields = new Dictionary<string, string> { ["Description"] = "Item descriptions can't change in Game Boy ROM mode yet." },
            };
            data.Items.Add(item);
            s.Items[key] = item;
        }

        // moves
        used.Clear();
        var moveNames = NameList(rom, t.MoveNames, Gen2Tables.MoveCount, out var moveLength);
        s.MoveNamesRegion = (t.MoveNames, moveLength);
        var moveKeys = new List<string> { "MOVE_NONE" };
        c.Moves.Add(new CatalogEntry("MOVE_NONE", "None"));
        s.MoveIndex["MOVE_NONE"] = 0;
        var effects = new SortedSet<int>();
        for (var i = 0; i < moveNames.Count; i++)
        {
            var id = i + 1;
            var key = Unique("MOVE_" + Gen2Text.Constant(moveNames[i]), used, id);
            moveKeys.Add(key);
            s.MoveIndex[key] = id;
            c.Moves.Add(new CatalogEntry(key, moveNames[i]));
            var at = t.Moves + (7 * i);
            var type = Type(rom.Bytes[at + 3]);
            var power = rom.Bytes[at + 2];
            effects.Add(rom.Bytes[at + 1]);
            var move = new Move
            {
                Id = id,
                Key = key,
                Name = moveNames[i],
                Type = type,
                Power = power,
                Accuracy = (int)Math.Round(rom.Bytes[at + 4] * 100.0 / 255),
                Pp = rom.Bytes[at + 5],
                Effect = $"EFFECT_{rom.Bytes[at + 1]}",
                Category = power == 0 ? MoveCategory.Status : GameCatalog.SpecialTypesBeforeSplit.Contains(type) ? MoveCategory.Special : MoveCategory.Physical,
                LockedFields = new Dictionary<string, string>
                {
                    ["Category"] = "In these games the move's type decides physical or special.",
                    ["Description"] = "Move descriptions can't change in Game Boy ROM mode yet.",
                    ["Priority"] = "In these games priority comes from the move's effect.",
                },
            };
            data.Moves.Add(move);
            s.Moves[key] = move;
        }

        foreach (var e in effects)
        {
            c.MoveEffects.Add(new CatalogEntry($"EFFECT_{e}", $"Effect {e}"));
        }

        // TMs, HMs (and Crystal's three tutor moves)
        for (var i = 0; i < 60 && rom.Bytes[t.TmMoves + i] is var mv && mv != 0; i++)
        {
            var key = mv < moveKeys.Count ? moveKeys[mv] : "MOVE_NONE";
            s.TmMoves.Add(key);
            c.Machines.Add((i < 50 ? $"TM{i + 1:00}" : i < 57 ? $"HM{i - 49:00}" : $"Tutor {i - 56}", key));
        }

        // species
        used.Clear();
        var speciesKeys = new List<string> { "SPECIES_NONE" };
        s.SpeciesIndex["SPECIES_NONE"] = 0;
        for (var i = 1; i <= Gen2Tables.SpeciesCount; i++)
        {
            var name = Gen2Text.Read(rom.Bytes.AsSpan(t.Names + (10 * (i - 1)), 10));
            var key = Unique("SPECIES_" + Gen2Text.Constant(name), used, i);
            speciesKeys.Add(key);
            s.SpeciesIndex[key] = i;
        }

        string SpeciesKey(int i) => i >= 0 && i < speciesKeys.Count ? speciesKeys[i] : "SPECIES_NONE";
        string ItemKey(int i) => i >= 0 && i < itemKeys.Count ? itemKeys[i] : "ITEM_NONE";
        string MoveKey(int i) => i >= 0 && i < moveKeys.Count ? moveKeys[i] : "MOVE_NONE";

        for (var i = 1; i <= Gen2Tables.SpeciesCount; i++)
        {
            var at = t.BaseStats + (32 * (i - 1));
            var b = rom.Bytes;
            var key = speciesKeys[i];
            var eggs = b[at + 23];
            var species = new Species
            {
                Id = i,
                Key = key,
                Name = Gen2Text.Read(b.AsSpan(t.Names + (10 * (i - 1)), 10)),
                Stats = new BaseStats(b[at + 1], b[at + 2], b[at + 3], b[at + 5], b[at + 6], b[at + 4]),
                Types = new[] { b[at + 7], b[at + 8] }.Select(x => Type(x)).Distinct().ToList(),
                Abilities = [],
                HeldItemCommon = b[at + 11] > 0 ? ItemKey(b[at + 11]) : null,
                HeldItemRare = b[at + 12] > 0 ? ItemKey(b[at + 12]) : null,
                CatchRate = b[at + 9],
                ExpYield = b[at + 10],
                Gender = b[at + 13] switch { 0 => GenderRatio.AlwaysMale, 254 => GenderRatio.AlwaysFemale, 255 => GenderRatio.NoGender, var g => new GenderRatio(Math.Round(g * 100.0 / 256, 1)) },
                Growth = "GROWTH_" + Growth[Math.Min((int)b[at + 22], Growth.Length - 1)],
                EggGroups = new[] { eggs >> 4, eggs & 0xF }.Select(x => "EGG_GROUP_" + EggGroups[x]).Distinct().ToList(),
                NationalDexNumber = i,
                NationalDex = i,
                Tags = i switch { 150 or 249 or 250 => SpeciesTags.RestrictedLegendary, 151 or 251 => SpeciesTags.Mythical, 144 or 145 or 146 or 243 or 244 or 245 => SpeciesTags.SubLegendary, _ => SpeciesTags.None },
                LearnsetKey = key,
                LockedFields = new Dictionary<string, string>
                {
                    ["Abilities"] = "Gen 2 games have no abilities.",
                    ["DexEntry"] = "Pokédex entries can't change in Game Boy ROM mode yet.",
                    ["Category"] = "Pokédex entries can't change in Game Boy ROM mode yet.",
                },
            };
            data.Species.Add(species);
            s.Species[key] = species;

            // evolutions, then level-up moves
            var p = rom.Follow(t.EvosAttacks + (2 * (i - 1)));
            var start = p;
            var evos = new List<Evolution>();
            while (p >= 0 && b[p] != 0 && evos.Count < 8)
            {
                var method = b[p];
                if (method == 5)
                {
                    evos.Add(E(SpeciesKey(b[p + 3]), "EVO_LEVEL", b[p + 1].ToString(CultureInfo.InvariantCulture), b[p + 2] switch { 1 => ("IF_ATK_GT_DEF", ""), 2 => ("IF_ATK_LT_DEF", ""), _ => ("IF_ATK_EQ_DEF", "") }));
                    p += 4;
                    continue;
                }

                var param = b[p + 1];
                var target = SpeciesKey(b[p + 2]);
                evos.Add(method switch
                {
                    1 => E(target, "EVO_LEVEL", param.ToString(CultureInfo.InvariantCulture)),
                    2 => E(target, "EVO_ITEM", ItemKey(param)),
                    3 => param == 0xFF ? E(target, "EVO_TRADE", "0") : E(target, "EVO_TRADE", "0", ("IF_HOLD_ITEM", ItemKey(param))),
                    _ => param switch
                    {
                        2 => E(target, "EVO_LEVEL", "0", ("IF_MIN_FRIENDSHIP", "220"), ("IF_TIME", "TIME_DAY")),
                        3 => E(target, "EVO_LEVEL", "0", ("IF_MIN_FRIENDSHIP", "220"), ("IF_TIME", "TIME_NIGHT")),
                        _ => E(target, "EVO_LEVEL", "0", ("IF_MIN_FRIENDSHIP", "220")),
                    },
                });
                p += 3;
            }

            p++;
            var levelUp = new List<LevelUpMove>();
            while (p >= 0 && b[p] != 0 && levelUp.Count < 64)
            {
                levelUp.Add(new LevelUpMove(b[p], MoveKey(b[p + 1])));
                p += 2;
            }

            s.EvoRegions[key] = (start, p + 1 - start);
            var bits = BitConverter.ToUInt64(b, at + 24);
            var learnset = new Learnset
            {
                Key = key,
                UsedBy = [key],
                LevelUp = levelUp,
                Teachable = Enumerable.Range(0, s.TmMoves.Count).Where(k => (bits >> k & 1) != 0).Select(k => s.TmMoves[k]).Distinct().ToList(),
                LockedParts = new Dictionary<string, string> { ["egg"] = "Egg moves can't change in Game Boy ROM mode yet." },
            };
            data.Learnsets.Add(learnset);
            s.Learnsets[key] = learnset;
            var set = new EvolutionSet { FromSpecies = key, Evolutions = evos };
            data.Evolutions.Add(set);
            s.Evolutions[key] = set;
        }

        ReadTrainers(rom, t, s, data, SpeciesKey, MoveKey, ItemKey);
        ReadWild(rom, t, s, data, SpeciesKey);
        c.Invalidate();
        return (data, s);
    }

    private static Evolution E(string target, string method, string param, params (string Kind, string Value)[] conditions) =>
        new(method, param, target) { Conditions = conditions.Select(x => new EvolutionCondition(x.Kind, x.Value)).ToList() };

    private static void ReadTrainers(GbRom rom, Gen2Tables t, State s, GameData data, Func<int, string> species, Func<int, string> move, Func<int, string> item)
    {
        if (t.TrainerGroups < 0)
        {
            return;
        }

        var c = data.Catalog;
        var classNames = t.ClassNames >= 0 ? NameList(rom, t.ClassNames, t.GroupCount, out _) : [];
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var g = 0; g < t.GroupCount; g++)
        {
            var name = g < classNames.Count ? classNames[g] : $"Class {g + 1}";
            var key = used.Add("TRAINER_CLASS_" + Gen2Text.Constant(name)) ? "TRAINER_CLASS_" + Gen2Text.Constant(name) : $"TRAINER_CLASS_{Gen2Text.Constant(name)}_{g + 1}";
            used.Add(key);
            s.ClassIndex[key] = g + 1;
            c.TrainerClasses.Add(new CatalogEntry(key, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant())));
        }

        var classKeys = s.ClassIndex.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList();
        var starts = Enumerable.Range(0, t.GroupCount).Select(g => rom.Follow(t.TrainerGroups + (2 * g))).ToList();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var id = 0;
        for (var g = 0; g < t.GroupCount; g++)
        {
            var p = starts[g];
            var end = starts.Where(x => x > p).DefaultIfEmpty(-1).Min();
            var trainers = new List<Trainer>();
            while (p >= 0 && (end < 0 || p < end) && Gen2Text.LooksLikeName(rom.Bytes.AsSpan(p), 10))
            {
                var nameEnd = Array.IndexOf(rom.Bytes, Gen2Text.End, p);
                var name = Gen2Text.Read(rom.Bytes.AsSpan(p, nameEnd - p + 1));
                var type = rom.Bytes[nameEnd + 1];
                if (type > 3)
                {
                    break;
                }

                p = nameEnd + 2;
                var party = new List<TrainerPokemon>();
                while (rom.Bytes[p] != 0xFF && party.Count < 6)
                {
                    var level = rom.Bytes[p];
                    var sp = rom.Bytes[p + 1];
                    p += 2;
                    string? held = null;
                    if ((type & 2) != 0)
                    {
                        held = rom.Bytes[p] > 0 ? item(rom.Bytes[p]) : null;
                        p++;
                    }

                    var moves = new List<string>();
                    if ((type & 1) != 0)
                    {
                        moves.AddRange(Enumerable.Range(0, 4).Select(k => rom.Bytes[p + k]).Where(m => m > 0).Select(m => move(m)));
                        p += 4;
                    }

                    party.Add(new TrainerPokemon { Species = species(sp), Level = level, HeldItem = held, Moves = moves });
                }

                p++;
                var baseKey = "TRAINER_" + Gen2Text.Constant(name.Length > 0 ? name : "UNNAMED");
                counts[baseKey] = counts.GetValueOrDefault(baseKey) + 1;
                var trainer = new Trainer
                {
                    Id = ++id,
                    Key = $"{baseKey}_{counts[baseKey]}",
                    Name = name,
                    TrainerClass = classKeys.ElementAtOrDefault(g) ?? "",
                    Party = party,
                    LockedReason = party.Count == 0 ? "This trainer has no team in the game." : null,
                };
                trainers.Add(trainer);
                data.Trainers.Add(trainer);
                s.Trainers[trainer.Key] = trainer;
            }

            s.Groups.Add((t.TrainerGroups + (2 * g), starts[g], p, trainers));
        }
    }

    private static void ReadWild(GbRom rom, Gen2Tables t, State s, GameData data, Func<int, string> species)
    {
        if (t.Wild < 0)
        {
            return;
        }

        var p = t.Wild;
        var area = 0;
        foreach (var kind in new[] { "grass", "water", "grass", "water" })
        {
            var size = kind == "grass" ? 47 : 9;
            while (p + size <= rom.Length && rom.Bytes[p] != 0xFF)
            {
                var slots = new List<EncounterSlot>();
                var first = kind == "grass" ? 5 : 3;
                var count = kind == "grass" ? 7 : 3; // daytime slots (morning and night follow for grass)
                var dayOffset = kind == "grass" ? first + 14 : first;
                for (var k = 0; k < count; k++)
                {
                    var lv = rom.Bytes[p + dayOffset + (2 * k)];
                    slots.Add(new EncounterSlot(species(rom.Bytes[p + dayOffset + (2 * k) + 1]), lv, lv));
                }

                var table = new EncounterTable
                {
                    Key = $"WILD_{kind}_{area}",
                    Map = $"MAP_{rom.Bytes[p]}_{rom.Bytes[p + 1]}",
                    Group = "Wild",
                    Methods = [new EncounterMethodTable { Type = kind == "grass" ? "land_mons" : "water_mons", Rate = rom.Bytes[p + (kind == "grass" ? 3 : 2)], Slots = slots }],
                };
                data.Encounters.Add(table);
                s.Encounters[table.Key] = (table, p, kind);
                area++;
                p += size;
            }

            p++; // the 0xFF ending this list
        }

        data.EncounterChances.Add(new EncounterSlotChances("land_mons", [30, 30, 20, 10, 5, 4, 1], new Dictionary<string, IReadOnlyList<int>>()));
        data.EncounterChances.Add(new EncounterSlotChances("water_mons", [60, 30, 10], new Dictionary<string, IReadOnlyList<int>>()));
    }

    // ---------- checks and writing ----------

    public IReadOnlyList<ValidationIssue> Validate(GameData data)
    {
        var issues = new List<ValidationIssue>();
        void Error(string area, string subject, string message, string? key = null) =>
            issues.Add(new ValidationIssue(IssueSeverity.Error, area, subject, message) { TargetKey = key });
        foreach (var sp in data.Species.Where(sp => sp.Name.Length > 10 || !Gen2Text.CanWrite(sp.Name)))
        {
            Error("Pokémon", sp.Name, "Names can be up to 10 letters this game can show.", sp.Key);
        }

        foreach (var set in data.Evolutions)
        {
            foreach (var e in set.Evolutions.Where(e => Encode(e, _ => 1, _ => 1) is null))
            {
                Error("Evolutions", set.FromSpecies, $"\"{e.Method} {e.Parameter}\" can't be done in these games. Use a level, an item, a trade or friendship.", set.FromSpecies);
            }
        }

        if (_statesFor(data) is { } s)
        {
            var tm = s.TmMoves.ToHashSet(StringComparer.Ordinal);
            foreach (var l in data.Learnsets.Where(l => l.Teachable.Any(m => !tm.Contains(m))))
            {
                Error("Learnsets", l.Key, "Only TM, HM and tutor moves can be taught in these games.", l.Key);
            }

            issues.AddRange(Gen2Dialogue.Check(s, data));
            issues.AddRange(Gen2EggMoves.Check(s, data));
        }

        foreach (var t in data.Trainers.Where(t => t.LockedReason is null && (t.Party.Count is 0 or > 6 || t.HasAdaptiveSlot || t.Name.Length > 10)))
        {
            Error("Trainers", t.Name, t.HasAdaptiveSlot ? "Adaptive boss slots need a project (decomp)." : "Trainers need 1 to 6 Pokémon and a name of up to 10 letters.", t.Key);
        }

        return issues;
    }

    private State? _statesFor(GameData data) => _states.TryGetValue(data, out var s) ? s : null;

    /// <summary>One evolution → Gen 2 bytes (method, then parameters and species), or null when these games can't do it.</summary>
    internal static byte[]? Encode(Evolution e, Func<string, int> item, Func<string, int> species)
    {
        var kinds = string.Join("+", e.Conditions.Select(x => x.Kind).Order(StringComparer.Ordinal));
        var time = e.Conditions.FirstOrDefault(x => x.Kind == "IF_TIME")?.Value;
        var target = (byte)species(e.TargetSpecies);
        int Level() => int.TryParse(e.Parameter, CultureInfo.InvariantCulture, out var l) ? l : -1;
        return (e.Method, kinds) switch
        {
            ("EVO_LEVEL", "") when Level() is > 0 and <= 100 => [1, (byte)Level(), target],
            ("EVO_ITEM", "") => [2, (byte)item(e.Parameter), target],
            ("EVO_TRADE", "") => [3, 0xFF, target],
            ("EVO_TRADE", "IF_HOLD_ITEM") => [3, (byte)item(e.Conditions[0].Value), target],
            ("EVO_LEVEL", "IF_MIN_FRIENDSHIP") => [4, 1, target],
            ("EVO_LEVEL", "IF_MIN_FRIENDSHIP+IF_TIME") => [4, time == "TIME_NIGHT" ? (byte)3 : (byte)2, target],
            ("EVO_LEVEL", "IF_ATK_GT_DEF") when Level() > 0 => [5, (byte)Level(), 1, target],
            ("EVO_LEVEL", "IF_ATK_LT_DEF") when Level() > 0 => [5, (byte)Level(), 2, target],
            ("EVO_LEVEL", "IF_ATK_EQ_DEF") when Level() > 0 => [5, (byte)Level(), 3, target],
            _ => null,
        };
    }

    private static void Write(State s, GameData data)
    {
        var rom = s.Rom;
        var t = s.Tables;
        var b = rom.Bytes;
        int Species(string key) => s.SpeciesIndex.GetValueOrDefault(key);
        int Move(string key) => s.MoveIndex.GetValueOrDefault(key);
        int Item(string? key) => key is null ? 0 : s.ItemIndex.GetValueOrDefault(key);
        int Type(string key) => Math.Max(0, Array.IndexOf(TypeIds, key.Replace("TYPE_", "", StringComparison.Ordinal)));

        foreach (var sp in data.Species)
        {
            if (!s.Species.TryGetValue(sp.Key, out var before) || ReferenceEquals(before, sp))
            {
                continue;
            }

            var i = Species(sp.Key);
            var at = t.BaseStats + (32 * (i - 1));
            b[at + 1] = (byte)sp.Stats.Hp;
            b[at + 2] = (byte)sp.Stats.Attack;
            b[at + 3] = (byte)sp.Stats.Defense;
            b[at + 4] = (byte)sp.Stats.Speed;
            b[at + 5] = (byte)sp.Stats.SpAttack;
            b[at + 6] = (byte)sp.Stats.SpDefense;
            b[at + 7] = (byte)Type(sp.Types.ElementAtOrDefault(0) ?? "TYPE_NORMAL");
            b[at + 8] = (byte)Type(sp.Types.ElementAtOrDefault(1) ?? sp.Types.ElementAtOrDefault(0) ?? "TYPE_NORMAL");
            b[at + 9] = (byte)sp.CatchRate;
            b[at + 10] = (byte)sp.ExpYield;
            b[at + 11] = (byte)Item(sp.HeldItemCommon);
            b[at + 12] = (byte)Item(sp.HeldItemRare);
            b[at + 13] = sp.Gender switch { { Genderless: true } => 255, { PercentFemale: 0 } => 0, { PercentFemale: 100 } => 254, { PercentFemale: { } f } => (byte)Math.Clamp(Math.Round(f * 256 / 100), 1, 253), _ => b[at + 13] };
            b[at + 22] = (byte)Math.Max(0, Array.IndexOf(Growth, sp.Growth.Replace("GROWTH_", "", StringComparison.Ordinal)));
            var eggs = sp.EggGroups.Select(g => Math.Max(0, Array.IndexOf(EggGroups, g.Replace("EGG_GROUP_", "", StringComparison.Ordinal)))).ToList();
            b[at + 23] = (byte)((eggs.ElementAtOrDefault(0) << 4) | (eggs.Count > 1 ? eggs[1] : eggs.ElementAtOrDefault(0)));
            if (sp.Name != before.Name)
            {
                var name = Gen2Text.Write(sp.Name);
                for (var k = 0; k < 10; k++)
                {
                    b[t.Names + (10 * (i - 1)) + k] = k < name.Length ? name[k] : Gen2Text.End;
                }
            }
        }

        foreach (var mv in data.Moves)
        {
            if (!s.Moves.TryGetValue(mv.Key, out var before) || ReferenceEquals(before, mv))
            {
                continue;
            }

            var at = t.Moves + (7 * (Move(mv.Key) - 1));
            b[at + 2] = (byte)mv.Power;
            b[at + 3] = (byte)Type(mv.Type);
            b[at + 4] = (byte)Math.Clamp(Math.Round(mv.Accuracy * 255 / 100.0), 0, 255);
            b[at + 5] = (byte)mv.Pp;
        }

        // learnsets: TM bits in place; evolutions + level-up moves rewritten together (moved to free space when longer)
        foreach (var sp in data.Species)
        {
            var key = sp.Key;
            var l = data.Learnsets.FirstOrDefault(x => x.Key == key);
            var e = data.Evolutions.FirstOrDefault(x => x.FromSpecies == key);
            var learnChanged = l is not null && s.Learnsets.TryGetValue(key, out var lb) && !ReferenceEquals(lb, l);
            var evoChanged = e is not null && s.Evolutions.TryGetValue(key, out var eb) && !ReferenceEquals(eb, e);
            var i = Species(key);
            if (learnChanged && !s.Learnsets[key].Teachable.SequenceEqual(l!.Teachable))
            {
                var at = t.BaseStats + (32 * (i - 1)) + 24;
                var bits = BitConverter.ToUInt64(b, at);
                for (var k = 0; k < s.TmMoves.Count; k++)
                {
                    bits = l.Teachable.Contains(s.TmMoves[k]) ? bits | (1UL << k) : bits & ~(1UL << k);
                }

                BitConverter.TryWriteBytes(b.AsSpan(at), bits);
            }

            if ((learnChanged && !s.Learnsets[key].LevelUp.SequenceEqual(l!.LevelUp)) || evoChanged)
            {
                var bytes = new List<byte>();
                foreach (var ev in (e ?? s.Evolutions[key]).Evolutions)
                {
                    bytes.AddRange(Encode(ev, x => Item(x), Species)!);
                }

                bytes.Add(0);
                foreach (var m in (l ?? s.Learnsets[key]).LevelUp.OrderBy(m => m.Level))
                {
                    bytes.Add((byte)m.Level);
                    bytes.Add((byte)Move(m.Move));
                }

                bytes.Add(0);
                var (start, length) = s.EvoRegions[key];
                var target = start;
                if (bytes.Count > length)
                {
                    target = rom.FindFree(GbRom.Bank(t.EvosAttacks), bytes.Count);
                    if (target < 0)
                    {
                        throw new InvalidOperationException("The ROM has no free space left for longer move lists in their bank.");
                    }

                    rom.W16(t.EvosAttacks + (2 * (i - 1)), GbRom.PointerTo(target));
                }

                bytes.ToArray().CopyTo(b, target);
            }
        }

        // trainers: a changed group is written again (moved to free space in its bank when it grew)
        foreach (var (pointer, start, end, trainers) in s.Groups)
        {
            var current = trainers.Select(tr => data.Trainers.FirstOrDefault(x => x.Key == tr.Key) ?? tr).ToList();
            if (current.Zip(trainers).All(p => ReferenceEquals(p.First, p.Second)))
            {
                continue;
            }

            var bytes = new List<byte>();
            foreach (var tr in current)
            {
                bytes.AddRange(Gen2Text.Write(tr.Name));
                var custom = tr.Party.Any(p => p.Moves.Count > 0);
                var held = tr.Party.Any(p => p.HeldItem is not null && p.HeldItem != "ITEM_NONE");
                bytes.Add((byte)((custom ? 1 : 0) | (held ? 2 : 0)));
                foreach (var p in tr.Party)
                {
                    bytes.Add((byte)Math.Clamp(p.Level, 1, 100));
                    bytes.Add((byte)Species(p.Species));
                    if (held)
                    {
                        bytes.Add((byte)Item(p.HeldItem));
                    }

                    if (custom)
                    {
                        for (var m = 0; m < 4; m++)
                        {
                            bytes.Add((byte)(m < p.Moves.Count ? Move(p.Moves[m]) : 0));
                        }
                    }
                }

                bytes.Add(0xFF);
            }

            var target = start;
            if (bytes.Count > end - start)
            {
                target = rom.FindFree(GbRom.Bank(start), bytes.Count);
                if (target < 0)
                {
                    throw new InvalidOperationException("The ROM has no free space left for bigger trainer teams in their bank.");
                }

                rom.W16(pointer, GbRom.PointerTo(target));
            }

            bytes.ToArray().CopyTo(b, target);
        }

        // wild Pokémon: the daytime slots (and morning/night when they matched)
        foreach (var e in data.Encounters)
        {
            if (!s.Encounters.TryGetValue(e.Key, out var before) || ReferenceEquals(before.Table, e))
            {
                continue;
            }

            var p = before.Offset;
            var oldSlots = before.Table.Methods[0].Slots;
            var newSlots = e.Methods[0].Slots;
            var grass = before.Kind == "grass";
            var dayOffset = grass ? 19 : 3;
            for (var k = 0; k < Math.Min(oldSlots.Count, newSlots.Count); k++)
            {
                foreach (var time in grass ? new[] { 5, 19, 33 } : [3])
                {
                    var at = p + time + (2 * k);
                    if (time != dayOffset && (b[at] != oldSlots[k].MinLevel || b[at + 1] != Species(oldSlots[k].Species)))
                    {
                        continue;
                    }

                    b[at] = (byte)newSlots[k].MinLevel;
                    b[at + 1] = (byte)Species(newSlots[k].Species);
                }
            }
        }

        foreach (var it in data.Items)
        {
            if (s.Items.TryGetValue(it.Key, out var before) && before.Price != it.Price)
            {
                rom.W16(t.ItemAttributes + (7 * Item(it.Key)), it.Price);
            }
        }
    }
}
