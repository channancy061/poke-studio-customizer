using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.App.ViewModels;

public sealed record MoveListItem(string Key, string Title, string Subtitle, string Power);

/// <summary>Move list (search + type filter) with the editor for the selected move.</summary>
public sealed partial class MovesPageViewModel : ViewModelBase
{
    private static readonly CatalogEntry AllTypes = new("", "All types");
    private readonly ShellViewModel _shell;

    public MovesPageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        TypeFilters = [AllTypes, .. Data.Catalog.Types];
        _typeFilter = AllTypes;
        Filter();
        SelectedItem = Items.FirstOrDefault(i => i.Key == shell.SelectedMoveKey) ?? Items.FirstOrDefault();
        shell.DataChanged += (_, _) =>
        {
            Editor?.Refresh();
            RefreshSelectedRow();
        };
    }

    private GameData Data => _shell.Session.Data;
    public ObservableCollection<MoveListItem> Items { get; } = [];
    public IReadOnlyList<CatalogEntry> TypeFilters { get; }
    public string CountText => Items.Count == Data.Moves.Count ? $"{Data.Moves.Count:N0}" : $"{Items.Count:N0} of {Data.Moves.Count:N0}";

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    private CatalogEntry _typeFilter;

    public CatalogEntry TypeFilter
    {
        get => _typeFilter;
        set
        {
            if (SetProperty(ref _typeFilter, value ?? AllTypes))
            {
                Filter();
            }
        }
    }

    [ObservableProperty]
    public partial MoveListItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial MoveEditorViewModel? Editor { get; set; }

    partial void OnSearchChanged(string value) => Filter();

    partial void OnSelectedItemChanged(MoveListItem? value)
    {
        if (value is null)
        {
            return;
        }

        _shell.SelectedMoveKey = value.Key;
        var index = Data.Moves.FindIndex(m => m.Key == value.Key);
        Editor = index < 0 ? null : new MoveEditorViewModel(_shell, index);
    }

    private MoveListItem ToItem(Move m)
    {
        var c = Data.Catalog;
        var category = m.Category switch { MoveCategory.Special => "Special", MoveCategory.Status => "Status", _ => "Physical" };
        return new MoveListItem(m.Key, m.Name, $"{c.NameOf(m.Type)} · {category}", m.Category == MoveCategory.Status ? "—" : m.Power.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private void Filter()
    {
        var q = Search.Trim();
        var selected = SelectedItem?.Key;
        Items.Clear();
        foreach (var m in Data.Moves)
        {
            if (TypeFilter.Key.Length > 0 && m.Type != TypeFilter.Key)
            {
                continue;
            }

            if (q.Length > 0 && !m.Name.Contains(q, StringComparison.OrdinalIgnoreCase) && !m.Key.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Items.Add(ToItem(m));
        }

        OnPropertyChanged(nameof(CountText));
        if (selected is not null && Items.FirstOrDefault(i => i.Key == selected) is { } again)
        {
            SelectedItem = again;
        }
    }

    /// <summary>Keeps the list row in step with renames and type changes.</summary>
    private void RefreshSelectedRow()
    {
        if (SelectedItem is not { } item || Data.Moves.FirstOrDefault(m => m.Key == item.Key) is not { } move)
        {
            return;
        }

        var fresh = ToItem(move);
        if (fresh != item)
        {
            var i = Items.IndexOf(item);
            if (i >= 0)
            {
                Items[i] = fresh;
                SelectedItem = fresh;
            }
        }
    }
}

public sealed record CategoryOption(string Label, MoveCategory Category);

/// <summary>Everything about one move.</summary>
public sealed partial class MoveEditorViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private readonly int _index;

    public MoveEditorViewModel(ShellViewModel shell, int index)
    {
        _shell = shell;
        _index = index;
        TypeOptions = Catalog.Types;
        EffectOptions = Catalog.MoveEffects;
        TargetOptions = Catalog.MoveTargets;
        _nameDraft = Model.Name;
        _descriptionDraft = Model.Description;
    }

    private GameCatalog Catalog => _shell.Session.Data.Catalog;
    private Core.Modules.GameLimits Limits => _shell.Session.Module.Limits;
    internal Move Model => _shell.Session.Data.Moves[_index];

    public IReadOnlyList<CatalogEntry> TypeOptions { get; }
    public IReadOnlyList<CatalogEntry> EffectOptions { get; }
    public IReadOnlyList<CatalogEntry> TargetOptions { get; }

    public IReadOnlyList<CategoryOption> CategoryOptions { get; } =
    [
        new("Physical", MoveCategory.Physical),
        new("Special", MoveCategory.Special),
        new("Status", MoveCategory.Status),
    ];

    public string CategoryHint => Model.Category switch
    {
        MoveCategory.Physical => "Damage uses Attack vs. Defense.",
        MoveCategory.Special => "Damage uses Sp. Atk vs. Sp. Def.",
        _ => "No damage; only its effect.",
    };

    public string Title => Model.Name;
    public string Subtitle => $"{Catalog.NameOf(Model.Type)} · {CategoryOptions.First(c => c.Category == Model.Category).Label}";
    public bool AdvancedMode => _shell.AdvancedMode;
    public string TechnicalInfo => $"{Model.Key} · {Model.Effect} · {Model.Target}";

    private bool Locked(string field) => Model.LockedFields.ContainsKey("*") || Model.LockedFields.ContainsKey(field);

    public bool CanEditName => !Locked("Name");
    public bool CanEditDescription => !Locked("Description");
    public bool CanEditPower => !Locked("Power") && Model.Category != MoveCategory.Status;
    public bool CanEditAccuracy => !Locked("Accuracy");
    public bool CanEditPp => !Locked("Pp");
    public bool CanEditPriority => !Locked("Priority");
    public bool CanEditOther => !Model.LockedFields.ContainsKey("*");
    public IReadOnlyList<string> LockNotes => Model.LockedFields.Values.Distinct().ToList();
    public bool HasLockNotes => LockNotes.Count > 0;
    public IReadOnlyList<string> SettingNotes => Model.SettingDependentFields.Select(kv => $"{kv.Key}: {kv.Value}").ToList();
    public bool HasSettingNotes => SettingNotes.Count > 0;
    public int MaxPower => Limits.MaxMovePower;
    public int MaxPp => Limits.MaxMovePp;
    public int MinPriority => Limits.MinMovePriority;
    public int MaxPriority => Limits.MaxMovePriority;

    public string PowerHint => Model.Category == MoveCategory.Status ? "Status moves don't deal damage." : Model.Power switch
    {
        0 => "0 = power is worked out by the move's effect.",
        < 60 => "Weak, like Tackle (40).",
        < 90 => "Solid, like Bite (60) or Slash (70).",
        < 120 => "Strong, like Thunderbolt (90).",
        _ => "Very strong, like Hyper Beam (150).",
    };

    public string AccuracyHint => Model.Accuracy == 0 ? "Never misses (like Swift)." : $"Hits {Model.Accuracy}% of the time before other effects.";

    public string PriorityHint => Model.Priority switch
    {
        > 0 => $"Goes before normal moves (Quick Attack is +1, Protect +4).",
        < 0 => "Goes after normal moves (like Trick Room).",
        _ => "Normal speed order.",
    };

    // ---- edits ----
    private void Edit(string field, Func<Move, Move> update, Func<Move, Move, string> describe, bool merge = true)
    {
        var before = Model;
        var after = update(before);
        if (after == before)
        {
            return;
        }

        var data = _shell.Session.Data;
        var index = _index;
        var name = before.Name;
        _shell.Execute(new ValueChange<Move>(
            "Moves", $"{name} · {describe(before, after)}",
            v =>
            {
                data.Moves[index] = v;
                data.Catalog.RenameMove(v.Key, v.Name);
            },
            before, after, merge ? $"move:{before.Key}:{field}" : null, (a, b) => $"{name} · {describe(a, b)}"));
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
                : t.Length > Limits.MoveNameLength ? $"{t.Length} / {Limits.MoveNameLength} — {t.Length - Limits.MoveNameLength} letters too long for this game."
                : null;
            if (NameError is null)
            {
                Edit("name", m => m with { Name = t }, (a, b) => $"renamed {a.Name} → {b.Name}");
            }

            OnPropertyChanged(nameof(NameCounter));
        }
    }

    public string NameCounter => $"{NameDraft.Trim().Length} / {Limits.MoveNameLength} letters";

    [ObservableProperty]
    public partial string? NameError { get; set; }

    private string _descriptionDraft;

    public string DescriptionDraft
    {
        get => _descriptionDraft;
        set
        {
            _descriptionDraft = value;
            var text = value.Replace("\r\n", "\n", StringComparison.Ordinal);
            var lines = text.Split('\n').Length;
            DescriptionError = lines > Limits.MoveDescriptionMaxLines ? $"{lines} lines — the summary screen shows {Limits.MoveDescriptionMaxLines}." : null;
            Edit("description", m => m with { Description = text }, (_, _) => "description edited");
        }
    }

    [ObservableProperty]
    public partial string? DescriptionError { get; set; }

    public CatalogEntry? Type
    {
        get => TypeOptions.FirstOrDefault(t => t.Key == Model.Type);
        set
        {
            if (value is not null)
            {
                Edit("type", m => m with { Type = value.Key }, (a, b) => $"type {Catalog.NameOf(a.Type)} → {Catalog.NameOf(b.Type)}", merge: false);
            }
        }
    }

    public CategoryOption? Category
    {
        get => CategoryOptions.FirstOrDefault(c => c.Category == Model.Category);
        set
        {
            if (value is not null)
            {
                Edit("category", m => m with { Category = value.Category, Power = value.Category == MoveCategory.Status ? 0 : m.Power },
                    (a, b) => $"now a {b.Category.ToString().ToLowerInvariant()} move", merge: false);
            }
        }
    }

    public decimal Power
    {
        get => Model.Power;
        set => Edit("power", m => m with { Power = (int)Math.Clamp(value, 0, MaxPower) }, (a, b) => $"power {a.Power} → {b.Power}");
    }

    public decimal Accuracy
    {
        get => Model.Accuracy;
        set => Edit("accuracy", m => m with { Accuracy = (int)Math.Clamp(value, 0, 100) }, (a, b) => $"accuracy {Pct(a.Accuracy)} → {Pct(b.Accuracy)}");
    }

    private static string Pct(int a) => a == 0 ? "never misses" : a + "%";

    public bool NeverMisses
    {
        get => Model.Accuracy == 0;
        set => Edit("accuracy", m => m with { Accuracy = value ? 0 : 100 }, (a, b) => $"accuracy {Pct(a.Accuracy)} → {Pct(b.Accuracy)}", merge: false);
    }

    public decimal Pp
    {
        get => Model.Pp;
        set => Edit("pp", m => m with { Pp = (int)Math.Clamp(value, 1, MaxPp) }, (a, b) => $"PP {a.Pp} → {b.Pp}");
    }

    public decimal Priority
    {
        get => Model.Priority;
        set => Edit("priority", m => m with { Priority = (int)Math.Clamp(value, MinPriority, MaxPriority) }, (a, b) => $"priority {a.Priority:+0;-0;0} → {b.Priority:+0;-0;0}");
    }

    public CatalogEntry? Effect
    {
        get => EffectOptions.FirstOrDefault(e => e.Key == Model.Effect) ?? new CatalogEntry(Model.Effect, Catalog.NameOf(Model.Effect));
        set
        {
            if (value is not null)
            {
                Edit("effect", m => m with { Effect = value.Key }, (a, b) => $"effect {Catalog.NameOf(a.Effect)} → {Catalog.NameOf(b.Effect)}", merge: false);
            }
        }
    }

    public CatalogEntry? Target
    {
        get => TargetOptions.FirstOrDefault(e => e.Key == Model.Target) ?? new CatalogEntry(Model.Target, Catalog.NameOf(Model.Target));
        set
        {
            if (value is not null)
            {
                Edit("target", m => m with { Target = value.Key }, (a, b) => $"hits {Catalog.NameOf(b.Target).ToLowerInvariant()}", merge: false);
            }
        }
    }

    public bool MakesContact
    {
        get => Model.MakesContact;
        set => Edit("contact", m => m with { MakesContact = value }, (_, b) => b.MakesContact ? "now makes contact" : "no longer makes contact", merge: false);
    }

    /// <summary>Pokémon that learn this move by level-up, to show how far a change reaches.</summary>
    public string LearnedBy
    {
        get
        {
            var data = _shell.Session.Data;
            var names = data.Species.Where(s => s.IsEnabled && data.LearnsetFor(s) is { } l && l.LevelUp.Any(m => m.Move == Model.Key))
                .Select(s => s.DisplayName).ToList();
            return names.Count == 0 ? "No Pokémon learns this move by level-up."
                : $"Learned by level-up by {names.Count} Pokémon: {string.Join(", ", names.Take(8))}{(names.Count > 8 ? "…" : "")}";
        }
    }

    public void Refresh()
    {
        // only replace drafts when the model moved away from them (undo/redo), not while typing
        if (_nameDraft.Trim() != Model.Name && NameError is null)
        {
            _nameDraft = Model.Name;
        }

        if (_descriptionDraft.Replace("\r\n", "\n", StringComparison.Ordinal) != Model.Description)
        {
            _descriptionDraft = Model.Description;
        }

        OnPropertyChanged(string.Empty);
    }
}
