using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Guide;

namespace PokeStudio.App.ViewModels;

/// <summary>Creates the Player's Guide (HTML to read or print, plus Markdown) from the hack's own data.</summary>
public sealed partial class GuidePageViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;

    public GuidePageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Title = $"{shell.Session.Project.Manifest.Name} — Player's Guide";
    }

    public string GuideFolder => Path.Combine(_shell.Session.Project.Directory, "guide");
    public string HtmlFile => Path.Combine(GuideFolder, "players-guide.html");
    public string MarkdownFile => Path.Combine(GuideFolder, "players-guide.md");

    [ObservableProperty]
    public partial string Title { get; set; }

    [ObservableProperty]
    public partial bool StartOptions { get; set; } = true;

    [ObservableProperty]
    public partial bool LevelCaps { get; set; } = true;

    [ObservableProperty]
    public partial bool Bosses { get; set; } = true;

    [ObservableProperty]
    public partial bool OtherTrainers { get; set; }

    [ObservableProperty]
    public partial bool Wild { get; set; } = true;

    [ObservableProperty]
    public partial bool Shops { get; set; } = true;

    [ObservableProperty]
    public partial bool Evolutions { get; set; } = true;

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial bool HasGuide { get; set; }

    public string Contents
    {
        get
        {
            var data = _shell.Session.Data;
            var lab = data.Lab ?? new LabSettings();
            var bosses = data.Trainers.Count(t => Lab.DifficultyOf(t) == Difficulty.Normal && t.Party.Count > 0 && Lab.GroupOf(data, lab, t) != TrainerGroup.Regular);
            var adaptive = data.Trainers.Count(t => t.HasAdaptiveSlot);
            return $"{bosses} important battles ({adaptive} with an adaptive slot), {data.Encounters.Count} wild areas, {data.Marts.Count} shops, {data.Rules?.LevelCaps.Count ?? 0} level caps.";
        }
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var data = _shell.Session.Data;
        var options = new GuideOptions
        {
            Title = Title.Trim(),
            StartOptions = StartOptions,
            LevelCaps = LevelCaps,
            Bosses = Bosses,
            OtherTrainers = OtherTrainers,
            Wild = Wild,
            Shops = Shops,
            Evolutions = Evolutions,
        };
        try
        {
            var module = _shell.Session.Module;
            var doc = await _shell.RunBusyAsync("Writing the Player's Guide…", () => PlayersGuide.Build(data, data.Lab ?? new LabSettings(), module.TrainerPlaces(data), options));
            Directory.CreateDirectory(GuideFolder);
            await File.WriteAllTextAsync(HtmlFile, PlayersGuide.ToHtml(doc));
            await File.WriteAllTextAsync(MarkdownFile, PlayersGuide.ToMarkdown(doc));
            HasGuide = true;
            Status = $"Guide created with {doc.Contents.Count} sections. Open players-guide.html in any web browser to read or print it.";
        }
        catch (IOException ex)
        {
            Status = "Couldn't write the guide: " + ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            Status = "Couldn't write the guide: " + ex.Message;
        }
    }

    [RelayCommand]
    private void ShowFolder()
    {
        Directory.CreateDirectory(GuideFolder);
        _shell.Main.ShowInFolder(GuideFolder);
    }
}
