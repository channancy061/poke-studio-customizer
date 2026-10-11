using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core.Randomizer;

namespace PokeStudio.App.ViewModels;

public sealed record WildChoice(WildRandomizing Value, string Label);

public sealed record TrainerChoice(TrainerRandomizing Value, string Label);

/// <summary>Shuffles wild Pokémon, trainers, abilities, types and moves for a fresh run; one undo step.</summary>
public sealed partial class RandomizerPageViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;

    public RandomizerPageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        SelectedWild = WildChoices[2];
        SelectedTrainers = TrainerChoices[0];
    }

    public IReadOnlyList<WildChoice> WildChoices { get; } =
    [
        new(WildRandomizing.Off, "Leave as they are"),
        new(WildRandomizing.Random, "Any Pokémon"),
        new(WildRandomizing.SimilarStrength, "Pokémon of similar strength"),
    ];

    public IReadOnlyList<TrainerChoice> TrainerChoices { get; } =
    [
        new(TrainerRandomizing.Off, "Leave as they are"),
        new(TrainerRandomizing.SimilarStrength, "Pokémon of similar strength"),
        new(TrainerRandomizing.KeepTypes, "Similar strength, same types (gym themes stay)"),
    ];

    public bool HasWild => _shell.Session.Data.Encounters.Any(e => e.LockedReason is null);
    public bool HasTrainers => _shell.Session.Data.Trainers.Any(t => t.LockedReason is null && t.Party.Count > 0);

    [ObservableProperty]
    public partial string Seed { get; set; } = Randomizer.NewSeed();

    [ObservableProperty]
    public partial WildChoice? SelectedWild { get; set; }

    [ObservableProperty]
    public partial bool WildSameInArea { get; set; } = true;

    [ObservableProperty]
    public partial bool WildLegendaries { get; set; }

    [ObservableProperty]
    public partial TrainerChoice? SelectedTrainers { get; set; }

    [ObservableProperty]
    public partial bool TrainerLegendaries { get; set; }

    [ObservableProperty]
    public partial bool Abilities { get; set; }

    [ObservableProperty]
    public partial bool Types { get; set; }

    [ObservableProperty]
    public partial bool LevelUpMoves { get; set; }

    [ObservableProperty]
    public partial bool TmCompatibility { get; set; }

    [ObservableProperty]
    public partial bool OfficialOnly { get; set; } = true;

    [ObservableProperty]
    public partial string? Status { get; set; }

    public RandomizerOptions Options => new()
    {
        Seed = Seed.Trim(),
        Wild = HasWild ? SelectedWild?.Value ?? WildRandomizing.Off : WildRandomizing.Off,
        WildSameInArea = WildSameInArea,
        WildLegendaries = WildLegendaries,
        Trainers = HasTrainers ? SelectedTrainers?.Value ?? TrainerRandomizing.Off : TrainerRandomizing.Off,
        TrainerLegendaries = TrainerLegendaries,
        Abilities = Abilities,
        Types = Types,
        LevelUpMoves = LevelUpMoves,
        TmCompatibility = TmCompatibility,
        OfficialOnly = OfficialOnly,
    };

    [RelayCommand]
    private void NewSeed() => Seed = Randomizer.NewSeed();

    [RelayCommand]
    private async Task RandomizeAsync()
    {
        if (Seed.Trim().Length == 0)
        {
            Seed = Randomizer.NewSeed();
        }

        var (data, options) = (_shell.Session.Data, Options);
        var (change, summary) = await _shell.RunBusyAsync("Randomizing…", () => Randomizer.Build(data, options));
        if (change is null)
        {
            Status = "Nothing to change with these choices.";
            return;
        }

        _shell.Execute(change);
        Status = $"Randomized with seed {Seed.Trim()}: {summary}. It's one step: Undo (Ctrl+Z) takes it all back. Save to keep it.";
    }
}
