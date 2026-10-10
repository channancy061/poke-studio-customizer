using CommunityToolkit.Mvvm.ComponentModel;
using PokeStudio.Core.Changes;
using PokeStudio.Core.GameOptions;
using PokeStudio.Core.Modules;

namespace PokeStudio.App.ViewModels;

public sealed record ModeOption(string Label, OptionMode Mode);

public sealed record DifficultyChoiceOption(string Label, DifficultyChoice Choice);

/// <summary>One new-game option: how the creator wants it handled (not added, asked, always on/off).</summary>
public sealed partial class GameOptionRowViewModel : ViewModelBase
{
    private readonly GameOptionsPageViewModel _page;
    private readonly Func<GameOptionsSettings, OptionMode> _get;
    private readonly Func<GameOptionsSettings, OptionMode, GameOptionsSettings> _set;

    public GameOptionRowViewModel(GameOptionsPageViewModel page, string title, string explanation, IReadOnlyList<ModeOption> modes,
        Func<GameOptionsSettings, OptionMode> get, Func<GameOptionsSettings, OptionMode, GameOptionsSettings> set)
    {
        _page = page;
        Title = title;
        Explanation = explanation;
        Modes = modes;
        _get = get;
        _set = set;
    }

    public string Title { get; }
    public string Explanation { get; }
    public IReadOnlyList<ModeOption> Modes { get; }

    public ModeOption? Mode
    {
        get => Modes.FirstOrDefault(m => m.Mode == _get(_page.Settings)) ?? Modes[0];
        set
        {
            if (value is not null)
            {
                _page.Update(s => _set(s, value.Mode), $"{Title}: {value.Label}");
            }
        }
    }

    public void Refresh() => OnPropertyChanged(nameof(Mode));
}

/// <summary>The new-game options menu: what the player is asked when a new game starts, and what is fixed by the creator.</summary>
public sealed partial class GameOptionsPageViewModel : ViewModelBase
{
    private static readonly ModeOption NotUsed = new("Not added", OptionMode.NotUsed);
    private static readonly ModeOption Asked = new("Player chooses", OptionMode.PlayerChooses);
    private readonly ShellViewModel _shell;

    public GameOptionsPageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Data.GameOptions ??= new GameOptionsSettings();
        DifficultyChoices = Enum.GetValues<DifficultyChoice>().Select(d => new DifficultyChoiceOption(Label(d), d)).ToList();
        Difficulty = new GameOptionRowViewModel(this, "Difficulty",
            "Which version of the important trainers' teams the game uses (make the versions in Champion Lab → Difficulty versions).",
            [NotUsed, Asked, new("Always the same", OptionMode.AlwaysOn)], s => s.Difficulty, (s, m) => s with { Difficulty = m });
        Rows =
        [
            Difficulty,
            new(this, "Switch off EVs & IVs", "On: every Pokémon (the player's and the trainers') has perfect IVs and no EVs, so nobody needs to train stats.",
                [NotUsed, Asked, new("Always switched off", OptionMode.AlwaysOn)], s => s.TrainingOff, (s, m) => s with { TrainingOff = m }),
            new(this, "Hard level cap", "On: Pokémon get no experience above the next important battle's level cap (set the caps on Levels & caps). Rare Candies stop at the cap too.",
                [NotUsed, Asked, new("Always on", OptionMode.AlwaysOn), new("Always off", OptionMode.AlwaysOff)], s => s.LevelCap, (s, m) => s with { LevelCap = m }),
            new(this, "No items in battle", "On: the bag can't be used in trainer battles (or in every battle, see below).",
                [NotUsed, Asked, new("Always on", OptionMode.AlwaysOn)], s => s.NoItemsInBattle, (s, m) => s with { NoItemsInBattle = m }),
            new(this, "Infinite Candy", "A Rare Candy that is never used up, so nobody has to grind. It still stops at the level cap.",
                [NotUsed, Asked, new("Always given", OptionMode.AlwaysOn)], s => s.InfiniteCandy, (s, m) => s with { InfiniteCandy = m }),
            new(this, "Adaptive boss slot", "Gym leaders, the rival and the Elite Four (never the Champion) swap one Pokémon at the start of the battle for the candidate that best counters the player's team. Give bosses their candidates in Champion Lab or the trainer editor.",
                [NotUsed, Asked, new("Always on", OptionMode.AlwaysOn)], s => s.AdaptiveSlot, (s, m) => s with { AdaptiveSlot = m }),
        ];
        Nuzlocke = new GameOptionRowViewModel(this, "Nuzlocke", "A challenge run with the rules below. \"Player chooses\" asks \"Play a Nuzlocke?\" first; each rule then has its own setting.",
            [NotUsed, Asked, new("Always on", OptionMode.AlwaysOn)], s => s.Nuzlocke, (s, m) => s with { Nuzlocke = m });
        IReadOnlyList<ModeOption> rule = [new("Off", OptionMode.AlwaysOff), Asked, new("Always on", OptionMode.AlwaysOn)];
        NuzlockeRules =
        [
            new(this, "First encounter only", "Only the first wild Pokémon met in each area can be caught. In a wild double battle, either of the two (balls can only be thrown once one is left).", rule, s => s.NuzFirstEncounter, (s, m) => s with { NuzFirstEncounter = m }),
            new(this, "Fainted Pokémon are gone", "No revives: revive items and Revival Blessing fail. Pokémon that faint are released after the battle (unless the whole team fainted - then the run is over and the player decides).", rule, s => s.NuzFaintedGone, (s, m) => s with { NuzFaintedGone = m }),
            new(this, "Duplicates clause", "A Pokémon whose evolution family was already caught doesn't use up the area, so the player gets another try.", rule, s => s.NuzDupes, (s, m) => s with { NuzDupes = m }),
            new(this, "Eevee clause", "With the duplicates clause: Eevee never counts as a duplicate, so the player can catch one for each evolution.", rule, s => s.NuzEeveeException, (s, m) => s with { NuzEeveeException = m }),
            new(this, "Shiny clause", "Shiny Pokémon can always be caught and don't use up the area.", rule, s => s.NuzShiny, (s, m) => s with { NuzShiny = m }),
            new(this, "Nicknames required", "The naming screen opens straight away for every Pokémon caught, and opens again if the player keeps the species name.", rule, s => s.NuzNicknames, (s, m) => s with { NuzNicknames = m }),
            new(this, "Respect the level cap", "The hard level cap is on during the Nuzlocke (set the caps on Levels & caps).", rule, s => s.NuzLevelCap, (s, m) => s with { NuzLevelCap = m }),
        ];
        shell.DataChanged += (_, _) => Refresh();
    }

    internal GameData Data => _shell.Session.Data;
    internal GameOptionsSettings Settings => Data.GameOptions!;
    public GameOptionRowViewModel Difficulty { get; }
    public IReadOnlyList<GameOptionRowViewModel> Rows { get; }
    public GameOptionRowViewModel Nuzlocke { get; }
    public IReadOnlyList<GameOptionRowViewModel> NuzlockeRules { get; }
    public bool NuzlockeShown => Settings.NuzlockeUsed;

    // ---- Pokémon Center helper ----
    private CenterHelperSettings Helper => Settings.CenterHelper;

    private void UpdateHelper(Func<CenterHelperSettings, CenterHelperSettings> change, string description) =>
        Update(s => s with { CenterHelper = change(s.CenterHelper) }, description);

    public bool HelperEnabled
    {
        get => Helper.Enabled;
        set => UpdateHelper(h => h with { Enabled = value }, value ? "Pokémon Center helper added" : "Pokémon Center helper removed");
    }

    public bool HelperNature
    {
        get => Helper.Nature;
        set => UpdateHelper(h => h with { Nature = value }, value ? "Helper changes natures" : "Helper doesn't change natures");
    }

    public bool HelperIvs
    {
        get => Helper.PerfectIvs;
        set => UpdateHelper(h => h with { PerfectIvs = value }, value ? "Helper gives perfect IVs" : "Helper doesn't give perfect IVs");
    }

    public bool HelperEvs
    {
        get => Helper.EvSpreads;
        set => UpdateHelper(h => h with { EvSpreads = value }, value ? "Helper sets EVs" : "Helper doesn't set EVs");
    }

    public decimal? NaturePrice
    {
        get => Helper.NaturePrice;
        set => UpdateHelper(h => h with { NaturePrice = Price(value) }, $"Nature change costs {Price(value)}");
    }

    public decimal? IvPrice
    {
        get => Helper.IvPrice;
        set => UpdateHelper(h => h with { IvPrice = Price(value) }, $"Perfect IVs cost {Price(value)}");
    }

    public decimal? EvPrice
    {
        get => Helper.EvPrice;
        set => UpdateHelper(h => h with { EvPrice = Price(value) }, $"EV spread costs {Price(value)}");
    }

    private static int Price(decimal? value) => (int)Math.Clamp(value ?? 0, 0, 999999);
    public IReadOnlyList<DifficultyChoiceOption> DifficultyChoices { get; }

    private static string Cost(int price) => price > 0 ? $" (¥{price})" : " (free)";

    public static string Label(DifficultyChoice d) => d == DifficultyChoice.ExtraHard ? "Extra Hard" : d.ToString();

    public bool AsksDifficulty => Settings.Difficulty == OptionMode.PlayerChooses;
    public bool FixedDifficultyShown => Settings.Difficulty == OptionMode.AlwaysOn;
    public bool NoItemsShown => Settings.NoItemsInBattle != OptionMode.NotUsed;

    public DifficultyChoiceOption DefaultDifficulty
    {
        get => DifficultyChoices[(int)Settings.DefaultDifficulty];
        set => Update(s => s with { DefaultDifficulty = value.Choice }, $"Difficulty menu starts on {value.Label}");
    }

    public DifficultyChoiceOption FixedDifficulty
    {
        get => DifficultyChoices[(int)Settings.FixedDifficulty];
        set => Update(s => s with { FixedDifficulty = value.Choice }, $"Difficulty is always {value.Label}");
    }

    public bool OfferExtraHard
    {
        get => Settings.OfferExtraHard;
        set => Update(s => s with { OfferExtraHard = value }, value ? "Difficulty menu offers Extra Hard" : "Difficulty menu without Extra Hard");
    }

    public bool NoItemsAlsoWild
    {
        get => Settings.NoItemsAlsoWild;
        set => Update(s => s with { NoItemsAlsoWild = value }, value ? "No items in any battle" : "No items in trainer battles only");
    }

    /// <summary>What happens when a new game starts, in order (also the base for the Player's Guide).</summary>
    public IReadOnlyList<string> Preview
    {
        get
        {
            var s = Settings;
            var lines = new List<string>();
            var asked = new[] { s.Difficulty, s.TrainingOff, s.LevelCap, s.NoItemsInBattle, s.InfiniteCandy, s.AdaptiveSlot, s.Nuzlocke }.Count(m => m == OptionMode.PlayerChooses);
            if (!s.AnyUsed)
            {
                return ["Nothing is added: the game starts as usual."];
            }

            if (asked > 0)
            {
                lines.Add("“Before you begin, choose how you want to play.”");
            }

            if (s.Difficulty == OptionMode.PlayerChooses)
            {
                var choices = DifficultyChoices.Where(d => d.Choice != DifficultyChoice.ExtraHard || s.OfferExtraHard).Select(d => d.Label);
                lines.Add($"Which difficulty? {string.Join(" / ", choices)} (starts on {Label(s.DefaultDifficulty)})");
            }

            if (s.TrainingOff == OptionMode.PlayerChooses) { lines.Add("Switch off EVs and IVs? Yes / No"); }
            if (s.LevelCap == OptionMode.PlayerChooses) { lines.Add("Use a hard level cap? Yes / No"); }
            if (s.NoItemsInBattle == OptionMode.PlayerChooses) { lines.Add((s.NoItemsAlsoWild ? "No items in any battle?" : "No items in trainer battles?") + " Yes / No"); }
            if (s.InfiniteCandy == OptionMode.PlayerChooses) { lines.Add("Take an Infinite Candy? Yes / No"); }
            if (s.AdaptiveSlot == OptionMode.PlayerChooses) { lines.Add("Bosses adapt one Pokémon to your team? Yes / No"); }
            if (s.Nuzlocke is OptionMode.PlayerChooses or OptionMode.AlwaysOn)
            {
                var asks = NuzlockeRules.Where(r => r.Mode?.Mode == OptionMode.PlayerChooses).Select(r => r.Title).ToList();
                var always = NuzlockeRules.Where(r => r.Mode?.Mode == OptionMode.AlwaysOn).Select(r => r.Title.ToLowerInvariant()).ToList();
                lines.Add((s.Nuzlocke == OptionMode.PlayerChooses ? "Play a Nuzlocke? Yes / No. If yes: " : "Nuzlocke: ")
                    + (always.Count > 0 ? "always " + string.Join(", ", always) + ". " : "")
                    + (asks.Count > 0 ? "Asked: " + string.Join(", ", asks) + "." : ""));
            }

            var fixedOnes = new List<string>();
            if (s.Difficulty == OptionMode.AlwaysOn) { fixedOnes.Add($"difficulty {Label(s.FixedDifficulty)}"); }
            if (s.TrainingOff == OptionMode.AlwaysOn) { fixedOnes.Add("EVs & IVs off"); }
            if (s.LevelCap == OptionMode.AlwaysOn) { fixedOnes.Add("hard level cap"); }
            if (s.LevelCap == OptionMode.AlwaysOff) { fixedOnes.Add("no level cap"); }
            if (s.NoItemsInBattle == OptionMode.AlwaysOn) { fixedOnes.Add(s.NoItemsAlsoWild ? "no items in any battle" : "no items in trainer battles"); }
            if (s.InfiniteCandy == OptionMode.AlwaysOn) { fixedOnes.Add("an Infinite Candy in the bag"); }
            if (s.AdaptiveSlot == OptionMode.AlwaysOn) { fixedOnes.Add("bosses adapt one Pokémon"); }
            if (fixedOnes.Count > 0)
            {
                lines.Add("Set without asking: " + string.Join(", ", fixedOnes) + ".");
            }

            if (asked > 0)
            {
                lines.Add("“All set. Have fun!”");
            }

            if (s.CenterHelper.AnyService)
            {
                lines.Add("In every Pokémon Center, a helper offers: " + string.Join(", ", new[]
                {
                    s.CenterHelper.Nature ? $"change nature{Cost(s.CenterHelper.NaturePrice)}" : null,
                    s.CenterHelper.PerfectIvs ? $"perfect IVs{Cost(s.CenterHelper.IvPrice)}" : null,
                    s.CenterHelper.EvSpreads ? $"EV spreads{Cost(s.CenterHelper.EvPrice)}" : null,
                }.Where(x => x is not null)) + ".");
            }

            return lines;
        }
    }

    public string WhereText => "The menu appears once, the first moment the player can move in a new game. Old saves aren't asked. "
        + "Saving writes a small script, src/poke_studio.c and a few marked lines of code into the project; test it with Build & test.";

    internal void Update(Func<GameOptionsSettings, GameOptionsSettings> change, string description)
    {
        var before = Settings;
        var after = change(before);
        if (after == before)
        {
            return;
        }

        var data = Data;
        _shell.Execute(new ValueChange<GameOptionsSettings?>("Game options", description, v => data.GameOptions = v, before, after));
    }

    private void Refresh()
    {
        Data.GameOptions ??= new GameOptionsSettings();
        foreach (var row in Rows.Concat(NuzlockeRules).Append(Nuzlocke))
        {
            row.Refresh();
        }

        foreach (var name in new[] { nameof(NuzlockeShown), nameof(HelperEnabled), nameof(HelperNature), nameof(HelperIvs), nameof(HelperEvs), nameof(NaturePrice), nameof(IvPrice), nameof(EvPrice) })
        {
            OnPropertyChanged(name);
        }

        OnPropertyChanged(nameof(AsksDifficulty));
        OnPropertyChanged(nameof(FixedDifficultyShown));
        OnPropertyChanged(nameof(NoItemsShown));
        OnPropertyChanged(nameof(DefaultDifficulty));
        OnPropertyChanged(nameof(FixedDifficulty));
        OnPropertyChanged(nameof(OfferExtraHard));
        OnPropertyChanged(nameof(NoItemsAlsoWild));
        OnPropertyChanged(nameof(Preview));
    }
}
