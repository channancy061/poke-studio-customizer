using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;
using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Settings;

namespace PokeStudio.App.Tests;

public class ChampionLabTwoUiTests
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

    [AvaloniaFact]
    public async Task Suggest_a_team_previews_then_applies_in_one_undo_step()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        shell.GoToTrainer("TRAINER_LEADER_FERN");
        var editor = Assert.IsType<TrainersPageViewModel>(shell.CurrentPage).Editor!;

        editor.ToggleSuggestCommand.Execute(null);
        Assert.True(editor.IsSuggestOpen);
        editor.SuggestDifficulty = editor.SuggestDifficulties.Single(d => d.Difficulty == Difficulty.Hard);
        editor.SuggestSize = 3;
        await editor.SuggestCommand.ExecuteAsync(null);
        Assert.True(editor.HasSuggestion);
        Assert.All(editor.Suggestions, s => Assert.False(string.IsNullOrWhiteSpace(s.Reasons)));
        var names = editor.Suggestions.Select(s => s.Name).ToList();

        editor.UseSuggestionCommand.Execute(null);
        var fern = shell.Session.Data.Trainers.Single(t => t.Key == "TRAINER_LEADER_FERN");
        Assert.Equal(names.Count, fern.Party.Count);
        Assert.Single(shell.Changes);
        shell.UndoCommand.Execute(null);
        Assert.Equal(2, shell.Session.Data.Trainers.Single(t => t.Key == "TRAINER_LEADER_FERN").Party.Count);
    }

    [AvaloniaFact]
    public async Task Report_card_shows_a_score_and_fixes()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        shell.GoToTrainer("TRAINER_LEADER_FERN");
        var editor = Assert.IsType<TrainersPageViewModel>(shell.CurrentPage).Editor!;

        await editor.MakeReportCommand.ExecuteAsync(null);

        Assert.True(editor.HasReport);
        Assert.Matches(@"^\d+/10 — ", editor.ReportTitle);
        Assert.NotEmpty(editor.Report!.Fixes);
    }

    [AvaloniaFact]
    public async Task Difficulty_versions_are_made_for_important_trainers_and_saved()
    {
        using var env = new TestEnvironment();
        var (shell, root) = await OpenFixtureAsync(env);
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.ChampionLab);
        var lab = Assert.IsType<ChampionLabPageViewModel>(shell.CurrentPage);

        lab.IsVersionsTab = true;
        Assert.Contains("1 important trainers", lab.VersionsSummary, StringComparison.Ordinal);
        lab.MakeEasy = false;
        await lab.GenerateVersionsCommand.ExecuteAsync(null);

        Assert.Contains(shell.Session.Data.Trainers, t => t.Key == "TRAINER_LEADER_FERN@Hard");
        Assert.Contains(shell.Session.Data.Trainers, t => t.Key == "TRAINER_LEADER_FERN@Extra Hard");
        Assert.DoesNotContain(shell.Session.Data.Trainers, t => t.Key == "TRAINER_LEADER_FERN@Easy");
        Assert.Single(shell.Changes);

        await SaveAsync(shell);
        Assert.Contains("Difficulty: Extra Hard", File.ReadAllText(Path.Combine(root, "src", "data", "trainers.party")), StringComparison.Ordinal);
        Assert.Contains("DIFFICULTY_EXTRA_HARD", File.ReadAllText(Path.Combine(root, "include", "constants", "difficulty.h")), StringComparison.Ordinal);

        // versions appear on the Trainers page and can be edited like any trainer
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Trainers);
        Assert.Contains(Assert.IsType<TrainersPageViewModel>(shell.CurrentPage).Items, i => i.Key == "TRAINER_LEADER_FERN@Hard");
    }

    [AvaloniaFact]
    public async Task Ai_tuning_sliders_change_the_games_ai_settings()
    {
        using var env = new TestEnvironment();
        var (shell, root) = await OpenFixtureAsync(env);
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.TrainerAi);
        var page = Assert.IsType<EnemyAiPageViewModel>(shell.CurrentPage);

        Assert.True(page.HasTuning);
        Assert.Contains(page.Presets, p => p.Id == "champion");
        var loses = page.Tuning.Single(t => t.Key == "SHOULD_SWITCH_LOSES_1V1_PERCENTAGE");
        Assert.Equal("Switch out when: loses 1v1", loses.Label);
        loses.Value = 60;
        loses.Value = 75; // one drag = one change
        Assert.Equal("75%", loses.ValueText);
        Assert.Single(shell.Changes);
        shell.UndoCommand.Execute(null);
        Assert.Equal("0%", loses.ValueText);
        shell.RedoCommand.Execute(null);

        await SaveAsync(shell);
        Assert.Contains("SHOULD_SWITCH_LOSES_1V1_PERCENTAGE                          75", File.ReadAllText(Path.Combine(root, "include", "config", "ai.h")), StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Light)]
    public async Task Capture_champion_lab_part_two_screens(ThemePreference theme)
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
        shell.IsChangesOpen = false;
        shell.GoToTrainer("TRAINER_LEADER_FERN");
        var editor = ((TrainersPageViewModel)shell.CurrentPage!).Editor!;
        editor.ToggleSuggestCommand.Execute(null);
        editor.SuggestSize = 3;
        await editor.SuggestCommand.ExecuteAsync(null);
        await editor.MakeReportCommand.ExecuteAsync(null);
        Save("suggest-and-report");

        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.TrainerAi);
        Save("ai-tuning");
        window.Close();
    }
}
