using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;
using PokeStudio.Core.Build;
using PokeStudio.Core.Models;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Settings;
using PokeStudio.Core.Tests;

namespace PokeStudio.App.Tests;

public class BuildUiTests
{
    private static async Task<(ShellViewModel Shell, string Root)> OpenFixtureAsync(TestEnvironment env)
    {
        var root = Path.Combine(env.Root, "my-hack");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(root));
        return (Assert.IsType<ShellViewModel>(env.Main.CurrentView), root);
    }

    private static FakeProcessRunner NoWsl() => new() { Handler = (_, _, _) => throw new ProgramNotFoundException("wsl.exe") };

    private static async Task<BuildPageViewModel> OpenBuildPageAsync(ShellViewModel shell)
    {
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Build);
        var page = Assert.IsType<BuildPageViewModel>(shell.CurrentPage);
        await page.Tools.CheckAsync();
        return page;
    }

    [AvaloniaFact]
    public async Task Without_wsl_build_explains_why_and_everything_else_still_works()
    {
        using var env = new TestEnvironment(NoWsl());
        var (shell, _) = await OpenFixtureAsync(env);

        var page = await OpenBuildPageAsync(shell);

        Assert.True(page.CanBuildGame);
        Assert.True(page.IsBuildBlocked);
        Assert.True(page.Tools.NeedsWsl);
        Assert.Contains("needs WSL", page.BuildBlockedReason, StringComparison.Ordinal);
        Assert.False(page.BuildCommand.CanExecute(null));
        var row = Assert.Single(page.Tools.Rows);
        Assert.False(row.CanAutoFix); // the user turns WSL on; the app doesn't change Windows
        Assert.Contains("wsl --install", row.HowToFix, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Build_saves_backs_up_builds_and_lists_the_result()
    {
        string? output = null;
        var runner = FakeProcessRunner.AllReady(make: _ =>
        {
            File.WriteAllBytes(output!, [1, 2, 3]);
            return new ProcessResult(0, "ok");
        });
        using var env = new TestEnvironment(runner);
        var (shell, root) = await OpenFixtureAsync(env);
        output = Path.Combine(root, "pokeemerald.gba");

        var page = await OpenBuildPageAsync(shell);
        Assert.True(page.Tools.AllReady);
        await page.BuildCommand.ExecuteAsync(null);

        Assert.True(page.ResultIsSuccess, page.Result);
        var build = Assert.Single(page.History);
        Assert.True(build.IsLatest);
        Assert.EndsWith("_pokeemerald.gba", build.Name, StringComparison.Ordinal);
        Assert.Contains(await shell.Session.ListBackupsAsync(), b => b.Reason == Core.Backups.BackupReason.BeforeBuild);
        Assert.Contains(runner.Ran, s => s.Arguments.Contains("--cd") && s.Arguments.Contains(root));
    }

    [AvaloniaFact]
    public async Task Build_errors_appear_as_plain_english_problems()
    {
        var runner = FakeProcessRunner.AllReady(make: _ => new ProcessResult(2, "src/x.c:3:1: error: 'MOVE_ZAPP' undeclared here"));
        using var env = new TestEnvironment(runner);
        var (shell, _) = await OpenFixtureAsync(env);
        var page = await OpenBuildPageAsync(shell);

        await page.BuildCommand.ExecuteAsync(null);

        Assert.True(page.HasResultError);
        var problem = Assert.Single(page.Problems);
        Assert.Equal("“MOVE_ZAPP” doesn't exist", problem.Title);
        Assert.Equal("src/x.c:3", problem.Location);
        Assert.Empty(page.History);
    }

    [AvaloniaFact]
    public async Task Test_my_game_opens_the_latest_build_in_the_chosen_emulator()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        var emulator = Path.Combine(env.Root, "mGBA", "mGBA.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(emulator)!);
        File.WriteAllBytes(emulator, [0]);

        shell.TestGameCommand.Execute(null); // no build yet
        Assert.IsType<BuildPageViewModel>(shell.CurrentPage);
        Assert.Contains("Build your game first", shell.Toast, StringComparison.Ordinal);

        var game = Path.Combine(env.Root, "pokeemerald.gba");
        File.WriteAllBytes(game, [0]);
        env.Main.Builds.Keep(game, shell.Session.Project.Directory);
        var page = Assert.IsType<BuildPageViewModel>(shell.CurrentPage);
        page.Emulator.Path = emulator;
        Assert.True(page.Emulator.IsReady);

        shell.TestGameCommand.Execute(null);

        var launch = Assert.Single(env.Runner.Launched);
        Assert.Equal(emulator, launch.FileName);
        Assert.EndsWith("_pokeemerald.gba", Assert.Single(launch.Arguments), StringComparison.Ordinal);
        Assert.Contains("in mGBA", shell.Toast, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Old_vba_still_launches_but_warns()
    {
        using var env = new TestEnvironment();
        var vba = Path.Combine(env.Root, "vba", "VisualBoyAdvance.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(vba)!);
        File.WriteAllBytes(vba, [0]);
        var game = Path.Combine(env.Root, "game.gba");
        File.WriteAllBytes(game, [0]);
        await env.Main.Settings.UpdateAsync(s => s with { EmulatorPaths = new Dictionary<GamePlatform, string> { [GamePlatform.Gba] = vba } });

        var (ok, message) = env.Main.LaunchGame(GamePlatform.Gba, game);

        Assert.True(ok);
        Assert.Contains("very old", message, StringComparison.Ordinal);
        Assert.Single(env.Runner.Launched);
    }

    [AvaloniaFact]
    public async Task Demo_game_cannot_be_built_but_can_be_tested()
    {
        using var env = new TestEnvironment();
        Assert.Null(await env.Main.CreateProjectFromGameAsync(HubViewModel.DemoGamePath));
        var shell = Assert.IsType<ShellViewModel>(env.Main.CurrentView);

        shell.Navigate(NavKey.Build);

        var page = Assert.IsType<BuildPageViewModel>(shell.CurrentPage);
        Assert.False(page.CanBuildGame);
        Assert.Contains("can't be built", page.CannotBuildText, StringComparison.Ordinal);
        Assert.Empty(env.Runner.Ran); // no tool checks for a game that can't be built
    }

    [AvaloniaFact]
    public async Task Setup_wizard_walks_through_four_steps_and_remembers_choices()
    {
        using var env = new TestEnvironment(NoWsl(), firstRun: true);
        env.Main.ShowSetup(searchNow: false);
        var wizard = Assert.IsType<SetupWizardViewModel>(env.Main.SetupWizard);
        Assert.True(env.Main.IsSetupOpen);
        Assert.Equal("Step 1 of 4", wizard.StepText);

        // emulators: the user's own folders are found and offered, not chosen for them
        var games = Path.Combine(env.Root, "Games");
        var vba = Path.Combine(games, "Pokemon", "GBA", "vbalink180b0", "VisualBoyAdvance.exe");
        var citra = Path.Combine(games, "Pokemon", "3DS", "Citra", "nightly", "citra-qt.exe");
        foreach (var f in new[] { vba, citra })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllBytes(f, [0]);
        }

        await wizard.NextCommand.ExecuteAsync(null);
        Assert.True(wizard.IsEmulators);
        await wizard.SearchAsync([games]);
        var gba = wizard.Slots[0];
        Assert.Null(gba.Path);
        Assert.Equal(vba, Assert.Single(gba.Found).Path);
        Assert.Contains("mGBA", gba.Suggestion, StringComparison.Ordinal);
        gba.UseCommand.Execute(gba.Found[0]);
        Assert.True(gba.IsWarning); // VBA 1.8 is old
        Assert.Equal(vba, env.Main.Settings.Current.EmulatorPaths[GamePlatform.Gba]);
        Assert.Equal(citra, Assert.Single(wizard.Slots[2].Found).Path);

        await wizard.NextCommand.ExecuteAsync(null);
        Assert.True(wizard.IsTools);
        Assert.True(wizard.Tools.NeedsWsl);

        await wizard.NextCommand.ExecuteAsync(null);
        Assert.True(wizard.IsBackups);
        wizard.BuildsKeepCount = 3;
        Assert.Equal(3, env.Main.Settings.Current.BuildsKeepCount);

        await wizard.FinishCommand.ExecuteAsync(null);
        Assert.False(env.Main.IsSetupOpen);
        Assert.True(env.Main.Settings.Current.FirstRunCompleted);
    }

    [AvaloniaFact]
    public async Task Guided_tour_runs_once_and_can_be_replayed()
    {
        using var env = new TestEnvironment(firstRun: true);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(HubViewModel.DemoGamePath));
        var shell = Assert.IsType<ShellViewModel>(env.Main.CurrentView);

        Assert.True(shell.IsTourOpen);
        Assert.True(shell.IsTourNav);
        Assert.Equal("1 of 4", shell.TourStepText);
        shell.IsChangesOpen = false;
        await shell.TourNextCommand.ExecuteAsync(null);
        await shell.TourNextCommand.ExecuteAsync(null);
        Assert.True(shell.IsTourChanges);
        Assert.True(shell.IsChangesOpen); // opened so the step can point at it
        await shell.TourNextCommand.ExecuteAsync(null);
        Assert.Equal("Got it", shell.TourNextText);
        await shell.TourNextCommand.ExecuteAsync(null);

        Assert.False(shell.IsTourOpen);
        Assert.True(env.Main.Settings.Current.TourCompleted);

        shell.Navigate(NavKey.About);
        var about = Assert.IsType<AboutPageViewModel>(shell.CurrentPage);
        about.ReplayTourCommand.Execute(null);
        Assert.True(shell.IsTourOpen);
    }

    [AvaloniaTheory]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Light)]
    public async Task Capture_build_setup_and_tour_screens(ThemePreference theme)
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "screens");
        Directory.CreateDirectory(outDir);
        using var env = new TestEnvironment(NoWsl());
        env.Main.Theme.Apply(theme);
        var window = new MainWindow { DataContext = env.Main, Width = 1440, Height = 900 };
        window.Show();
        void Save(string name)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(outDir, $"{name}-{theme}.png"));
        }

        var (shell, _) = await OpenFixtureAsync(env);
        await OpenBuildPageAsync(shell);
        Save("build-needs-wsl");

        shell.StartTour();
        Save("tour-step1");
        await shell.EndTourCommand.ExecuteAsync(null);

        env.Main.ShowSetup(searchNow: false);
        var wizard = env.Main.SetupWizard!;
        var emu = Path.Combine(env.Root, "Games", "Pokemon", "GBA", "vbalink180b0", "VisualBoyAdvance.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(emu)!);
        File.WriteAllBytes(emu, [0]);
        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.SearchAsync([Path.Combine(env.Root, "Games")]);
        Save("setup-emulators");
        await wizard.NextCommand.ExecuteAsync(null);
        Save("setup-build-tools");
        window.Close();
    }
}
