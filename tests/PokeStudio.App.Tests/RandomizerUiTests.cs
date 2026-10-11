using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Randomizer;
using PokeStudio.Core.Settings;

namespace PokeStudio.App.Tests;

public class RandomizerUiTests
{
    [AvaloniaTheory]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Light)]
    public async Task Randomizing_is_one_undo_step(ThemePreference theme)
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "screens");
        Directory.CreateDirectory(outDir);
        using var env = new TestEnvironment();
        env.Main.Theme.Apply(theme);
        var window = new MainWindow { DataContext = env.Main, Width = 1440, Height = 900 };
        window.Show();
        var root = Path.Combine(env.Root, "my-hack");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(root));
        var shell = Assert.IsType<ShellViewModel>(env.Main.CurrentView);
        shell.IsChangesOpen = false;
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Randomizer);
        var page = Assert.IsType<RandomizerPageViewModel>(shell.CurrentPage);
        Assert.Matches("^[0-9]{6}$", page.Seed);

        var before = shell.Session.Data.Trainers.SelectMany(t => t.Party).Select(p => p.Species).ToList();
        page.Seed = "777";
        page.SelectedWild = page.WildChoices.Single(c => c.Value == WildRandomizing.Random);
        page.SelectedTrainers = page.TrainerChoices.Single(c => c.Value == TrainerRandomizing.SimilarStrength);
        page.Abilities = true;
        page.OfficialOnly = false; // the mini project's Pokémon are invented
        await page.RandomizeCommand.ExecuteAsync(null);
        Assert.Contains("seed 777", page.Status, StringComparison.Ordinal);
        Assert.Equal(1, shell.UnsavedCount);

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(outDir, $"randomizer-{theme}.png"));

        shell.UndoCommand.Execute(null);
        Assert.Equal(before, shell.Session.Data.Trainers.SelectMany(t => t.Party).Select(p => p.Species));
        window.Close();
    }
}
