using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Progression;

namespace PokeStudio.App.ViewModels;

public sealed record EvolutionFixOption(string Label, EvolutionFix Fix);

public sealed partial class EvolutionFixRowViewModel : ViewModelBase
{
    private readonly EvolutionFixPageViewModel _page;

    public EvolutionFixRowViewModel(EvolutionFixPageViewModel page, EvolutionFixRow row)
    {
        _page = page;
        Row = row;
        Options = row.Choices.Select(c => new EvolutionFixOption(page.LabelFor(row, c), c)).ToList();
        Choice = Options.First(o => o.Fix == row.Suggested);
    }

    public EvolutionFixRow Row { get; }
    public IReadOnlyList<EvolutionFixOption> Options { get; }
    public string Title => $"{_page.SpeciesName(Row.FromSpecies)} → {_page.SpeciesName(Row.Original.TargetSpecies)}";
    public string Before => EvolutionFixer.Describe(_page.Data, Row.Original);
    public string After => Choice.Fix == EvolutionFix.Leave ? "unchanged" : EvolutionFixer.Describe(_page.Data, EvolutionFixer.Apply(Row, Choice.Fix, _page.TradeLevel));
    public string Reason => Row.Reason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(After))]
    public partial EvolutionFixOption Choice { get; set; }

    public void RefreshAfter() => OnPropertyChanged(nameof(After));
}

/// <summary>Turns trade and time-of-day evolutions into ones a single player can do.</summary>
public sealed partial class EvolutionFixPageViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;

    public EvolutionFixPageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Reload();
    }

    internal GameData Data => _shell.Session.Data;
    public ObservableCollection<EvolutionFixRowViewModel> Rows { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public bool HasRows => Rows.Count > 0;
    public bool HasNoRows => !HasRows;
    public bool HasWarnings => Warnings.Count > 0;
    public int LockedCount { get; private set; }
    public bool HasLocked => LockedCount > 0;
    public string LockedText => $"{LockedCount} Pokémon's evolutions depend on game settings in the source file, so they aren't listed (edit them there).";

    [ObservableProperty]
    public partial bool IncludeTrades { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeTimeOfDay { get; set; } = true;

    [ObservableProperty]
    public partial int TradeLevel { get; set; } = EvolutionFixer.DefaultTradeLevel;

    [ObservableProperty]
    public partial string? Status { get; set; }

    partial void OnIncludeTradesChanged(bool value) => Reload();

    partial void OnIncludeTimeOfDayChanged(bool value) => Reload();

    partial void OnTradeLevelChanged(int value)
    {
        foreach (var r in Rows)
        {
            r.RefreshAfter();
        }
    }

    public string Summary => Rows.Count == 0
        ? "Nothing to fix: every evolution can be done without trading or a particular time of day."
        : $"{Rows.Count} evolutions need a trade or a time of day. Each has a suggested fix; change any you like, then apply.";

    internal string SpeciesName(string key) => Data.Species.FirstOrDefault(s => s.Key == key)?.Name ?? key;

    internal string LabelFor(EvolutionFixRow row, EvolutionFix fix) => fix switch
    {
        EvolutionFix.Leave => "Leave as it is",
        EvolutionFix.HoldItem when row.Problem == EvolutionProblem.Spin => "Level up holding it",
        EvolutionFix.Level when row.Problem == EvolutionProblem.TimeOfDay => "Any time of day",
        EvolutionFix.Level => "Level up at a level",
        EvolutionFix.Stone => row.Problem == EvolutionProblem.TimeOfDay ? "Use a stone" : "Use a Linking Cord",
        _ => "Level up holding an item",
    };

    private void Reload()
    {
        Rows.Clear();
        foreach (var row in EvolutionFixer.Find(Data, IncludeTrades, IncludeTimeOfDay))
        {
            Rows.Add(new EvolutionFixRowViewModel(this, row));
        }

        var enabled = Data.Species.Where(s => s.IsEnabled).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        LockedCount = Data.Evolutions.Count(s => s.LockedReason is not null && !s.EntriesReplaceable && enabled.Contains(s.FromSpecies) && s.Evolutions.Any(e => e.Method == "EVO_TRADE" || e.Conditions.Any(c => c.Kind is "IF_TIME" or "IF_NOT_TIME")));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(HasNoRows));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(LockedCount));
        OnPropertyChanged(nameof(HasLocked));
        OnPropertyChanged(nameof(LockedText));
    }

    [RelayCommand]
    private void UseSuggestions()
    {
        foreach (var r in Rows)
        {
            r.Choice = r.Options.First(o => o.Fix == r.Row.Suggested);
        }
    }

    [RelayCommand]
    private void Apply()
    {
        var level = Math.Clamp(TradeLevel, 1, _shell.Session.Module.Limits.MaxLevel);
        var (change, warnings) = EvolutionFixer.Build(Data, Rows.Select(r => (r.Row, r.Choice.Fix)), level);
        Warnings.Clear();
        foreach (var w in warnings)
        {
            Warnings.Add(w);
        }

        OnPropertyChanged(nameof(HasWarnings));
        if (change is null)
        {
            Status = "Nothing to change.";
            return;
        }

        var count = Rows.Count(r => r.Choice.Fix != EvolutionFix.Leave);
        _shell.Execute(change);
        Reload();
        Status = $"Changed {count} evolutions (one undo step).";
    }
}
