using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PokeStudio.App.ViewModels;

public sealed record SearchResult(string Kind, string Title, string Subtitle, Action Go);

/// <summary>Ctrl+K: jump to any Pokémon, move or tool by typing part of its name.</summary>
public sealed partial class GlobalSearchViewModel(ShellViewModel shell) : ViewModelBase
{
    private const int MaxResults = 40;

    public ObservableCollection<SearchResult> Results { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial SearchResult? Selected { get; set; }

    public bool HasNoResults => IsOpen && Query.Trim().Length > 0 && Results.Count == 0;

    partial void OnQueryChanged(string value) => Update();

    [RelayCommand]
    public void Open()
    {
        Query = "";
        IsOpen = true;
        Update();
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var i = Selected is null ? -1 : Results.IndexOf(Selected);
        Selected = Results[Math.Clamp(i + delta, 0, Results.Count - 1)];
    }

    [RelayCommand]
    public void Activate(SearchResult? result = null)
    {
        if ((result ?? Selected) is { } r)
        {
            IsOpen = false;
            r.Go();
        }
    }

    private void Update()
    {
        Results.Clear();
        var q = Query.Trim();
        var data = shell.Session.Data;
        var c = data.Catalog;
        var found = new List<(int Rank, SearchResult Result)>();

        static int Rank(string name, string q) =>
            q.Length == 0 ? 2 : name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : name.Contains(q, StringComparison.OrdinalIgnoreCase) ? 1 : -1;

        foreach (var nav in shell.NavItems.Where(n => n.IsEnabled))
        {
            var r = Rank(nav.Label, q);
            if (r >= 0)
            {
                var key = nav.Key;
                found.Add((r + (q.Length == 0 ? 0 : 1), new SearchResult("Tool", nav.Label, "Open " + nav.Label, () => shell.Select(key))));
            }
        }

        if (q.Length > 0)
        {
            foreach (var s in data.Species)
            {
                var r = Rank(s.DisplayName, q);
                if (r >= 0)
                {
                    var key = s.Key;
                    found.Add((r, new SearchResult("Pokémon", s.DisplayName, string.Join(" · ", s.Types.Select(c.NameOf)), () => shell.GoToSpecies(key))));
                }
            }

            if (shell.NavItems.Any(n => n.Key == NavKey.Moves && n.IsEnabled))
            {
                foreach (var m in data.Moves)
                {
                    var r = Rank(m.Name, q);
                    if (r >= 0)
                    {
                        var key = m.Key;
                        found.Add((r, new SearchResult("Move", m.Name, $"{c.NameOf(m.Type)} · {m.Category}", () => shell.GoToMove(key))));
                    }
                }
            }
        }

        foreach (var (_, result) in found.OrderBy(f => f.Rank).ThenBy(f => f.Result.Title.Length).Take(MaxResults))
        {
            Results.Add(result);
        }

        Selected = Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasNoResults));
    }
}
