using System.Runtime.CompilerServices;
using PokeStudio.Core.Build;
using PokeStudio.Core.Modernize;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.SourceCode;
using PokeStudio.Core.Validation;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// GBA project mode for pokeemerald-expansion (Emerald-based decomp with Gen 1–9 Pokémon).
/// Edits the user's own project folder in place: only the data files listed in
/// <see cref="ManagedFiles"/>, each backed up first, changing only the values that were edited.
/// </summary>
public sealed class ExpansionModule : IGameModule
{
    public const string ModuleId = "gba.pokeemerald-expansion";

    /// <summary>The release this module was written and tested against.</summary>
    public static readonly Version TestedVersion = new(1, 17, 1);

    private readonly ConditionalWeakTable<GameData, LoadedState> _states = new();
    private GameLimits _limits = new() { SpeciesNameLength = 12 };

    public string Id => ModuleId;
    public string DisplayName => "pokeemerald-expansion project";
    public GamePlatform Platform => GamePlatform.Gba;
    public ModuleCapabilities Capabilities =>
        ModuleCapabilities.Species | ModuleCapabilities.Moves | ModuleCapabilities.Learnsets | ModuleCapabilities.Evolutions | ModuleCapabilities.Build;
    public bool WorksInPlace => true;
    public GameLimits Limits => _limits;

    /// <summary>The project's own Makefile builds pokeemerald.gba (needs WSL on Windows, native tools on Linux).</summary>
    public BuildDefinition Build { get; } = new(
        "make -j\"$(nproc)\"",
        "pokeemerald.gba",
        ["make", "gcc", "arm-none-eabi-gcc", "python3"],
        ["/usr/include/png.h"])
    {
        ToolsDescription = "GBA build tools",
        EstimatedSteps = 1800,
    };

    public IReadOnlyList<FeatureSupport> DescribeFeatures() =>
    [
        new(ModuleCapabilities.Species, SupportLevel.Supported),
        new(ModuleCapabilities.Learnsets, SupportLevel.Supported),
        new(ModuleCapabilities.Evolutions, SupportLevel.Supported),
        new(ModuleCapabilities.Build, SupportLevel.Supported),
        new(ModuleCapabilities.Moves, SupportLevel.Supported),
        new(ModuleCapabilities.Trainers, SupportLevel.NotYet, "Trainer editing arrives in Phase 5."),
        new(ModuleCapabilities.TrainerAi, SupportLevel.NotYet, "Enemy AI presets arrive in Phase 5."),
        new(ModuleCapabilities.Encounters, SupportLevel.NotYet, "Wild encounters arrive in Phase 6."),
        new(ModuleCapabilities.ItemsAndMarts, SupportLevel.NotYet, "Items and marts arrive in Phase 6."),
        new(ModuleCapabilities.Text, SupportLevel.NotYet, "The text editor arrives in Phase 6. Names and Pokédex entries can already be edited."),
        new(ModuleCapabilities.Graphics, SupportLevel.NotYet, "The graphics studio arrives in Phase 11."),
        new(ModuleCapabilities.Models3d, SupportLevel.Never, "GBA games have no 3D models."),
    ];

    public DetectionResult Detect(GameSource source)
    {
        if (!Directory.Exists(source.Path) || !ExpansionLayout.LooksLikeExpansion(source.Path))
        {
            return DetectionResult.NoMatch;
        }

        var version = ExpansionLayout.ReadVersion(source.Path);
        var notes = new List<string> { "Your project folder is edited directly. Every file is backed up before the app changes it." };
        if (version is null)
        {
            notes.Add("The app couldn't read the expansion version. Editing may still work.");
        }
        else if (version.Major != TestedVersion.Major || version.Minor != TestedVersion.Minor)
        {
            notes.Add($"This app was made for version {TestedVersion.Major}.{TestedVersion.Minor}. Version {version} may only partly work.");
        }

        return new DetectionResult
        {
            IsMatch = true,
            Confidence = 0.95,
            GameTitle = "pokeemerald-expansion" + (version is null ? "" : $" {version}"),
            Version = version?.ToString(),
            SourceKind = GameSourceKind.DecompProject,
            Notes = notes,
        };
    }

    public IReadOnlyList<string> ManagedFiles(string workingSourcePath)
    {
        var layout = new ExpansionLayout(workingSourcePath);
        return [.. layout.SpeciesFiles(), layout.LevelUpFile, ExpansionLayout.EggMovesFile, ExpansionLayout.LearnablesJson, MovesInfo.File];
    }

    public Task<GameData> LoadDataAsync(string workingSourcePath, string projectDataDir, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            var (data, state) = Parse(workingSourcePath, cancellationToken);
            _states.AddOrUpdate(data, state);
            return data;
        }, cancellationToken);

    public async Task SaveDataAsync(GameData data, ModuleSaveContext context, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue(data, out var state))
        {
            throw new InvalidOperationException("This data wasn't loaded by the expansion module.");
        }

        var edits = new Dictionary<string, List<TextEdit>>(StringComparer.Ordinal);
        void Add(string file, IEnumerable<TextEdit> list)
        {
            if (!edits.TryGetValue(file, out var l))
            {
                edits[file] = l = [];
            }

            l.AddRange(list);
        }

        foreach (var species in data.Species)
        {
            if (state.Species.TryGetValue(species.Key, out var before) && !ReferenceEquals(before, species) && state.Blocks.TryGetValue(species.Key, out var block) && block.Active)
            {
                Add(block.File, SpeciesInfo.EditsFor(state.Files[block.File], block, before, species));
            }
        }

        foreach (var move in data.Moves)
        {
            if (state.Moves.TryGetValue(move.Key, out var before) && !ReferenceEquals(before, move) && state.MoveBlocks.TryGetValue(move.Key, out var block) && block.Active)
            {
                Add(block.File, MovesInfo.EditsFor(state.Files[block.File], block, before, move));
            }
        }

        foreach (var set in data.Evolutions.Where(e => e.LockedReason is null))
        {
            if (state.Evolutions.TryGetValue(set.FromSpecies, out var before) && !SameEvolutions(before, set) && state.Blocks.TryGetValue(set.FromSpecies, out var block))
            {
                Add(block.File, SpeciesInfo.EvolutionEdits(state.Files[block.File], block, set));
            }
        }

        var learnablesChanged = false;
        var writtenLevelUp = new HashSet<string>(StringComparer.Ordinal);
        var writtenEgg = new HashSet<string>(StringComparer.Ordinal);
        foreach (var learnset in data.Learnsets)
        {
            if (!state.Learnsets.TryGetValue(learnset.Key, out var before) || ReferenceEquals(before, learnset))
            {
                continue;
            }

            var (levelUpSym, teachSym, eggSym) = SplitKey(learnset.Key);
            if (!learnset.LockedParts.ContainsKey("levelUp") && !before.LevelUp.SequenceEqual(learnset.LevelUp)
                && levelUpSym is not null && state.LevelUpArrays.TryGetValue(levelUpSym, out var lv) && writtenLevelUp.Add(levelUpSym))
            {
                Add(lv.File, [LearnsetFiles.LevelUpEdit(state.Files[lv.File], lv, learnset.LevelUp)]);
            }

            if (!learnset.LockedParts.ContainsKey("egg") && !before.EggMoves.SequenceEqual(learnset.EggMoves)
                && eggSym is not null && state.EggArrays.TryGetValue(eggSym, out var eg) && writtenEgg.Add(eggSym))
            {
                Add(eg.File, [LearnsetFiles.EggEdit(state.Files[eg.File], eg, learnset.EggMoves)]);
            }

            if (!learnset.LockedParts.ContainsKey("teachable") && !before.Teachable.SequenceEqual(learnset.Teachable)
                && teachSym is not null && state.Learnables is not null)
            {
                var key = SpeciesInfo.LearnablesKey(teachSym);
                if (!state.Learnables.Moves.ContainsKey(key))
                {
                    state.Learnables.Order.Add(key);
                }

                state.Learnables.Moves[key] = learnset.Teachable.Distinct().Order(StringComparer.Ordinal).ToList();
                learnablesChanged = true;
            }
        }

        foreach (var (file, list) in edits.Where(e => e.Value.Count > 0))
        {
            var updated = TextEdits.Apply(state.Files[file].Text, list);
            await context.Writer.WriteAllTextAsync(state.Layout.Full(file), updated, cancellationToken);
        }

        if (learnablesChanged && state.Learnables is not null)
        {
            await context.Writer.WriteAllTextAsync(state.Layout.Full(ExpansionLayout.LearnablesJson), state.Learnables.Serialize(), cancellationToken);
        }

        // Positions changed: read the files again so the next save starts from what's on disk now.
        var (_, fresh) = Parse(state.Layout.Root, cancellationToken);
        _states.AddOrUpdate(data, fresh);
    }

    private static readonly string[] GenerationGames =
    [
        "", "Red, Blue & Yellow", "Gold, Silver & Crystal", "Ruby, Sapphire, Emerald, FireRed & LeafGreen",
        "Diamond, Pearl, Platinum, HeartGold & SoulSilver", "Black, White, Black 2 & White 2", "X, Y, Omega Ruby & Alpha Sapphire",
        "Sun, Moon, Ultra Sun & Ultra Moon", "Sword & Shield", "Scarlet & Violet",
    ];

    /// <summary>One choice per level_up_learnsets/gen_N.h the project ships.</summary>
    public IReadOnlyList<GenerationChoice> ModernizeGenerations(GameData data)
    {
        if (!_states.TryGetValue(data, out var state))
        {
            return [];
        }

        var gens = Enumerable.Range(1, 9).Where(n => File.Exists(state.Layout.Full(LevelUpFileFor(n)))).ToList();
        var latest = gens.Count == 0 ? 0 : gens.Max();
        return gens.OrderDescending()
            .Select(n => new GenerationChoice(n, $"Gen {n} — {GenerationGames[n]}{(n == latest ? " (latest)" : "")}", n == latest))
            .ToList();
    }

    private static string LevelUpFileFor(int generation) => $"src/data/pokemon/level_up_learnsets/gen_{generation}.h";

    public Task<ModernizeReference?> LoadModernizeReferenceAsync(GameData data, int generation, CancellationToken cancellationToken = default) =>
        Task.Run<ModernizeReference?>(() =>
        {
            if (!_states.TryGetValue(data, out var state) || !File.Exists(state.Layout.Full(LevelUpFileFor(generation))))
            {
                return null;
            }

            var layout = state.Layout;

            // level-up moves from that generation's file
            var refFile = SpeciesInfo.LoadFile(layout, LevelUpFileFor(generation));
            var arrays = LearnsetFiles.FindArrays(refFile, levelUp: true);
            var levelUp = new Dictionary<string, IReadOnlyList<LevelUpMove>>(StringComparer.Ordinal);
            foreach (var key in state.Learnsets.Keys)
            {
                var (lv, _, _) = SplitKey(key);
                if (lv is not null && arrays.TryGetValue(lv, out var block) && !block.Locked)
                {
                    levelUp[key] = LearnsetFiles.ReadLevelUp(refFile, block);
                }
            }

            // stats, types and abilities as that generation has them: read the species files with the "updated" settings set to it
            var settings = layout.Macros.Clone();
            foreach (var name in new[] { "P_UPDATED_STATS", "P_UPDATED_TYPES", "P_UPDATED_ABILITIES" })
            {
                settings.Define(name, $"GEN_{generation}");
            }

            var species = new Dictionary<string, SpeciesUpdate>(StringComparer.Ordinal);
            foreach (var rel in layout.SpeciesFiles())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = SpeciesInfo.LoadFile(layout, rel, settings);
                foreach (var block in SpeciesInfo.ParseBlocks(file).Where(b => b.Active && state.Species.ContainsKey(b.Symbol) && !species.ContainsKey(b.Symbol)))
                {
                    var s = SpeciesInfo.ToSpecies(file, block, 0, out _, out _, out _, out _);
                    species[block.Symbol] = new SpeciesUpdate(s.Stats, s.Types, s.Abilities);
                }
            }

            var notes = new List<string>
            {
                "pokeemerald-expansion already uses the latest games' TM, tutor and egg move lists, so Modernize changes level-up moves.",
            };
            if (state.Learnables is null)
            {
                notes.Add("TM and tutor lists appear after you build the game once.");
            }

            return new ModernizeReference(generation, levelUp, species, notes);
        }, cancellationToken);

    public IReadOnlyList<ValidationIssue> Validate(GameData data)
    {
        var issues = new List<ValidationIssue>();
        var items = data.Catalog.Items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        var abilities = data.Catalog.Abilities.Select(a => a.Key).ToHashSet(StringComparer.Ordinal);
        var types = data.Catalog.Types.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var s in data.Species.Where(s => s.IsEnabled))
        {
            foreach (var t in s.Types.Where(t => types.Count > 0 && !types.Contains(t)))
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, "Pokémon", s.DisplayName, $"has the type “{t}”, which this game doesn't have.") { TargetKey = s.Key });
            }

            foreach (var a in s.Abilities.Where(a => abilities.Count > 0 && !abilities.Contains(a)))
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, "Pokémon", s.DisplayName, $"has the ability “{a}”, which this game doesn't have.") { TargetKey = s.Key });
            }

            foreach (var i in new[] { s.HeldItemCommon, s.HeldItemRare }.Where(i => i is not null && items.Count > 0 && !items.Contains(i)))
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, "Pokémon", s.DisplayName, $"holds “{i}”, which isn't an item in this game.") { TargetKey = s.Key });
            }

            if (s.Name.Any(c => c > 0x7E && "éÉèàâêîôûçÀÈÌÒÙ♂♀…“”‘’·".IndexOf(c, StringComparison.Ordinal) < 0))
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, "Pokémon", s.DisplayName, "uses a character the game's font doesn't have.", "Use plain letters, numbers and common accents.") { TargetKey = s.Key });
            }
        }

        return issues;
    }

    // ---------- parsing ----------

    private (GameData Data, LoadedState State) Parse(string root, CancellationToken cancellationToken)
    {
        var layout = new ExpansionLayout(root);
        _limits = new GameLimits
        {
            SpeciesNameLength = layout.NameLength("POKEMON_NAME_LENGTH", 12),
            CategoryNameLength = 12,
            MoveNameLength = layout.NameLength("MOVE_NAME_LENGTH", 16),
            MaxMovePower = 511, // u16 power:9
            MinMovePriority = -8, // s32 priority:4
            MaxMovePriority = 7,
        };

        var state = new LoadedState { Layout = layout };
        var data = new GameData();
        SourceFile Load(string rel)
        {
            if (!state.Files.TryGetValue(rel, out var f))
            {
                f = SpeciesInfo.LoadFile(layout, rel);
                state.Files[rel] = f;
            }

            return f;
        }

        // learnset sources
        var levelUpFile = File.Exists(layout.Full(layout.LevelUpFile)) ? Load(layout.LevelUpFile) : null;
        if (levelUpFile is not null)
        {
            foreach (var (k, v) in LearnsetFiles.FindArrays(levelUpFile, levelUp: true))
            {
                state.LevelUpArrays[k] = v;
            }
        }

        var eggFile = File.Exists(layout.Full(ExpansionLayout.EggMovesFile)) ? Load(ExpansionLayout.EggMovesFile) : null;
        if (eggFile is not null)
        {
            foreach (var (k, v) in LearnsetFiles.FindArrays(eggFile, levelUp: false))
            {
                state.EggArrays[k] = v;
            }
        }

        state.Learnables = LearnablesFile.Load(layout.Full(ExpansionLayout.LearnablesJson));

        // species
        var index = 0;
        var levelUpUsers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var pendingLearnsets = new List<(string Key, string? LevelUp, string? Teach, string? Egg, string? TeachingType)>();
        foreach (var rel in layout.SpeciesFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = Load(rel);
            foreach (var block in SpeciesInfo.ParseBlocks(file))
            {
                if (block.Symbol is "SPECIES_NONE" or "SPECIES_EGG" || state.Blocks.ContainsKey(block.Symbol))
                {
                    continue;
                }

                var species = SpeciesInfo.ToSpecies(file, block, index++, out var lv, out var teach, out var egg, out var teachingType);
                state.Blocks[block.Symbol] = block;
                state.Species[block.Symbol] = species;
                data.Species.Add(species);

                var evo = SpeciesInfo.ToEvolutions(file, block);
                state.Evolutions[block.Symbol] = evo;
                data.Evolutions.Add(evo);

                if (lv is not null)
                {
                    if (!levelUpUsers.TryGetValue(lv, out var users))
                    {
                        levelUpUsers[lv] = users = [];
                    }

                    users.Add(block.Symbol);
                }

                if (species.LearnsetKey is { } key && pendingLearnsets.All(p => p.Key != key))
                {
                    pendingLearnsets.Add((key, lv, teach, egg, teachingType));
                }
            }
        }

        foreach (var (key, lv, teach, egg, teachingType) in pendingLearnsets)
        {
            var locked = new Dictionary<string, string>();
            var levelUp = new List<LevelUpMove>();
            if (lv is not null && state.LevelUpArrays.TryGetValue(lv, out var lvBlock))
            {
                levelUp = LearnsetFiles.ReadLevelUp(levelUpFile!, lvBlock);
                if (lvBlock.Locked)
                {
                    locked["levelUp"] = "These level-up moves change with game settings, so they can only be edited in the source file for now.";
                }
            }
            else
            {
                locked["levelUp"] = $"The app couldn't find this Pokémon's level-up list in {layout.LevelUpFile}.";
            }

            var eggMoves = new List<string>();
            if (egg is not null && state.EggArrays.TryGetValue(egg, out var eggBlock))
            {
                eggMoves = LearnsetFiles.ReadEggMoves(eggFile!, eggBlock);
                if (eggBlock.Locked)
                {
                    locked["egg"] = "These egg moves change with game settings, so they can only be edited in the source file for now.";
                }
            }
            else
            {
                locked["egg"] = egg is null ? "This Pokémon has no egg moves list." : "The app couldn't find this Pokémon's egg moves list.";
            }

            var teachable = new List<string>();
            if (teachingType == "ALL_TEACHABLES")
            {
                locked["teachable"] = "This Pokémon can learn every TM and tutor move in the game.";
            }
            else if (state.Learnables is null)
            {
                locked["teachable"] = "The TM and tutor list appears after you build the game once.";
            }
            else if (teach is not null && state.Learnables.Moves.TryGetValue(SpeciesInfo.LearnablesKey(teach), out var moves))
            {
                teachable = moves.ToList();
            }

            var learnset = new Learnset
            {
                Key = key,
                UsedBy = lv is not null && levelUpUsers.TryGetValue(lv, out var users) ? users : [],
                LevelUp = levelUp,
                EggMoves = eggMoves,
                Teachable = teachable,
                LockedParts = locked,
            };
            state.Learnsets[key] = learnset;
            data.Learnsets.Add(learnset);
        }

        if (File.Exists(layout.Full(MovesInfo.File)))
        {
            var movesFile = Load(MovesInfo.File);
            var moveIndex = 0;
            foreach (var block in SpeciesInfo.ParseBlocks(movesFile, moves: true))
            {
                if (block.Symbol == "MOVE_NONE" || state.MoveBlocks.ContainsKey(block.Symbol))
                {
                    continue;
                }

                var move = MovesInfo.ToMove(movesFile, block, moveIndex++);
                state.MoveBlocks[block.Symbol] = block;
                state.Moves[block.Symbol] = move;
                data.Moves.Add(move);
            }
        }

        FillCatalog(layout, data.Catalog);
        return (data, state);
    }

    private static void FillCatalog(ExpansionLayout layout, GameCatalog c)
    {
        string? Read(string rel) => File.Exists(layout.Full(rel)) ? File.ReadAllText(layout.Full(rel)) : null;
        var pokemonConstants = Read("include/constants/pokemon.h");
        c.Types.AddRange(Catalogs.Build(pokemonConstants, null, "TYPE_").Where(t => t.Key != "TYPE_NONE"));
        c.GrowthRates.AddRange(Catalogs.Build(pokemonConstants, null, "GROWTH_"));
        c.EggGroups.AddRange(Catalogs.Build(pokemonConstants, null, "EGG_GROUP_").Where(e => e.Key != "EGG_GROUP_NONE"));
        c.Moves.AddRange(Catalogs.Build(Read("include/constants/moves.h"), Read("src/data/moves_info.h"), "MOVE_").Where(m => m.Key != "MOVE_NONE"));
        c.Abilities.AddRange(Catalogs.Build(Read("include/constants/abilities.h"), Read("src/data/abilities.h"), "ABILITY_"));
        c.Items.AddRange(Catalogs.Build(Read("include/constants/items.h"), Read("src/data/items.h"), "ITEM_"));
        c.EvolutionMethods.AddRange(Catalogs.EvolutionMethods.Select(m => new CatalogEntry(m.Key, m.Name, m.Description)));
        c.EvolutionConditions.AddRange(Catalogs.EvolutionConditions.Select(m => new CatalogEntry(m.Key, m.Name)));
        if (Read("include/constants/battle_move_effects.h") is { } effects)
        {
            c.MoveEffects.AddRange(Catalogs.EnumMembers(effects, "EFFECT_").Select(k => new CatalogEntry(k, GameCatalog.Prettify(k))));
        }

        var battle = Read("include/constants/battle.h");
        c.MoveTargets.AddRange(MovesInfo.Targets
            .Where(t => battle is null || battle.Contains(t.Key, StringComparison.Ordinal))
            .Select(t => new CatalogEntry(t.Key, t.Name)));
        if (Read(ExpansionLayout.TmsHmsFile) is { } tms)
        {
            c.Machines.AddRange(LearnsetFiles.ReadMachines(tms));
        }

        c.Invalidate();
    }

    private static (string? LevelUp, string? Teach, string? Egg) SplitKey(string key)
    {
        var p = key.Split('|');
        static string? N(string s) => s.Length == 0 ? null : s;
        return (N(p[0]), p.Length > 1 ? N(p[1]) : null, p.Length > 2 ? N(p[2]) : null);
    }

    private static bool SameEvolutions(EvolutionSet a, EvolutionSet b) =>
        a.Evolutions.Count == b.Evolutions.Count
        && a.Evolutions.Zip(b.Evolutions).All(p => p.First.Method == p.Second.Method && p.First.Parameter == p.Second.Parameter
            && p.First.TargetSpecies == p.Second.TargetSpecies && p.First.Conditions.SequenceEqual(p.Second.Conditions));

    private sealed class LoadedState
    {
        public required ExpansionLayout Layout { get; init; }
        public Dictionary<string, SourceFile> Files { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SpeciesBlock> Blocks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Species> Species { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, EvolutionSet> Evolutions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SpeciesBlock> MoveBlocks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Move> Moves { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Learnset> Learnsets { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ArrayBlock> LevelUpArrays { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ArrayBlock> EggArrays { get; } = new(StringComparer.Ordinal);
        public LearnablesFile? Learnables { get; set; }
    }
}
