using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Progression;
using PokeStudio.Core.Validation;

namespace PokeStudio.App.ViewModels;

public sealed record PlaceItem(string Key, string Title, string Subtitle);

// ---------------- wild encounters ----------------

public sealed partial class EncountersPageViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private bool _updating;

    public EncountersPageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        SpeciesOptions = Data.Species.Where(s => s.IsEnabled).Select(s => new CatalogEntry(s.Key, s.DisplayName)).ToList();
        Filter();
        SelectedItem = Items.FirstOrDefault(i => i.Key == shell.SelectedEncounterKey) ?? Items.FirstOrDefault();
        shell.DataChanged += (_, _) =>
        {
            _updating = true;
            try
            {
                Editor?.Refresh();
            }
            finally
            {
                _updating = false;
            }
        };
    }

    private GameData Data => _shell.Session.Data;
    public ObservableCollection<PlaceItem> Items { get; } = [];
    public IReadOnlyList<CatalogEntry> SpeciesOptions { get; }
    public string CountText => $"{Items.Count:N0} of {Data.Encounters.Count:N0}";

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial PlaceItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial EncounterEditorViewModel? Editor { get; set; }

    partial void OnSearchChanged(string value) => Filter();

    partial void OnSelectedItemChanged(PlaceItem? value)
    {
        if (value is null || _updating)
        {
            return;
        }

        _shell.SelectedEncounterKey = value.Key;
        var index = Data.Encounters.FindIndex(e => e.Key == value.Key);
        Editor = index < 0 ? null : new EncounterEditorViewModel(_shell, index, SpeciesOptions);
    }

    private void Filter()
    {
        var q = Search.Trim();
        var selected = SelectedItem?.Key;
        Items.Clear();
        foreach (var e in Data.Encounters)
        {
            var title = GameValidator.PlaceName(e);
            var species = e.Methods.SelectMany(m => m.Slots).Select(s => s.Species).Distinct().ToList();
            if (q.Length > 0 && !title.Contains(q, StringComparison.OrdinalIgnoreCase)
                && !species.Any(s => Name(s).Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            Items.Add(new PlaceItem(e.Key, title, string.Join(" · ", e.Methods.Select(m => MethodLabel(m.Type)))));
        }

        OnPropertyChanged(nameof(CountText));
        if (selected is not null && Items.FirstOrDefault(i => i.Key == selected) is { } again)
        {
            SelectedItem = again;
        }
    }

    private string Name(string key) => Data.Species.FirstOrDefault(s => s.Key == key)?.DisplayName ?? Data.Catalog.NameOf(key);

    internal static string MethodLabel(string type) => type switch
    {
        "land_mons" => "Grass",
        "water_mons" => "Surfing",
        "fishing_mons" => "Fishing",
        "rock_smash_mons" => "Rock Smash",
        "hidden_mons" => "Hidden",
        _ => GameCatalog.Prettify(type.Replace("_mons", "", StringComparison.Ordinal)),
    };

    // ---- tools ----
    [ObservableProperty]
    public partial bool IsToolsOpen { get; set; }

    [ObservableProperty]
    public partial decimal LevelChange { get; set; } = 5;

    [ObservableProperty]
    public partial bool AllAreas { get; set; }

    [ObservableProperty]
    public partial CatalogEntry? ReplaceFrom { get; set; }

    [ObservableProperty]
    public partial CatalogEntry? ReplaceTo { get; set; }

    [ObservableProperty]
    public partial bool ReplaceInTrainers { get; set; }

    [RelayCommand]
    private void ToggleTools() => IsToolsOpen = !IsToolsOpen;

    [RelayCommand]
    private void ApplyLevelChange()
    {
        var keys = AllAreas ? Data.Encounters.Select(e => e.Key) : SelectedItem is { } s ? [s.Key] : [];
        if (WildTools.ChangeLevels(Data, keys, (int)LevelChange, _shell.Session.Module.Limits) is { } change)
        {
            _shell.Execute(change);
            _shell.Toast = change.Description + ". One Undo puts it back.";
        }
    }

    [RelayCommand]
    private void ApplyReplace()
    {
        if (ReplaceFrom is not { } from || ReplaceTo is not { } to || from.Key == to.Key)
        {
            _shell.Toast = "Pick two different Pokémon.";
            return;
        }

        var change = WildTools.Replace(Data, from.Key, to.Key, ReplaceInTrainers, from.Name, to.Name);
        if (change is null)
        {
            _shell.Toast = $"{from.Name} doesn't appear anywhere.";
            return;
        }

        _shell.Execute(change);
        _shell.Toast = change.Description + ". One Undo puts it back.";
    }
}

public sealed partial class EncounterSlotViewModel(EncounterEditorViewModel owner, int method, int slot, string chance, IReadOnlyList<CatalogEntry> species) : ViewModelBase
{
    public string Chance { get; } = chance;
    public IReadOnlyList<CatalogEntry> SpeciesOptions { get; } = species;
    private EncounterSlot Slot => owner.Model.Methods[method].Slots[slot];

    public CatalogEntry? Species
    {
        get => SpeciesOptions.FirstOrDefault(s => s.Key == Slot.Species) ?? new CatalogEntry(Slot.Species, owner.Name(Slot.Species));
        set
        {
            if (value is not null && value.Key != Slot.Species)
            {
                owner.SetSlot(method, slot, Slot with { Species = value.Key }, $"slot {slot + 1} → {value.Name}");
            }
        }
    }

    public decimal MinLevel
    {
        get => Slot.MinLevel;
        set
        {
            var v = (int)value;
            owner.SetSlot(method, slot, Slot with { MinLevel = v, MaxLevel = Math.Max(v, Slot.MaxLevel) }, $"slot {slot + 1} level {v}", $"min:{method}:{slot}");
        }
    }

    public decimal MaxLevel
    {
        get => Slot.MaxLevel;
        set
        {
            var v = (int)value;
            owner.SetSlot(method, slot, Slot with { MaxLevel = v, MinLevel = Math.Min(v, Slot.MinLevel) }, $"slot {slot + 1} up to level {v}", $"max:{method}:{slot}");
        }
    }
}

public sealed record EncounterMethodViewModel(string Title, string Hint, IReadOnlyList<EncounterSlotViewModel> Slots);

public sealed partial class EncounterEditorViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private readonly int _index;
    private readonly IReadOnlyList<CatalogEntry> _species;

    public EncounterEditorViewModel(ShellViewModel shell, int index, IReadOnlyList<CatalogEntry> species)
    {
        _shell = shell;
        _index = index;
        _species = species;
        Build();
    }

    internal EncounterTable Model => _shell.Session.Data.Encounters[_index];
    public string Title => GameValidator.PlaceName(Model);
    public string Subtitle => Model.Key;
    public bool IsLocked => Model.LockedReason is not null;
    public bool IsEditable => !IsLocked;
    public string? LockedReason => Model.LockedReason;
    public ObservableCollection<EncounterMethodViewModel> Methods { get; } = [];

    internal string Name(string key) => _shell.Session.Data.Species.FirstOrDefault(s => s.Key == key)?.DisplayName ?? _shell.Session.Data.Catalog.NameOf(key);

    private void Build()
    {
        Methods.Clear();
        var chances = _shell.Session.Data.EncounterChances;
        for (var m = 0; m < Model.Methods.Count; m++)
        {
            var method = Model.Methods[m];
            var info = chances.FirstOrDefault(c => c.Type == method.Type);
            var slots = new List<EncounterSlotViewModel>();
            for (var s = 0; s < method.Slots.Count; s++)
            {
                var percent = info is not null && s < info.Percent.Count ? info.Percent[s] : 0;
                var rod = info?.Groups.FirstOrDefault(g => g.Value.Contains(s)).Key;
                var groupTotal = rod is null || info is null ? 100 : info.Groups[rod].Sum(i => i < info.Percent.Count ? info.Percent[i] : 0);
                var share = rod is null ? $"{percent}%" : $"{Math.Round(100.0 * percent / Math.Max(1, groupTotal))}% · {GameCatalog.Prettify(rod)}";
                slots.Add(new EncounterSlotViewModel(this, m, s, share, _species));
            }

            var hint = method.Type switch
            {
                "land_mons" => "Walking in tall grass. Each row's chance is shared by every grass area in the game.",
                "water_mons" => "Surfing on water.",
                "fishing_mons" => "Fishing — each rod uses its own rows.",
                "rock_smash_mons" => "Smashing rocks.",
                _ => "",
            };
            Methods.Add(new EncounterMethodViewModel(EncountersPageViewModel.MethodLabel(method.Type), hint, slots));
        }
    }

    internal void SetSlot(int method, int slot, EncounterSlot value, string description, string? merge = null)
    {
        var before = Model;
        var slots = before.Methods[method].Slots.ToList();
        if (slots[slot] == value)
        {
            return;
        }

        slots[slot] = value;
        var methods = before.Methods.ToList();
        methods[method] = methods[method] with { Slots = slots };
        var data = _shell.Session.Data;
        var index = _index;
        var title = Title;
        _shell.Execute(new ValueChange<EncounterTable>("Wild encounters", $"{title} · {description}", v => data.Encounters[index] = v, before, before with { Methods = methods },
            merge is null ? null : $"enc:{before.Key}:{merge}", (_, _) => $"{title} · {description}"));
    }

    public void Refresh()
    {
        Build();
        OnPropertyChanged(string.Empty);
    }
}

// ---------------- items & shops ----------------

public sealed partial class ItemsPageViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private bool _updating;

    public ItemsPageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        ItemOptions = Data.Catalog.Items.Where(i => i.Key != "ITEM_NONE").ToList();
        Presets = MartPresets.For(Data.Catalog);
        FilterItems();
        FilterMarts();
        SelectedItem = Items.FirstOrDefault(i => i.Key == shell.SelectedItemKey) ?? Items.FirstOrDefault();
        SelectedMart = Marts.FirstOrDefault(m => m.Key == shell.SelectedMartKey) ?? Marts.FirstOrDefault();
        ShowShops = shell.SelectedMartKey is not null && shell.SelectedItemKey is null;
        shell.DataChanged += (_, _) =>
        {
            _updating = true;
            try
            {
                ItemEditor?.Refresh();
                MartEditor?.Refresh();
            }
            finally
            {
                _updating = false;
            }
        };
    }

    private GameData Data => _shell.Session.Data;
    public IReadOnlyList<CatalogEntry> ItemOptions { get; }
    public IReadOnlyList<MartPreset> Presets { get; }
    public ObservableCollection<PlaceItem> Items { get; } = [];
    public ObservableCollection<PlaceItem> Marts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowItems))]
    public partial bool ShowShops { get; set; }

    public bool ShowItems
    {
        get => !ShowShops;
        set => ShowShops = !value;
    }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial PlaceItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial PlaceItem? SelectedMart { get; set; }

    [ObservableProperty]
    public partial ItemEditorViewModel? ItemEditor { get; set; }

    [ObservableProperty]
    public partial MartEditorViewModel? MartEditor { get; set; }

    partial void OnSearchChanged(string value)
    {
        FilterItems();
        FilterMarts();
    }

    partial void OnSelectedItemChanged(PlaceItem? value)
    {
        if (value is null || _updating)
        {
            return;
        }

        _shell.SelectedItemKey = value.Key;
        var i = Data.Items.FindIndex(x => x.Key == value.Key);
        ItemEditor = i < 0 ? null : new ItemEditorViewModel(_shell, i);
    }

    partial void OnSelectedMartChanged(PlaceItem? value)
    {
        if (value is null || _updating)
        {
            return;
        }

        _shell.SelectedMartKey = value.Key;
        var i = Data.Marts.FindIndex(x => x.Key == value.Key);
        MartEditor = i < 0 ? null : new MartEditorViewModel(_shell, i, ItemOptions, Presets);
    }

    private void FilterItems()
    {
        var q = Search.Trim();
        Items.Clear();
        foreach (var i in Data.Items.Where(i => q.Length == 0 || i.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
        {
            Items.Add(new PlaceItem(i.Key, i.Name, i.Price == 0 ? "Not sold" : $"₽{i.Price.ToString("N0", CultureInfo.CurrentCulture)}"));
        }
    }

    private void FilterMarts()
    {
        var q = Search.Trim();
        Marts.Clear();
        foreach (var m in Data.Marts.Where(m => q.Length == 0 || m.Place.Contains(q, StringComparison.OrdinalIgnoreCase)
            || m.Items.Any(i => Data.Catalog.NameOf(i).Contains(q, StringComparison.OrdinalIgnoreCase))))
        {
            Marts.Add(new PlaceItem(m.Key, m.Place, $"{m.Items.Count} items{(m.BadgeTiers.Count > 0 ? " · more with badges" : "")}"));
        }
    }

    [ObservableProperty]
    public partial decimal CandyPrice { get; set; } = 1;

    /// <summary>Rare Candies in every shop at a token price, so players never have to grind (level caps still apply if switched on).</summary>
    [RelayCommand]
    private void CheapCandies()
    {
        const string candy = "ITEM_RARE_CANDY";
        var parts = new List<IChange>();
        var ci = Data.Items.FindIndex(i => i.Key == candy);
        if (ci < 0)
        {
            _shell.Toast = "This game has no Rare Candy item.";
            return;
        }

        if (Data.Items[ci].Price != (int)CandyPrice)
        {
            var before = Data.Items[ci];
            parts.Add(new ValueChange<Item>("Items", "Rare Candy price", v => Data.Items[ci] = v, before, before with { Price = (int)CandyPrice }));
        }

        for (var i = 0; i < Data.Marts.Count; i++)
        {
            var m = Data.Marts[i];
            if (m.LockedReason is null && !m.Items.Contains(candy))
            {
                var index = i;
                parts.Add(new ValueChange<Mart>("Shops", m.Place, v => Data.Marts[index] = v, m, m with { Items = [.. m.Items, candy] }));
            }
        }

        if (parts.Count == 0)
        {
            _shell.Toast = "Every shop already sells Rare Candies at that price.";
            return;
        }

        _shell.Execute(new CompositeChange("Shops", $"Rare Candies for ₽{(int)CandyPrice} in every shop", parts));
        FilterMarts();
        FilterItems();
        _shell.Toast = "Rare Candies added to every shop. One Undo puts it back.";
    }
}

public sealed partial class ItemEditorViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private readonly int _index;

    public ItemEditorViewModel(ShellViewModel shell, int index)
    {
        _shell = shell;
        _index = index;
        _nameDraft = Model.Name;
        _descriptionDraft = Model.Description;
    }

    private Item Model => _shell.Session.Data.Items[_index];
    private GameLimits Limits => _shell.Session.Module.Limits;
    public string Title => Model.Name;
    public string Subtitle => $"{GameCatalog.Prettify(Model.Pocket.Replace("POCKET_", "", StringComparison.Ordinal))} pocket" + (Model.HoldEffect.Length > 0 ? $" · {GameCatalog.Prettify(Model.HoldEffect.Replace("HOLD_EFFECT_", "", StringComparison.Ordinal))}" : "");
    public bool IsEditable => !Model.LockedFields.ContainsKey("*");
    public bool CanEditPrice => !Model.LockedFields.ContainsKey("Price");
    public bool HasEffectAmount => Model.EffectAmount is not null;
    public IReadOnlyList<string> Notes => Model.LockedFields.Values.Concat(Model.SettingDependentFields.Select(kv => $"{kv.Key}: {kv.Value}")).ToList();
    public bool HasNotes => Notes.Count > 0;
    public string Usage
    {
        get
        {
            var shops = _shell.Session.Data.Marts.Where(m => m.Items.Contains(Model.Key) || m.BadgeTiers.Values.Any(t => t.Contains(Model.Key))).Select(m => m.Place).ToList();
            return shops.Count == 0 ? "Not sold in any shop." : $"Sold in {shops.Count} shop{(shops.Count == 1 ? "" : "s")}: {string.Join(", ", shops.Take(6))}{(shops.Count > 6 ? "…" : "")}";
        }
    }

    private void Edit(string field, Func<Item, Item> update, string description, bool merge = true)
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
        _shell.Execute(new ValueChange<Item>("Items", $"{name} · {description}", v =>
        {
            data.Items[index] = v;
            var c = data.Catalog.Items.FindIndex(x => x.Key == v.Key);
            if (c >= 0 && data.Catalog.Items[c].Name != v.Name)
            {
                data.Catalog.Items[c] = data.Catalog.Items[c] with { Name = v.Name };
                data.Catalog.Invalidate();
            }
        }, before, after, merge ? $"item:{before.Key}:{field}" : null, (_, _) => $"{name} · {description}"));
    }

    private string _nameDraft;

    public string NameDraft
    {
        get => _nameDraft;
        set
        {
            _nameDraft = value;
            var t = value.Trim();
            NameError = t.Length == 0 ? "A name can't be empty." : t.Length > Limits.ItemNameLength ? $"{t.Length} / {Limits.ItemNameLength} letters — too long for this game." : null;
            if (NameError is null)
            {
                Edit("name", i => i with { Name = t }, $"renamed to {t}");
            }
        }
    }

    [ObservableProperty]
    public partial string? NameError { get; set; }

    public decimal Price
    {
        get => Model.Price;
        set => Edit("price", i => i with { Price = (int)Math.Clamp(value, 0, Limits.MaxPrice) }, $"price ₽{(int)value}");
    }

    public decimal EffectAmount
    {
        get => Model.EffectAmount ?? 0;
        set => Edit("effect", i => i with { EffectAmount = (int)Math.Clamp(value, 0, 255) }, $"effect strength {(int)value}");
    }

    private string _descriptionDraft;

    public string DescriptionDraft
    {
        get => _descriptionDraft;
        set
        {
            _descriptionDraft = value;
            Edit("description", i => i with { Description = value.Replace("\r\n", "\n", StringComparison.Ordinal) }, "description edited");
        }
    }

    public void Refresh()
    {
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

public sealed partial class BadgeTierViewModel(MartEditorViewModel owner, int badge, IReadOnlyList<string> items, GameCatalog catalog) : ViewModelBase
{
    public int Badge { get; } = badge;
    public string Title => $"After badge {Badge}";
    public IReadOnlyList<MoveChipViewModel> Items { get; } = items.Select(i => new MoveChipViewModel(i, catalog.NameOf(i), k => owner.RemoveFromTier(badge, k))).ToList();
    public bool IsEmpty => Items.Count == 0;
}

public sealed partial class MartEditorViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private readonly int _index;

    public MartEditorViewModel(ShellViewModel shell, int index, IReadOnlyList<CatalogEntry> items, IReadOnlyList<MartPreset> presets)
    {
        _shell = shell;
        _index = index;
        ItemOptions = items;
        Presets = presets;
        Build();
    }

    private Mart Model => _shell.Session.Data.Marts[_index];
    private GameCatalog Catalog => _shell.Session.Data.Catalog;
    public IReadOnlyList<CatalogEntry> ItemOptions { get; }
    public IReadOnlyList<MartPreset> Presets { get; }
    public IReadOnlyList<int> Badges { get; } = [1, 2, 3, 4, 5, 6, 7, 8];
    public string Title => Model.Place;
    public string Subtitle => Model.Key;
    public bool IsEditable => Model.LockedReason is null;
    public ObservableCollection<MoveChipViewModel> Stock { get; } = [];
    public ObservableCollection<BadgeTierViewModel> Tiers { get; } = [];

    [ObservableProperty]
    public partial CatalogEntry? NewItem { get; set; }

    [ObservableProperty]
    public partial MartPreset? Preset { get; set; }

    [ObservableProperty]
    public partial int PresetBadge { get; set; }

    [ObservableProperty]
    public partial CatalogEntry? TierItem { get; set; }

    [ObservableProperty]
    public partial int TierBadge { get; set; } = 4;

    private void Edit(Func<Mart, Mart> update, string description)
    {
        var before = Model;
        var after = update(before);
        var data = _shell.Session.Data;
        var index = _index;
        _shell.Execute(new ValueChange<Mart>("Shops", $"{before.Place} · {description}", v => data.Marts[index] = v, before, after));
    }

    private static Dictionary<int, IReadOnlyList<string>> WithTier(IReadOnlyDictionary<int, IReadOnlyList<string>> tiers, int badge, Func<IReadOnlyList<string>, IReadOnlyList<string>> change)
    {
        var copy = tiers.ToDictionary(kv => kv.Key, kv => kv.Value);
        copy[badge] = change(copy.GetValueOrDefault(badge) ?? []);
        if (copy[badge].Count == 0)
        {
            copy.Remove(badge);
        }

        return copy;
    }

    [RelayCommand]
    private void AddItem()
    {
        if (NewItem is { } item && !Model.Items.Contains(item.Key))
        {
            Edit(m => m with { Items = [.. m.Items, item.Key] }, $"now sells {item.Name}");
            NewItem = null;
        }
    }

    private void RemoveItem(string key) => Edit(m => m with { Items = m.Items.Where(i => i != key).ToList() }, $"stops selling {Catalog.NameOf(key)}");

    /// <summary>Adds a ready-made list, from the start (badge 0) or after a badge.</summary>
    [RelayCommand]
    private void AddPreset()
    {
        if (Preset is not { } p)
        {
            return;
        }

        if (PresetBadge == 0)
        {
            var add = p.Items.Where(i => !Model.Items.Contains(i)).ToList();
            Edit(m => m with { Items = [.. m.Items, .. add] }, $"added {p.Name} ({add.Count})");
        }
        else
        {
            var badge = PresetBadge;
            Edit(m => m with { BadgeTiers = WithTier(m.BadgeTiers, badge, t => [.. t, .. p.Items.Where(i => !t.Contains(i) && !m.Items.Contains(i))]) }, $"{p.Name} after badge {badge}");
        }

        Preset = null;
    }

    [RelayCommand]
    private void AddToTier()
    {
        if (TierItem is { } item)
        {
            var badge = TierBadge;
            Edit(m => m with { BadgeTiers = WithTier(m.BadgeTiers, badge, t => t.Contains(item.Key) ? t : [.. t, item.Key]) }, $"sells {item.Name} after badge {badge}");
            TierItem = null;
        }
    }

    internal void RemoveFromTier(int badge, string key) =>
        Edit(m => m with { BadgeTiers = WithTier(m.BadgeTiers, badge, t => t.Where(i => i != key).ToList()) }, $"{Catalog.NameOf(key)} no longer added at badge {badge}");

    private void Build()
    {
        Stock.Clear();
        foreach (var i in Model.Items)
        {
            Stock.Add(new MoveChipViewModel(i, Catalog.NameOf(i), RemoveItem));
        }

        Tiers.Clear();
        foreach (var (badge, items) in Model.BadgeTiers.OrderBy(kv => kv.Key))
        {
            Tiers.Add(new BadgeTierViewModel(this, badge, items, Catalog));
        }
    }

    public bool HasTiers => Tiers.Count > 0;

    public void Refresh()
    {
        Build();
        OnPropertyChanged(string.Empty);
    }
}

// ---------------- text ----------------

public sealed record TextLineViewModel(string Text, int Width, bool TooWide, bool NewBox);

public sealed partial class TextPageViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private const int MaxListed = 500;

    public TextPageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Filter();
        SelectedItem = Items.FirstOrDefault(i => i.Key == shell.SelectedTextKey) ?? Items.FirstOrDefault();
    }

    private GameData Data => _shell.Session.Data;
    public ObservableCollection<PlaceItem> Items { get; } = [];
    public string CountText { get; private set; } = "";

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial PlaceItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial TextEditorViewModel? Editor { get; set; }

    partial void OnSearchChanged(string value) => Filter();

    partial void OnSelectedItemChanged(PlaceItem? value)
    {
        if (value is null)
        {
            return;
        }

        _shell.SelectedTextKey = value.Key;
        var i = Data.Texts.FindIndex(t => t.Key == value.Key);
        Editor = i < 0 ? null : new TextEditorViewModel(_shell, i);
    }

    private void Filter()
    {
        var q = Search.Trim();
        Items.Clear();
        var matches = Data.Texts.Where(t => q.Length == 0 || t.Text.Contains(q, StringComparison.OrdinalIgnoreCase)
            || t.Place.Contains(q, StringComparison.OrdinalIgnoreCase) || t.Key.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var t in matches.Take(MaxListed))
        {
            var preview = t.Text.Replace("\\n", " ", StringComparison.Ordinal).Replace("\\l", " ", StringComparison.Ordinal).Replace("\\p", " ", StringComparison.Ordinal);
            Items.Add(new PlaceItem(t.Key, preview.Length > 48 ? preview[..47] + "…" : preview, t.Place));
        }

        CountText = matches.Count > MaxListed ? $"first {MaxListed} of {matches.Count:N0} — type to narrow down" : $"{matches.Count:N0}";
        OnPropertyChanged(nameof(CountText));
    }
}

public sealed partial class TextEditorViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private readonly int _index;

    public TextEditorViewModel(ShellViewModel shell, int index)
    {
        _shell = shell;
        _index = index;
        _draft = ToEditable(Model.Text);
        BuildPreview();
    }

    private TextEntry Model => _shell.Session.Data.Texts[_index];
    public string Title => Model.Place;
    public string Subtitle => Model.Key;
    public int? Width => _shell.Session.Module.Limits.TextLineWidth;
    public ObservableCollection<TextLineViewModel> Preview { get; } = [];
    public bool HasTooWide => Preview.Any(l => l.TooWide);

    private string _draft;

    /// <summary>Real line breaks; a blank line starts a new text box.</summary>
    public string Draft
    {
        get => _draft;
        set
        {
            _draft = value;
            var raw = FromEditable(value);
            var before = Model;
            if (raw != before.Text)
            {
                var data = _shell.Session.Data;
                var index = _index;
                _shell.Execute(new ValueChange<TextEntry>("Text", $"{before.Place} · text edited", v => data.Texts[index] = v, before, before with { Text = raw },
                    $"text:{before.Key}", (_, _) => $"{before.Place} · text edited"));
            }

            BuildPreview();
        }
    }

    private void BuildPreview()
    {
        Preview.Clear();
        var boxes = Model.Text.Split("\\p");
        foreach (var box in boxes)
        {
            var first = true;
            foreach (var line in box.Replace("\\l", "\\n", StringComparison.Ordinal).Split("\\n"))
            {
                var px = _shell.Session.Module.MeasureText(line) ?? 0;
                Preview.Add(new TextLineViewModel(line, px, Width is { } w && px > w, first && Preview.Count > 0));
                first = false;
            }
        }

        OnPropertyChanged(nameof(HasTooWide));
    }

    internal static string ToEditable(string raw) =>
        raw.Replace("\\p", "\n\n", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\l", "\n", StringComparison.Ordinal);

    internal static string FromEditable(string editable)
    {
        var boxes = editable.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n");
        return string.Join("\\p", boxes.Select(box =>
        {
            var lines = box.Split('\n');
            return lines[0] + string.Concat(lines.Skip(1).Select((l, i) => (i == 0 ? "\\n" : "\\l") + l));
        }));
    }
}

// ---------------- levels & caps ----------------

public sealed partial class MilestoneViewModel(LevelCap cap, Action changed) : ViewModelBase
{
    public string Label { get; } = cap.Label;
    public int Current { get; } = cap.Level;

    [ObservableProperty]
    public partial decimal Target { get; set; } = cap.Level;

    partial void OnTargetChanged(decimal value) => changed();
}

public sealed partial class ProgressionPageViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;

    public ProgressionPageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        foreach (var c in Rules?.LevelCaps ?? [])
        {
            Milestones.Add(new MilestoneViewModel(c, UpdatePreview));
        }

        UpdatePreview();
    }

    private GameData Data => _shell.Session.Data;
    private GameRules? Rules => Data.Rules;
    public bool HasCaps => Milestones.Count > 0;
    public bool NoCaps => !HasCaps;
    public bool HasSplit => Rules?.PhysicalSpecialSplit is not null;
    public ObservableCollection<MilestoneViewModel> Milestones { get; } = [];
    public IReadOnlyList<int> ChampionPresets { get; } = [70, 80, 90, 95, 100];
    public IReadOnlyList<string> ExpCapOptions { get; } = ["No cap", "Hard cap (no XP at the cap)", "Soft cap (less XP at the cap)"];

    [ObservableProperty]
    public partial bool IncludeTrainers { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeWild { get; set; } = true;

    [ObservableProperty]
    public partial bool UpdateCaps { get; set; } = true;

    [ObservableProperty]
    public partial bool EvolveTrainers { get; set; } = true;

    [ObservableProperty]
    public partial decimal OtherEvolutionLevel { get; set; } = 40;

    [ObservableProperty]
    public partial string Preview { get; set; } = "";

    /// <summary>Scales every milestone so the Champion lands on <paramref name="champion"/>, keeping their spacing.</summary>
    [RelayCommand]
    private void UsePreset(int champion)
    {
        if (Milestones.Count == 0)
        {
            return;
        }

        var last = Math.Max(1, Milestones[^1].Current);
        foreach (var m in Milestones)
        {
            m.Target = Math.Clamp(Math.Round(m.Current * champion / (decimal)last, MidpointRounding.AwayFromZero), 1, _shell.Session.Module.Limits.MaxLevel);
        }
    }

    private IReadOnlyList<CurvePoint> Curve => Milestones.Select(m => new CurvePoint(m.Current, (int)m.Target)).ToList();

    private void UpdatePreview()
    {
        var max = _shell.Session.Module.Limits.MaxLevel;
        Preview = string.Join(" · ", new[] { 5, 15, 30, 45 }.Select(l => $"Lv {l} → {LevelCurve.Map(l, Curve, max)}"));
    }

    [RelayCommand]
    private void Apply()
    {
        var targets = Milestones.Select(m => (int)m.Target).ToList();
        var change = LevelCurve.Apply(Data, Curve, targets,
            new LevelCurve.Options(IncludeTrainers, IncludeWild, UpdateCaps, EvolveTrainers, (int)OtherEvolutionLevel),
            _shell.Session.Module.Limits, $"Level curve: {Milestones[^1].Label} at {targets[^1]}");
        if (change is null)
        {
            _shell.Toast = "Nothing to change.";
            return;
        }

        _shell.Execute(change);
        _shell.Toast = change.Description + ". One Undo puts it back.";
        _shell.Navigate(NavKey.Progression);
    }

    private void SetRules(Func<GameRules, GameRules> update, string description)
    {
        if (Rules is not { } before)
        {
            return;
        }

        var after = update(before);
        if (after != before)
        {
            _shell.Execute(new ValueChange<GameRules?>("Level caps", description, v => Data.Rules = v, before, after));
        }
    }

    public bool CapsEnabled
    {
        get => Rules?.CapsEnabled ?? false;
        set => SetRules(r => r with { CapsEnabled = value }, value ? "Level caps switched on" : "Level caps switched off");
    }

    public int ExpCapIndex
    {
        get => (int)(Rules?.ExpCap ?? ExpCapKind.None);
        set => SetRules(r => r with { ExpCap = (ExpCapKind)Math.Clamp(value, 0, 2) }, $"XP at the cap: {ExpCapOptions[Math.Clamp(value, 0, 2)]}");
    }

    public bool RareCandyCap
    {
        get => Rules?.RareCandyCap ?? false;
        set => SetRules(r => r with { RareCandyCap = value }, value ? "Rare Candies stop at the cap" : "Rare Candies ignore the cap");
    }

    public bool ExpBoostBelowCap
    {
        get => Rules?.ExpBoostBelowCap ?? false;
        set => SetRules(r => r with { ExpBoostBelowCap = value }, value ? "More XP below the cap" : "Normal XP below the cap");
    }

    public bool PhysicalSpecialSplit
    {
        get => Rules?.PhysicalSpecialSplit ?? true;
        set => SetRules(r => r with { PhysicalSpecialSplit = value }, value ? "Physical/Special split on (Gen 4+)" : "Gen 1–3 rule: type decides physical/special");
    }
}
