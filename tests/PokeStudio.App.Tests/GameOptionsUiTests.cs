using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;
using PokeStudio.Core.GameOptions;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Settings;

namespace PokeStudio.App.Tests;

public class GameOptionsUiTests
{
    private static async Task<(ShellViewModel Shell, string Root)> OpenFixtureAsync(TestEnvironment env)
    {
        var root = Path.Combine(env.Root, "my-hack");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(root));
        return (Assert.IsType<ShellViewModel>(env.Main.CurrentView), root);
    }

    private static async Task SaveAsync(ShellViewModel shell)
    {
        await shell.SaveCommand.ExecuteAsync(null);
        if (shell.IsValidationOpen)
        {
            Assert.False(shell.HasErrors, string.Join(" | ", shell.Issues.Where(i => i.IsError).Select(i => i.Text)));
            await shell.SaveAnywayCommand.ExecuteAsync(null);
        }
    }

    private static GameOptionsPageViewModel Open(ShellViewModel shell)
    {
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.GameOptions);
        return Assert.IsType<GameOptionsPageViewModel>(shell.CurrentPage);
    }

    [AvaloniaFact]
    public async Task Options_are_undoable_saved_with_the_project_and_written_into_the_game()
    {
        using var env = new TestEnvironment();
        var (shell, root) = await OpenFixtureAsync(env);
        var page = Open(shell);
        Assert.Equal(["Nothing is added: the game starts as usual."], page.Preview);

        page.Difficulty.Mode = page.Difficulty.Modes.Single(m => m.Mode == OptionMode.PlayerChooses);
        Assert.True(page.AsksDifficulty);
        page.DefaultDifficulty = page.DifficultyChoices[2];
        var cap = page.Rows.Single(r => r.Title == "Hard level cap");
        cap.Mode = cap.Modes.Single(m => m.Mode == OptionMode.AlwaysOn);
        Assert.Contains(page.Preview, l => l.StartsWith("Which difficulty? Easy / Normal / Hard / Extra Hard (starts on Hard)", StringComparison.Ordinal));
        Assert.Contains(page.Preview, l => l.Contains("hard level cap", StringComparison.Ordinal));

        shell.UndoCommand.Execute(null);
        Assert.Equal(OptionMode.NotUsed, shell.Session.Data.GameOptions!.LevelCap);
        Assert.Equal(OptionMode.NotUsed, cap.Mode!.Mode);
        shell.RedoCommand.Execute(null);
        Assert.Equal(OptionMode.AlwaysOn, cap.Mode!.Mode);

        await SaveAsync(shell);
        var script = File.ReadAllText(Path.Combine(root, "data", "scripts", "poke_studio_options.inc"));
        Assert.Contains("dynmultichoice 0, 0, TRUE, 4, 2,", script, StringComparison.Ordinal);
        Assert.Contains("PokeStudio_OnFrameTable", File.ReadAllText(Path.Combine(root, "data", "maps", "InsideOfTruck", "scripts.inc")), StringComparison.Ordinal);
        Assert.Contains("DIFFICULTY_EXTRA_HARD", File.ReadAllText(Path.Combine(root, "include", "constants", "difficulty.h")), StringComparison.Ordinal);
        Assert.Contains("FLAG_PS_LEVEL_CAP_OFF", File.ReadAllText(Path.Combine(root, "src", "caps.c")), StringComparison.Ordinal);

        // the settings come back when the project is opened again
        var dataDir = shell.Session.Project.DataDir;
        Assert.True(File.Exists(Path.Combine(dataDir, ProjectSession.GameOptionsFileName)));
        var reopened = await env.Main.OpenProjectFolderAsync(shell.Session.Project.Directory);
        Assert.Null(reopened);
        var again = Assert.IsType<ShellViewModel>(env.Main.CurrentView);
        Assert.Equal(DifficultyChoice.Hard, again.Session.Data.GameOptions!.DefaultDifficulty);
        Assert.Equal(OptionMode.AlwaysOn, again.Session.Data.GameOptions.LevelCap);
    }

    [AvaloniaTheory]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Light)]
    public async Task Capture_game_options_screen(ThemePreference theme)
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "screens");
        Directory.CreateDirectory(outDir);
        using var env = new TestEnvironment();
        env.Main.Theme.Apply(theme);
        var window = new MainWindow { DataContext = env.Main, Width = 1440, Height = 900 };
        window.Show();
        var (shell, _) = await OpenFixtureAsync(env);
        shell.IsChangesOpen = false;
        var page = Open(shell);
        foreach (var row in page.Rows)
        {
            row.Mode = row.Modes.Single(m => m.Mode == OptionMode.PlayerChooses);
        }

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(outDir, $"game-options-{theme}.png"));
        window.Close();
    }
}
