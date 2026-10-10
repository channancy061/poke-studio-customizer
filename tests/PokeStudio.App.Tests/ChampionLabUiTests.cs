using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;
using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Models;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Settings;

namespace PokeStudio.App.Tests;

public class ChampionLabUiTests
{
    private static async Task<(ShellViewModel Shell, string Root)> OpenFixtureAsync(TestEnvironment env)
    {
        var root = Path.Combine(env.Root, "my-hack");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(root));
        return (Assert.IsType<ShellViewModel>(env.Main.CurrentView), root);
    }

    private static ChampionLabPageViewModel OpenLab(ShellViewModel shell)
    {
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.ChampionLab);
        return Assert.IsType<ChampionLabPageViewModel>(shell.CurrentPage);
    }

    [AvaloniaFact]
    public async Task Groups_themes_and_rules_are_undoable_and_saved_with_the_project()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        var lab = OpenLab(shell);

        var fern = Assert.Single(lab.Trainers); // only important trainers by default
        Assert.Equal("Leader FERN", fern.Title);
        Assert.Equal(TrainerGroup.EarlyGym, fern.Group.Group);
        Assert.Equal("Team is mostly Grass", fern.Suggested);

        lab.ApplySuggestedThemesCommand.Execute(null);
        Assert.Equal("TYPE_GRASS", shell.Session.Data.Lab!.Themes["TRAINER_LEADER_FERN"].Type);

        lab.ShowAllTrainers = true;
        var rick = lab.Trainers.Single(t => t.Title == "Hiker RICK");
        rick.Group = lab.GroupOptions.Single(g => g.Group == TrainerGroup.Rival);
        Assert.Equal(TrainerGroup.Rival, shell.Session.Data.Lab!.GroupOverrides["TRAINER_RICK_1"]);

        lab.IsRulesTab = true;
        lab.Difficulty = lab.DifficultyOptions.Single(d => d.Difficulty == Difficulty.Hard);
        var champion = lab.Rules.Single(r => r.Group == "Champion");
        champion.MaxRestricted = 2;
        Assert.Equal(2, shell.Session.Data.Lab!.RulesFor(TrainerGroup.Champion, Difficulty.Hard).MaxRestricted);
        Assert.Equal(3, shell.Changes.Count);

        shell.UndoCommand.Execute(null);
        Assert.Equal(1, shell.Session.Data.Lab!.RulesFor(TrainerGroup.Champion, Difficulty.Hard).MaxRestricted);
        shell.RedoCommand.Execute(null);

        await shell.SaveCommand.ExecuteAsync(null);
        if (shell.IsValidationOpen)
        {
            Assert.False(shell.HasErrors);
            await shell.SaveAnywayCommand.ExecuteAsync(null);
        }

        var file = Path.Combine(shell.Session.Project.DataDir, "champion-lab.json");
        Assert.True(File.Exists(file)); // saved with the Poke Studio project, not in the game files

        var projectDir = shell.Session.Project.Directory;
        env.Main.GoHome();
        Assert.Null(await env.Main.OpenProjectFolderAsync(projectDir));
        var reopened = Assert.IsType<ShellViewModel>(env.Main.CurrentView);
        Assert.Equal("TYPE_GRASS", reopened.Session.Data.Lab!.Themes["TRAINER_LEADER_FERN"].Type);
        Assert.Equal(2, reopened.Session.Data.Lab.RulesFor(TrainerGroup.Champion, Difficulty.Hard).MaxRestricted);
    }

    [AvaloniaFact]
    public async Task Rule_breaks_show_as_check_this_items_and_lead_to_the_trainer()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        shell.GoToSpecies("SPECIES_BLOOMTLE");
        var editor = Assert.IsType<PokemonEditorViewModel>(((PokemonPageViewModel)shell.CurrentPage!).Editor);
        editor.IsRestrictedLegendary = true;

        await shell.SaveCommand.ExecuteAsync(null);

        // Fern (a gym) and Mia (a regular trainer) both use Bloomtle; neither group allows restricted legendaries
        Assert.Equal(2, shell.Issues.Count(i => i.Text.Contains("restricted legendary", StringComparison.Ordinal)));
        var issue = shell.Issues.Single(i => i.Text.Contains("FERN", StringComparison.Ordinal) && i.Text.Contains("restricted legendary", StringComparison.Ordinal));
        Assert.True(issue.IsWarning);
        issue.GoCommand.Execute(null);
        Assert.Equal("TRAINER_LEADER_FERN", Assert.IsType<TrainersPageViewModel>(shell.CurrentPage).SelectedItem!.Key);
    }

    [AvaloniaFact]
    public async Task Story_locks_and_scouting()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        shell.GoToSpecies("SPECIES_SPROUTLE_FROST");
        ((PokemonEditorViewModel)((PokemonPageViewModel)shell.CurrentPage!).Editor!).IsSubLegendary = true;
        var lab = OpenLab(shell);

        lab.IsRulesTab = true;
        lab.LockSpecies = lab.LegendaryOptions.Single(o => o.Key == "SPECIES_SPROUTLE_FROST");
        lab.LockMilestone = 3;
        lab.AddStoryLockCommand.Execute(null);
        Assert.Equal("Sproutle (Frost) — not before Champion", Assert.Single(lab.StoryLocks).Text);

        lab.IsScoutingTab = true;
        Assert.Equal(4, lab.Scouting.Count);
        Assert.Contains("Ice", lab.Scouting[1].Types, StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Light)]
    public async Task Capture_champion_lab_screens(ThemePreference theme)
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "screens");
        Directory.CreateDirectory(outDir);
        using var env = new TestEnvironment();
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
        var lab = OpenLab(shell);
        lab.ShowAllTrainers = true;
        Save("lab-groups");
        lab.IsRulesTab = true;
        Save("lab-rules");
        lab.IsScoutingTab = true;
        Save("lab-scouting");
        window.Close();
    }
}
