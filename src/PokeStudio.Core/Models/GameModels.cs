namespace PokeStudio.Core.Models;

// Storage-independent models. Adapters (decomp project, GBA ROM, NDS NitroFS/NARC, 3DS GARC/LayeredFS)
// translate between these and each game's native formats.
//
// References between objects use string keys (e.g. "SPECIES_BULBASAUR", "MOVE_TACKLE" in decomp
// projects, or "1" / "33" in ROM modules) so every platform can use its natural identifiers.
// Display names come from the GameCatalog.

public enum GamePlatform
{
    Gba,
    Nds,
    N3ds,
    Demo,

    /// <summary>Game Boy / Game Boy Color.</summary>
    Gb,
}

public enum ElementType
{
    Normal, Fire, Water, Electric, Grass, Ice, Fighting, Poison, Ground,
    Flying, Psychic, Bug, Rock, Ghost, Dragon, Dark, Steel, Fairy,
}

public enum GrowthRate
{
    Fast, MediumFast, MediumSlow, Slow, Erratic, Fluctuating,
}

public sealed record BaseStats(int Hp, int Attack, int Defense, int SpAttack, int SpDefense, int Speed)
{
    public const int Max = 255;
    public int Total => Hp + Attack + Defense + SpAttack + SpDefense + Speed;
}

/// <summary>Gender ratio as the share of females, or a fixed kind.</summary>
public sealed record GenderRatio(double? PercentFemale, bool Genderless = false)
{
    public static GenderRatio AlwaysMale { get; } = new(0);
    public static GenderRatio AlwaysFemale { get; } = new(100);
    public static GenderRatio NoGender { get; } = new(null, Genderless: true);

    public string Describe() =>
        Genderless ? "No gender"
        : PercentFemale is 0 ? "Always male"
        : PercentFemale is 100 ? "Always female"
        : $"{PercentFemale:0.#}% female";
}

public sealed record Species
{
    /// <summary>Position in the game's list (for display/sorting).</summary>
    public required int Id { get; init; }

    /// <summary>Stable identifier inside the game, e.g. "SPECIES_RATICATE_ALOLA".</summary>
    public string Key { get; init; } = "";

    public required string Name { get; init; }

    /// <summary>Extra label for alternate forms, e.g. "Alolan" or "Mega".</summary>
    public string? FormName { get; init; }

    public required BaseStats Stats { get; init; }

    /// <summary>Type keys, e.g. "TYPE_GRASS". One or two entries.</summary>
    public IReadOnlyList<string> Types { get; init; } = [];

    /// <summary>Ability keys by slot: [0] first, [1] second, [2] hidden. "ABILITY_NONE" for empty.</summary>
    public IReadOnlyList<string> Abilities { get; init; } = [];

    public string? HeldItemCommon { get; init; }
    public string? HeldItemRare { get; init; }
    public int CatchRate { get; init; } = 45;
    public int ExpYield { get; init; }

    /// <summary>Growth-rate key, e.g. "GROWTH_MEDIUM_SLOW".</summary>
    public string Growth { get; init; } = "";

    public GenderRatio? Gender { get; init; }
    public IReadOnlyList<string> EggGroups { get; init; } = [];
    public string Category { get; init; } = "";
    public int NationalDexNumber { get; init; }
    public string DexEntry { get; init; } = "";

    /// <summary>False when the game's settings switch this Pokémon off (it isn't built into the game).</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>Learnset this species uses (forms often share one).</summary>
    public string? LearnsetKey { get; init; }

    /// <summary>Fields the user can't edit here, with a plain-English reason (field name → reason).</summary>
    public IReadOnlyDictionary<string, string> LockedFields { get; init; } = new Dictionary<string, string>();

    /// <summary>Legendary, mythical, Ultra Beast, Paradox, Mega… (from the game's species data).</summary>
    public SpeciesTags Tags { get; init; }

    /// <summary>National Pokédex number (official Pokémon are 1–1025), or null when the game doesn't say.</summary>
    public int? NationalDex { get; init; }

    /// <summary>Fields whose value comes from a game setting (field name → note shown in Advanced mode).</summary>
    public IReadOnlyDictionary<string, string> SettingDependentFields { get; init; } = new Dictionary<string, string>();

    public string DisplayName => FormName is null ? Name : $"{Name} ({FormName})";
}

public enum MoveCategory
{
    Physical, Special, Status,
}

public sealed record Move
{
    public required int Id { get; init; }
    public string Key { get; init; } = "";
    public required string Name { get; init; }

    /// <summary>Text shown in the summary screen; lines separated by "\n".</summary>
    public string Description { get; init; } = "";

    public string Type { get; init; } = "";
    public MoveCategory Category { get; init; }
    public int Power { get; init; }

    /// <summary>0 = never misses.</summary>
    public int Accuracy { get; init; } = 100;

    public int Pp { get; init; } = 10;
    public int Priority { get; init; }

    /// <summary>Battle effect key, e.g. "EFFECT_HIT".</summary>
    public string Effect { get; init; } = "";

    /// <summary>Who it hits, e.g. "TARGET_SELECTED".</summary>
    public string Target { get; init; } = "";

    public bool MakesContact { get; init; }
    public string EffectDescription { get; init; } = "";

    /// <summary>Fields that can't be edited here, with the plain-English reason ("*" = the whole move).</summary>
    public IReadOnlyDictionary<string, string> LockedFields { get; init; } = new Dictionary<string, string>();

    /// <summary>Fields whose value depends on a game setting, with an explanation.</summary>
    public IReadOnlyDictionary<string, string> SettingDependentFields { get; init; } = new Dictionary<string, string>();
}

public sealed record LevelUpMove(int Level, string Move);

/// <summary>Moves a Pokémon can learn. Keyed by learnset (several forms may share one).</summary>
public sealed record Learnset
{
    public required string Key { get; init; }

    /// <summary>Species that use this learnset (for "shared with" warnings).</summary>
    public IReadOnlyList<string> UsedBy { get; init; } = [];

    public IReadOnlyList<LevelUpMove> LevelUp { get; init; } = [];

    /// <summary>TM/HM/tutor moves this Pokémon can be taught.</summary>
    public IReadOnlyList<string> Teachable { get; init; } = [];

    public IReadOnlyList<string> EggMoves { get; init; } = [];

    /// <summary>Parts that can't be edited here, with a reason (e.g. "teachable" → "…").</summary>
    public IReadOnlyDictionary<string, string> LockedParts { get; init; } = new Dictionary<string, string>();
}

public sealed record EvolutionCondition(string Kind, string Value);

public sealed record Evolution(string Method, string Parameter, string TargetSpecies)
{
    public IReadOnlyList<EvolutionCondition> Conditions { get; init; } = [];
}

/// <summary>All evolutions starting from one species.</summary>
public sealed record EvolutionSet
{
    public required string FromSpecies { get; init; }
    public IReadOnlyList<Evolution> Evolutions { get; init; } = [];

    /// <summary>Reason the evolutions can't be edited here, if any.</summary>
    public string? LockedReason { get; init; }

    /// <summary>
    /// Locked for adding, removing or reordering (the list is split by game settings in the source), but each entry the
    /// game uses can still be replaced one for one (used by Fix evolutions).
    /// </summary>
    public bool EntriesReplaceable { get; init; }
}

/// <summary>Special categories a species can belong to (from the game's own data).</summary>
[Flags]
public enum SpeciesTags
{
    None = 0,
    RestrictedLegendary = 1 << 0,
    SubLegendary = 1 << 1,
    Mythical = 1 << 2,
    UltraBeast = 1 << 3,
    Paradox = 1 << 4,

    /// <summary>Mega Evolution or Primal Reversion form.</summary>
    Mega = 1 << 5,
}

public enum BattleType
{
    Single, Double, Triple, Rotation,
}

/// <summary>One Pokémon on a trainer's team. Keys are game constants (SPECIES_…, MOVE_…, ITEM_…); null = the game's default.</summary>
public sealed record TrainerPokemon
{
    public required string Species { get; init; }
    public string? Nickname { get; init; }

    /// <summary>"M", "F" or null (random by the species' ratio).</summary>
    public string? Gender { get; init; }

    public int Level { get; init; } = 100;

    /// <summary>Empty = the last four moves it learns by level-up at its level.</summary>
    public IReadOnlyList<string> Moves { get; init; } = [];

    public string? HeldItem { get; init; }
    public string? Ability { get; init; }
    public string? Nature { get; init; }
    public BaseStats? Evs { get; init; }
    public BaseStats? Ivs { get; init; }
    public string? Ball { get; init; }
    public int? Happiness { get; init; }
    public bool Shiny { get; init; }
    public string? TeraType { get; init; }
    public int? DynamaxLevel { get; init; }
    public bool Gigantamax { get; init; }

    /// <summary>Lines the app doesn't edit, kept as they are ("Key", "Value").</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Extra { get; init; } = [];

    public bool Equivalent(TrainerPokemon other) =>
        Species == other.Species && Nickname == other.Nickname && Gender == other.Gender && Level == other.Level
        && Moves.SequenceEqual(other.Moves) && HeldItem == other.HeldItem && Ability == other.Ability && Nature == other.Nature
        && Evs == other.Evs && Ivs == other.Ivs && Ball == other.Ball && Happiness == other.Happiness && Shiny == other.Shiny
        && TeraType == other.TeraType && DynamaxLevel == other.DynamaxLevel && Gigantamax == other.Gigantamax
        && Extra.SequenceEqual(other.Extra);
}

public sealed record Trainer
{
    public required int Id { get; init; }

    /// <summary>Code name, e.g. "TRAINER_ROXANNE_1" (plus "@Hard" for difficulty variants).</summary>
    public string Key { get; init; } = "";

    public required string Name { get; init; }
    public string TrainerClass { get; init; } = "";
    public string Pic { get; init; } = "";

    /// <summary>"Male", "Female" or "" (not set).</summary>
    public string Gender { get; init; } = "";

    public string Music { get; init; } = "";
    public IReadOnlyList<string> Items { get; init; } = [];
    public BattleType Battle { get; init; } = BattleType.Single;

    /// <summary>Enemy AI flags, e.g. AI_FLAG_BASIC_TRAINER.</summary>
    public IReadOnlyList<string> AiFlags { get; init; } = [];

    public IReadOnlyList<TrainerPokemon> Party { get; init; } = [];

    /// <summary>
    /// Adaptive boss slot: which party member is the flexible one (null = none). With the new-game option on, the game swaps
    /// it at battle start for whichever of it and <see cref="FlexCandidates"/> best counters the player's party.
    /// </summary>
    public int? FlexIndex { get; init; }

    public IReadOnlyList<TrainerPokemon> FlexCandidates { get; init; } = [];
    public bool HasAdaptiveSlot => FlexIndex is { } i && i < Party.Count && FlexCandidates.Count > 0;
    public int PrizeMoney { get; init; }

    /// <summary>Why this trainer can't be edited here, or null.</summary>
    public string? LockedReason { get; init; }

    /// <summary>
    /// No map starts this battle (e.g. leftover placeholder trainers in FireRed's list). Still editable, but left out
    /// of the Player's Guide and the boss tools.
    /// </summary>
    public bool NeverFought { get; init; }
}

public sealed record ModelAsset(string OwnerKey, string Name, string? ImportedFilePath);

/// <summary>One wild slot: a Pokémon and its level range.</summary>
public sealed record EncounterSlot(string Species, int MinLevel, int MaxLevel);

/// <summary>A way of meeting wild Pokémon on a map (grass, surfing, fishing, rock smash…).</summary>
public sealed record EncounterMethodTable
{
    /// <summary>Storage key, e.g. "land_mons".</summary>
    public required string Type { get; init; }

    /// <summary>How often a step/attempt triggers an encounter (game-specific scale).</summary>
    public int Rate { get; init; }

    public IReadOnlyList<EncounterSlot> Slots { get; init; } = [];
}

/// <summary>All wild encounters of one map (or one special area).</summary>
public sealed record EncounterTable
{
    /// <summary>Unique key, e.g. "gRoute101".</summary>
    public required string Key { get; init; }

    /// <summary>Map constant, e.g. "MAP_ROUTE101" (empty for non-map tables).</summary>
    public string Map { get; init; } = "";

    public string Group { get; init; } = "";
    public IReadOnlyList<EncounterMethodTable> Methods { get; init; } = [];
    public string? LockedReason { get; init; }
}

/// <summary>Per-slot chances for a method type, shared by every map (e.g. land: 20,20,10,…).</summary>
public sealed record EncounterSlotChances(string Type, IReadOnlyList<int> Percent, IReadOnlyDictionary<string, IReadOnlyList<int>> Groups);

public sealed record Item
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public int Price { get; init; }
    public string Description { get; init; } = "";

    /// <summary>Strength of the item's effect where it's a simple number (e.g. HP a Potion restores).</summary>
    public int? EffectAmount { get; init; }

    public string Pocket { get; init; } = "";
    public string HoldEffect { get; init; } = "";
    public IReadOnlyDictionary<string, string> LockedFields { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> SettingDependentFields { get; init; } = new Dictionary<string, string>();
}

/// <summary>A shop's stock list.</summary>
public sealed record Mart
{
    /// <summary>Label of the list, e.g. "OldaleTown_Mart_Pokemart_Basic".</summary>
    public required string Key { get; init; }

    /// <summary>Friendly place name, e.g. "Oldale Town Mart".</summary>
    public string Place { get; init; } = "";

    public IReadOnlyList<string> Items { get; init; } = [];

    /// <summary>Extra stock unlocked by badges: badge number → items added at that point.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<string>> BadgeTiers { get; init; } = new Dictionary<int, IReadOnlyList<string>>();

    public string? LockedReason { get; init; }

    /// <summary>Why badge-unlocked stock can't be added to this shop, or null.</summary>
    public string? TiersLockedReason { get; init; }
}

/// <summary>A piece of in-game text (dialogue, sign…). Line breaks use "\n" (new line), "\l" (scroll), "\p" (new box).</summary>
public sealed record TextEntry
{
    public required string Key { get; init; }
    public string Place { get; init; } = "";
    public required string Text { get; init; }

    /// <summary>The text as it was when the project was opened; only changed text is checked for width.</summary>
    public string? Original { get; init; }

    public string? LockedReason { get; init; }
}

/// <summary>Level caps and related settings.</summary>
public sealed record LevelCap(string Milestone, string Label, int Level);

public enum ExpCapKind
{
    None,
    /// <summary>No experience at or above the cap.</summary>
    Hard,
    /// <summary>Reduced experience at or above the cap.</summary>
    Soft,
}

/// <summary>A number in the game's AI settings, e.g. how often it switches out when it would lose the 1-on-1 (0–100%).</summary>
public sealed record AiSetting(string Key, string Label, int Value, string? Comment, bool IsPercentage);

/// <summary>An on/off game setting, e.g. showing IVs and EVs on the summary screen.</summary>
public sealed record GameSwitch(string Key, string Label, string Help, bool Value);

public sealed record GameRules
{
    public IReadOnlyList<LevelCap> LevelCaps { get; init; } = [];
    public bool CapsEnabled { get; init; }
    public ExpCapKind ExpCap { get; init; }
    public bool RareCandyCap { get; init; }
    public bool ExpBoostBelowCap { get; init; }

    /// <summary>
    /// True: each move's own category decides physical/special (Gen 4+). False: the move's type decides (Gen 1–3).
    /// Null when the game has no such setting.
    /// </summary>
    public bool? PhysicalSpecialSplit { get; init; }

    /// <summary>True: TMs aren't used up when taught (Gen 5+). Null when the game can't be switched.</summary>
    public bool? ReusableTms { get; init; }
}
