using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core.Modernize;

namespace PokeStudio.App.ViewModels;

public sealed partial class ModernizeRowViewModel(ModernizePlanRow row, string summary, string details, Action changed) : ViewModelBase
{
    public ModernizePlanRow Row { get; } = row;
    public string Title => Row.Title;
    public string Summary { get; } = summary;
    public string Details { get; } = details;
    public bool HasShared => Row.SharedWith.Count > 0;
    public string SharedText => $"Also changes: {string.Join(", ", Row.SharedWith)}";

    [ObservableProperty]
    public partial bool IsIncluded { get; set; } = true;

    partial void OnIsIncludedChanged(bool value) => changed();
}

/// <summary>Switches every Pokémon's level-up moves (and optionally stats, types, abilities) to a chosen generation, with a preview.</summary>
public sealed partial class ModernizePageViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;

    public ModernizePageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Generations = shell.Session.Module.ModernizeGenerations(shell.Session.Data);
        _generation = Generations.FirstOrDefault(g => g.IsLatest) ?? Generations.FirstOrDefault();
    }

    public IReadOnlyList<GenerationChoice> Generations { get; }
    public bool IsAvailable => Generations.Count > 0;
    public bool IsUnavailable => !IsAvailable;
    public ObservableCollection<ModernizeRowViewModel> Rows { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];

    private GenerationChoice? _generation;

    public GenerationChoice? Generation
    {
        get => _generation;
        set
        {
            if (SetProperty(ref _generation, value))
            {
                ClearPreview();
            }
        }
    }

    [ObservableProperty]
    public partial bool IncludeMoves { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeSpecies { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCommand))]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool HasPreview { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    partial void OnIncludeMovesChanged(bool value) => ClearPreview();

    partial void OnIncludeSpeciesChanged(bool value) => ClearPreview();

    public int IncludedCount => Rows.Count(r => r.IsIncluded);
    public string ApplyText => IncludedCount == 1 ? "Apply to 1 Pokémon" : $"Apply to {IncludedCount} Pokémon";
    public bool CanApply => IncludedCount > 0;
    public bool NothingToChange => HasPreview && Rows.Count == 0;
    public string Summary => Rows.Count == 0 ? "" : $"{Rows.Count} Pokémon would change. Untick any you want to keep as they are.";

    private void ClearPreview()
    {
        Rows.Clear();
        HasPreview = false;
        Message = null;
        Changed();
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(IncludedCount));
        OnPropertyChanged(nameof(ApplyText));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(NothingToChange));
        OnPropertyChanged(nameof(Summary));
    }

    private bool CanPreview() => !IsLoading && IsAvailable;

    [RelayCommand(CanExecute = nameof(CanPreview))]
    public async Task PreviewAsync()
    {
        if (Generation is not { } gen || (!IncludeMoves && !IncludeSpecies))
        {
            Message = "Tick at least one thing to update.";
            return;
        }

        IsLoading = true;
        Message = null;
        try
        {
            var data = _shell.Session.Data;
            var reference = await _shell.Session.Module.LoadModernizeReferenceAsync(data, gen.Generation);
            Rows.Clear();
            Notes.Clear();
            if (reference is null)
            {
                Message = "This generation's data couldn't be read.";
                return;
            }

            foreach (var n in reference.Notes)
            {
                Notes.Add(n);
            }

            var c = data.Catalog;
            foreach (var row in Modernizer.Plan(data, reference, IncludeMoves, IncludeSpecies))
            {
                var parts = new List<string>();
                if (row.ChangesMoves)
                {
                    parts.Add(row.MovesAdded.Count == 0 && row.MovesRemoved.Count == 0
                        ? "level-up order changes"
                        : $"+{row.MovesAdded.Count} / −{row.MovesRemoved.Count} level-up moves");
                }

                parts.AddRange(row.SpeciesChanges);
                var details = new List<string>();
                if (row.MovesAdded.Count > 0)
                {
                    details.Add("Learns: " + string.Join(", ", row.MovesAdded.Select(c.NameOf)));
                }

                if (row.MovesRemoved.Count > 0)
                {
                    details.Add("No longer learns: " + string.Join(", ", row.MovesRemoved.Select(c.NameOf)));
                }

                var shared = row with { SharedWith = row.SharedWith.Select(k => data.Species.FirstOrDefault(s => s.Key == k)?.DisplayName ?? k).ToList() };
                Rows.Add(new ModernizeRowViewModel(shared, string.Join(" · ", parts), string.Join("\n", details), Changed));
            }

            HasPreview = true;
        }
        finally
        {
            IsLoading = false;
            Changed();
        }
    }

    [RelayCommand]
    private void SelectAll(bool include)
    {
        foreach (var r in Rows)
        {
            r.IsIncluded = include;
        }
    }

    [RelayCommand]
    private void Apply()
    {
        var chosen = Rows.Where(r => r.IsIncluded).Select(r => r.Row).ToList();
        if (chosen.Count == 0 || Generation is not { } gen)
        {
            return;
        }

        var data = _shell.Session.Data;
        // rows hold display names in SharedWith; the change only needs keys and lists
        _shell.Execute(Modernizer.ToChange(data, chosen, $"Gen {gen.Generation}"));
        Rows.Clear();
        HasPreview = false;
        Changed();
        Message = $"Done: {chosen.Count} Pokémon updated to Gen {gen.Generation}. One Undo (Ctrl+Z) puts everything back. Save to write it to your project.";
    }
}
