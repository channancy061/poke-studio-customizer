using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Story;

namespace PokeStudio.App.ViewModels;

public sealed record MapChoice(string Map, string Name);

public sealed record LookChoice(string Key, string Name);

/// <summary>A crossover pack the open game can take.</summary>
public sealed partial class PackRowViewModel(StoryBattlesPageViewModel page, CrossoverPack pack, string needs) : ViewModelBase
{
    public CrossoverPack Pack { get; } = pack;
    public string Title => Pack.Title;
    public string Description => Pack.Description;
    public string Needs { get; } = needs;

    [RelayCommand]
    private Task AddAsync() => page.AddPackAsync(Pack);
}

/// <summary>One story battle in the list.</summary>
public sealed partial class StoryRowViewModel(StoryBattlesPageViewModel page, StoryBattle battle, string name, string major) : ViewModelBase
{
    public StoryBattle Battle { get; } = battle;
    public string Name { get; } = name;
    public string Where => $"{StoryBattles.PlaceName(Battle.Map)} · in front of {Battle.Entrance}";
    public string Major { get; } = major;

    [RelayCommand]
    private void EditTeam() => page.EditTeam(Battle.TrainerKey);

    [RelayCommand]
    private void Remove() => page.Remove(Battle.TrainerKey);
}

/// <summary>
/// Story battles: a trainer guarding an entrance, so the player can't skip the fight. Pick a map and an entrance, then
/// who the trainer is; the team can be edited on the Trainers page or redesigned in Champion Lab like any other.
/// </summary>
public sealed partial class StoryBattlesPageViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;

    public StoryBattlesPageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        var module = shell.Session.Module;
        Maps = module.StoryMaps(Data).Select(m => new MapChoice(m.Map, m.Name)).ToList();
        Looks = module.StoryLooks(Data).Select(l => new LookChoice(l, StoryBattles.LookName(l))).ToList();
        ClassOptions = Data.Catalog.TrainerClasses;
        PicOptions = [.. Data.Catalog.TrainerPics,
            .. (Data.Story?.Imports ?? []).Where(i => i.Kind == StoryImport.Pic && Data.Catalog.TrainerPics.All(p => p.Key != i.Key))
                .Select(i => new CatalogEntry(i.Key, GameCatalog.Prettify(i.Key.Replace("TRAINER_PIC_", "", StringComparison.Ordinal)) + " (imported on save)"))];
        SelectedClass = ClassOptions.FirstOrDefault(c => c.Key.EndsWith("_LEADER", StringComparison.Ordinal)) ?? ClassOptions.FirstOrDefault();
        SelectedLook = Looks.FirstOrDefault(l => l.Key == "OBJ_EVENT_GFX_COOLTRAINER_F") ?? Looks.FirstOrDefault();
        Milestones = Data.Rules?.LevelCaps.Where(c => !c.Milestone.StartsWith("FLAG_PS_BEAT_", StringComparison.Ordinal)).ToList() ?? [];
        SelectedMilestone = Milestones.FirstOrDefault();
        IsMajor = SelectedMilestone is not null;
        Refresh();
        RefreshPacks();
        shell.DataChanged += (_, _) => Refresh();
    }

    // ---- crossover packs ----
    public ObservableCollection<PackRowViewModel> Packs { get; } = [];
    public bool HasPacks => Packs.Count > 0;

    private void RefreshPacks()
    {
        Packs.Clear();
        var module = _shell.Session.Module;
        foreach (var pack in CrossoverPacks.All.Where(p => Maps.Any(m => m.Map == p.NeedsMap)))
        {
            var (pics, looks) = module.MissingSprites(Data, pack.Pics, pack.Looks);
            var needs = pics.Count + looks.Count == 0
                ? $"{pack.Entries.Count} battles. Everything they need is already in your game."
                : $"{pack.Entries.Count} battles. {pics.Count} battle sprite(s) and {looks.Count} overworld sprite(s) come from your own {pack.SourceProject} folder (you'll be asked for it).";
            Packs.Add(new PackRowViewModel(this, pack, needs));
        }

        OnPropertyChanged(nameof(HasPacks));
    }

    internal async Task AddPackAsync(CrossoverPack pack)
    {
        var module = _shell.Session.Module;
        var (pics, looks) = module.MissingSprites(Data, pack.Pics, pack.Looks);
        var imports = new List<StoryImport>();
        if (pics.Count + looks.Count > 0)
        {
            var folder = await _shell.Main.Dialogs.PickFolderAsync($"Pick your {pack.SourceProject} folder (the sprites come from it)");
            if (folder is null)
            {
                return;
            }

            if (module.CheckImportSource(folder, pics, looks) is { } problem)
            {
                Message = problem + $" Pick your {pack.SourceProject} folder.";
                return;
            }

            imports.AddRange(pics.Select(p => new StoryImport(StoryImport.Pic, p, folder)));
            imports.AddRange(looks.Select(l => new StoryImport(StoryImport.Look, l, folder)));
        }

        var data = Data;
        var limit = module.Limits.TrainerNameLength;
        var (change, notes, added) = await _shell.RunBusyAsync($"Adding {pack.Title} (building {pack.Entries.Count} teams)…",
            () => CrossoverPacks.Build(data, pack, map => module.StorySpots(data, map), imports, limit));
        if (change is null)
        {
            Message = notes.Count > 0 ? string.Join(" ", notes) : "Nothing was added.";
            return;
        }

        _shell.Execute(change);
        Refresh();
        RefreshPacks();
        Message = notes.Count > 0 ? string.Join(" ", notes) : null;
        _shell.Toast = $"{pack.Title}: {added} story battles added, with level caps and Champion Lab teams. One Undo takes them all out."
            + (imports.Count > 0 ? " The sprites are copied in when you save." : "");
    }

    private GameData Data => _shell.Session.Data;

    public ObservableCollection<StoryRowViewModel> Battles { get; } = [];
    public bool HasBattles => Battles.Count > 0;
    public IReadOnlyList<MapChoice> Maps { get; }
    public IReadOnlyList<LookChoice> Looks { get; }
    public IReadOnlyList<CatalogEntry> ClassOptions { get; }
    public IReadOnlyList<CatalogEntry> PicOptions { get; }
    public IReadOnlyList<LevelCap> Milestones { get; }
    public bool HasMilestones => Milestones.Count > 0;
    public IReadOnlyList<int> TeamSizes { get; } = [1, 2, 3, 4, 5, 6];

    [ObservableProperty]
    public partial string NewName { get; set; } = "";

    [ObservableProperty]
    public partial string NewTitle { get; set; } = "";

    [ObservableProperty]
    public partial CatalogEntry? SelectedClass { get; set; }

    [ObservableProperty]
    public partial CatalogEntry? SelectedPic { get; set; }

    [ObservableProperty]
    public partial MapChoice? SelectedMap { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<StorySpot> Spots { get; set; } = [];

    [ObservableProperty]
    public partial StorySpot? SelectedSpot { get; set; }

    [ObservableProperty]
    public partial LookChoice? SelectedLook { get; set; }

    [ObservableProperty]
    public partial bool IsMajor { get; set; }

    [ObservableProperty]
    public partial LevelCap? SelectedMilestone { get; set; }

    [ObservableProperty]
    public partial int TeamSize { get; set; } = 3;

    [ObservableProperty]
    public partial string Intro { get; set; } = "";

    [ObservableProperty]
    public partial string Defeat { get; set; } = "";

    [ObservableProperty]
    public partial string After { get; set; } = "";

    [ObservableProperty]
    public partial string? Message { get; set; }

    public bool NoSpots => SelectedMap is not null && Spots.Count == 0;

    partial void OnNewNameChanged(string value)
    {
        // a battle sprite with the trainer's name, if the game has one (Roxanne → TRAINER_PIC_LEADER_ROXANNE)
        var word = value.Trim().ToUpperInvariant().Replace(' ', '_').Replace("&", "AND", StringComparison.Ordinal);
        if (word.Length >= 3 && PicOptions.FirstOrDefault(p => p.Key.EndsWith("_" + word, StringComparison.Ordinal)) is { } pic)
        {
            SelectedPic = pic;
        }
    }

    partial void OnSelectedMapChanged(MapChoice? value) => SpotsLoading = LoadSpotsAsync(value);

    /// <summary>Finding spots reads the map's layout and tiles: done in the background.</summary>
    internal Task SpotsLoading { get; private set; } = Task.CompletedTask;

    private async Task LoadSpotsAsync(MapChoice? value)
    {
        var (module, data) = (_shell.Session.Module, Data);
        var spots = value is null ? [] : await _shell.RunBusyAsync($"Looking for entrances in {value.Name}…", () => module.StorySpots(data, value.Map));
        if (value != SelectedMap)
        {
            return; // another map was picked meanwhile
        }

        Spots = spots;
        SelectedSpot = Spots.FirstOrDefault(s => s.Entrance.Contains("Gym", StringComparison.Ordinal)) ?? Spots.FirstOrDefault();
        OnPropertyChanged(nameof(NoSpots));
    }

    private void Refresh()
    {
        Battles.Clear();
        foreach (var b in Data.Story?.Battles ?? [])
        {
            var name = Data.Trainers.FirstOrDefault(t => t.Key == b.TrainerKey)?.Name ?? b.Id;
            var before = b.Before is null ? null : Data.Rules?.LevelCaps.FirstOrDefault(c => c.Milestone == b.Before);
            var cap = Data.Rules?.LevelCaps.FirstOrDefault(c => c.Milestone == b.BeatFlag);
            var major = before is null ? "Ordinary battle" : $"Major battle before {before.Label}" + (cap is null ? "" : $" · level cap {cap.Level}");
            Battles.Add(new StoryRowViewModel(this, b, GameCatalog.Prettify(name), major));
        }

        OnPropertyChanged(nameof(HasBattles));
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        var name = NewName.Trim();
        if (name.Length == 0 || SelectedSpot is not { } spot || SelectedClass is null || SelectedPic is null || SelectedLook is null)
        {
            Message = "Give the trainer a name, then pick a class, a battle sprite, a map, an entrance and a look.";
            return;
        }

        var limit = _shell.Session.Module.Limits.TrainerNameLength;
        if (name.Length > limit)
        {
            Message = $"The name is too long: this game allows {limit} letters.";
            return;
        }

        if (Data.Story?.Battles.Any(b => b.Map == spot.Map && b.X == spot.X && b.Y == spot.Y) == true)
        {
            Message = "Another story battle already stands there. Pick another entrance.";
            return;
        }

        // the game's own style: FireRed writes trainer names in capitals, Emerald doesn't
        var capitals = Data.Trainers.Count(t => t.Name.Length > 1 && !t.Name.Any(char.IsLower)) > Data.Trainers.Count / 2;
        var sameClass = Data.Trainers.FirstOrDefault(t => t.TrainerClass == SelectedClass.Key && t.Party.Count > 0);
        var level = IsMajor && SelectedMilestone is { } m ? m.Level : Milestones.FirstOrDefault()?.Level ?? 10;
        var female = SelectedPic.Key.Contains("_F_", StringComparison.Ordinal) || SelectedPic.Key.EndsWith("_F", StringComparison.Ordinal)
            || (SelectedLook?.Key.EndsWith("_F", StringComparison.Ordinal) ?? false);
        var trainer = new Trainer
        {
            Id = Data.Trainers.Select(t => t.Id).DefaultIfEmpty(0).Max() + 1,
            Key = StoryBattles.NewTrainerKey(Data, name),
            Name = capitals ? name.ToUpperInvariant() : name,
            TrainerClass = SelectedClass.Key,
            Pic = SelectedPic.Key,
            Gender = female ? "Female" : "Male",
            Music = sameClass?.Music ?? "",
            AiFlags = sameClass?.AiFlags ?? [],
        };
        var lab = Data.Lab ?? new LabSettings();
        var (data, draft, options) = (Data, trainer, new TeamBuilder.Options(Difficulty.Normal, TeamSize, level));
        var team = await _shell.RunBusyAsync($"Building a team for {name}…", () => TeamBuilder.Suggest(data, lab, draft, options));
        trainer = trainer with { Party = team.Members.Select(x => x.Pokemon).ToList() };
        string? copiedFrom = null;
        if (trainer.Party.Count == 0
            && Data.Trainers.Where(t => t.TrainerClass == trainer.TrainerClass && t.Party.Count > 0 && !t.Key.StartsWith(StoryBattles.TrainerPrefix, StringComparison.Ordinal))
                .OrderBy(t => Math.Abs(t.Party.Max(p => p.Level) - level)).FirstOrDefault() is { } model)
        {
            // nothing fits the Champion Lab rules at this level: start from a similar trainer's team
            trainer = trainer with { Party = model.Party.Take(TeamSize).Select(p => p with { Level = level }).ToList() };
            copiedFrom = GameCatalog.Prettify(model.Name);
        }

        if (trainer.Party.Count == 0)
        {
            Message = "No team could be suggested. Check the Champion Lab rules for this trainer.";
            return;
        }

        var battle = new StoryBattle
        {
            TrainerKey = trainer.Key,
            Map = spot.Map,
            Entrance = spot.Entrance,
            X = spot.X,
            Y = spot.Y,
            Elevation = spot.Elevation,
            Facing = spot.Facing,
            Sight = spot.Sight,
            Look = SelectedLook!.Key,
            Intro = Intro.Trim().Length > 0 ? Intro.Trim() : StoryBattles.DefaultIntro(name, NewTitle.Trim()),
            Defeat = Defeat.Trim().Length > 0 ? Defeat.Trim() : StoryBattles.DefaultDefeat(name),
            After = After.Trim().Length > 0 ? After.Trim() : StoryBattles.DefaultAfter(name),
            Before = IsMajor ? SelectedMilestone?.Milestone : null,
        };
        _shell.Execute(StoryBattles.Add(Data, battle, trainer, level));
        Refresh();
        Message = null;
        _shell.Toast = $"{GameCatalog.Prettify(trainer.Name)} now guards {spot.Entrance}."
            + (copiedFrom is null ? "" : $" Their team starts as a copy of {copiedFrom}'s.")
            + " Edit the team on the Trainers page or redesign it in Champion Lab.";
        NewName = NewTitle = Intro = Defeat = After = "";
    }

    internal void EditTeam(string trainerKey) => _shell.GoToTrainer(trainerKey);

    internal void Remove(string trainerKey)
    {
        if (StoryBattles.Remove(Data, trainerKey) is { } change)
        {
            _shell.Execute(change);
            Refresh();
            _shell.Toast = change.Description + ". One Undo puts it back.";
        }
    }
}
