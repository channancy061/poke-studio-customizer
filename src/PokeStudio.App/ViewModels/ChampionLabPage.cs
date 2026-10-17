using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.App.ViewModels;

public sealed record GroupOption(string Label, TrainerGroup Group);

public sealed record DifficultyOption(string Label, Difficulty Difficulty);

/// <summary>Champion Lab: trainer groups and type themes, rules per group and difficulty, and scouting.</summary>
public sealed partial class ChampionLabPageViewModel : ViewModelBase
{
    private static readonly CatalogEntry NoTheme = new("", "No theme");
    private readonly ShellViewModel _shell;

    public ChampionLabPageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        GroupOptions = Enum.GetValues<TrainerGroup>().Select(g => new GroupOption(Lab.Label(g), g)).ToList();
        DifficultyOptions = Enum.GetValues<Difficulty>().Select(d => new DifficultyOption(Lab.Label(d), d)).ToList();
        ThemeOptions = [NoTheme, .. Data.Catalog.Types];
        _difficulty = DifficultyOptions[1];
        Data.Lab ??= new LabSettings();
        Refresh();
        shell.DataChanged += (_, _) => Refresh();
    }

    internal GameData Data => _shell.Session.Data;
    internal LabSettings Settings => Data.Lab!;
    public IReadOnlyList<GroupOption> GroupOptions { get; }
    public IReadOnlyList<DifficultyOption> DifficultyOptions { get; }
    public IReadOnlyList<CatalogEntry> ThemeOptions { get; }
    public IReadOnlyList<int> OffTypeOptions { get; } = [0, 1, 2];
    public ObservableCollection<LabTrainerRowViewModel> Trainers { get; } = [];
    public ObservableCollection<GroupRulesRowViewModel> Rules { get; } = [];
    public ObservableCollection<ScoutRowViewModel> Scouting { get; } = [];
    public ObservableCollection<StoryLockRowViewModel> StoryLocks { get; } = [];
    public bool HasStarters => Data.Starters.Count == 3;
    public string StartersText => HasStarters ? "Starters: " + string.Join(", ", Data.Starters.Select(Name)) : "This project's starters couldn't be found, so the rival rule is skipped.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGroupsTab), nameof(IsRulesTab), nameof(IsScoutingTab), nameof(IsVersionsTab))]
    public partial int Tab { get; set; }

    public bool IsGroupsTab { get => Tab == 0; set { if (value) { Tab = 0; } } }
    public bool IsRulesTab { get => Tab == 1; set { if (value) { Tab = 1; } } }
    public bool IsScoutingTab { get => Tab == 2; set { if (value) { Tab = 2; } } }
    public bool IsVersionsTab { get => Tab == 3; set { if (value) { Tab = 3; } } }

    // ---- difficulty versions ----
    [ObservableProperty]
    public partial bool MakeEasy { get; set; } = true;

    [ObservableProperty]
    public partial bool MakeHard { get; set; } = true;

    [ObservableProperty]
    public partial bool MakeExtraHard { get; set; } = true;

    public string VersionsSummary
    {
        get
        {
            var bosses = Data.Trainers.Where(t => Lab.DifficultyOf(t) == Core.ChampionLab.Difficulty.Normal && Lab.GroupOf(Data, Settings, t) != TrainerGroup.Regular).ToList();
            var withVersions = bosses.Count(b => Data.Trainers.Any(t => Lab.BaseKey(t) == b.Key && t.Key != b.Key));
            return $"{bosses.Count} important trainers; {withVersions} already have other difficulty versions.";
        }
    }

    [RelayCommand]
    private async Task RedesignBossesAsync()
    {
        var (data, settings) = (Data, Settings);
        var (change, count) = await _shell.RunBusyAsync("Redesigning every boss's team…", () => TeamBuilder.RedesignBosses(data, settings));
        if (change is null)
        {
            _shell.Toast = "No bosses to redesign. Set the trainer groups first.";
            return;
        }

        _shell.Execute(change);
        _shell.Toast = $"{count} bosses redesigned. One Undo puts their old teams back. Make the difficulty versions next.";
        OnPropertyChanged(nameof(VersionsSummary));
    }

    [RelayCommand]
    private async Task GenerateVersionsAsync()
    {
        var versions = new List<Core.ChampionLab.Difficulty>();
        if (MakeEasy) { versions.Add(Core.ChampionLab.Difficulty.Easy); }
        if (MakeHard) { versions.Add(Core.ChampionLab.Difficulty.Hard); }
        if (MakeExtraHard) { versions.Add(Core.ChampionLab.Difficulty.ExtraHard); }
        var keys = Data.Trainers.Where(t => Lab.DifficultyOf(t) == Core.ChampionLab.Difficulty.Normal && t.LockedReason is null && Lab.GroupOf(Data, Settings, t) != TrainerGroup.Regular)
            .Select(t => t.Key).ToList();
        if (versions.Count == 0 || keys.Count == 0)
        {
            _shell.Toast = versions.Count == 0 ? "Tick at least one difficulty." : "No important trainers to make versions for. Set groups first.";
            return;
        }

        var settings = Settings;
        if (await _shell.RunBusyAsync($"Making difficulty versions for {keys.Count} trainers…", () => DifficultyVersions.Generate(Data, settings, keys, versions)) is { } change)
        {
            _shell.Execute(change);
            _shell.Toast = $"Made {versions.Count} version(s) for {keys.Count} trainers. One Undo removes them all.";
            OnPropertyChanged(nameof(VersionsSummary));
        }
    }

    [ObservableProperty]
    public partial bool ShowAllTrainers { get; set; }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    partial void OnShowAllTrainersChanged(bool value) => RefreshTrainers();

    partial void OnSearchChanged(string value) => RefreshTrainers();

    private DifficultyOption _difficulty;

    public DifficultyOption Difficulty
    {
        get => _difficulty;
        set
        {
            if (value is not null && SetProperty(ref _difficulty, value))
            {
                RefreshRules();
            }
        }
    }

    internal string Name(string key) => Data.Species.FirstOrDefault(s => s.Key == key)?.DisplayName ?? Data.Catalog.NameOf(key);

    internal void Update(Func<LabSettings, LabSettings> change, string description)
    {
        var before = Settings;
        var after = change(before);
        if (after == before)
        {
            return;
        }

        var data = Data;
        _shell.Execute(new ValueChange<LabSettings?>("Champion Lab", description, v => data.Lab = v, before, after));
    }

    public bool RivalKeepsStarter
    {
        get => Settings.RivalKeepsStarter;
        set => Update(s => s with { RivalKeepsStarter = value }, value ? "Rule on: rivals keep their starter" : "Rule off: rivals keep their starter");
    }

    /// <summary>Adaptive slots need new game code, so only decomp projects get them.</summary>
    public bool AdaptiveSupported => _shell.Session.Module.SupportsAdaptiveSlots(Data);

    [RelayCommand]
    private async Task SuggestAdaptiveSlotsAsync()
    {
        var settings = Settings;
        var (change, count) = await _shell.RunBusyAsync("Choosing counter candidates for every boss…", () => AdaptiveSlots.SuggestForAll(Data, settings));
        if (change is null)
        {
            _shell.Toast = "Every gym leader, rival and Elite Four member already has an adaptive slot (or none fit).";
            return;
        }

        _shell.Execute(change);
        _shell.Toast = $"Adaptive slots suggested for {count} bosses. One Undo removes them all.";
    }

    public bool ChampionLegends
    {
        get => Settings.ChampionLegends;
        set => Update(s => s with { ChampionLegends = value }, value ? "Rule on: the Champion has a legendary and a mythical" : "Rule off: the Champion has a legendary and a mythical");
    }

    public bool OfficialOnly
    {
        get => Settings.OfficialOnly;
        set => Update(s => s with { OfficialOnly = value }, value ? "Rule on: official Pokémon only" : "Rule off: official Pokémon only");
    }

    public bool FairPlay
    {
        get => Settings.FairPlay;
        set => Update(s => s with { FairPlay = value }, value ? "Rule on: fair play with legendaries" : "Rule off: fair play with legendaries");
    }

    public bool UniqueTypeCombos
    {
        get => Settings.UniqueTypeCombos;
        set => Update(s => s with { UniqueTypeCombos = value }, value ? "Rule on: varied type combinations" : "Rule off: varied type combinations");
    }

    // ---- story locks ----
    public IReadOnlyList<CatalogEntry> LegendaryOptions =>
        Data.Species.Where(s => (s.Tags & (SpeciesTags.RestrictedLegendary | SpeciesTags.SubLegendary | SpeciesTags.Mythical)) != 0)
            .Select(s => new CatalogEntry(s.Key, s.DisplayName)).ToList();

    public IReadOnlyList<string> MilestoneOptions => Data.Rules?.LevelCaps.Select(c => c.Label).ToList() ?? [];

    [ObservableProperty]
    public partial CatalogEntry? LockSpecies { get; set; }

    [ObservableProperty]
    public partial int LockMilestone { get; set; }

    [RelayCommand]
    private void AddStoryLock()
    {
        if (LockSpecies is { } s)
        {
            var milestone = LockMilestone;
            Update(x => x with { StoryLocks = new Dictionary<string, int>(x.StoryLocks) { [s.Key] = milestone } }, $"{s.Name} locked until {MilestoneOptions.ElementAtOrDefault(milestone)}");
            LockSpecies = null;
        }
    }

    internal void RemoveStoryLock(string species) =>
        Update(x => x with { StoryLocks = x.StoryLocks.Where(kv => kv.Key != species).ToDictionary(kv => kv.Key, kv => kv.Value) }, $"{Name(species)} no longer story-locked");

    [RelayCommand]
    private void ApplySuggestedThemes()
    {
        var themes = new Dictionary<string, TypeTheme>(Settings.Themes);
        var added = 0;
        foreach (var t in Data.Trainers.Where(t => Lab.GroupOf(Data, Settings, t) is TrainerGroup.EarlyGym or TrainerGroup.LateGym or TrainerGroup.EliteFour))
        {
            var key = Lab.BaseKey(t);
            if (!themes.ContainsKey(key) && Lab.SuggestTheme(Data, t) is { } type)
            {
                themes[key] = new TypeTheme(type);
                added++;
            }
        }

        if (added == 0)
        {
            _shell.Toast = "No new themes to suggest.";
            return;
        }

        Update(s => s with { Themes = themes }, $"Type themes set for {added} trainers");
        _shell.Toast = $"Themes set for {added} gym leaders and Elite Four members from their current teams.";
    }

    [RelayCommand]
    private void ResetRules() => Update(s => s with { Rules = Defaults.Rules() }, "Rules reset to the defaults");

    private void Refresh()
    {
        if (Data.Lab is null)
        {
            Data.Lab = new LabSettings();
        }

        RefreshTrainers();
        RefreshRules();
        if (Scouting.Count == 0)
        {
            foreach (var r in Core.ChampionLab.Scouting.Run(Data))
            {
                Scouting.Add(new ScoutRowViewModel(r, this));
            }
        }

        StoryLocks.Clear();
        foreach (var (species, milestone) in Settings.StoryLocks.OrderBy(kv => kv.Value))
        {
            StoryLocks.Add(new StoryLockRowViewModel(this, species, Name(species), MilestoneOptions.ElementAtOrDefault(milestone) ?? $"#{milestone + 1}"));
        }

        OnPropertyChanged(nameof(RivalKeepsStarter));
        OnPropertyChanged(nameof(FairPlay));
        OnPropertyChanged(nameof(OfficialOnly));
        OnPropertyChanged(nameof(ChampionLegends));
        OnPropertyChanged(nameof(UniqueTypeCombos));
    }

    private void RefreshTrainers()
    {
        Trainers.Clear();
        var q = Search.Trim();
        foreach (var t in Data.Trainers.Where(t => t.Key != "TRAINER_NONE" && t.Party.Count > 0 && Lab.DifficultyOf(t) == Core.ChampionLab.Difficulty.Normal))
        {
            var group = Lab.GroupOf(Data, Settings, t);
            if (!ShowAllTrainers && group == TrainerGroup.Regular)
            {
                continue;
            }

            var title = $"{Data.Catalog.NameOf(t.TrainerClass)} {t.Name}".Trim();
            if (q.Length > 0 && !title.Contains(q, StringComparison.OrdinalIgnoreCase) && !t.Key.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Trainers.Add(new LabTrainerRowViewModel(this, t, title, group));
            if (Trainers.Count >= 400)
            {
                break;
            }
        }
    }

    private void RefreshRules()
    {
        Rules.Clear();
        foreach (var g in Enum.GetValues<TrainerGroup>())
        {
            Rules.Add(new GroupRulesRowViewModel(this, Settings.RulesFor(g, Difficulty.Difficulty)));
        }
    }
}

public sealed partial class LabTrainerRowViewModel : ViewModelBase
{
    private readonly ChampionLabPageViewModel _page;
    private readonly string _key;

    public LabTrainerRowViewModel(ChampionLabPageViewModel page, Trainer t, string title, TrainerGroup group)
    {
        _page = page;
        _key = Lab.BaseKey(t);
        Title = title;
        Subtitle = $"{t.Key} · Lv {t.Party.Max(p => p.Level)}";
        _group = page.GroupOptions.First(o => o.Group == group);
        var theme = page.Settings.Themes.GetValueOrDefault(_key);
        _theme = theme is null ? page.ThemeOptions[0] : page.ThemeOptions.FirstOrDefault(o => o.Key == theme.Type) ?? page.ThemeOptions[0];
        OffType = theme?.OffTypeAllowed ?? 0;
        Suggested = Lab.SuggestTheme(page.Data, t) is { } s ? $"Team is mostly {page.ThemeOptions.FirstOrDefault(o => o.Key == s)?.Name ?? s}" : null;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string? Suggested { get; }
    public bool HasSuggestion => Suggested is not null;

    private GroupOption _group;

    public GroupOption Group
    {
        get => _group;
        set
        {
            if (value is null || value == _group)
            {
                return;
            }

            _group = value;
            var g = value.Group;
            _page.Update(s => s with { GroupOverrides = new Dictionary<string, TrainerGroup>(s.GroupOverrides) { [_key] = g } }, $"{Title} → {value.Label}");
        }
    }

    private CatalogEntry _theme;

    public CatalogEntry Theme
    {
        get => _theme;
        set
        {
            if (value is null || value == _theme)
            {
                return;
            }

            _theme = value;
            SaveTheme();
        }
    }

    private int _offType;

    public int OffType
    {
        get => _offType;
        set
        {
            if (SetProperty(ref _offType, value) && _theme.Key.Length > 0)
            {
                SaveTheme();
            }
        }
    }

    private void SaveTheme()
    {
        var type = _theme.Key;
        var off = _offType;
        _page.Update(s =>
        {
            var themes = new Dictionary<string, TypeTheme>(s.Themes);
            if (type.Length == 0)
            {
                themes.Remove(_key);
            }
            else
            {
                themes[_key] = new TypeTheme(type, off);
            }

            return s with { Themes = themes };
        }, type.Length == 0 ? $"{Title}: no type theme" : $"{Title}: {_theme.Name} theme{(off > 0 ? $" (up to {off} off-type)" : "")}");
    }
}

public sealed partial class GroupRulesRowViewModel(ChampionLabPageViewModel page, GroupRules rules) : ViewModelBase
{
    public string Group => Lab.Label(rules.Group);

    private void Set(Func<GroupRules, GroupRules> change, string what) =>
        page.Update(s =>
        {
            var list = s.Rules.Where(r => !(r.Group == rules.Group && r.Difficulty == rules.Difficulty)).ToList();
            list.Add(change(s.RulesFor(rules.Group, rules.Difficulty) with { Group = rules.Group, Difficulty = rules.Difficulty }));
            return s with { Rules = list };
        }, $"{Lab.Label(rules.Group)} ({Lab.Label(rules.Difficulty)}): {what}");

    public decimal MaxRestricted { get => rules.MaxRestricted; set => Set(r => r with { MaxRestricted = (int)value }, $"restricted legendaries {Limit(value)}"); }
    public decimal MaxSubLegendary { get => rules.MaxSubLegendary; set => Set(r => r with { MaxSubLegendary = (int)value }, $"sub-legendaries {Limit(value)}"); }
    public decimal MaxMythical { get => rules.MaxMythical; set => Set(r => r with { MaxMythical = (int)value }, $"mythicals {Limit(value)}"); }
    public decimal MaxUltraBeast { get => rules.MaxUltraBeast; set => Set(r => r with { MaxUltraBeast = (int)value }, $"Ultra Beasts {Limit(value)}"); }
    public decimal MaxParadox { get => rules.MaxParadox; set => Set(r => r with { MaxParadox = (int)value }, $"Paradox {Limit(value)}"); }
    public decimal MaxMega { get => rules.MaxMega; set => Set(r => r with { MaxMega = (int)value }, $"Megas {Limit(value)}"); }
    public bool RestrictedOrSub { get => rules.RestrictedOrSub; set => Set(r => r with { RestrictedOrSub = value }, value ? "one restricted OR the sub-legendaries" : "restricted and sub-legendaries counted separately"); }
    public bool SpeciesClause { get => rules.SpeciesClause; set => Set(r => r with { SpeciesClause = value }, value ? "Species Clause on" : "Species Clause off"); }
    public bool ItemClause { get => rules.ItemClause; set => Set(r => r with { ItemClause = value }, value ? "Item Clause on" : "Item Clause off"); }

    private static string Limit(decimal v) => v < 0 ? "no limit" : $"max {(int)v}";
}

public sealed record ScoutRowViewModel(string Milestone, int Cap, string Summary, string Types, string Pokemon)
{
    public ScoutRowViewModel(ScoutReport r, ChampionLabPageViewModel page)
        : this(r.Milestone, r.Cap, $"{r.Species.Count} Pokémon from {r.Types.Count} types",
            string.Join(", ", r.Types.Select(t => page.ThemeOptions.FirstOrDefault(o => o.Key == t)?.Name ?? t)),
            string.Join(", ", r.Species.Take(40).Select(page.Name)) + (r.Species.Count > 40 ? "…" : ""))
    {
    }
}

public sealed partial class StoryLockRowViewModel(ChampionLabPageViewModel page, string species, string name, string milestone) : ViewModelBase
{
    public string Text { get; } = $"{name} — not before {milestone}";

    [RelayCommand]
    private void Remove() => page.RemoveStoryLock(species);
}
