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
        ];
        shell.DataChanged += (_, _) => Refresh();
    }

    internal GameData Data => _shell.Session.Data;
    internal GameOptionsSettings Settings => Data.GameOptions!;
    public GameOptionRowViewModel Difficulty { get; }
    public IReadOnlyList<GameOptionRowViewModel> Rows { get; }
    public IReadOnlyList<DifficultyChoiceOption> DifficultyChoices { get; }

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
            var asked = new[] { s.Difficulty, s.TrainingOff, s.LevelCap, s.NoItemsInBattle, s.InfiniteCandy }.Count(m => m == OptionMode.PlayerChooses);
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

            var fixedOnes = new List<string>();
            if (s.Difficulty == OptionMode.AlwaysOn) { fixedOnes.Add($"difficulty {Label(s.FixedDifficulty)}"); }
            if (s.TrainingOff == OptionMode.AlwaysOn) { fixedOnes.Add("EVs & IVs off"); }
            if (s.LevelCap == OptionMode.AlwaysOn) { fixedOnes.Add("hard level cap"); }
            if (s.LevelCap == OptionMode.AlwaysOff) { fixedOnes.Add("no level cap"); }
            if (s.NoItemsInBattle == OptionMode.AlwaysOn) { fixedOnes.Add(s.NoItemsAlsoWild ? "no items in any battle" : "no items in trainer battles"); }
            if (s.InfiniteCandy == OptionMode.AlwaysOn) { fixedOnes.Add("an Infinite Candy in the bag"); }
            if (fixedOnes.Count > 0)
            {
                lines.Add("Set without asking: " + string.Join(", ", fixedOnes) + ".");
            }

            if (asked > 0)
            {
                lines.Add("“All set. Have fun!”");
            }

            return lines;
        }
    }

    public string WhereText => "The menu appears once, the first moment the player can move in a new game. Old saves aren't asked. "
        + "Saving writes a small script and a few marked lines of code into the project; test it with Build & test.";

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
        foreach (var row in Rows)
        {
            row.Refresh();
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
