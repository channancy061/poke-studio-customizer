using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Settings;

namespace PokeStudio.App.Tests;

public class StoryUiTests
{
    private static async Task SaveAsync(ShellViewModel shell)
    {
        await shell.SaveCommand.ExecuteAsync(null);
        if (shell.IsValidationOpen)
        {
            Assert.False(shell.HasErrors, string.Join(" | ", shell.Issues.Where(i => i.IsError).Select(i => i.Text)));
            await shell.SaveAnywayCommand.ExecuteAsync(null);
        }
    }

    [AvaloniaFact]
    public async Task A_story_battle_guards_the_gym_door_and_gets_its_own_level_cap()
    {
        using var env = new TestEnvironment();
        var root = Path.Combine(env.Root, "my-hack");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(root));
        var shell = Assert.IsType<ShellViewModel>(env.Main.CurrentView);
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.StoryBattles);
        var page = Assert.IsType<StoryBattlesPageViewModel>(shell.CurrentPage);
        Assert.False(page.HasBattles);

        // the town's gym door: a spot that sees the tile in front of it
        page.SelectedMap = Assert.Single(page.Maps, m => m.Map == "MeadowTown");
        await page.SpotsLoading;
        var spot = Assert.Single(page.Spots);
        Assert.Equal("Meadow Town Gym", spot.Entrance);
        Assert.Equal((5, 2), (spot.FrontX, spot.FrontY));

        // the name picks the matching battle sprite; a major battle before Gym 1
        page.NewName = "Fern";
        Assert.Equal("TRAINER_PIC_LEADER_FERN", page.SelectedPic!.Key);
        Assert.True(page.IsMajor);
        Assert.Equal("FLAG_BADGE01_GET", page.SelectedMilestone!.Milestone);
        await page.AddCommand.ExecuteAsync(null);
        Assert.Null(page.Message);

        var row = Assert.Single(page.Battles);
        Assert.Contains("Major battle before Gym 1", row.Major, StringComparison.Ordinal);
        var key = row.Battle.TrainerKey;
        var trainer = Assert.Single(shell.Session.Data.Trainers, t => t.Key == key);
        Assert.NotEmpty(trainer.Party);
        Assert.Equal("FLAG_PS_BEAT_FERN", shell.Session.Data.Rules!.LevelCaps[0].Milestone);

        await SaveAsync(shell);
        Assert.Contains($"    {key}, // Poke Studio story battle", File.ReadAllText(Path.Combine(root, "include", "constants", "opponents.h")), StringComparison.Ordinal);
        Assert.Contains($"=== {key} ===", File.ReadAllText(Path.Combine(root, "src", "data", "trainers.party")), StringComparison.Ordinal);
        Assert.Contains("{FLAG_PS_BEAT_FERN, ", File.ReadAllText(Path.Combine(root, "src", "caps.c")), StringComparison.Ordinal);
        Assert.Contains("PokeStudio_Story_FERN", File.ReadAllText(Path.Combine(root, "data", "maps", "MeadowTown", "map.json")), StringComparison.Ordinal);
        Assert.Contains($"trainerbattle_single {key}", File.ReadAllText(Path.Combine(root, "data", "scripts", "poke_studio_story.inc")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(shell.Session.Project.DataDir, ProjectSession.StoryFileName)));

        // removing it takes everything out again on the next save
        page = Assert.IsType<StoryBattlesPageViewModel>(shell.CurrentPage);
        Assert.Single(page.Battles).RemoveCommand.Execute(null);
        Assert.DoesNotContain(shell.Session.Data.Trainers, t => t.Key == key);
        await SaveAsync(shell);
        Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(root, "include", "constants", "opponents.h")), StringComparison.Ordinal);
        Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(root, "src", "data", "trainers.party")), StringComparison.Ordinal);
        Assert.DoesNotContain("FLAG_PS_BEAT_FERN", File.ReadAllText(Path.Combine(root, "src", "caps.c")), StringComparison.Ordinal);
        Assert.DoesNotContain("PokeStudio_Story_", File.ReadAllText(Path.Combine(root, "data", "maps", "MeadowTown", "map.json")), StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Light)]
    public async Task Capture_story_battles_screen(ThemePreference theme)
    {
        using var env = new TestEnvironment();
        env.Main.Theme.Apply(theme);
        var root = Path.Combine(env.Root, "my-hack");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(root));
        var shell = Assert.IsType<ShellViewModel>(env.Main.CurrentView);
        var window = new MainWindow { DataContext = env.Main, Width = 1440, Height = 900 };
        window.Show();
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.StoryBattles);
        var page = Assert.IsType<StoryBattlesPageViewModel>(shell.CurrentPage);
        page.SelectedMap = page.Maps[0];
        await page.SpotsLoading;
        page.NewName = "Fern";
        page.NewTitle = "Gym Leader of Meadow Town";
        await page.AddCommand.ExecuteAsync(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "screens");
        Directory.CreateDirectory(outDir);
        window.CaptureRenderedFrame()!.Save(Path.Combine(outDir, $"story-battles-{theme}.png"));

        // the "Working…" overlay
        shell.BusyText = "Building a team for Fern…";
        shell.IsBusy = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(outDir, $"busy-{theme}.png"));
        shell.IsBusy = false;
        window.Close();
    }

    [AvaloniaFact]
    public async Task Heavy_work_shows_the_loader_until_it_is_done()
    {
        using var env = new TestEnvironment();
        var root = Path.Combine(env.Root, "my-hack");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(root));
        var shell = Assert.IsType<ShellViewModel>(env.Main.CurrentView);

        var seen = false;
        var result = await shell.RunBusyAsync("Testing…", () =>
        {
            seen = shell.IsBusy;
            return 42;
        });
        Assert.Equal(42, result);
        Assert.True(seen);
        Assert.Equal("Testing…", shell.BusyText);
        Assert.False(shell.IsBusy);

        // a failure still takes the loader down
        await Assert.ThrowsAsync<InvalidOperationException>(() => shell.RunBusyAsync<int>("Failing…", () => throw new InvalidOperationException()));
        Assert.False(shell.IsBusy);
    }
}
