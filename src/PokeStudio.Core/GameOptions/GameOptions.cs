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

    /// <summary>
    /// The difficulty also changes how trainers behave: on Easy only major battles stop the player (other trainers
    /// battle only when talked to); on Hard and Extra Hard every trainer sees far in every direction, and gym leaders
    /// wait until every trainer in their gym is beaten.
    /// </summary>
    public bool TrainerRulesByDifficulty { get; init; }

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

    /// <summary>A second person in every Poké Mart selling held items, special Poké Balls and TMs, more with each badge.</summary>
    public bool MartSpecialist { get; init; }

    /// <summary>Hard and up: route trainers move onto chokepoints so they can't be walked past (with the difficulty rules).</summary>
    public bool HardChokepoints { get; init; } = true;

    /// <summary>
    /// Fix the battle AI's switch-in memory: a Pokémon coming back in kept what the AI knew about the one before it (a
    /// revealed Levitate made it skip Earthquake).
    /// </summary>
    public bool FixAiSwitchInMemory { get; init; } = true;

    /// <summary>FireRed's shops sell an important item (a reusable TM) only once, like Emerald's.</summary>
    public bool OneOfEachImportantItem { get; init; } = true;

    /// <summary>
    /// FireRed's naming screen says a caught Pokémon went to the PC (party full) before the battle asks whether to add
    /// it to the party; with that question the naming screen stays quiet.
    /// </summary>
    public bool QuietNamingWhenPartyFull { get; init; } = true;

    /// <summary>
    /// pokeemerald-expansion's debug menus (overworld B/R + Start, Select in battle, Select on the summary screen). Off: a
    /// build for players can't open them (the expansion only switches them off for "make release").
    /// </summary>
    public bool DebugMenus { get; init; }

    /// <summary>
    /// How many different items the bag's Items pocket holds (0 = the game's own: FireRed 42, Emerald 30; at most 150, as
    /// many as fit). The bigger pocket lives in save block 3, and an older save's items move into it on load.
    /// </summary>
    public int BagItemsSlots { get; init; }

    /// <summary>How many different items the PC's item storage holds (0 = the game's own: FireRed 30, Emerald 50).</summary>
    public int PcItemsSlots { get; init; }

    public bool NuzlockeUsed => Nuzlocke != OptionMode.NotUsed;

    public bool AnyUsed => Difficulty != OptionMode.NotUsed || TrainingOff != OptionMode.NotUsed || LevelCap != OptionMode.NotUsed
        || NoItemsInBattle != OptionMode.NotUsed || InfiniteCandy != OptionMode.NotUsed || NuzlockeUsed || AdaptiveSlot != OptionMode.NotUsed
        || CenterHelper.Enabled || MartSpecialist || BagItemsSlots > 0 || PcItemsSlots > 0;

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

    /// <summary>Teaching forgotten moves again for free (the game's own move relearner).</summary>
    public bool MoveRelearner { get; init; } = true;

    /// <summary>Forgetting a move, HMs too, for free (the game's own Move Deleter).</summary>
    public bool MoveDeleter { get; init; } = true;

    /// <summary>
    /// Changing a Pokémon's ability to another one its species has (normal ones, and the hidden one once
    /// <see cref="HiddenAbilityBadge"/> is won): not used, the player chooses at a new game, or always there.
    /// </summary>
    public OptionMode Ability { get; init; } = OptionMode.NotUsed;
    public int AbilityPrice { get; init; } = 5000;
    public int HiddenAbilityPrice { get; init; } = 20000;

    /// <summary>The badge (1–8) that opens hidden abilities; 0 = from the start.</summary>
    public int HiddenAbilityBadge { get; init; } = 6;

    public bool AbilityService => Ability is OptionMode.PlayerChooses or OptionMode.AlwaysOn;

    public bool AnyService => Enabled && (Nature || PerfectIvs || EvSpreads || MoveRelearner || MoveDeleter || AbilityService);
}
