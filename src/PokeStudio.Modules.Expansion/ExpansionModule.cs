using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PokeStudio.Core.Build;
using PokeStudio.Core.Modernize;
using PokeStudio.Core.Models;
using PokeStudio.Core.GameOptions;
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
    public string DisplayName => "pokeemerald-expansion project (Emerald or FireRed base)";
    public GamePlatform Platform => GamePlatform.Gba;
    public ModuleCapabilities Capabilities =>
        ModuleCapabilities.Species | ModuleCapabilities.Moves | ModuleCapabilities.Learnsets | ModuleCapabilities.Evolutions | ModuleCapabilities.Build
        | ModuleCapabilities.Trainers | ModuleCapabilities.TrainerAi | ModuleCapabilities.Encounters | ModuleCapabilities.ItemsAndMarts
        | ModuleCapabilities.Text;
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

    /// <summary>Same build, but the ROM is named after the base game (pokeemerald.gba / pokefirered.gba).</summary>
    public BuildDefinition BuildFor(string workingSourcePath) =>
        Build with { OutputFile = $"poke{new ExpansionLayout(workingSourcePath).BuildName}.gba" };

    public IReadOnlyList<FeatureSupport> DescribeFeatures() =>
    [
        new(ModuleCapabilities.Species, SupportLevel.Supported),
        new(ModuleCapabilities.Learnsets, SupportLevel.Supported),
        new(ModuleCapabilities.Evolutions, SupportLevel.Supported),
        new(ModuleCapabilities.Build, SupportLevel.Supported),
        new(ModuleCapabilities.Moves, SupportLevel.Supported),
        new(ModuleCapabilities.Trainers, SupportLevel.Supported),
        new(ModuleCapabilities.TrainerAi, SupportLevel.Supported),
        new(ModuleCapabilities.Encounters, SupportLevel.Supported),
        new(ModuleCapabilities.ItemsAndMarts, SupportLevel.Supported),
        new(ModuleCapabilities.Text, SupportLevel.Supported),
        new(ModuleCapabilities.Graphics, SupportLevel.NotYet, "The graphics studio arrives in Phase 14."),
        new(ModuleCapabilities.Models3d, SupportLevel.Never, "GBA games have no 3D models."),
    ];

    public DetectionResult Detect(GameSource source)
    {
        if (!Directory.Exists(source.Path) || !ExpansionLayout.LooksLikeExpansion(source.Path))
        {
            return DetectionResult.NoMatch;
        }

        var version = ExpansionLayout.ReadVersion(source.Path);
        var fireRed = new ExpansionLayout(source.Path).IsFireRedBase;
        var notes = new List<string> { "Your project folder is edited directly. Every file is backed up before the app changes it." };
        if (fireRed)
        {
            notes.Add("FireRed-based (pokefirered-expansion, a community port of pokeemerald-expansion).");
        }
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
            GameTitle = (fireRed ? "pokefirered-expansion" : "pokeemerald-expansion") + (version is null ? "" : $" {version}"),
            Version = version?.ToString(),
            SourceKind = GameSourceKind.DecompProject,
            Notes = notes,
        };
    }

    public IReadOnlyList<string> ManagedFiles(string workingSourcePath)
    {
        var layout = new ExpansionLayout(workingSourcePath);
        return [.. layout.SpeciesFiles(), layout.LevelUpFile, ExpansionLayout.EggMovesFile, ExpansionLayout.LearnablesJson, MovesInfo.File, TrainerParty.File,
            EncountersFile.File, ItemsInfo.File, CapsFiles.Source, CapsFiles.Config, BattleConfig, ShopsJson.File, ShopsJson.ShopC, DifficultyFile, AiConfig,
            .. GameOptionsWriter.Files(layout).Where(f => !f.StartsWith("data/maps/", StringComparison.Ordinal)), .. ScriptFiles(layout.Root)];
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

        if (state.TrainersText is not null)
        {
            // how to write each species: its name when that converts back to the right constant (directly or through an alias)
            var speciesNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var s in data.Species)
            {
                if (TrainerParty.SpeciesConstant(s.Name) == s.Key)
                {
                    speciesNames.TryAdd(s.Key, s.Name);
                }
            }

            foreach (var (alias, target) in state.SpeciesAliases)
            {
                if (data.Species.FirstOrDefault(s => s.Key == target) is { } s && TrainerParty.SpeciesConstant(s.Name) == alias)
                {
                    speciesNames.TryAdd(target, s.Name);
                }
            }

            var trainerEdits = new List<TextEdit>();
            foreach (var t in data.Trainers.Where(t => t.LockedReason is null))
            {
                if (state.Trainers.TryGetValue(t.Key, out var before) && !ReferenceEquals(before, t) && state.TrainerBlocks.TryGetValue(t.Key, out var tb))
                {
                    trainerEdits.AddRange(TrainerParty.EditsFor(state.TrainersText, tb, before, t, data.Catalog, speciesNames));
                }
            }

            // difficulty versions added in the app: a new block after the trainer's last existing version
            var nl = CScanner.LineEnding(state.TrainersText);
            foreach (var group in data.Trainers.Where(t => !state.TrainerBlocks.ContainsKey(t.Key) && t.LockedReason is null).GroupBy(t => t.Key.Split('@')[0]))
            {
                var anchor = state.TrainerBlocks.Values.Where(b => b.Symbol == group.Key).OrderBy(b => b.End).LastOrDefault();
                var at = anchor?.End ?? state.TrainersText.Length;
                var text = string.Concat(group.Select(t => TrainerParty.FormatTrainer(t, data.Catalog, speciesNames, nl)));
                if (at == state.TrainersText.Length && !state.TrainersText.EndsWith(nl + nl, StringComparison.Ordinal))
                {
                    text = (state.TrainersText.EndsWith(nl, StringComparison.Ordinal) ? nl : nl + nl) + text.TrimEnd() + nl;
                }

                trainerEdits.Add(new TextEdit(at, 0, text));
            }

            // versions removed in the app
            var keep = data.Trainers.Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var removed in state.TrainerBlocks.Values.Where(b => !keep.Contains(b.Key) && b.Key.Contains('@', StringComparison.Ordinal)))
            {
                trainerEdits.Add(new TextEdit(removed.Start, removed.End - removed.Start, ""));
            }

            if (trainerEdits.Count > 0)
            {
                await context.Writer.WriteAllTextAsync(state.Layout.Full(TrainerParty.File), TextEdits.Apply(state.TrainersText, trainerEdits), cancellationToken);
            }

        }

        // Extra Hard: needed by difficulty versions and by an options menu that offers it
        var options = data.GameOptions;
        var optionsExtraHard = options is not null && (options.Difficulty == OptionMode.PlayerChooses && options.OfferExtraHard
            || options.Difficulty == OptionMode.AlwaysOn && options.FixedDifficulty == DifficultyChoice.ExtraHard);
        if ((optionsExtraHard || data.Trainers.Any(t => t.Key.EndsWith("@Extra Hard", StringComparison.Ordinal))) && File.Exists(state.Layout.Full(DifficultyFile)))
        {
            var diff = await File.ReadAllTextAsync(state.Layout.Full(DifficultyFile), cancellationToken);
            if (!diff.Contains("DIFFICULTY_EXTRA_HARD", StringComparison.Ordinal)
                && Regex.Match(diff, @"^([ \t]*)DIFFICULTY_HARD,[^\n]*\n", RegexOptions.Multiline) is { Success: true } m)
            {
                var added = $"{m.Groups[1].Value}DIFFICULTY_EXTRA_HARD, // added by Poke Studio{CScanner.LineEnding(diff)}";
                await context.Writer.WriteAllTextAsync(state.Layout.Full(DifficultyFile), TextEdits.Apply(diff, [new TextEdit(m.Index + m.Length, 0, added)]), cancellationToken);
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

        await SaveWorldAsync(data, state, context, edits, cancellationToken);

        foreach (var (file, list) in edits.Where(e => e.Value.Count > 0))
        {
            var updated = TextEdits.Apply(state.Files[file].Text, list);
            await context.Writer.WriteAllTextAsync(state.Layout.Full(file), updated, cancellationToken);
        }

        if (learnablesChanged && state.Learnables is not null)
        {
            await context.Writer.WriteAllTextAsync(state.Layout.Full(ExpansionLayout.LearnablesJson), state.Learnables.Serialize(), cancellationToken);
        }

        // new-game options last: they read the files as the steps above left them
        if (options is { AnyUsed: true })
        {
            foreach (var (file, text) in GameOptionsWriter.PlanFor(state.Layout, options))
            {
                await context.Writer.WriteAllTextAsync(state.Layout.Full(file), text, cancellationToken);
            }
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

    public bool SupportsGameOptions(GameData data) =>
        _states.TryGetValue(data, out var state) && File.Exists(state.Layout.Full(GameOptionsWriter.StartMapScripts(state.Layout)))
        && File.Exists(state.Layout.Full(GameOptionsWriter.EventScripts)) && File.Exists(state.Layout.Full(GameOptionsWriter.FlagsFile));

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

    /// <summary>Map scripts and shared text files that may hold shops or dialogue.</summary>
    private static IEnumerable<string> ScriptFiles(string root)
    {
        var maps = Path.Combine(root, "data", "maps");
        if (Directory.Exists(maps))
        {
            foreach (var dir in Directory.GetDirectories(maps).Order(StringComparer.Ordinal))
            {
                foreach (var name in new[] { "scripts.inc", "text.inc" })
                {
                    if (File.Exists(Path.Combine(dir, name)))
                    {
                        yield return $"data/maps/{Path.GetFileName(dir)}/{name}";
                    }
                }
            }
        }

        var text = Path.Combine(root, "data", "text");
        if (Directory.Exists(text))
        {
            foreach (var f in Directory.GetFiles(text, "*.inc").Order(StringComparer.Ordinal))
            {
                yield return $"data/text/{Path.GetFileName(f)}";
            }
        }
    }

    private const string BattleConfig = "include/config/battle.h";
    private const string DifficultyFile = "include/constants/difficulty.h";
    private const string AiConfig = "include/config/ai.h";

    /// <summary>Grass, fire, water starter (Emerald: src/starter_choose.c; FireRed: src/field_specials.c).</summary>
    private static List<string> ReadStarters(ExpansionLayout layout)
    {
        var fireRed = layout.IsFireRedBase;
        var choose = layout.Full("src/starter_choose.c");
        if (!fireRed && File.Exists(choose))
        {
            var text = File.ReadAllText(choose);
            var list = new List<string>();
            foreach (var name in new[] { "GRASS_STARTER", "FIRE_STARTER", "WATER_STARTER" })
            {
                var m = Regex.Match(text, $@"#define\s+{name}\s+\(?\s*(?:IS_FRLG\s*\?\s*(SPECIES_\w+)\s*:\s*)?(SPECIES_\w+)");
                if (m.Success)
                {
                    list.Add(m.Groups[2].Value);
                }
            }

            if (list.Count == 3)
            {
                return list;
            }
        }

        var specials = layout.Full("src/field_specials.c");
        if (fireRed && File.Exists(specials))
        {
            var m = Regex.Match(File.ReadAllText(specials), @"sStarterSpecies\[\]\s*=\s*\{([^}]*)\}");
            if (m.Success)
            {
                var list = Regex.Matches(m.Groups[1].Value, @"SPECIES_\w+").Select(x => x.Value).ToList();
                if (list.Count == 3)
                {
                    return list;
                }
            }
        }

        return [];
    }

    public int? MeasureText(string line) => _font?.Measure(line);

    private FontMetrics? _font;

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
            TrainerNameLength = layout.NameLength("TRAINER_NAME_LENGTH", 10),
            MaxPartySize = layout.NameLength("PARTY_SIZE", 6),
            ItemNameLength = layout.NameLength("ITEM_NAME_LENGTH", 20),
            TextLineWidth = layout.TextBoxWidth,
            MaxMovePower = 511, // u16 power:9
            MinMovePriority = -8, // s32 priority:4
            MaxMovePriority = 7,
        };

        var state = new LoadedState { Layout = layout };
        var nationalDex = File.Exists(layout.Full("include/constants/pokedex.h"))
            ? Catalogs.EnumMembers(File.ReadAllText(layout.Full("include/constants/pokedex.h")), "NATIONAL_DEX_")
                .Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i, StringComparer.Ordinal)
            : new Dictionary<string, int>();
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

                var species = SpeciesInfo.ToSpecies(file, block, index++, out var lv, out var teach, out var egg, out var teachingType, nationalDex);
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

        if (File.Exists(layout.Full(TrainerParty.File)))
        {
            state.TrainersText = File.ReadAllText(layout.Full(TrainerParty.File));
            // the game resolves aliases such as SPECIES_CASTFORM = SPECIES_CASTFORM_NORMAL; so do we
            var speciesAliases = Catalogs.Aliases(File.Exists(layout.Full("include/constants/species.h")) ? File.ReadAllText(layout.Full("include/constants/species.h")) : "", "SPECIES_");
            var moveAliases = Catalogs.Aliases(File.Exists(layout.Full("include/constants/moves.h")) ? File.ReadAllText(layout.Full("include/constants/moves.h")) : "", "MOVE_");
            state.SpeciesAliases = speciesAliases;
            foreach (var (block, parsed) in TrainerParty.Parse(state.TrainersText, TrainerParty.SpeciesConstant))
            {
                var trainer = parsed with
                {
                    Party = parsed.Party.Select(p => p with
                    {
                        Species = speciesAliases.GetValueOrDefault(p.Species, p.Species),
                        Moves = p.Moves.Select(m => moveAliases.GetValueOrDefault(m, m)).ToList(),
                    }).ToList(),
                };
                if (state.TrainerBlocks.TryAdd(block.Key, block))
                {
                    state.Trainers[block.Key] = trainer;
                    data.Trainers.Add(trainer);
                }
            }
        }

        ParseWorld(layout, state, data);
        data.Starters.AddRange(ReadStarters(layout));
        if (File.Exists(layout.Full(AiConfig)))
        {
            var ai = File.ReadAllText(layout.Full(AiConfig));
            foreach (var d in Catalogs.DefineMembers(ai, ""))
            {
                if (int.TryParse(d.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && (d.Key.Contains("PERCENTAGE", StringComparison.Ordinal) || d.Key.StartsWith("STAY_IN", StringComparison.Ordinal)))
                {
                    var setting = new AiSetting(d.Key, AiLabel(d.Key), v, d.Comment, d.Key.Contains("PERCENTAGE", StringComparison.Ordinal));
                    state.AiSettings[d.Key] = setting;
                    data.AiSettings.Add(setting);
                }
            }
        }
        FillCatalog(layout, data.Catalog);
        return (data, state);
    }

    private void ParseWorld(ExpansionLayout layout, LoadedState state, GameData data)
    {
        _font = FontMetrics.Load(layout.Root);

        state.Encounters = EncountersFile.Load(layout.Full(EncountersFile.File));
        if (state.Encounters is { } enc)
        {
            var (tables, chances) = enc.Read();
            foreach (var t in tables.Where(t => state.EncounterTables.TryAdd(t.Key, t)))
            {
                data.Encounters.Add(t);
            }

            data.EncounterChances.AddRange(chances);
        }

        if (File.Exists(layout.Full(ItemsInfo.File)))
        {
            var file = SpeciesInfo.LoadFile(layout, ItemsInfo.File);
            state.Files[ItemsInfo.File] = file;
            foreach (var block in SpeciesInfo.ParseBlocks(file, items: true).Where(b => b.Symbol != "ITEM_NONE"))
            {
                if (state.ItemBlocks.TryAdd(block.Symbol, block))
                {
                    var item = ItemsInfo.ToItem(file, block);
                    state.Items[block.Symbol] = item;
                    data.Items.Add(item);
                }
            }
        }

        foreach (var rel in ScriptFiles(layout.Root))
        {
            var text = File.ReadAllText(layout.Full(rel));
            if (text.Contains("pokemartlistend", StringComparison.Ordinal))
            {
                var parsed = MartScripts.Parse(rel, text);
                state.ScriptFiles[rel] = parsed;
                foreach (var list in parsed.Lists)
                {
                    var tiers = parsed.Tiers.TryGetValue(list.Label, out var full)
                        ? MartScripts.Additions(list.Items, full)
                        : new Dictionary<int, IReadOnlyList<string>>();
                    var mart = new Mart
                    {
                        Key = list.Label,
                        Place = MartScripts.PlaceName(rel, list.Label, parsed.Lists.Count),
                        Items = list.Items,
                        BadgeTiers = tiers,
                    };
                    if (state.Marts.TryAdd(mart.Key, mart))
                    {
                        state.MartFiles[mart.Key] = rel;
                        data.Marts.Add(mart);
                    }
                }
            }

            state.TextSources[rel] = text;
            foreach (var entry in TextScripts.Parse(rel, text))
            {
                var key = entry.Label;
                if (state.TextEntries.ContainsKey(key))
                {
                    continue;
                }

                state.TextEntries[key] = entry;
                var model = new TextEntry { Key = key, Place = TextScripts.PlaceName(rel), Text = entry.Text, Original = entry.Text };
                state.Texts[key] = model;
                data.Texts.Add(model);
            }
        }

        if (File.Exists(layout.Full(ShopsJson.File)))
        {
            state.ShopsJson = ShopsJson.Parse(File.ReadAllText(layout.Full(ShopsJson.File)));
            foreach (var mart in state.ShopsJson is null ? [] : ShopsJson.ToMarts(state.ShopsJson))
            {
                if (state.Marts.TryAdd(mart.Key, mart))
                {
                    state.MartFiles[mart.Key] = ShopsJson.File;
                    data.Marts.Add(mart);
                }
            }
        }

        if (File.Exists(layout.Full(CapsFiles.Source)) && File.Exists(layout.Full(CapsFiles.Config)))
        {
            var src = File.ReadAllText(layout.Full(CapsFiles.Source));
            var cfg = File.ReadAllText(layout.Full(CapsFiles.Config));
            var table = CapsFiles.ReadTable(src);
            if (table.Count > 0)
            {
                string Def(string n) => CapsFiles.Define(cfg, n)?.Value ?? "";
                state.CapsSource = src;
                state.CapsConfig = cfg;
                state.CapEntries = table;
                state.Rules = new GameRules
                {
                    LevelCaps = table.Select(e => new LevelCap(e.Flag, CapsFiles.Label(e.Flag), e.Level)).ToList(),
                    CapsEnabled = Def("B_LEVEL_CAP_TYPE") is { Length: > 0 } t && t != "LEVEL_CAP_NONE",
                    ExpCap = Def("B_EXP_CAP_TYPE") switch { "EXP_CAP_HARD" => ExpCapKind.Hard, "EXP_CAP_SOFT" => ExpCapKind.Soft, _ => ExpCapKind.None },
                    RareCandyCap = Def("B_RARE_CANDY_CAP") == "TRUE",
                    ExpBoostBelowCap = Def("B_LEVEL_CAP_EXP_UP") == "TRUE",
                    PhysicalSpecialSplit = SplitSetting(layout),
                };
                data.Rules = state.Rules;
            }
        }

        if (data.Rules is null && SplitSetting(layout) is { } split)
        {
            state.Rules = new GameRules { PhysicalSpecialSplit = split };
            data.Rules = state.Rules;
        }
    }

    /// <summary>"SHOULD_SWITCH_LOSES_1V1_PERCENTAGE" → "Switch out when: loses 1v1".</summary>
    private static string AiLabel(string key)
    {
        var words = key.Replace("_PERCENTAGE", "", StringComparison.Ordinal);
        var prefix = "";
        if (words.StartsWith("SHOULD_SWITCH_", StringComparison.Ordinal))
        {
            prefix = "Switch out when: ";
            words = words["SHOULD_SWITCH_".Length..];
        }
        else if (words.StartsWith("STAY_IN_", StringComparison.Ordinal))
        {
            prefix = "Stay in when: ";
            words = words["STAY_IN_".Length..];
        }

        var text = words.Replace('_', ' ').ToLowerInvariant();
        return prefix + text;
    }

    /// <summary>B_PHYSICAL_SPECIAL_SPLIT ≥ GEN_4 means each move's category counts.</summary>
    private static bool? SplitSetting(ExpansionLayout layout) =>
        layout.Macros.IsDefined("B_PHYSICAL_SPECIAL_SPLIT") && layout.Macros.TryEvaluate("B_PHYSICAL_SPECIAL_SPLIT >= GEN_4") is { } v ? v != 0 : null;

    private async Task SaveWorldAsync(GameData data, LoadedState state, ModuleSaveContext context, Dictionary<string, List<TextEdit>> edits, CancellationToken cancellationToken)
    {
        void Add(string file, IEnumerable<TextEdit> list)
        {
            if (!edits.TryGetValue(file, out var l))
            {
                edits[file] = l = [];
            }

            l.AddRange(list);
        }

        // wild encounters (JSON)
        if (state.Encounters is { RoundTrips: true } enc)
        {
            var changed = data.Encounters.Where(t => state.EncounterTables.TryGetValue(t.Key, out var b) && !ReferenceEquals(b, t)).ToList();
            if (changed.Count > 0)
            {
                enc.Update(changed);
                await context.Writer.WriteAllTextAsync(state.Layout.Full(EncountersFile.File), enc.Serialize(), cancellationToken);
            }
        }

        // items
        foreach (var item in data.Items)
        {
            if (state.Items.TryGetValue(item.Key, out var before) && !ReferenceEquals(before, item) && state.ItemBlocks.TryGetValue(item.Key, out var block) && block.Active)
            {
                Add(ItemsInfo.File, ItemsInfo.EditsFor(state.Files[ItemsInfo.File], block, before, item));
            }
        }

        // shops and text share the map script files
        var scriptEdits = new Dictionary<string, List<TextEdit>>(StringComparer.Ordinal);
        var jsonShops = data.Marts.Where(m => state.MartFiles.GetValueOrDefault(m.Key) == ShopsJson.File
            && state.Marts.TryGetValue(m.Key, out var b) && !ReferenceEquals(b, m)).ToList();
        if (jsonShops.Count > 0 && state.ShopsJson is { } shopsJson)
        {
            var (shopEdits, needsFuncs) = ShopsJson.EditsFor(shopsJson, jsonShops.Select(m => (state.Marts[m.Key], m)).ToList());
            if (shopEdits.Count > 0)
            {
                await context.Writer.WriteAllTextAsync(state.Layout.Full(ShopsJson.File), TextEdits.Apply(shopsJson.Text, shopEdits), cancellationToken);
            }

            if (needsFuncs && File.Exists(state.Layout.Full(ShopsJson.ShopC)))
            {
                var shopC = await File.ReadAllTextAsync(state.Layout.Full(ShopsJson.ShopC), cancellationToken);
                if (ShopsJson.BadgeFunctionsEdit(shopC) is { } add)
                {
                    await context.Writer.WriteAllTextAsync(state.Layout.Full(ShopsJson.ShopC), TextEdits.Apply(shopC, [add]), cancellationToken);
                }
            }
        }

        foreach (var group in data.Marts
            .Where(m => state.MartFiles.GetValueOrDefault(m.Key) != ShopsJson.File && state.Marts.TryGetValue(m.Key, out var b) && !ReferenceEquals(b, m))
            .GroupBy(m => state.MartFiles[m.Key]))
        {
            var file = state.ScriptFiles[group.Key];
            var pairs = group.Select(m => (state.Marts[m.Key], m)).ToList();
            scriptEdits[group.Key] = MartScripts.EditsFor(file, pairs);
        }

        foreach (var t in data.Texts)
        {
            if (state.Texts.TryGetValue(t.Key, out var before) && before.Text != t.Text && state.TextEntries.TryGetValue(t.Key, out var entry))
            {
                var src = state.TextSources[entry.File];
                if (!scriptEdits.TryGetValue(entry.File, out var list))
                {
                    scriptEdits[entry.File] = list = [];
                }

                list.Add(new TextEdit(entry.Start, entry.End - entry.Start, TextScripts.Format(t.Text, entry.Indent, entry.Terminated, CScanner.LineEnding(src))));
            }
        }

        foreach (var (rel, list) in scriptEdits.Where(e => e.Value.Count > 0))
        {
            await context.Writer.WriteAllTextAsync(state.Layout.Full(rel), TextEdits.Apply(state.TextSources[rel], list), cancellationToken);
        }

        // AI tuning (include/config/ai.h)
        var changedAi = data.AiSettings.Where(s => state.AiSettings.TryGetValue(s.Key, out var b) && b.Value != s.Value).ToList();
        if (changedAi.Count > 0 && File.Exists(state.Layout.Full(AiConfig)))
        {
            var ai = await File.ReadAllTextAsync(state.Layout.Full(AiConfig), cancellationToken);
            var aiEdits = changedAi.Select(s => CapsFiles.Define(ai, s.Key) is { } d ? new TextEdit(d.Start, d.End - d.Start, s.Value.ToString(CultureInfo.InvariantCulture)) : null)
                .Where(e => e is not null).Select(e => e!).ToList();
            await context.Writer.WriteAllTextAsync(state.Layout.Full(AiConfig), TextEdits.Apply(ai, aiEdits), cancellationToken);
        }

        // physical/special split (include/config/battle.h)
        if (data.Rules?.PhysicalSpecialSplit is { } split && state.Rules?.PhysicalSpecialSplit is { } oldSplit && split != oldSplit
            && File.Exists(state.Layout.Full(BattleConfig)))
        {
            var cfg = await File.ReadAllTextAsync(state.Layout.Full(BattleConfig), cancellationToken);
            if (CapsFiles.Define(cfg, "B_PHYSICAL_SPECIAL_SPLIT") is { } d)
            {
                await context.Writer.WriteAllTextAsync(state.Layout.Full(BattleConfig),
                    TextEdits.Apply(cfg, [new TextEdit(d.Start, d.End - d.Start, split ? "GEN_LATEST" : "GEN_3")]), cancellationToken);
            }
        }

        // level caps
        if (data.Rules is { } rules && state.Rules is { } beforeRules && !ReferenceEquals(rules, beforeRules) && state.CapsSource is { } capsSrc && state.CapsConfig is { } capsCfg)
        {
            var srcEdits = new List<TextEdit>();
            for (var i = 0; i < rules.LevelCaps.Count && i < state.CapEntries.Count; i++)
            {
                if (rules.LevelCaps[i].Level != state.CapEntries[i].Level)
                {
                    var e = state.CapEntries[i];
                    srcEdits.Add(new TextEdit(e.NumberStart, e.NumberEnd - e.NumberStart, rules.LevelCaps[i].Level.ToString(CultureInfo.InvariantCulture)));
                }
            }

            if (srcEdits.Count > 0)
            {
                await context.Writer.WriteAllTextAsync(state.Layout.Full(CapsFiles.Source), TextEdits.Apply(capsSrc, srcEdits), cancellationToken);
            }

            var cfgEdits = new List<TextEdit>();
            void Def(string name, string value)
            {
                if (CapsFiles.Define(capsCfg, name) is { } d && d.Value != value)
                {
                    cfgEdits.Add(new TextEdit(d.Start, d.End - d.Start, value));
                }
            }

            if (rules.CapsEnabled != beforeRules.CapsEnabled)
            {
                Def("B_LEVEL_CAP_TYPE", rules.CapsEnabled ? "LEVEL_CAP_FLAG_LIST" : "LEVEL_CAP_NONE");
            }

            if (rules.ExpCap != beforeRules.ExpCap)
            {
                Def("B_EXP_CAP_TYPE", rules.ExpCap switch { ExpCapKind.Hard => "EXP_CAP_HARD", ExpCapKind.Soft => "EXP_CAP_SOFT", _ => "EXP_CAP_NONE" });
            }

            if (rules.RareCandyCap != beforeRules.RareCandyCap)
            {
                Def("B_RARE_CANDY_CAP", rules.RareCandyCap ? "TRUE" : "FALSE");
            }

            if (rules.ExpBoostBelowCap != beforeRules.ExpBoostBelowCap)
            {
                Def("B_LEVEL_CAP_EXP_UP", rules.ExpBoostBelowCap ? "TRUE" : "FALSE");
            }

            if (cfgEdits.Count > 0)
            {
                await context.Writer.WriteAllTextAsync(state.Layout.Full(CapsFiles.Config), TextEdits.Apply(capsCfg, cfgEdits), cancellationToken);
            }
        }
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

        if (Read("include/constants/trainers.h") is { } trainers)
        {
            c.TrainerClasses.AddRange(Catalogs.EnumMembers(trainers, "TRAINER_CLASS_").Select(k => new CatalogEntry(k, GameCatalog.Prettify(k))));
            c.TrainerPics.AddRange(Catalogs.EnumMembers(trainers, "TRAINER_PIC_").Where(k => !k.StartsWith("TRAINER_PIC_BACK", StringComparison.Ordinal))
                .Select(k => new CatalogEntry(k, GameCatalog.Prettify(k))));
            c.TrainerMusic.AddRange(Catalogs.DefineMembers(trainers, "TRAINER_ENCOUNTER_MUSIC_").Select(d => new CatalogEntry(d.Key, GameCatalog.Prettify(d.Key), d.Comment)));
        }

        c.Natures.AddRange(Catalogs.DefineMembers(pokemonConstants ?? "", "NATURE_").Where(d => d.Key != "NATURE_COUNT" && d.Key != "NATURE_RANDOM")
            .Select(d => new CatalogEntry(d.Key, GameCatalog.Prettify(d.Key), d.Comment)));
        if (Read("include/constants/battle_ai.h") is { } ai)
        {
            c.AiFlags.AddRange(Catalogs.DefineMembers(ai, "AI_FLAG_")
                .Where(d => d.Key is not ("AI_FLAG_COUNT" or "AI_FLAG_DYNAMIC_FUNC" or "AI_FLAG_ROAMING" or "AI_FLAG_SAFARI" or "AI_FLAG_FIRST_BATTLE") && !d.Value.StartsWith("(1", StringComparison.Ordinal))
                .Select(d => new CatalogEntry(d.Key, GameCatalog.Prettify(d.Key),
                    d.Value.Contains('|', StringComparison.Ordinal) ? "Preset: " + string.Join(" + ", Regex.Matches(d.Value, @"AI_FLAG_\w+").Select(m => GameCatalog.Prettify(m.Value))) : d.Comment)));
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
        public string? TrainersText { get; set; }
        public Dictionary<string, string> SpeciesAliases { get; set; } = new(StringComparer.Ordinal);
        public EncountersFile? Encounters { get; set; }
        public Dictionary<string, EncounterTable> EncounterTables { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SpeciesBlock> ItemBlocks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Item> Items { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, MartScripts.ScriptFile> ScriptFiles { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Mart> Marts { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> MartFiles { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> TextSources { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TextScripts.Entry> TextEntries { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TextEntry> Texts { get; } = new(StringComparer.Ordinal);
        public string? CapsSource { get; set; }
        public string? CapsConfig { get; set; }
        public List<CapsFiles.CapEntry> CapEntries { get; set; } = [];
        public ShopsJson.Parsed? ShopsJson { get; set; }
        public Dictionary<string, AiSetting> AiSettings { get; } = new(StringComparer.Ordinal);
        public GameRules? Rules { get; set; }
        public Dictionary<string, TrainerBlock> TrainerBlocks { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Trainer> Trainers { get; } = new(StringComparer.Ordinal);
    }
}
