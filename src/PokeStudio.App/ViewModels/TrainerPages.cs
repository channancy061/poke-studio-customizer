using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Trainers;

namespace PokeStudio.App.ViewModels;

public sealed record TrainerListItem(string Key, string Title, string Subtitle, bool IsLocked)
{
    public double Opacity => IsLocked ? 0.55 : 1;
}

public sealed record ScopeOption(string Label, string Id);

/// <summary>Trainer list (search + class filter), the editor, and the "change levels" tool.</summary>
public sealed partial class TrainersPageViewModel : ViewModelBase
{
    private static readonly CatalogEntry AllClasses = new("", "All classes");
    private readonly ShellViewModel _shell;

    public TrainersPageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        var used = Data.Trainers.Select(t => t.TrainerClass).ToHashSet(StringComparer.Ordinal);
        ClassFilters = [AllClasses, .. Data.Catalog.TrainerClasses.Where(c => used.Contains(c.Key))];
        _classFilter = AllClasses;
        Filter();
        SelectedItem = Items.FirstOrDefault(i => i.Key == shell.SelectedTrainerKey) ?? Items.FirstOrDefault(i => i.Key != "TRAINER_NONE") ?? Items.FirstOrDefault();
        shell.DataChanged += (_, _) =>
        {
            Editor?.Refresh();
            RefreshRow();
        };
    }

    private GameData Data => _shell.Session.Data;
    public ObservableCollection<TrainerListItem> Items { get; } = [];
    public IReadOnlyList<CatalogEntry> ClassFilters { get; }
    public string CountText => Items.Count == Data.Trainers.Count ? $"{Data.Trainers.Count:N0}" : $"{Items.Count:N0} of {Data.Trainers.Count:N0}";

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    private CatalogEntry _classFilter;

    public CatalogEntry ClassFilter
    {
        get => _classFilter;
        set
        {
            if (SetProperty(ref _classFilter, value ?? AllClasses))
            {
                Filter();
            }
        }
    }

    [ObservableProperty]
    public partial TrainerListItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial TrainerEditorViewModel? Editor { get; set; }

    partial void OnSearchChanged(string value) => Filter();

    private bool _updatingRow;

    partial void OnSelectedItemChanged(TrainerListItem? value)
    {
        if (value is null || _updatingRow)
        {
            return; // a refreshed row for the same trainer keeps its editor (and the text box being typed in)
        }

        _shell.SelectedTrainerKey = value.Key;
        var index = Data.Trainers.FindIndex(t => t.Key == value.Key);
        Editor = index < 0 ? null : new TrainerEditorViewModel(_shell, index);
        OnPropertyChanged(nameof(ScopeOptions));
    }

    internal TrainerListItem ToItem(Trainer t)
    {
        var c = Data.Catalog;
        var levels = t.Party.Count == 0 ? "no team" : t.Party.Min(p => p.Level) == t.Party.Max(p => p.Level)
            ? $"Lv {t.Party[0].Level}" : $"Lv {t.Party.Min(p => p.Level)}–{t.Party.Max(p => p.Level)}";
        var title = t.Name.Length == 0 ? t.Key : $"{c.NameOf(t.TrainerClass)} {Title(t.Name)}";
        return new TrainerListItem(t.Key, title, $"{t.Party.Count} Pokémon · {levels}{(t.Battle == BattleType.Double ? " · Double" : "")}", t.LockedReason is not null);
    }

    /// <summary>"ROXANNE" → "Roxanne" for the list; the game shows names as written.</summary>
    private static string Title(string name) =>
        name.Length > 1 && name.All(ch => !char.IsLetter(ch) || char.IsUpper(ch)) ? name[0] + name[1..].ToLowerInvariant() : name;

    private void Filter()
    {
        var q = Search.Trim();
        var selected = SelectedItem?.Key;
        Items.Clear();
        foreach (var t in Data.Trainers)
        {
            if (ClassFilter.Key.Length > 0 && t.TrainerClass != ClassFilter.Key)
            {
                continue;
            }

            var item = ToItem(t);
            if (q.Length > 0 && !item.Title.Contains(q, StringComparison.OrdinalIgnoreCase) && !t.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !t.Party.Any(p => Data.Catalog.NameOf(p.Species).Contains(q, StringComparison.OrdinalIgnoreCase)
                    || (Data.Species.FirstOrDefault(s => s.Key == p.Species)?.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)))
            {
                continue;
            }

            Items.Add(item);
        }

        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ScopeOptions));
        if (selected is not null && Items.FirstOrDefault(i => i.Key == selected) is { } again)
        {
            SelectedItem = again;
        }
    }

    private void RefreshRow()
    {
        if (SelectedItem is not { } item || Data.Trainers.FirstOrDefault(t => t.Key == item.Key) is not { } t)
        {
            return;
        }

        var fresh = ToItem(t);
        var i = Items.IndexOf(item);
        if (fresh != item && i >= 0)
        {
            _updatingRow = true;
            try
            {
                Items[i] = fresh;
                SelectedItem = fresh;
            }
            finally
            {
                _updatingRow = false;
            }
        }
    }

    // ---- change levels ----
    public IReadOnlyList<ScopeOption> ScopeOptions =>
    [
        new("This trainer", "one"),
        new($"Trainers in the list ({Items.Count})", "list"),
        new($"Every trainer ({Data.Trainers.Count})", "all"),
    ];

    public IReadOnlyList<string> LevelModes { get; } = ["Add levels", "Percent of current"];

    [ObservableProperty]
    public partial bool IsLevelToolOpen { get; set; }

    [ObservableProperty]
    public partial int ScopeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AmountMin), nameof(AmountMax), nameof(LevelPreview))]
    public partial int LevelModeIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelPreview))]
    public partial decimal Amount { get; set; } = 5;

    public decimal AmountMin => LevelModeIndex == 0 ? -99 : 10;
    public decimal AmountMax => LevelModeIndex == 0 ? 99 : 300;
    private LevelChangeMode Mode => LevelModeIndex == 0 ? LevelChangeMode.AddLevels : LevelChangeMode.Percent;

    partial void OnLevelModeIndexChanged(int value) => Amount = value == 0 ? 5 : 120;

    public string LevelPreview
    {
        get
        {
            var max = _shell.Session.Module.Limits.MaxLevel;
            return string.Join(" · ", new[] { 5, 20, 50 }.Select(l => $"Lv {l} → {TrainerTools.NewLevel(l, Mode, (int)Amount, max)}"));
        }
    }

    [RelayCommand]
    private void ToggleLevelTool() => IsLevelToolOpen = !IsLevelToolOpen;

    [RelayCommand]
    private void ApplyLevels()
    {
        IEnumerable<string> keys = ScopeIndex switch
        {
            0 => SelectedItem is { } s ? [s.Key] : [],
            1 => Items.Select(i => i.Key).ToList(),
            _ => Data.Trainers.Select(t => t.Key).ToList(),
        };
        var change = TrainerTools.ChangeLevels(Data, keys.Where(k => k != "TRAINER_NONE"), Mode, (int)Amount, _shell.Session.Module.Limits);
        if (change is null)
        {
            _shell.Toast = "No levels changed.";
            return;
        }

        _shell.Execute(change);
        IsLevelToolOpen = false;
        Filter();
        _shell.Toast = change.Description + ". One Undo puts it back.";
    }
}

public sealed record GenderChoice(string Label, string Value);

/// <summary>One trainer: details, items, enemy AI and team.</summary>
public sealed partial class TrainerEditorViewModel : ViewModelBase
{
    private static readonly CatalogEntry NoneEntry = new("", "None");
    private readonly ShellViewModel _shell;
    private readonly int _index;

    public TrainerEditorViewModel(ShellViewModel shell, int index)
    {
        _shell = shell;
        _index = index;
        var c = Catalog;
        ClassOptions = c.TrainerClasses;
        PicOptions = c.TrainerPics;
        MusicOptions = c.TrainerMusic;
        ItemOptions = c.Items.Where(i => i.Key != "ITEM_NONE").ToList();
        SpeciesOptions = shell.Session.Data.Species.Where(s => s.IsEnabled).Select(s => new CatalogEntry(s.Key, s.DisplayName)).ToList();
        Presets = AiPresets.For(c);
        _nameDraft = Model.Name;
        Rebuild();
    }

    private GameCatalog Catalog => _shell.Session.Data.Catalog;
    private GameLimits Limits => _shell.Session.Module.Limits;
    internal Trainer Model => _shell.Session.Data.Trainers[_index];

    public IReadOnlyList<CatalogEntry> ClassOptions { get; }
    public IReadOnlyList<CatalogEntry> PicOptions { get; }
    public IReadOnlyList<CatalogEntry> MusicOptions { get; }
    public IReadOnlyList<CatalogEntry> ItemOptions { get; }
    public IReadOnlyList<CatalogEntry> SpeciesOptions { get; }
    public IReadOnlyList<AiPreset> Presets { get; }
    public IReadOnlyList<GenderChoice> GenderOptions { get; } = [new("Male", "Male"), new("Female", "Female")];

    public ObservableCollection<TrainerMonViewModel> Party { get; } = [];
    public ObservableCollection<MoveChipViewModel> Items { get; } = [];
    public ObservableCollection<AiFlagRowViewModel> AiFlags { get; } = [];

    public string Title => Model.Name.Length == 0 ? Model.Key : $"{Catalog.NameOf(Model.TrainerClass)} {Model.Name}";
    public string Subtitle => Model.Key;
    public bool IsLocked => Model.LockedReason is not null;
    public bool IsEditable => !IsLocked;
    public string? LockedReason => Model.LockedReason;
    public bool CanAddPokemon => IsEditable && Model.Party.Count < Limits.MaxPartySize;
    public bool CanAddItem => IsEditable && Model.Items.Count < Limits.MaxTrainerItems;
    public string PartyHeader => $"Team ({Model.Party.Count} / {Limits.MaxPartySize})";
    public bool AdvancedMode => _shell.AdvancedMode;

    // ---- edits ----
    internal void Edit(string field, Func<Trainer, Trainer> update, string description, bool merge = false)
    {
        var before = Model;
        var after = update(before);
        if (after == before)
        {
            return;
        }

        var data = _shell.Session.Data;
        var index = _index;
        var name = before.Name.Length == 0 ? before.Key : before.Name;
        _shell.Execute(new ValueChange<Trainer>("Trainers", $"{name} · {description}", v => data.Trainers[index] = v, before, after,
            merge ? $"trainer:{before.Key}:{field}" : null, (_, _) => $"{name} · {description}"));
    }

    private string _nameDraft;

    public string NameDraft
    {
        get => _nameDraft;
        set
        {
            _nameDraft = value;
            var t = value.Trim();
            NameError = t.Length > Limits.TrainerNameLength ? $"{t.Length} / {Limits.TrainerNameLength} — {t.Length - Limits.TrainerNameLength} letters too long for this game." : null;
            if (NameError is null)
            {
                Edit("name", x => x with { Name = t }, $"renamed to {t}", merge: true);
            }
        }
    }

    [ObservableProperty]
    public partial string? NameError { get; set; }

    private CatalogEntry? Find(IReadOnlyList<CatalogEntry> list, string key) =>
        list.FirstOrDefault(e => e.Key == key) ?? (key.Length == 0 ? null : new CatalogEntry(key, Catalog.NameOf(key)));

    public CatalogEntry? TrainerClass
    {
        get => Find(ClassOptions, Model.TrainerClass);
        set
        {
            if (value is not null)
            {
                Edit("class", x => x with { TrainerClass = value.Key }, $"class → {value.Name}");
            }
        }
    }

    public CatalogEntry? Pic
    {
        get => Find(PicOptions, Model.Pic);
        set
        {
            if (value is not null)
            {
                Edit("pic", x => x with { Pic = value.Key }, $"picture → {value.Name}");
            }
        }
    }

    public CatalogEntry? Music
    {
        get => Find(MusicOptions, Model.Music);
        set
        {
            if (value is not null)
            {
                Edit("music", x => x with { Music = value.Key }, $"music → {value.Name}");
            }
        }
    }

    public GenderChoice? Gender
    {
        get => GenderOptions.FirstOrDefault(g => g.Value == Model.Gender);
        set
        {
            if (value is not null)
            {
                Edit("gender", x => x with { Gender = value.Value }, $"gender → {value.Label}");
            }
        }
    }

    public bool IsDouble
    {
        get => Model.Battle == BattleType.Double;
        set => Edit("battle", x => x with { Battle = value ? BattleType.Double : BattleType.Single }, value ? "now a double battle" : "now a single battle");
    }

    [ObservableProperty]
    public partial CatalogEntry? NewItem { get; set; }

    [RelayCommand]
    private void AddItem()
    {
        if (NewItem is { } item && CanAddItem)
        {
            Edit("items", x => x with { Items = [.. x.Items, item.Key] }, $"carries {item.Name}");
            NewItem = null;
        }
    }

    private void RemoveItem(int index)
    {
        var name = Catalog.NameOf(Model.Items[index]);
        Edit("items", x => x with { Items = x.Items.Where((_, i) => i != index).ToList() }, $"no longer carries {name}");
    }

    // ---- AI ----
    private static readonly AiPreset CustomPreset = new("custom", "Custom", "A mix of individual behaviours, chosen below.", []);

    public IReadOnlyList<AiPreset> PresetOptions => [.. Presets, CustomPreset];

    public AiPreset? Preset
    {
        get => AiPresets.Match(Model.AiFlags, Presets) ?? CustomPreset;
        set
        {
            if (value is not null && value != CustomPreset)
            {
                Edit("ai", x => x with { AiFlags = value.Flags.ToList() }, $"AI set to {value.Name}");
            }
            else if (value == CustomPreset)
            {
                ShowAiFlags = true;
            }
        }
    }

    public string PresetDescription => Preset?.Description ?? "";

    [ObservableProperty]
    public partial bool ShowAiFlags { get; set; }

    internal void SetFlag(string flag, bool on)
    {
        if (Model.AiFlags.Contains(flag) == on)
        {
            return;
        }

        Edit("ai", x => x with { AiFlags = on ? [.. x.AiFlags, flag] : x.AiFlags.Where(f => f != flag).ToList() },
            on ? $"AI: {Catalog.NameOf(flag)} on" : $"AI: {Catalog.NameOf(flag)} off");
    }

    // ---- team ----
    [RelayCommand]
    private void AddPokemon()
    {
        if (!CanAddPokemon)
        {
            return;
        }

        var last = Model.Party.LastOrDefault();
        var species = last?.Species ?? SpeciesOptions.FirstOrDefault()?.Key ?? "SPECIES_NONE";
        var mon = new TrainerPokemon { Species = species, Level = last?.Level ?? 5, Ivs = last?.Ivs };
        Edit("party", x => x with { Party = [.. x.Party, mon] }, $"added {Catalog.NameOf(species)}");
    }

    internal void ReplaceMon(int slot, TrainerPokemon value, string description, string? mergeField = null) =>
        Edit(mergeField is null ? "party" : $"party:{slot}:{mergeField}", x =>
        {
            var list = x.Party.ToList();
            list[slot] = value;
            return x with { Party = list };
        }, description, merge: mergeField is not null);

    internal void RemoveMon(int slot)
    {
        var name = SpeciesName(Model.Party[slot].Species);
        Edit("party", x => x with { Party = x.Party.Where((_, i) => i != slot).ToList() }, $"removed {name}");
    }

    internal void MoveMon(int slot, int delta)
    {
        var target = slot + delta;
        if (target < 0 || target >= Model.Party.Count)
        {
            return;
        }

        var name = SpeciesName(Model.Party[slot].Species);
        Edit("party", x =>
        {
            var list = x.Party.ToList();
            (list[slot], list[target]) = (list[target], list[slot]);
            return x with { Party = list };
        }, $"moved {name} {(delta < 0 ? "up" : "down")}");
    }

    internal string SpeciesName(string key) => _shell.Session.Data.Species.FirstOrDefault(s => s.Key == key)?.DisplayName ?? Catalog.NameOf(key);

    internal Species? SpeciesOf(string key) => _shell.Session.Data.Species.FirstOrDefault(s => s.Key == key);

    internal GameCatalog CatalogForRows => Catalog;
    internal GameLimits LimitsForRows => Limits;

    // ---- suggest a team (Champion Lab, rule-based) ----
    public IReadOnlyList<DifficultyOption> SuggestDifficulties { get; } =
        Enum.GetValues<Core.ChampionLab.Difficulty>().Select(d => new DifficultyOption(Core.ChampionLab.Lab.Label(d), d)).ToList();

    [ObservableProperty]
    public partial DifficultyOption? SuggestDifficulty { get; set; }

    [ObservableProperty]
    public partial decimal SuggestSize { get; set; }

    [ObservableProperty]
    public partial bool IsSuggestOpen { get; set; }

    private int _variant;
    private Core.ChampionLab.TeamSuggestion? _suggestion;

    public ObservableCollection<SuggestionRowViewModel> Suggestions { get; } = [];
    public ObservableCollection<string> SuggestionNotes { get; } = [];
    public bool HasSuggestion => Suggestions.Count > 0;

    private Core.ChampionLab.LabSettings Lab => _shell.Session.Data.Lab ??= new Core.ChampionLab.LabSettings();

    [RelayCommand]
    private void ToggleSuggest()
    {
        IsSuggestOpen = !IsSuggestOpen;
        SuggestDifficulty ??= SuggestDifficulties[(int)Core.ChampionLab.Lab.DifficultyOf(Model)];
        if (SuggestSize == 0)
        {
            SuggestSize = Math.Max(1, Model.Party.Count);
        }
    }

    [RelayCommand]
    private Task SuggestAsync() => RunSuggestAsync(0);

    [RelayCommand]
    private Task TryAnotherAsync() => RunSuggestAsync(++_variant);

    private async Task RunSuggestAsync(int variant)
    {
        var d = SuggestDifficulty?.Difficulty ?? Core.ChampionLab.Difficulty.Normal;
        var (data, lab, model) = (_shell.Session.Data, Lab, Model);
        var options = new Core.ChampionLab.TeamBuilder.Options(d, (int)Math.Clamp(SuggestSize, 1, Limits.MaxPartySize), Variant: variant);
        _suggestion = await _shell.RunBusyAsync("Building a team…", () => Core.ChampionLab.TeamBuilder.Suggest(data, lab, model, options));
        Suggestions.Clear();
        foreach (var m in _suggestion.Members)
        {
            Suggestions.Add(new SuggestionRowViewModel(SpeciesName(m.Pokemon.Species), m.Pokemon.Level, m.Role, string.Join(" ", m.Reasons)));
        }

        SuggestionNotes.Clear();
        foreach (var n in _suggestion.Notes)
        {
            SuggestionNotes.Add(n);
        }

        OnPropertyChanged(nameof(HasSuggestion));
    }

    [RelayCommand]
    private void UseSuggestion()
    {
        if (_suggestion is not { Members.Count: > 0 } s)
        {
            return;
        }

        _shell.Execute(Core.ChampionLab.TeamBuilder.Apply(_shell.Session.Data, Model, s, $"suggested {SuggestDifficulty?.Label ?? "Normal"} team"));
        IsSuggestOpen = false;
        _shell.Toast = "Suggested team applied. One Undo puts the old team back.";
    }

    // ---- report card ----
    [ObservableProperty]
    public partial Core.ChampionLab.BossReport? Report { get; set; }

    public bool HasReport => Report is not null;
    public string ReportTitle => Report is { } r ? $"{r.Score}/10 — {r.Verdict}" : "";
    public string ReportContext => Report is { } r ? $"Against the {r.ThreatCount} Pokémon the player can have by now, at level {r.Cap}." : "";

    partial void OnReportChanged(Core.ChampionLab.BossReport? value)
    {
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(ReportTitle));
        OnPropertyChanged(nameof(ReportContext));
    }

    [RelayCommand]
    private async Task MakeReportAsync()
    {
        var (data, lab, model) = (_shell.Session.Data, Lab, Model);
        Report = await _shell.RunBusyAsync("Grading this team…", () => Core.ChampionLab.ReportCard.For(data, lab, model));
    }

    // ---- adaptive boss slot ----
    // only games that can get the new game code (decomp projects) can run an adaptive slot
    public bool AdaptiveAllowed => IsEditable && _shell.Session.Module.SupportsAdaptiveSlots(_shell.Session.Data) && Model.Party.Count >= Core.ChampionLab.AdaptiveSlots.MinTeam
        && Core.ChampionLab.AdaptiveSlots.Allowed(Core.ChampionLab.Lab.GroupOf(_shell.Session.Data, Lab, Model));

    public bool HasAdaptive => Model.HasAdaptiveSlot;
    public bool NoAdaptive => !HasAdaptive;

    public string AdaptiveSummary => Model.HasAdaptiveSlot
        ? $"Flexible: {SpeciesName(Model.Party[Model.FlexIndex!.Value].Species)}. Candidates: {string.Join(", ", Model.FlexCandidates.Select(c => SpeciesName(c.Species)))}."
            + (_shell.Session.Data.GameOptions?.AdaptiveSlot is null or Core.GameOptions.OptionMode.NotUsed ? " (Switch it on in Game options for it to work in the game.)" : "")
        : "This boss has no adaptive slot.";

    public IReadOnlyList<CatalogEntry> FlexOptions => Model.Party.Select((p, i) => new CatalogEntry(i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{i + 1}. {SpeciesName(p.Species)}")).ToList();

    public CatalogEntry? FlexMember
    {
        get => FlexOptions.ElementAtOrDefault(Model.FlexIndex ?? Core.ChampionLab.AdaptiveSlots.DefaultFlexIndex(Model));
        set
        {
            if (value is not null && Model.HasAdaptiveSlot && int.Parse(value.Key, System.Globalization.CultureInfo.InvariantCulture) is var i && i != Model.FlexIndex)
            {
                Edit("flex", x => x with { FlexIndex = i }, $"flexible member is now {SpeciesName(Model.Party[i].Species)}");
            }
        }
    }


    [RelayCommand]
    private async Task SuggestCandidatesAsync()
    {
        var flex = Model.FlexIndex ?? Core.ChampionLab.AdaptiveSlots.FlexIndexFor(_shell.Session.Data, Lab, Model);
        if (flex < 0)
        {
            _shell.Toast = "Every member of this team has to stay (a rival's starter always stays).";
            return;
        }

        var (data, lab, model) = (_shell.Session.Data, Lab, Model);
        var candidates = await _shell.RunBusyAsync("Choosing counter candidates…", () => Core.ChampionLab.AdaptiveSlots.SuggestCandidates(data, lab, model, flex));
        if (candidates.Count == 0)
        {
            _shell.Toast = "No candidates fit this boss's theme and rules.";
            return;
        }

        Edit("flex", x => x with { FlexIndex = flex, FlexCandidates = candidates }, "adaptive slot candidates suggested");
    }

    [RelayCommand]
    private void RemoveAdaptive() => Edit("flex", x => x with { FlexIndex = null, FlexCandidates = [] }, "adaptive slot removed");

    private void RefreshAdaptive()
    {
        foreach (var name in new[] { nameof(AdaptiveAllowed), nameof(HasAdaptive), nameof(NoAdaptive), nameof(AdaptiveSummary), nameof(FlexOptions), nameof(FlexMember) })
        {
            OnPropertyChanged(name);
        }
    }

    // ---- copy a team ----
    public IReadOnlyList<CatalogEntry> TrainerOptions =>
        _shell.Session.Data.Trainers.Where(t => t.Key != Model.Key && t.Party.Count > 0)
            .Select(t => new CatalogEntry(t.Key, t.Name.Length == 0 ? t.Key : $"{Catalog.NameOf(t.TrainerClass)} {t.Name} ({t.Key})")).ToList();

    [ObservableProperty]
    public partial CatalogEntry? CopyFrom { get; set; }

    [RelayCommand]
    private void CopyTeam()
    {
        if (CopyFrom is not { } from || _shell.Session.Data.Trainers.FirstOrDefault(t => t.Key == from.Key) is not { } source)
        {
            return;
        }

        Edit("party", x => x with { Party = source.Party.ToList() }, $"team copied from {source.Name}");
        CopyFrom = null;
    }

    private void Rebuild()
    {
        RefreshAdaptive();
        Party.Clear();
        for (var i = 0; i < Model.Party.Count; i++)
        {
            Party.Add(new TrainerMonViewModel(this, i));
        }

        Items.Clear();
        for (var i = 0; i < Model.Items.Count; i++)
        {
            var slot = i;
            Items.Add(new MoveChipViewModel(Model.Items[i], Catalog.NameOf(Model.Items[i]), _ => RemoveItem(slot)));
        }

        AiFlags.Clear();
        foreach (var f in Catalog.AiFlags.Where(f => f.Description?.StartsWith("Preset:", StringComparison.Ordinal) != true))
        {
            AiFlags.Add(new AiFlagRowViewModel(f, Model.AiFlags.Contains(f.Key), SetFlag));
        }

        // flags used by the trainer that are presets (e.g. Basic Trainer inside a custom mix)
        foreach (var f in Model.AiFlags.Where(k => AiFlags.All(r => r.Key != k)))
        {
            AiFlags.Insert(0, new AiFlagRowViewModel(Catalog.AiFlags.FirstOrDefault(x => x.Key == f) ?? new CatalogEntry(f, Catalog.NameOf(f)), true, SetFlag));
        }
    }

    public void Refresh()
    {
        if (_nameDraft.Trim() != Model.Name && NameError is null)
        {
            _nameDraft = Model.Name;
        }

        Rebuild();
        Report = null; // the team changed; make a fresh report when asked
        OnPropertyChanged(string.Empty);
    }
}

public sealed partial class AiSettingRowViewModel(EnemyAiPageViewModel page, AiSetting setting) : ViewModelBase
{
    private AiSetting setting = setting;

    public string Key => setting.Key;

    /// <summary>Shows the current value after an edit, undo or redo (without rebuilding the row mid-drag).</summary>
    internal void Sync(AiSetting current)
    {
        if (current != setting)
        {
            setting = current;
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(ValueText));
        }
    }

    public string Label => char.ToUpperInvariant(setting.Label[0]) + setting.Label[1..];
    public string? Comment => setting.Comment;
    public bool IsPercentage => setting.IsPercentage;
    public double Maximum => setting.IsPercentage ? 100 : 10;
    public string ValueText => setting.IsPercentage ? $"{setting.Value}%" : setting.Value.ToString(CultureInfo.InvariantCulture);

    public double Value
    {
        get => setting.Value;
        set => page.SetTuning(setting.Key, (int)Math.Round(value));
    }
}

public sealed record SuggestionRowViewModel(string Name, int Level, string Role, string Reasons);

public sealed partial class AiFlagRowViewModel(CatalogEntry flag, bool on, Action<string, bool> toggle) : ViewModelBase
{
    public string Key => flag.Key;
    public string Name => flag.Name;
    public string? Description => flag.Description;

    public bool IsOn
    {
        get => on;
        set => toggle(flag.Key, value);
    }
}

public sealed record IvChoice(string Label, BaseStats? Value);

/// <summary>One team member's card.</summary>
public sealed partial class TrainerMonViewModel : ViewModelBase
{
    private static readonly CatalogEntry Default = new("", "Game default");
    private readonly TrainerEditorViewModel _owner;

    public TrainerMonViewModel(TrainerEditorViewModel owner, int slot)
    {
        _owner = owner;
        Slot = slot;
        var c = owner.CatalogForRows;
        var species = owner.SpeciesOf(Mon.Species);
        AbilityOptions = [Default, .. (species?.Abilities ?? []).Where(a => a != "ABILITY_NONE").Distinct().Select(a => new CatalogEntry(a, c.NameOf(a)))];
        if (Mon.Ability is { } ab && AbilityOptions.All(o => o.Key != ab))
        {
            AbilityOptions = [.. AbilityOptions, new CatalogEntry(ab, c.NameOf(ab) + " (unusual)")];
        }

        NatureOptions = [Default, .. c.Natures];
        HeldOptions = [new("", "Nothing"), .. owner.ItemOptions];
        MoveOptions = [new("", "— learned automatically —"), .. c.Moves];
        IvOptions =
        [
            new("Default (31)", null),
            new("All 0 (weakest)", new BaseStats(0, 0, 0, 0, 0, 0)),
            new("All 31 (strongest)", new BaseStats(31, 31, 31, 31, 31, 31)),
        ];
        if (Mon.Ivs is { } iv && IvOptions.All(o => o.Value != iv))
        {
            IvOptions = [.. IvOptions, new IvChoice($"Custom ({TrainerIvs(iv)})", iv)];
        }
    }

    private static string TrainerIvs(BaseStats s) => $"{s.Hp}/{s.Attack}/{s.Defense}/{s.SpAttack}/{s.SpDefense}/{s.Speed}";

    public int Slot { get; }
    private TrainerPokemon Mon => _owner.Model.Party[Slot];
    public IReadOnlyList<CatalogEntry> SpeciesOptions => _owner.SpeciesOptions;
    public IReadOnlyList<CatalogEntry> AbilityOptions { get; }
    public IReadOnlyList<CatalogEntry> NatureOptions { get; }
    public IReadOnlyList<CatalogEntry> HeldOptions { get; }
    public IReadOnlyList<CatalogEntry> MoveOptions { get; }
    public IReadOnlyList<IvChoice> IvOptions { get; }
    public bool IsEditable => _owner.IsEditable;
    public string Title => (Mon.Nickname is { Length: > 0 } n ? $"{n} (" : "") + _owner.SpeciesName(Mon.Species) + (Mon.Nickname is { Length: > 0 } ? ")" : "");
    public string Header => $"{Slot + 1}. {Title}";
    public string AutoMovesNote => Mon.Moves.Count == 0 ? "No moves chosen: it uses the last four it learns by level-up at this level." : "";

    private void Set(Func<TrainerPokemon, TrainerPokemon> update, string description, string? mergeField = null)
    {
        var after = update(Mon);
        if (!after.Equivalent(Mon))
        {
            _owner.ReplaceMon(Slot, after, $"{_owner.SpeciesName(Mon.Species)} · {description}", mergeField);
        }
    }

    public CatalogEntry? Species
    {
        get => SpeciesOptions.FirstOrDefault(s => s.Key == Mon.Species) ?? new CatalogEntry(Mon.Species, _owner.SpeciesName(Mon.Species));
        set
        {
            if (value is not null && value.Key != Mon.Species)
            {
                // a different Pokémon keeps level and moves, but not a now-impossible ability
                Set(m => m with { Species = value.Key, Ability = null }, $"→ {value.Name}");
            }
        }
    }

    public decimal Level
    {
        get => Mon.Level;
        set => Set(m => m with { Level = (int)Math.Clamp(value, 1, _owner.LimitsForRows.MaxLevel) }, $"level {(int)value}", "level");
    }

    public CatalogEntry? Held
    {
        get => HeldOptions.FirstOrDefault(i => i.Key == (Mon.HeldItem ?? "")) ?? HeldOptions[0];
        set
        {
            if (value is not null)
            {
                Set(m => m with { HeldItem = value.Key.Length == 0 ? null : value.Key }, value.Key.Length == 0 ? "holds nothing" : $"holds {value.Name}");
            }
        }
    }

    public CatalogEntry? Ability
    {
        get => AbilityOptions.FirstOrDefault(a => a.Key == (Mon.Ability ?? "")) ?? AbilityOptions[0];
        set
        {
            if (value is not null)
            {
                Set(m => m with { Ability = value.Key.Length == 0 ? null : value.Key }, $"ability {value.Name}");
            }
        }
    }

    public CatalogEntry? Nature
    {
        get => NatureOptions.FirstOrDefault(a => a.Key == (Mon.Nature ?? "")) ?? NatureOptions[0];
        set
        {
            if (value is not null)
            {
                Set(m => m with { Nature = value.Key.Length == 0 ? null : value.Key }, $"nature {value.Name}");
            }
        }
    }

    public IvChoice? Ivs
    {
        get => IvOptions.FirstOrDefault(o => o.Value == Mon.Ivs) ?? IvOptions[0];
        set
        {
            if (value is not null)
            {
                Set(m => m with { Ivs = value.Value }, $"IVs {value.Label}");
            }
        }
    }

    public string Nickname
    {
        get => Mon.Nickname ?? "";
        set => Set(m => m with { Nickname = value.Trim().Length == 0 ? null : value.Trim() }, "nickname changed", "nick");
    }

    public bool Shiny
    {
        get => Mon.Shiny;
        set => Set(m => m with { Shiny = value }, value ? "shiny" : "not shiny");
    }

    private CatalogEntry? GetMove(int i) => i < Mon.Moves.Count ? MoveOptions.FirstOrDefault(m => m.Key == Mon.Moves[i]) ?? new CatalogEntry(Mon.Moves[i], _owner.CatalogForRows.NameOf(Mon.Moves[i])) : MoveOptions[0];

    private void SetMove(int i, CatalogEntry? value)
    {
        if (value is null)
        {
            return;
        }

        var moves = Mon.Moves.ToList();
        while (moves.Count <= i)
        {
            moves.Add("");
        }

        moves[i] = value.Key;
        var cleaned = moves.Where(m => m.Length > 0).ToList();
        Set(m => m with { Moves = cleaned }, value.Key.Length == 0 ? $"move {i + 1} cleared" : $"knows {value.Name}");
    }

    public CatalogEntry? Move1 { get => GetMove(0); set => SetMove(0, value); }
    public CatalogEntry? Move2 { get => GetMove(1); set => SetMove(1, value); }
    public CatalogEntry? Move3 { get => GetMove(2); set => SetMove(2, value); }
    public CatalogEntry? Move4 { get => GetMove(3); set => SetMove(3, value); }

    [RelayCommand]
    private void Remove() => _owner.RemoveMon(Slot);

    [RelayCommand]
    private void MoveUp() => _owner.MoveMon(Slot, -1);

    [RelayCommand]
    private void MoveDown() => _owner.MoveMon(Slot, +1);
}

/// <summary>Enemy AI overview: how many trainers use each setup, and applying one to many trainers at once.</summary>
public sealed partial class EnemyAiPageViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;

    public EnemyAiPageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Presets = AiPresets.For(shell.Session.Data.Catalog);
        _preset = Presets.FirstOrDefault(p => p.Id == "smart") ?? Presets.LastOrDefault();
        Flags = shell.Session.Data.Catalog.AiFlags;
        Refresh();
        shell.DataChanged += (_, _) =>
        {
            foreach (var row in Tuning)
            {
                if (shell.Session.Data.AiSettings.FirstOrDefault(s => s.Key == row.Key) is { } current)
                {
                    row.Sync(current);
                }
            }
        };
    }

    public IReadOnlyList<AiPreset> Presets { get; }
    public IReadOnlyList<CatalogEntry> Flags { get; }
    public ObservableCollection<string> Usage { get; } = [];
    public ObservableCollection<AiSettingRowViewModel> Tuning { get; } = [];
    public bool HasTuning => Tuning.Count > 0;

    public IReadOnlyList<ScopeOption> Scopes { get; } =
    [
        new("Every trainer", "all"),
        new("Gym Leaders, Elite Four, Champions and team bosses", "bosses"),
        new("Everyone except bosses", "others"),
    ];

    private AiPreset? _preset;

    public AiPreset? Preset
    {
        get => _preset;
        set
        {
            if (SetProperty(ref _preset, value))
            {
                OnPropertyChanged(nameof(PresetDescription));
            }
        }
    }

    public string PresetDescription => Preset?.Description ?? "";

    [ObservableProperty]
    public partial ScopeOption? Scope { get; set; }

    internal static bool IsBoss(GameCatalog c, Trainer t)
    {
        var cls = c.NameOf(t.TrainerClass);
        return new[] { "Leader", "Elite", "Champion", "Boss", "Admin", "Rival" }.Any(w => cls.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    internal void SetTuning(string key, int value)
    {
        var data = _shell.Session.Data;
        var i = data.AiSettings.FindIndex(s => s.Key == key);
        if (i < 0 || data.AiSettings[i].Value == value)
        {
            return;
        }

        var before = data.AiSettings[i];
        _shell.Execute(new ValueChange<AiSetting>("Enemy AI", $"AI setting · {before.Label}: {before.Value} → {value}", v => data.AiSettings[i] = v, before, before with { Value = value },
            $"ai:{key}", (a, b) => $"AI setting · {before.Label}: {a.Value} → {b.Value}"));
    }

    private void Refresh()
    {
        Tuning.Clear();
        foreach (var s in _shell.Session.Data.AiSettings)
        {
            Tuning.Add(new AiSettingRowViewModel(this, s));
        }

        Usage.Clear();
        var data = _shell.Session.Data;
        var groups = data.Trainers.Where(t => t.Key != "TRAINER_NONE")
            .GroupBy(t => AiPresets.Match(t.AiFlags, Presets)?.Name ?? "Custom")
            .OrderByDescending(g => g.Count());
        foreach (var g in groups)
        {
            Usage.Add($"{g.Key}: {g.Count().ToString("N0", CultureInfo.CurrentCulture)} trainer{(g.Count() == 1 ? "" : "s")}");
        }
    }

    [RelayCommand]
    private void Apply()
    {
        if (Preset is not { } preset)
        {
            return;
        }

        var data = _shell.Session.Data;
        var scope = (Scope ?? Scopes[0]).Id;
        var keys = data.Trainers.Where(t => t.Key != "TRAINER_NONE" && scope switch
        {
            "bosses" => IsBoss(data.Catalog, t),
            "others" => !IsBoss(data.Catalog, t),
            _ => true,
        }).Select(t => t.Key).ToList();
        var change = TrainerTools.SetAi(data, keys, preset.Flags, preset.Name);
        if (change is null)
        {
            _shell.Toast = "Those trainers already use that AI.";
            return;
        }

        _shell.Execute(change);
        Refresh();
        _shell.Toast = change.Description + ". One Undo puts it back.";
    }
}
