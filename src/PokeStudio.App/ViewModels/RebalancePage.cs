using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core.Modernize;
using PokeStudio.Core.Modules;

namespace PokeStudio.App.ViewModels;

public sealed partial class RebalanceRowViewModel(RebalanceRow row, bool chosen) : ViewModelBase
{
    public RebalanceRow Row { get; } = row;
    public string Title => Row.Name;
    public string Summary => Row.Summary;
    public string Why => Row.Change.Why;
    public string Moves => Row.MoveNotes.Count == 0 ? "" : "Learns for its new ability: " + string.Join(", ", Row.MoveNotes) + ".";
    public bool HasMoves => Row.MoveNotes.Count > 0;

    [ObservableProperty]
    public partial bool Chosen { get; set; } = chosen;
}

/// <summary>Replaces abilities that hold their own Pokémon back and buffs weak Pokémon, with the moves new abilities need.</summary>
public sealed partial class RebalancePageViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;

    public RebalancePageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Reload();
    }

    private GameData Data => _shell.Session.Data;
    public ObservableCollection<RebalanceRowViewModel> Abilities { get; } = [];
    public ObservableCollection<RebalanceRowViewModel> Buffs { get; } = [];
    public bool HasAbilities => Abilities.Count > 0;
    public bool HasBuffs => Buffs.Count > 0;
    public bool HasNothing => !HasAbilities && !HasBuffs;

    [ObservableProperty]
    public partial string? Status { get; set; }

    public string HmText => Rebalance.MissingHms(Data) is var n and > 0
        ? $"{n} learnsets can't learn every HM yet."
        : "Every Pokémon can already learn every HM.";

    public bool HmsMissing => Rebalance.MissingHms(Data) > 0;

    [RelayCommand]
    private void HmsForEveryone()
    {
        if (Rebalance.HmsForEveryone(Data) is { } change)
        {
            _shell.Execute(change);
            Status = "Every Pokémon can now learn every HM (one undo step).";
        }

        OnPropertyChanged(nameof(HmText));
        OnPropertyChanged(nameof(HmsMissing));
    }

    private void Reload()
    {
        Abilities.Clear();
        Buffs.Clear();
        foreach (var row in Rebalance.Rows(Data))
        {
            // ability fixes start ticked; buffs are a matter of taste, so they start unticked
            var vm = new RebalanceRowViewModel(row, row.Change.Group == RebalanceGroup.HarmfulAbility);
            (row.Change.Group == RebalanceGroup.HarmfulAbility ? Abilities : Buffs).Add(vm);
        }

        OnPropertyChanged(nameof(HasAbilities));
        OnPropertyChanged(nameof(HasBuffs));
        OnPropertyChanged(nameof(HasNothing));
    }

    [RelayCommand]
    private void Apply()
    {
        var chosen = Abilities.Concat(Buffs).Where(r => r.Chosen).ToList();
        if (Rebalance.Build(Data, chosen.Select(r => r.Row.Change)) is not { } change)
        {
            Status = "Nothing to change.";
            return;
        }

        _shell.Execute(change);
        Reload();
        Status = $"Changed {chosen.Count} Pokémon (one undo step). Rebuild the game to play it; bosses and the guide use the new data.";
    }
}
