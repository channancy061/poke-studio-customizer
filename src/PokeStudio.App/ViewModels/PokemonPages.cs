using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.App.ViewModels;

public sealed record SpeciesListItem(string Key, string Title, string Subtitle, string Number, bool IsEnabled)
{
    public double Opacity => IsEnabled ? 1 : 0.55;
}

/// <summary>Left-hand Pokémon list shared by the Pokémon, Learnset and Evolution pages.</summary>
public abstract partial class SpeciesBrowserViewModel : ViewModelBase
{
    protected SpeciesBrowserViewModel(ShellViewModel shell, string title)
    {
        Shell = shell;
        Title = title;
        Filter();
        SelectedItem = Items.FirstOrDefault(i => i.Key == shell.SelectedSpeciesKey) ?? Items.FirstOrDefault();
        shell.DataChanged += (_, _) => (Editor as IRefreshable)?.Refresh();
    }

    protected ShellViewModel Shell { get; }
    protected GameData Data => Shell.Session.Data;
    public string Title { get; }
    public ObservableCollection<SpeciesListItem> Items { get; } = [];
    public int TotalCount => Data.Species.Count;
    public string CountText => Items.Count == TotalCount ? $"{TotalCount:N0}" : $"{Items.Count:N0} of {TotalCount:N0}";

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowDisabled { get; set; } = true;

    [ObservableProperty]
    public partial SpeciesListItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial ViewModelBase? Editor { get; set; }

    partial void OnSearchChanged(string value) => Filter();

    partial void OnShowDisabledChanged(bool value) => Filter();

    partial void OnSelectedItemChanged(SpeciesListItem? value)
    {
        if (value is null)
        {
            return;
        }

        Shell.SelectedSpeciesKey = value.Key;
        var index = Data.Species.FindIndex(s => s.Key == value.Key);
        Editor = index < 0 ? null : CreateEditor(index);
    }

    protected abstract ViewModelBase CreateEditor(int speciesIndex);

    private void Filter()
    {
        var q = Search.Trim();
        var c = Data.Catalog;
        var selected = SelectedItem?.Key;
        Items.Clear();
        foreach (var s in Data.Species)
        {
            if (!ShowDisabled && !s.IsEnabled)
            {
                continue;
            }

            if (q.Length > 0 && !s.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) && !s.Key.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var types = string.Join(" · ", s.Types.Select(c.NameOf));
            Items.Add(new SpeciesListItem(s.Key, s.DisplayName, s.IsEnabled ? types : "Switched off in settings", (s.Id + 1).ToString("0000", CultureInfo.InvariantCulture), s.IsEnabled));
        }

        OnPropertyChanged(nameof(CountText));
        if (selected is not null && Items.FirstOrDefault(i => i.Key == selected) is { } again)
        {
            SelectedItem = again;
        }
    }
}

public interface IRefreshable
{
    void Refresh();
}

public sealed class PokemonPageViewModel(ShellViewModel shell) : SpeciesBrowserViewModel(shell, "Pokémon")
{
    protected override ViewModelBase CreateEditor(int speciesIndex) => new PokemonEditorViewModel(Shell, speciesIndex);
}

public sealed class LearnsetPageViewModel(ShellViewModel shell) : SpeciesBrowserViewModel(shell, "Learnsets")
{
    protected override ViewModelBase CreateEditor(int speciesIndex) => new LearnsetEditorViewModel(Shell, speciesIndex);
}

public sealed class EvolutionPageViewModel(ShellViewModel shell) : SpeciesBrowserViewModel(shell, "Evolutions")
{
    protected override ViewModelBase CreateEditor(int speciesIndex) => new EvolutionEditorViewModel(Shell, speciesIndex);
}

public sealed record GenderOption(string Label, GenderRatio Ratio);

/// <summary>Everything about one Pokémon: stats, types, abilities, items, growth, Pokédex text.</summary>
public sealed partial class PokemonEditorViewModel : ViewModelBase, IRefreshable
{
    private static readonly CatalogEntry NoneEntry = new("", "None");
    private readonly ShellViewModel _shell;
    private readonly int _index;

    public PokemonEditorViewModel(ShellViewModel shell, int index)
    {
        _shell = shell;
        _index = index;
        var c = Catalog;
        TypeOptions = c.Types;
        Type2Options = [NoneEntry, .. c.Types];
        AbilityOptions = c.Abilities;
        ItemOptions = [NoneEntry, .. c.Items.Where(i => i.Key != "ITEM_NONE")];
        GrowthOptions = c.GrowthRates;
        EggGroupOptions = c.EggGroups;
        EggGroup2Options = [NoneEntry, .. c.EggGroups];
        GenderOptions =
        [
            new("Always male", GenderRatio.AlwaysMale), new("1 in 8 female (12.5%)", new GenderRatio(12.5)), new("1 in 4 female (25%)", new GenderRatio(25)),
            new("Half female (50%)", new GenderRatio(50)), new("3 in 4 female (75%)", new GenderRatio(75)), new("Always female", GenderRatio.AlwaysFemale),
            new("No gender", GenderRatio.NoGender),
        ];
        if (Model.Gender is { } g && GenderOptions.All(o => o.Ratio != g))
        {
            GenderOptions = [.. GenderOptions, new GenderOption(g.Describe(), g)];
        }

        Stats =
        [
            new StatViewModel(this, "HP", s => s.Hp, (s, v) => s with { Hp = v }),
            new StatViewModel(this, "Attack", s => s.Attack, (s, v) => s with { Attack = v }),
            new StatViewModel(this, "Defense", s => s.Defense, (s, v) => s with { Defense = v }),
            new StatViewModel(this, "Sp. Atk", s => s.SpAttack, (s, v) => s with { SpAttack = v }),
            new StatViewModel(this, "Sp. Def", s => s.SpDefense, (s, v) => s with { SpDefense = v }),
            new StatViewModel(this, "Speed", s => s.Speed, (s, v) => s with { Speed = v }),
        ];
        _nameDraft = Model.Name;
    }

    private GameCatalog Catalog => _shell.Session.Data.Catalog;

    internal Species Model
    {
        get => _shell.Session.Data.Species[_index];
        set => _shell.Session.Data.Species[_index] = value;
    }

    private int NameLimit => _shell.Session.Module.Limits.SpeciesNameLength;

    public IReadOnlyList<CatalogEntry> TypeOptions { get; }
    public IReadOnlyList<CatalogEntry> Type2Options { get; }
    public IReadOnlyList<CatalogEntry> AbilityOptions { get; }
    public IReadOnlyList<CatalogEntry> ItemOptions { get; }
    public IReadOnlyList<CatalogEntry> GrowthOptions { get; }
    public IReadOnlyList<CatalogEntry> EggGroupOptions { get; }
    public IReadOnlyList<CatalogEntry> EggGroup2Options { get; }
    public IReadOnlyList<GenderOption> GenderOptions { get; }
    public IReadOnlyList<StatViewModel> Stats { get; }

    public string Title => Model.DisplayName;
    public string Number => (Model.Id + 1).ToString("0000", CultureInfo.InvariantCulture);
    public string TypesText => string.Join(" · ", Model.Types.Select(Catalog.NameOf));
    public bool AdvancedMode => _shell.AdvancedMode;
    public string TechnicalInfo => $"{Model.Key} · module {_shell.Session.Module.Id}";

    // ---- locks ----
    private bool Locked(string field) => Model.LockedFields.ContainsKey("*") || Model.LockedFields.ContainsKey(field);

    public bool IsReadOnly => Model.LockedFields.ContainsKey("*");
    public bool CanEditName => !Locked("Name");
    public bool CanEditStats => !Locked("Stats");
    public bool CanEditTypes => !Locked("Types");
    public bool CanEditAbilities => !Locked("Abilities");
    public bool CanEditItems => !Locked("HeldItemCommon") && !Locked("HeldItemRare");
    public bool CanEditDex => !Locked("DexEntry");
    public bool CanEditOther => !IsReadOnly;
    public IReadOnlyList<string> LockNotes => Model.LockedFields.Values.Distinct().ToList();
    public bool HasLockNotes => LockNotes.Count > 0;
    public IReadOnlyList<string> SettingNotes => Model.SettingDependentFields.Select(kv => $"{kv.Key}: {kv.Value}").ToList();
    public bool ShowSettingNotes => AdvancedMode && SettingNotes.Count > 0;
    public bool HasSettingDependent => Model.SettingDependentFields.Count > 0;

    // ---- stats ----
    public int Total => Model.Stats.Total;
    public double TotalPercent => Math.Min(100, Total / 6.8);
    public string Tier => Total switch { < 300 => "Early route", < 400 => "Mid game", < 520 => "Late game", _ => "Legend-tier" };
    public IReadOnlyList<double> RadarValues => [Model.Stats.Hp, Model.Stats.Attack, Model.Stats.Defense, Model.Stats.Speed, Model.Stats.SpDefense, Model.Stats.SpAttack];

    public IReadOnlyList<double> RadarOriginal =>
        _shell.OriginalStats.TryGetValue(Model.Key, out var o) ? [o.Hp, o.Attack, o.Defense, o.Speed, o.SpDefense, o.SpAttack] : RadarValues;

    // ---- edits ----
    private void Edit(string field, Func<Species, Species> update, Func<Species, Species, string> describe)
    {
        var before = Model;
        var after = update(before);
        if (after == before)
        {
            return;
        }

        var key = before.Key;
        var name = before.DisplayName;
        var idx = _index;
        var data = _shell.Session.Data;
        _shell.Execute(new ValueChange<Species>(
            "Pokémon", $"{name} · {describe(before, after)}",
            v => data.Species[idx] = v, before, after,
            $"species:{key}:{field}", (a, b) => $"{name} · {describe(a, b)}"));
    }

    internal void SetStat(string label, Func<BaseStats, int> get, Func<BaseStats, int, BaseStats> with, int value)
    {
        value = Math.Clamp(value, 1, BaseStats.Max);
        if (get(Model.Stats) == value)
        {
            return;
        }

        Edit("stat:" + label, s => s with { Stats = with(s.Stats, value) }, (a, b) => $"{label} {get(a.Stats)} → {get(b.Stats)}");
    }

    private string _nameDraft;

    public string NameDraft
    {
        get => _nameDraft;
        set
        {
            _nameDraft = value;
            var t = value.Trim();
            NameError = t.Length == 0 ? "A name can't be empty."
                : t.Length > NameLimit ? $"{t.Length} / {NameLimit} — {t.Length - NameLimit} letters too long for this game."
                : null;
            if (NameError is null)
            {
                Edit("name", s => s with { Name = t }, (a, b) => $"renamed {a.Name} → {b.Name}");
            }

            OnPropertyChanged(nameof(NameCounter));
        }
    }

    public string NameCounter => $"{NameDraft.Trim().Length} / {NameLimit} letters";

    [ObservableProperty]
    public partial string? NameError { get; set; }

    private CatalogEntry? Find(IReadOnlyList<CatalogEntry> list, string? key) =>
        string.IsNullOrEmpty(key) ? (list.Count > 0 && list[0] == NoneEntry ? NoneEntry : null) : list.FirstOrDefault(e => e.Key == key) ?? new CatalogEntry(key, Catalog.NameOf(key));

    public CatalogEntry? Type1
    {
        get => Find(TypeOptions, Model.Types.ElementAtOrDefault(0));
        set
        {
            if (value is null)
            {
                return;
            }

            Edit("types", s => s with { Types = s.Types.Count > 1 && s.Types[1] != value.Key ? [value.Key, s.Types[1]] : [value.Key] }, (a, b) => $"type {TypesOf(a)} → {TypesOf(b)}");
        }
    }

    public CatalogEntry? Type2
    {
        get => Find(Type2Options, Model.Types.ElementAtOrDefault(1));
        set
        {
            if (value is null)
            {
                return;
            }

            Edit("types", s => s with { Types = value == NoneEntry || value.Key == s.Types.ElementAtOrDefault(0) ? [s.Types[0]] : [s.Types[0], value.Key] }, (a, b) => $"type {TypesOf(a)} → {TypesOf(b)}");
        }
    }

    private string TypesOf(Species s) => string.Join("/", s.Types.Select(Catalog.NameOf));

    private CatalogEntry? GetAbility(int slot) => Find(AbilityOptions, Model.Abilities.ElementAtOrDefault(slot) ?? "ABILITY_NONE");

    private void SetAbility(int slot, CatalogEntry? value, string label)
    {
        if (value is null)
        {
            return;
        }

        Edit("ability" + slot, s =>
        {
            var list = s.Abilities.ToList();
            while (list.Count < 3)
            {
                list.Add("ABILITY_NONE");
            }

            list[slot] = value.Key;
            return s with { Abilities = list };
        }, (a, b) => $"{label} {Catalog.NameOf(a.Abilities.ElementAtOrDefault(slot))} → {Catalog.NameOf(b.Abilities[slot])}");
    }

    public CatalogEntry? Ability1 { get => GetAbility(0); set => SetAbility(0, value, "ability 1"); }
    public CatalogEntry? Ability2 { get => GetAbility(1); set => SetAbility(1, value, "ability 2"); }
    public CatalogEntry? AbilityHidden { get => GetAbility(2); set => SetAbility(2, value, "hidden ability"); }

    public CatalogEntry? ItemCommon
    {
        get => Find(ItemOptions, Model.HeldItemCommon is "ITEM_NONE" ? null : Model.HeldItemCommon);
        set
        {
            if (value is not null)
            {
                Edit("itemCommon", s => s with { HeldItemCommon = value == NoneEntry ? null : value.Key }, (a, b) => $"common item → {Catalog.NameOf(b.HeldItemCommon)}");
            }
        }
    }

    public CatalogEntry? ItemRare
    {
        get => Find(ItemOptions, Model.HeldItemRare is "ITEM_NONE" ? null : Model.HeldItemRare);
        set
        {
            if (value is not null)
            {
                Edit("itemRare", s => s with { HeldItemRare = value == NoneEntry ? null : value.Key }, (a, b) => $"rare item → {Catalog.NameOf(b.HeldItemRare)}");
            }
        }
    }

    public decimal CatchRate
    {
        get => Model.CatchRate;
        set => Edit("catchRate", s => s with { CatchRate = (int)Math.Clamp(value, 0, 255) }, (a, b) => $"catch rate {a.CatchRate} → {b.CatchRate}");
    }

    public string CatchRateHelp => Model.CatchRate switch
    {
        <= 3 => "Extremely hard to catch (like a legendary).",
        <= 45 => "Hard to catch (like a starter).",
        <= 120 => "Average to catch.",
        <= 190 => "Easy to catch.",
        _ => "Very easy to catch.",
    };

    public decimal ExpYield
    {
        get => Model.ExpYield;
        set => Edit("expYield", s => s with { ExpYield = (int)Math.Clamp(value, 0, 1000) }, (a, b) => $"EXP given {a.ExpYield} → {b.ExpYield}");
    }

    public CatalogEntry? Growth
    {
        get => Find(GrowthOptions, Model.Growth);
        set
        {
            if (value is not null)
            {
                Edit("growth", s => s with { Growth = value.Key }, (a, b) => $"growth speed → {value.Name}");
            }
        }
    }

    public GenderOption? Gender
    {
        get => GenderOptions.FirstOrDefault(o => o.Ratio == Model.Gender);
        set
        {
            if (value is not null)
            {
                Edit("gender", s => s with { Gender = value.Ratio }, (a, b) => $"gender → {value.Label}");
            }
        }
    }

    public CatalogEntry? EggGroup1
    {
        get => Find(EggGroupOptions, Model.EggGroups.ElementAtOrDefault(0));
        set
        {
            if (value is not null)
            {
                Edit("eggGroups", s => s with { EggGroups = s.EggGroups.Count > 1 ? [value.Key, s.EggGroups[1]] : [value.Key] }, (a, b) => "egg groups changed");
            }
        }
    }

    public CatalogEntry? EggGroup2
    {
        get => Find(EggGroup2Options, Model.EggGroups.ElementAtOrDefault(1));
        set
        {
            if (value is not null && Model.EggGroups.Count > 0)
            {
                Edit("eggGroups", s => s with { EggGroups = value == NoneEntry ? [s.EggGroups[0]] : [s.EggGroups[0], value.Key] }, (a, b) => "egg groups changed");
            }
        }
    }

    public string Category
    {
        get => Model.Category;
        set => Edit("category", s => s with { Category = value.Trim() }, (a, b) => $"category → {b.Category}");
    }

    public string DexEntry
    {
        get => Model.DexEntry;
        set
        {
            Edit("dex", s => s with { DexEntry = value.Replace("\r\n", "\n", StringComparison.Ordinal) }, (a, b) => "Pokédex entry edited");
            OnPropertyChanged(nameof(DexCounter));
        }
    }

    public string DexCounter
    {
        get
        {
            var lines = Model.DexEntry.Split('\n');
            var limits = _shell.Session.Module.Limits;
            return $"{lines.Length} / {limits.DexEntryMaxLines} lines · longest line {lines.Max(l => l.Length)} / {limits.DexEntryLineLength}";
        }
    }

    public void Refresh()
    {
        foreach (var s in Stats)
        {
            s.Refresh();
        }

        if (_nameDraft.Trim() != Model.Name && NameError is null)
        {
            _nameDraft = Model.Name;
        }

        OnPropertyChanged(string.Empty);
    }
}

public sealed partial class StatViewModel(PokemonEditorViewModel owner, string label, Func<BaseStats, int> get, Func<BaseStats, int, BaseStats> with) : ViewModelBase
{
    public string Label { get; } = label;

    public double Value
    {
        get => get(owner.Model.Stats);
        set
        {
            owner.SetStat(Label, get, with, (int)Math.Round(value));
            owner.Refresh();
        }
    }

    public decimal ValueNumber
    {
        get => (decimal)Value;
        set => Value = (double)value;
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(ValueNumber));
    }
}

// ---------------- learnsets ----------------

public sealed partial class LevelUpRowViewModel(LearnsetEditorViewModel owner, int index, LevelUpMove move, IReadOnlyList<CatalogEntry> moves, GameCatalog catalog) : ViewModelBase
{
    public int Index { get; } = index;
    public IReadOnlyList<CatalogEntry> MoveOptions { get; } = moves;
    public string LevelLabel => move.Level == 0 ? "On evolving" : "Level";

    public decimal Level
    {
        get => move.Level;
        set => owner.SetLevel(Index, (int)value);
    }

    public CatalogEntry? Move
    {
        get => MoveOptions.FirstOrDefault(m => m.Key == move.Move) ?? new CatalogEntry(move.Move, catalog.NameOf(move.Move));
        set
        {
            if (value is not null)
            {
                owner.SetMove(Index, value.Key);
            }
        }
    }

    [RelayCommand]
    private void Remove() => owner.RemoveLevelUp(Index);

    [RelayCommand]
    private void MoveUp() => owner.MoveRow(Index, -1);

    [RelayCommand]
    private void MoveDown() => owner.MoveRow(Index, +1);
}

public sealed partial class MoveChipViewModel(string key, string name, Action<string> remove) : ViewModelBase
{
    public string Key { get; } = key;
    public string Name { get; } = name;

    [RelayCommand]
    private void Remove() => remove(Key);
}

public sealed partial class MachineRowViewModel(string label, string move, string name, bool isChecked, Action<string, bool> toggle) : ViewModelBase
{
    public string Label { get; } = label;
    public string Name { get; } = name;

    public bool IsChecked
    {
        get => isChecked;
        set => toggle(move, value);
    }
}

/// <summary>Level-up moves (reorderable), egg moves and TM/tutor compatibility for one Pokémon.</summary>
public sealed partial class LearnsetEditorViewModel : ViewModelBase, IRefreshable
{
    private readonly ShellViewModel _shell;
    private readonly string _speciesKey;

    public LearnsetEditorViewModel(ShellViewModel shell, int speciesIndex)
    {
        _shell = shell;
        var species = shell.Session.Data.Species[speciesIndex];
        _speciesKey = species.Key;
        Title = species.DisplayName;
        MoveOptions = Catalog.Moves;
        Refresh();
    }

    private GameData Data => _shell.Session.Data;
    private GameCatalog Catalog => Data.Catalog;
    private int LearnsetIndex => Data.Learnsets.FindIndex(l => l.Key == Data.Species.First(s => s.Key == _speciesKey).LearnsetKey);
    private Learnset? Current => LearnsetIndex >= 0 ? Data.Learnsets[LearnsetIndex] : null;

    public string Title { get; }
    public IReadOnlyList<CatalogEntry> MoveOptions { get; }
    public bool HasLearnset => Current is not null;
    public bool NoLearnset => Current is null;

    public ObservableCollection<LevelUpRowViewModel> LevelUp { get; } = [];
    public ObservableCollection<MoveChipViewModel> EggMoves { get; } = [];
    public ObservableCollection<MachineRowViewModel> Machines { get; } = [];
    public ObservableCollection<MoveChipViewModel> Tutors { get; } = [];

    [ObservableProperty]
    public partial LevelUpRowViewModel? SelectedRow { get; set; }

    [ObservableProperty]
    public partial CatalogEntry? NewEggMove { get; set; }

    [ObservableProperty]
    public partial CatalogEntry? NewTutorMove { get; set; }

    private string? Lock(string part) => Current?.LockedParts.GetValueOrDefault(part);

    public bool CanEditLevelUp => Lock("levelUp") is null;
    public bool CanEditEgg => Lock("egg") is null;
    public bool CanEditTeachable => Lock("teachable") is null;
    public string? LevelUpNote => Lock("levelUp");
    public string? EggNote => Lock("egg");
    public string? TeachableNote => Lock("teachable");

    public string? SharedNote
    {
        get
        {
            var others = Current?.UsedBy.Where(k => k != _speciesKey).ToList() ?? [];
            return others.Count == 0 ? null
                : $"These level-up moves are shared with {string.Join(", ", others.Take(3).Select(k => Data.Species.FirstOrDefault(s => s.Key == k)?.DisplayName ?? k))}{(others.Count > 3 ? $" and {others.Count - 3} more" : "")}. Changes apply to all of them.";
        }
    }

    public int LevelOneCount => Current?.LevelUp.Count(m => m.Level == 1) ?? 0;
    public bool TooManyLevelOne => LevelOneCount > _shell.Session.Module.Limits.MovesKnown;
    public string LevelOneWarning => $"Learns {LevelOneCount} moves at level 1 but can only hold {_shell.Session.Module.Limits.MovesKnown}. The first ones will be forgotten.";

    private void Update(string description, Func<Learnset, Learnset> update, string? mergeKey = null)
    {
        var index = LearnsetIndex;
        if (index < 0)
        {
            return;
        }

        var before = Data.Learnsets[index];
        var after = update(before);
        var data = Data;
        _shell.Execute(new ValueChange<Learnset>("Learnsets", $"{Title} · {description}", v => data.Learnsets[index] = v, before, after, mergeKey, (_, _) => $"{Title} · {description}"));
    }

    internal void SetLevel(int row, int level)
    {
        level = Math.Clamp(level, 0, _shell.Session.Module.Limits.MaxLevel);
        var current = Current!.LevelUp[row];
        if (current.Level != level)
        {
            Update($"{Catalog.NameOf(current.Move)} now at level {level}", l => l with { LevelUp = Replace(l.LevelUp, row, current with { Level = level }) }, $"learn:{_speciesKey}:{row}:level");
        }
    }

    internal void SetMove(int row, string move)
    {
        var current = Current!.LevelUp[row];
        if (current.Move != move)
        {
            Update($"level {current.Level}: {Catalog.NameOf(current.Move)} → {Catalog.NameOf(move)}", l => l with { LevelUp = Replace(l.LevelUp, row, current with { Move = move }) });
        }
    }

    internal void RemoveLevelUp(int row)
    {
        var m = Current!.LevelUp[row];
        Update($"removed {Catalog.NameOf(m.Move)} (level {m.Level})", l => l with { LevelUp = l.LevelUp.Where((_, i) => i != row).ToList() });
    }

    internal void MoveRow(int row, int delta)
    {
        var target = row + delta;
        if (Current is not { } l || target < 0 || target >= l.LevelUp.Count)
        {
            return;
        }

        Update($"moved {Catalog.NameOf(l.LevelUp[row].Move)} {(delta < 0 ? "up" : "down")}", x =>
        {
            var list = x.LevelUp.ToList();
            (list[row], list[target]) = (list[target], list[row]);
            return x with { LevelUp = list };
        });
        SelectedRow = LevelUp.ElementAtOrDefault(target);
    }

    /// <summary>Drag-and-drop: moves a row to another position in one undo step.</summary>
    internal void MoveRowTo(int from, int to)
    {
        if (Current is not { } l || from == to || from < 0 || to < 0 || from >= l.LevelUp.Count || to >= l.LevelUp.Count)
        {
            return;
        }

        var name = Catalog.NameOf(l.LevelUp[from].Move);
        Update($"moved {name} to position {to + 1}", x =>
        {
            var list = x.LevelUp.ToList();
            var item = list[from];
            list.RemoveAt(from);
            list.Insert(to, item);
            return x with { LevelUp = list };
        });
        SelectedRow = LevelUp.ElementAtOrDefault(to);
    }

    [RelayCommand]
    private void AddLevelUp()
    {
        var level = Current?.LevelUp.LastOrDefault()?.Level ?? 1;
        var move = MoveOptions.FirstOrDefault()?.Key ?? "MOVE_TACKLE";
        Update($"added {Catalog.NameOf(move)} at level {level}", l => l with { LevelUp = [.. l.LevelUp, new LevelUpMove(level, move)] });
        SelectedRow = LevelUp.LastOrDefault();
    }

    [RelayCommand]
    private void SortByLevel() =>
        Update("sorted by level", l => l with { LevelUp = l.LevelUp.Select((m, i) => (m, i)).OrderBy(x => x.m.Level).ThenBy(x => x.i).Select(x => x.m).ToList() });

    [RelayCommand]
    private void MoveSelectedUp()
    {
        if (SelectedRow is { } r)
        {
            MoveRow(r.Index, -1);
        }
    }

    [RelayCommand]
    private void MoveSelectedDown()
    {
        if (SelectedRow is { } r)
        {
            MoveRow(r.Index, +1);
        }
    }

    [RelayCommand]
    private void AddEggMove()
    {
        if (NewEggMove is { } m && Current is { } l && !l.EggMoves.Contains(m.Key))
        {
            Update($"added egg move {m.Name}", x => x with { EggMoves = [.. x.EggMoves, m.Key] });
            NewEggMove = null;
        }
    }

    [RelayCommand]
    private void AddTutorMove()
    {
        if (NewTutorMove is { } m && Current is { } l && !l.Teachable.Contains(m.Key))
        {
            Update($"can now be taught {m.Name}", x => x with { Teachable = [.. x.Teachable, m.Key] });
            NewTutorMove = null;
        }
    }

    private void ToggleTeachable(string move, bool on)
    {
        if (Current is not { } l || l.Teachable.Contains(move) == on)
        {
            return;
        }

        Update(on ? $"can now learn {Catalog.NameOf(move)} by TM" : $"can no longer learn {Catalog.NameOf(move)} by TM",
            x => x with { Teachable = on ? [.. x.Teachable, move] : x.Teachable.Where(t => t != move).ToList() });
    }

    private static List<LevelUpMove> Replace(IReadOnlyList<LevelUpMove> list, int index, LevelUpMove value)
    {
        var copy = list.ToList();
        copy[index] = value;
        return copy;
    }

    public void Refresh()
    {
        var l = Current;
        var selected = SelectedRow?.Index;
        LevelUp.Clear();
        EggMoves.Clear();
        Machines.Clear();
        Tutors.Clear();
        if (l is not null)
        {
            for (var i = 0; i < l.LevelUp.Count; i++)
            {
                LevelUp.Add(new LevelUpRowViewModel(this, i, l.LevelUp[i], MoveOptions, Catalog));
            }

            foreach (var m in l.EggMoves)
            {
                EggMoves.Add(new MoveChipViewModel(m, Catalog.NameOf(m), k => Update($"removed egg move {Catalog.NameOf(k)}", x => x with { EggMoves = x.EggMoves.Where(e => e != k).ToList() })));
            }

            var machineMoves = Catalog.Machines.Select(m => m.Move).ToHashSet(StringComparer.Ordinal);
            foreach (var (label, move) in Catalog.Machines)
            {
                Machines.Add(new MachineRowViewModel(label, move, Catalog.NameOf(move), l.Teachable.Contains(move), ToggleTeachable));
            }

            foreach (var t in l.Teachable.Where(t => !machineMoves.Contains(t)).Order(StringComparer.Ordinal))
            {
                Tutors.Add(new MoveChipViewModel(t, Catalog.NameOf(t), k => ToggleTeachable(k, false)));
            }
        }

        if (selected is int s)
        {
            SelectedRow = LevelUp.ElementAtOrDefault(s);
        }

        OnPropertyChanged(string.Empty);
    }
}

// ---------------- evolutions ----------------

public sealed partial class ConditionRowViewModel(EvolutionRowViewModel row, int index, EvolutionCondition condition, IReadOnlyList<CatalogEntry> kinds, GameCatalog catalog) : ViewModelBase
{
    public IReadOnlyList<CatalogEntry> KindOptions { get; } = kinds;

    public CatalogEntry? Kind
    {
        get => KindOptions.FirstOrDefault(k => k.Key == condition.Kind) ?? new CatalogEntry(condition.Kind, catalog.NameOf(condition.Kind));
        set
        {
            if (value is not null)
            {
                row.SetCondition(index, condition with { Kind = value.Key });
            }
        }
    }

    public string Value
    {
        get => condition.Value;
        set => row.SetCondition(index, condition with { Value = value.Trim() });
    }

    [RelayCommand]
    private void Remove() => row.RemoveCondition(index);
}

public sealed partial class EvolutionRowViewModel : ViewModelBase
{
    private readonly EvolutionEditorViewModel _owner;
    private readonly Evolution _evolution;
    private readonly GameCatalog _catalog;

    public EvolutionRowViewModel(EvolutionEditorViewModel owner, int index, Evolution evolution, GameCatalog catalog, IReadOnlyList<CatalogEntry> speciesOptions, bool readOnly)
    {
        _owner = owner;
        _evolution = evolution;
        _catalog = catalog;
        Index = index;
        SpeciesOptions = speciesOptions;
        IsEditable = !readOnly;
        MethodOptions = catalog.EvolutionMethods;
        Conditions = new ObservableCollection<ConditionRowViewModel>(evolution.Conditions.Select((c, i) => new ConditionRowViewModel(this, i, c, catalog.EvolutionConditions, catalog)));
    }

    public int Index { get; }
    public bool IsEditable { get; }
    public IReadOnlyList<CatalogEntry> MethodOptions { get; }
    public IReadOnlyList<CatalogEntry> SpeciesOptions { get; }
    public IReadOnlyList<CatalogEntry> ItemOptions => _catalog.Items;
    public ObservableCollection<ConditionRowViewModel> Conditions { get; }

    public CatalogEntry? Method
    {
        get => MethodOptions.FirstOrDefault(m => m.Key == _evolution.Method) ?? new CatalogEntry(_evolution.Method, _catalog.NameOf(_evolution.Method));
        set
        {
            if (value is not null && value.Key != _evolution.Method)
            {
                var parameter = value.Key switch
                {
                    "EVO_ITEM" => _catalog.Items.FirstOrDefault(i => i.Key != "ITEM_NONE")?.Key ?? "ITEM_NONE",
                    "EVO_LEVEL" or "EVO_LEVEL_BATTLE_ONLY" => "16",
                    _ => "0",
                };
                _owner.Replace(Index, _evolution with { Method = value.Key, Parameter = parameter }, $"evolution method → {value.Name}");
            }
        }
    }

    public string? MethodDescription => Method?.Description;
    public bool ParameterIsItem => _evolution.Method == "EVO_ITEM";
    public bool ParameterIsNumber => !ParameterIsItem && _evolution.Method is "EVO_LEVEL" or "EVO_LEVEL_BATTLE_ONLY";
    public string ParameterLabel => ParameterIsItem ? "Item" : ParameterIsNumber ? "Level" : "Value";
    public bool ParameterIsText => !ParameterIsItem && !ParameterIsNumber;

    public decimal ParameterLevel
    {
        get => decimal.TryParse(_evolution.Parameter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        set => _owner.Replace(Index, _evolution with { Parameter = ((int)value).ToString(CultureInfo.InvariantCulture) }, $"evolves at level {(int)value}", $"evo:{Index}:level");
    }

    public CatalogEntry? ParameterItem
    {
        get => ItemOptions.FirstOrDefault(i => i.Key == _evolution.Parameter) ?? new CatalogEntry(_evolution.Parameter, _catalog.NameOf(_evolution.Parameter));
        set
        {
            if (value is not null && value.Key != _evolution.Parameter)
            {
                _owner.Replace(Index, _evolution with { Parameter = value.Key }, $"evolves with {value.Name}");
            }
        }
    }

    public string ParameterText
    {
        get => _evolution.Parameter;
        set => _owner.Replace(Index, _evolution with { Parameter = value.Trim() }, "evolution value changed", $"evo:{Index}:param");
    }

    public CatalogEntry? Target
    {
        get => SpeciesOptions.FirstOrDefault(s => s.Key == _evolution.TargetSpecies) ?? new CatalogEntry(_evolution.TargetSpecies, _evolution.TargetSpecies);
        set
        {
            if (value is not null && value.Key != _evolution.TargetSpecies)
            {
                _owner.Replace(Index, _evolution with { TargetSpecies = value.Key }, $"evolves into {value.Name}");
            }
        }
    }

    /// <summary>"Sproutle evolves into Bloomtle when it reaches level 16, at night."</summary>
    public string Sentence
    {
        get
        {
            var into = Target?.Name ?? _evolution.TargetSpecies;
            var how = _evolution.Method switch
            {
                "EVO_LEVEL" when _evolution.Parameter == "0" => "when it levels up",
                "EVO_LEVEL" => $"when it reaches level {_evolution.Parameter}",
                "EVO_LEVEL_BATTLE_ONLY" => $"when it reaches level {_evolution.Parameter} in battle",
                "EVO_ITEM" => $"when you use a {_catalog.NameOf(_evolution.Parameter)} on it",
                "EVO_TRADE" => "when it is traded",
                "EVO_BATTLE_END" => "after a battle",
                _ => "— " + (Method?.Name ?? _evolution.Method).ToLowerInvariant(),
            };
            var conditions = _evolution.Conditions.Select(c => $"{_catalog.NameOf(c.Kind).ToLowerInvariant()} ({_catalog.NameOf(c.Value)})");
            var tail = _evolution.Conditions.Count == 0 ? "" : ", if " + string.Join(" and ", conditions);
            return $"{_owner.Title} evolves into {into} {how}{tail}.";
        }
    }

    internal void SetCondition(int index, EvolutionCondition condition)
    {
        var list = _evolution.Conditions.ToList();
        if (list[index] == condition)
        {
            return;
        }

        list[index] = condition;
        _owner.Replace(Index, _evolution with { Conditions = list }, "evolution condition changed", $"evo:{Index}:cond:{index}");
    }

    internal void RemoveCondition(int index) =>
        _owner.Replace(Index, _evolution with { Conditions = _evolution.Conditions.Where((_, i) => i != index).ToList() }, "removed an evolution condition");

    [RelayCommand]
    private void AddCondition()
    {
        var kind = _catalog.EvolutionConditions.FirstOrDefault()?.Key ?? "IF_TIME";
        _owner.Replace(Index, _evolution with { Conditions = [.. _evolution.Conditions, new EvolutionCondition(kind, "0")] }, "added an evolution condition");
    }

    [RelayCommand]
    private void Remove() => _owner.RemoveAt(Index);
}

/// <summary>Visual list of how a Pokémon evolves, with plain-language sentences.</summary>
public sealed partial class EvolutionEditorViewModel : ViewModelBase, IRefreshable
{
    private readonly ShellViewModel _shell;
    private readonly string _speciesKey;
    private readonly IReadOnlyList<CatalogEntry> _speciesOptions;

    public EvolutionEditorViewModel(ShellViewModel shell, int speciesIndex)
    {
        _shell = shell;
        var species = shell.Session.Data.Species[speciesIndex];
        _speciesKey = species.Key;
        Title = species.DisplayName;
        _speciesOptions = shell.Session.Data.Species.Select(s => new CatalogEntry(s.Key, s.DisplayName)).ToList();
        if (Data.Evolutions.All(e => e.FromSpecies != _speciesKey))
        {
            Data.Evolutions.Add(new EvolutionSet { FromSpecies = _speciesKey });
        }

        Refresh();
    }

    private GameData Data => _shell.Session.Data;
    private int SetIndex => Data.Evolutions.FindIndex(e => e.FromSpecies == _speciesKey);
    private EvolutionSet Current => Data.Evolutions[SetIndex];

    public string Title { get; }
    public ObservableCollection<EvolutionRowViewModel> Rows { get; } = [];
    public string? LockedReason => Current.LockedReason;
    public bool IsEditable => Current.LockedReason is null;
    public bool HasNone => Rows.Count == 0;

    /// <summary>"Evolves from Sproutle (level 16)".</summary>
    public string EvolvesFrom
    {
        get
        {
            var from = Data.Evolutions
                .SelectMany(set => set.Evolutions.Where(e => e.TargetSpecies == _speciesKey).Select(e => Data.Species.FirstOrDefault(s => s.Key == set.FromSpecies)?.DisplayName ?? set.FromSpecies))
                .Distinct().ToList();
            return from.Count == 0 ? "Doesn't evolve from another Pokémon." : "Evolves from " + string.Join(", ", from) + ".";
        }
    }

    internal void Replace(int index, Evolution value, string description, string? mergeKey = null) =>
        Update(description, s =>
        {
            var list = s.Evolutions.ToList();
            list[index] = value;
            return s with { Evolutions = list };
        }, mergeKey);

    internal void RemoveAt(int index) => Update("removed an evolution", s => s with { Evolutions = s.Evolutions.Where((_, i) => i != index).ToList() });

    [RelayCommand]
    private void AddEvolution()
    {
        var target = _speciesOptions.FirstOrDefault(s => s.Key != _speciesKey)?.Key ?? _speciesKey;
        Update("added an evolution", s => s with { Evolutions = [.. s.Evolutions, new Evolution("EVO_LEVEL", "16", target)] });
    }

    private void Update(string description, Func<EvolutionSet, EvolutionSet> update, string? mergeKey = null)
    {
        var index = SetIndex;
        var before = Data.Evolutions[index];
        if (before.LockedReason is not null)
        {
            return;
        }

        var after = update(before);
        var data = Data;
        var title = Title;
        _shell.Execute(new ValueChange<EvolutionSet>("Evolutions", $"{title} · {description}", v => data.Evolutions[index] = v, before, after,
            mergeKey is null ? null : $"evo:{_speciesKey}:{mergeKey}", (_, _) => $"{title} · {description}"));
    }

    public void Refresh()
    {
        Rows.Clear();
        var set = Current;
        for (var i = 0; i < set.Evolutions.Count; i++)
        {
            Rows.Add(new EvolutionRowViewModel(this, i, set.Evolutions[i], Data.Catalog, _speciesOptions, set.LockedReason is not null));
        }

        OnPropertyChanged(string.Empty);
    }
}
