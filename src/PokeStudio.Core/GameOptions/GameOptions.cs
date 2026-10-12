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

    /// <summary>Nuzlocke as a whole: PlayerChooses = "Play a Nuzlocke?"; each rule below then has its own setting.</summary>
    public OptionMode Nuzlocke { get; init; } = OptionMode.NotUsed;

    /// <summary>Only the first wild Pokémon met in each area can be caught.</summary>
    public OptionMode NuzFirstEncounter { get; init; } = OptionMode.AlwaysOn;

    /// <summary>Pokémon that faint are gone (released after the battle, unless the whole team fainted).</summary>
    public OptionMode NuzFaintedGone { get; init; } = OptionMode.AlwaysOn;

    /// <summary>Duplicates clause: a Pokémon whose evolution family was already caught doesn't use up the area.</summary>
    public OptionMode NuzDupes { get; init; } = OptionMode.PlayerChooses;

    /// <summary>Eevee exception to the duplicates clause: Eevee never counts as a duplicate (one per evolution).</summary>
    public OptionMode NuzEeveeException { get; init; } = OptionMode.PlayerChooses;

    /// <summary>Shiny clause: shiny Pokémon can always be caught and don't use up the area.</summary>
    public OptionMode NuzShiny { get; init; } = OptionMode.PlayerChooses;

    /// <summary>Every caught Pokémon must be nicknamed (the naming screen opens straight away).</summary>
    public OptionMode NuzNicknames { get; init; } = OptionMode.PlayerChooses;

    /// <summary>The hard level cap is on while playing a Nuzlocke.</summary>
    public OptionMode NuzLevelCap { get; init; } = OptionMode.PlayerChooses;

    /// <summary>
    /// Static encounters (legendaries and other Pokémon met by walking up to them) count as the area's first encounter.
    /// Asked with a warning and a second confirmation, because a legendary can then be lost.
    /// </summary>
    public OptionMode NuzStatic { get; init; } = OptionMode.PlayerChooses;

    /// <summary>Adaptive boss slot: gym leaders, the rival and the Elite Four swap one Pokémon to counter the player's party.</summary>
    public OptionMode AdaptiveSlot { get; init; } = OptionMode.NotUsed;

    /// <summary>A helper in every Pokémon Center who changes natures and stats.</summary>
    public CenterHelperSettings CenterHelper { get; init; } = new();

    public bool NuzlockeUsed => Nuzlocke != OptionMode.NotUsed;

    public bool AnyUsed => Difficulty != OptionMode.NotUsed || TrainingOff != OptionMode.NotUsed || LevelCap != OptionMode.NotUsed
        || NoItemsInBattle != OptionMode.NotUsed || InfiniteCandy != OptionMode.NotUsed || NuzlockeUsed || AdaptiveSlot != OptionMode.NotUsed
        || CenterHelper.Enabled;

    /// <summary>The level cap code is needed (by the cap option itself or the Nuzlocke cap rule).</summary>
    public bool LevelCapCodeUsed => LevelCap != OptionMode.NotUsed || NuzRuleUsed(NuzLevelCap);

    /// <summary>A rule that only applies when the Nuzlocke is on, as the game sees it.</summary>
    public bool NuzRuleUsed(OptionMode rule) => NuzlockeUsed && rule is OptionMode.AlwaysOn or OptionMode.PlayerChooses;
}

/// <summary>The Pokémon Center helper's services and prices (0 = free).</summary>
public sealed record CenterHelperSettings
{
    public bool Enabled { get; init; }
    public bool Nature { get; init; } = true;
    public bool PerfectIvs { get; init; } = true;
    public bool EvSpreads { get; init; } = true;
    public int NaturePrice { get; init; }
    public int IvPrice { get; init; }
    public int EvPrice { get; init; }

    public bool AnyService => Enabled && (Nature || PerfectIvs || EvSpreads);
}
