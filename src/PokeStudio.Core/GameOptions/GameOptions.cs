namespace PokeStudio.Core.GameOptions;

/// <summary>How the hack creator wants an option handled.</summary>
public enum OptionMode
{
    /// <summary>Not added to the game at all.</summary>
    NotUsed,

    /// <summary>Asked in the new-game options menu.</summary>
    PlayerChooses,

    AlwaysOn,
    AlwaysOff,
}

public enum DifficultyChoice
{
    Easy,
    Normal,
    Hard,
    ExtraHard,
}

/// <summary>The hack creator's new-game options. Saved with the Poke Studio project; written into the game on save.</summary>
public sealed record GameOptionsSettings
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>PlayerChooses = a difficulty menu; AlwaysOn = always <see cref="FixedDifficulty"/>.</summary>
    public OptionMode Difficulty { get; init; } = OptionMode.NotUsed;

    public DifficultyChoice DefaultDifficulty { get; init; } = DifficultyChoice.Normal;
    public DifficultyChoice FixedDifficulty { get; init; } = DifficultyChoice.Normal;
    public bool OfferExtraHard { get; init; } = true;

    /// <summary>On = EVs and IVs are switched off (everyone has perfect IVs and no EVs).</summary>
    public OptionMode TrainingOff { get; init; } = OptionMode.NotUsed;

    /// <summary>On = hard level cap (no experience at the cap).</summary>
    public OptionMode LevelCap { get; init; } = OptionMode.NotUsed;

    /// <summary>On = no bag in battle.</summary>
    public OptionMode NoItemsInBattle { get; init; } = OptionMode.NotUsed;

    public bool NoItemsAlsoWild { get; init; }

    /// <summary>On = the player receives an Infinite Candy (never used up; level caps still apply).</summary>
    public OptionMode InfiniteCandy { get; init; } = OptionMode.NotUsed;

    public bool AnyUsed => Difficulty != OptionMode.NotUsed || TrainingOff != OptionMode.NotUsed || LevelCap != OptionMode.NotUsed
        || NoItemsInBattle != OptionMode.NotUsed || InfiniteCandy != OptionMode.NotUsed;
}
