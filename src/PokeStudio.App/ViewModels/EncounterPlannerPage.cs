using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Models;
using PokeStudio.Core.Progression;

namespace PokeStudio.App.ViewModels;

/// <summary>One area in the planner's preview; untick it to leave the area as it is.</summary>
public sealed partial class PlanAreaViewModel(EncounterPlanRow row, Func<string, string> name) : ViewModelBase
{
    public EncounterPlanRow Row { get; } = row;
    public string Title => $"{Row.Title} · before {Row.Before}";
    public string Bosses => "Coming up: " + string.Join(", ", Row.Threats.Select(t => GameCatalog.Prettify(t.Replace("TYPE_", "", StringComparison.Ordinal)))) + " types";
    public string Swaps => string.Join("\n", Row.Swaps.Select(s => $"{name(s.From)} → {name(s.To)} ({Method(s.Method)}; {s.Why})"));

    [ObservableProperty]
    public partial bool Include { get; set; } = true;

    private static string Method(string type) => type switch
    {
        _ when type.Contains("land", StringComparison.Ordinal) => "grass",
        _ when type.Contains("water", StringComparison.Ordinal) => "surfing",
        _ when type.Contains("fish", StringComparison.Ordinal) => "fishing",
        _ when type.Contains("rock", StringComparison.Ordinal) => "rock smash",
        _ => type,
    };
}

/// <summary>
/// Plan wild Pokémon for the story: each area gets Pokémon that do well against the next major battles, in the area's
/// habitat, so players have real answers to the bosses without relying on luck.
/// </summary>
public sealed partial class EncounterPlannerPageViewModel(ShellViewModel shell) : ViewModelBase
{
    public ObservableCollection<PlanAreaViewModel> Areas { get; } = [];
    public bool HasAreas => Areas.Count > 0;
    public IReadOnlyList<int> Shares { get; } = [25, 50, 75, 100];
    public IReadOnlyList<int> Lookaheads { get; } = [1, 2, 3];
    public bool HasCaps => (shell.Session.Data.Rules?.LevelCaps.Count ?? 0) > 0;
    public bool NoCaps => !HasCaps;

    [ObservableProperty]
    public partial int Share { get; set; } = 75;

    [ObservableProperty]
    public partial int Lookahead { get; set; } = 2;

    [ObservableProperty]
    public partial string? Status { get; set; }

    private string Name(string species) => shell.Session.Data.Species.FirstOrDefault(s => s.Key == species)?.DisplayName ?? species;

    [RelayCommand]
    private async Task PreviewAsync()
    {
        var data = shell.Session.Data;
        var lab = data.Lab ?? new LabSettings();
        var options = new EncounterPlanner.Options(Share / 100.0, Lookahead);
        var rows = await shell.RunBusyAsync("Planning wild Pokémon for every area…", () => EncounterPlanner.Plan(data, lab, options));
        Areas.Clear();
        foreach (var r in rows)
        {
            Areas.Add(new PlanAreaViewModel(r, Name));
        }

        OnPropertyChanged(nameof(HasAreas));
        Status = rows.Count == 0
            ? "Nothing to change: the areas already fit the coming battles, or no better Pokémon were found."
            : $"{rows.Count} areas would change ({rows.Sum(r => r.Swaps.Count)} Pokémon). Untick any you want to keep as they are, then apply.";
    }

    [RelayCommand]
    private void Apply()
    {
        var chosen = Areas.Where(a => a.Include).Select(a => a.Row).ToList();
        if (EncounterPlanner.ToChange(shell.Session.Data, chosen) is not { } change)
        {
            Status = "Nothing chosen.";
            return;
        }

        shell.Execute(change);
        Areas.Clear();
        OnPropertyChanged(nameof(HasAreas));
        Status = null;
        shell.Toast = change.Description + ". One Undo puts the old Pokémon back.";
    }
}
