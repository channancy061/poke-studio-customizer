using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;
using PokeStudio.Core.GameOptions;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Settings;

namespace PokeStudio.App.Tests;

public class GameCodeUiTests
{
    private static async Task<(ShellViewModel Shell, string Root)> OpenFixtureAsync(TestEnvironment env)
    {
        var root = Path.Combine(env.Root, "my-hack");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(root));
        return (Assert.IsType<ShellViewModel>(env.Main.CurrentView), root);
    }

    private static GameOptionsPageViewModel Options(ShellViewModel shell)
    {
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.GameOptions);
        return Assert.IsType<GameOptionsPageViewModel>(shell.CurrentPage);
    }

    [AvaloniaFact]
    public async Task Nuzlocke_rules_and_the_center_helper_are_set_per_option_and_undoable()
    {
        using var env = new TestEnvironment();
        var (shell, root) = await OpenFixtureAsync(env);
        var page = Options(shell);
        Assert.False(page.NuzlockeShown);
        page.Nuzlocke.Mode = page.Nuzlocke.Modes.Single(m => m.Mode == OptionMode.PlayerChooses);
        Assert.True(page.NuzlockeShown);
        var eevee = page.NuzlockeRules.Single(r => r.Title == "Eevee clause");
        eevee.Mode = eevee.Modes.Single(m => m.Mode == OptionMode.AlwaysOn);
        Assert.Equal(OptionMode.AlwaysOn, shell.Session.Data.GameOptions!.NuzEeveeException);
        Assert.Contains(page.Preview, l => l.StartsWith("Play a Nuzlocke? Yes / No. If yes: always first encounter only, fainted pokémon are gone, eevee clause.", StringComparison.Ordinal));

        page.HelperEnabled = true;
        page.NaturePrice = 2500;
        Assert.Contains(page.Preview, l => l.Contains("change nature (¥2500)", StringComparison.Ordinal) && l.Contains("perfect IVs (free)", StringComparison.Ordinal));
        shell.UndoCommand.Execute(null);
        Assert.Equal(0, shell.Session.Data.GameOptions!.CenterHelper.NaturePrice);
        shell.RedoCommand.Execute(null);

        await shell.SaveCommand.ExecuteAsync(null);
        if (shell.IsValidationOpen)
        {
            await shell.SaveAnywayCommand.ExecuteAsync(null);
        }

        var script = File.ReadAllText(Path.Combine(root, "data", "scripts", "poke_studio_options.inc"));
        Assert.Contains("PokeStudio_EventScript_CenterHelper::", script, StringComparison.Ordinal);
        Assert.Contains("\tsetflag FLAG_PS_NUZ_EEVEE\n", script, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "src", "poke_studio.c")));
    }

    [AvaloniaFact]
    public async Task A_gym_leader_gets_an_adaptive_slot_in_the_trainer_editor()
    {
        using var env = new TestEnvironment();
        var (shell, root) = await OpenFixtureAsync(env);
        shell.GoToTrainer("TRAINER_LEADER_FERN");
        var editor = Assert.IsType<TrainersPageViewModel>(shell.CurrentPage).Editor!;
        Assert.False(editor.AdaptiveAllowed); // two Pokemon: too small a team to swap one
        editor.AddPokemonCommand.Execute(null);
        Assert.True(editor.AdaptiveAllowed);
        Assert.True(editor.NoAdaptive);
        editor.SuggestCandidatesCommand.Execute(null);
        // the test project is tiny: everything else evolves into Fern's own Pokemon by her level, so nothing fits
        Assert.Equal("No candidates fit this boss's theme and rules.", shell.Toast);
        var fern = shell.Session.Data.Trainers.Single(t => t.Key == "TRAINER_LEADER_FERN");
        shell.Execute(Core.ChampionLab.AdaptiveSlots.Set(shell.Session.Data, fern, 1, [new Core.Models.TrainerPokemon { Species = "SPECIES_GHOSTLING", Level = 22 }], "test")!);
        Assert.True(editor.HasAdaptive);
        Assert.StartsWith("Flexible: ", editor.AdaptiveSummary, StringComparison.Ordinal);
        Assert.EndsWith("(Switch it on in Game options for it to work in the game.)", editor.AdaptiveSummary, StringComparison.Ordinal);

        await shell.SaveCommand.ExecuteAsync(null);
        if (shell.IsValidationOpen)
        {
            await shell.SaveAnywayCommand.ExecuteAsync(null);
        }

        var party = File.ReadAllText(Path.Combine(root, "src", "data", "trainers.party"));
        Assert.Contains("Pool Pick Functions: Poke Studio Adaptive", party, StringComparison.Ordinal);
        editor.RemoveAdaptiveCommand.Execute(null);
        Assert.True(editor.NoAdaptive);
    }

    [AvaloniaTheory]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Light)]
    public async Task Capture_game_code_screens(ThemePreference theme)
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "screens");
        Directory.CreateDirectory(outDir);
        using var env = new TestEnvironment();
        env.Main.Theme.Apply(theme);
        var window = new MainWindow { DataContext = env.Main, Width = 1440, Height = 1400 };
        window.Show();
        void Save(string name)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(outDir, $"{name}-{theme}.png"));
        }

        var (shell, _) = await OpenFixtureAsync(env);
        shell.IsChangesOpen = false;
        var page = Options(shell);
        page.Nuzlocke.Mode = page.Nuzlocke.Modes.Single(m => m.Mode == OptionMode.PlayerChooses);
        page.HelperEnabled = true;
        Save("game-options-nuzlocke");

        shell.GoToTrainer("TRAINER_LEADER_FERN");
        var fern = shell.Session.Data.Trainers.Single(t => t.Key == "TRAINER_LEADER_FERN");
        shell.Execute(Core.ChampionLab.AdaptiveSlots.Set(shell.Session.Data, fern, 1, [new Core.Models.TrainerPokemon { Species = "SPECIES_GHOSTLING", Level = 22 }], "test")!);
        Save("adaptive-slot");
        window.Close();
    }
}
