namespace PokeStudio.Core.Models;

// Storage-independent models. Adapters (decomp project, GBA ROM, NDS NitroFS/NARC, 3DS GARC/LayeredFS)
// translate between these and each game's native formats. Fields grow in later phases; Phase 1
// keeps them minimal so the plugin and editing pipeline can be exercised end to end.

public enum GamePlatform
{
    Gba,
    Nds,
    N3ds,
    Demo,
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

public sealed record Species
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required BaseStats Stats { get; init; }
    public ElementType Type1 { get; init; }
    public ElementType? Type2 { get; init; }
    public IReadOnlyList<string> Abilities { get; init; } = [];
    public string? HeldItemCommon { get; init; }
    public string? HeldItemRare { get; init; }
    public int CatchRate { get; init; } = 45;
    public GrowthRate Growth { get; init; } = GrowthRate.MediumFast;
    public string DexEntry { get; init; } = "";
}

public enum MoveCategory
{
    Physical, Special, Status,
}

public sealed record Move
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public ElementType Type { get; init; }
    public MoveCategory Category { get; init; }
    public int Power { get; init; }
    public int Accuracy { get; init; } = 100;
    public int Pp { get; init; } = 10;
    public int Priority { get; init; }
    public string EffectDescription { get; init; } = "";
}

public sealed record LevelUpMove(int Level, int MoveId);

public sealed record Learnset
{
    public required int SpeciesId { get; init; }
    public IReadOnlyList<LevelUpMove> LevelUp { get; init; } = [];
    public IReadOnlySet<int> MachineMoves { get; init; } = new HashSet<int>();
    public IReadOnlySet<int> TutorMoves { get; init; } = new HashSet<int>();
    public IReadOnlySet<int> EggMoves { get; init; } = new HashSet<int>();
}

public enum EvolutionKind
{
    Level, UseItem, LevelHoldingItem, Friendship, FriendshipDay, FriendshipNight,
    Trade, TradeHoldingItem, KnowsMove, Location, Custom,
}

public sealed record EvolutionMethod(int FromSpeciesId, int ToSpeciesId, EvolutionKind Kind, int Parameter = 0, string? Note = null);

[Flags]
public enum AiBehavior
{
    None = 0,
    PrefersStrongMoves = 1 << 0,
    AvoidsUselessMoves = 1 << 1,
    SwitchesBadMatchups = 1 << 2,
    HealsWhenLow = 1 << 3,
    SetsUpBoosts = 1 << 4,
    PlaysAroundStatus = 1 << 5,
    PredictsSwitches = 1 << 6,
}

public enum AiPreset
{
    Beginner, SmartSwitcher, GymLeader, Champion, Custom,
}

public sealed record AiProfile(AiPreset Preset, AiBehavior Behaviors);

public enum BattleType
{
    Single, Double, Triple, Rotation,
}

public sealed record TrainerPokemon
{
    public required int SpeciesId { get; init; }
    public required int Level { get; init; }
    public IReadOnlyList<int> MoveIds { get; init; } = [];
    public string? HeldItem { get; init; }
    public string? Ability { get; init; }
    public string? Nature { get; init; }
    public BaseStats? Evs { get; init; }
    public BaseStats? Ivs { get; init; }
}

public sealed record Trainer
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public string TrainerClass { get; init; } = "";
    public int PrizeMoney { get; init; }
    public BattleType Battle { get; init; } = BattleType.Single;
    public IReadOnlyList<TrainerPokemon> Party { get; init; } = [];
    public AiProfile Ai { get; init; } = new(AiPreset.Beginner, AiBehavior.AvoidsUselessMoves);
}

public enum SpriteKind
{
    Front, Back, Icon, Overworld, Trainer, Npc,
}

public sealed record SpriteAsset(int OwnerId, SpriteKind Kind, int Width, int Height, string? ImportedFilePath);

public sealed record ModelAsset(int OwnerId, string Name, string? ImportedFilePath);

public enum EncounterMethod
{
    Grass, Surf, OldRod, GoodRod, SuperRod, RockSmash,
}

public sealed record EncounterSlot(int SpeciesId, int MinLevel, int MaxLevel, int RatePercent);

public sealed record Encounter
{
    public required string LocationName { get; init; }
    public EncounterMethod Method { get; init; }
    public IReadOnlyList<EncounterSlot> Slots { get; init; } = [];
    public int TotalRate => Slots.Sum(s => s.RatePercent);
}
