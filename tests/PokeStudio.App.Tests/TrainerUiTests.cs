using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;
using PokeStudio.Core.Models;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Settings;

namespace PokeStudio.App.Tests;

public class TrainerUiTests
{
    private static async Task<(ShellViewModel Shell, string Root)> OpenFixtureAsync(TestEnvironment env)
    {
        var root = Path.Combine(env.Root, "my-hack");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(root));
        return (Assert.IsType<ShellViewModel>(env.Main.CurrentView), root);
    }

    private static TrainersPageViewModel OpenTrainers(ShellViewModel shell)
    {
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Trainers);
        return Assert.IsType<TrainersPageViewModel>(shell.CurrentPage);
    }

    private static Trainer Get(ShellViewModel shell, string key) => shell.Session.Data.Trainers.Single(t => t.Key == key);

    [AvaloniaFact]
    public async Task Trainers_page_lists_filters_and_searches_by_pokemon()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        var page = OpenTrainers(shell);

        Assert.Equal(5, page.Items.Count);
        Assert.Equal("TRAINER_RICK_1", page.SelectedItem!.Key); // TRAINER_NONE is skipped as the first choice
        Assert.Equal("Hiker Rick", page.SelectedItem.Title);
        Assert.Equal("2 Pokémon · Lv 12–13", page.SelectedItem.Subtitle);

        page.Search = "bloom";
        Assert.Equal(["TRAINER_MIA", "TRAINER_LEADER_FERN"], page.Items.Select(i => i.Key));
        page.Search = "";
        page.ClassFilter = page.ClassFilters.Single(c => c.Key == "TRAINER_CLASS_LEADER");
        Assert.Equal("Leader Fern", Assert.Single(page.Items).Title);
    }

    [AvaloniaFact]
    public async Task Editing_details_team_and_ai_goes_through_undo_and_saves()
    {
        using var env = new TestEnvironment();
        var (shell, root) = await OpenFixtureAsync(env);
        var page = OpenTrainers(shell);
        var editor = Assert.IsType<TrainerEditorViewModel>(page.Editor);

        editor.NameDraft = "RICKY";
        Assert.Equal("Hiker RICKY", page.SelectedItem!.Title.Replace("Ricky", "RICKY", StringComparison.Ordinal));
        editor.Preset = editor.PresetOptions.Single(p => p.Name == "Smart");
        Assert.Equal(["AI_FLAG_SMART_TRAINER"], Get(shell, "TRAINER_RICK_1").AiFlags);

        var first = editor.Party[0];
        first.Level = 20;
        first.Held = first.HeldOptions.Single(i => i.Key == "ITEM_ORAN_BERRY");
        first.Move1 = first.MoveOptions.Single(m => m.Key == "MOVE_SEED_BOMB");
        first.Ivs = first.IvOptions.Single(o => o.Label.StartsWith("All 31", StringComparison.Ordinal));
        var rick = Get(shell, "TRAINER_RICK_1").Party[0];
        Assert.Equal((20, "ITEM_ORAN_BERRY", "MOVE_SEED_BOMB"), (rick.Level, rick.HeldItem, rick.Moves.Single()));

        editor.AddPokemonCommand.Execute(null);
        Assert.Equal(3, Get(shell, "TRAINER_RICK_1").Party.Count);
        editor.Party[2].RemoveCommand.Execute(null);
        Assert.Equal(2, Get(shell, "TRAINER_RICK_1").Party.Count);

        shell.UndoCommand.Execute(null); // the removal
        Assert.Equal(3, Get(shell, "TRAINER_RICK_1").Party.Count);
        shell.UndoCommand.Execute(null); // the addition
        Assert.Equal(2, Get(shell, "TRAINER_RICK_1").Party.Count);

        await shell.SaveCommand.ExecuteAsync(null);
        if (shell.IsValidationOpen)
        {
            Assert.False(shell.HasErrors);
            await shell.SaveAnywayCommand.ExecuteAsync(null);
        }

        var text = File.ReadAllText(Path.Combine(root, "src", "data", "trainers.party"));
        Assert.Contains("Name: RICKY\n", text, StringComparison.Ordinal);
        Assert.Contains("AI: Smart Trainer\n", text, StringComparison.Ordinal);
        Assert.Contains("Sproutle @ Oran Berry\nLevel: 20\nIVs: 31 HP / 31 Atk / 31 Def / 31 SpA / 31 SpD / 31 Spe\n- Seed Bomb\n", text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Change_levels_tool_scales_the_listed_trainers_in_one_step()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        var page = OpenTrainers(shell);

        page.ToggleLevelToolCommand.Execute(null);
        page.LevelModeIndex = 1; // percent
        Assert.Equal(120, page.Amount);
        Assert.Contains("Lv 20 → 24", page.LevelPreview, StringComparison.Ordinal);
        page.ScopeIndex = 2; // everyone
        page.ApplyLevelsCommand.Execute(null);

        Assert.Equal("3 trainers · levels × 120%", Assert.Single(shell.Changes).Description); // NONE and the locked pool trainer aren't changed
        Assert.Equal([14, 16], Get(shell, "TRAINER_RICK_1").Party.Select(p => p.Level));
        Assert.False(page.IsLevelToolOpen);
    }

    [AvaloniaFact]
    public async Task Copying_a_team_and_checks_that_take_you_to_the_trainer()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        var page = OpenTrainers(shell);
        var editor = page.Editor!;

        editor.CopyFrom = editor.TrainerOptions.Single(t => t.Key == "TRAINER_LEADER_FERN");
        editor.CopyTeamCommand.Execute(null);
        Assert.Equal("SPECIES_BLOOMTLE", Get(shell, "TRAINER_RICK_1").Party[0].Species);

        editor.IsDouble = true;
        editor.Party[1].RemoveCommand.Execute(null); // one Pokémon in a double battle
        await shell.SaveCommand.ExecuteAsync(null);
        var issue = Assert.Single(shell.Issues, i => i.Text.Contains("double battle", StringComparison.Ordinal));
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Overview);
        issue.GoCommand.Execute(null);
        Assert.Equal("TRAINER_RICK_1", Assert.IsType<TrainersPageViewModel>(shell.CurrentPage).SelectedItem!.Key);
        issue.FixCommand.Execute(null);
        Assert.Equal(BattleType.Single, Get(shell, "TRAINER_RICK_1").Battle);
    }

    [AvaloniaFact]
    public async Task Pool_trainers_are_shown_locked()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        shell.SelectedTrainerKey = "TRAINER_TWINS_POOL";
        var page = OpenTrainers(shell);

        Assert.True(page.Editor!.IsLocked);
        Assert.Contains("pool", page.Editor.LockedReason, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Enemy_ai_page_shows_usage_and_applies_to_bosses()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.TrainerAi);
        var page = Assert.IsType<EnemyAiPageViewModel>(shell.CurrentPage);

        Assert.Contains("Beginner: 1 trainer", page.Usage);
        page.Preset = page.Presets.Single(p => p.Id == "expert");
        page.Scope = page.Scopes.Single(s => s.Id == "bosses");
        page.ApplyCommand.Execute(null);

        Assert.Equal(["AI_FLAG_SMART_TRAINER", "AI_FLAG_PREDICTION", "AI_FLAG_HP_AWARE"], Get(shell, "TRAINER_LEADER_FERN").AiFlags);
        Assert.Equal(["AI_FLAG_CHECK_BAD_MOVE"], Get(shell, "TRAINER_RICK_1").AiFlags);
        Assert.Contains("Expert: 1 trainer", page.Usage);
    }

    [AvaloniaFact]
    public async Task Search_finds_trainers()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        shell.Search.Open();
        shell.Search.Query = "fern";

        var hit = Assert.Single(shell.Search.Results, r => r.Kind == "Trainer");
        shell.Search.Activate(hit);
        Assert.Equal("TRAINER_LEADER_FERN", Assert.IsType<TrainersPageViewModel>(shell.CurrentPage).SelectedItem!.Key);
    }

    [AvaloniaTheory]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Light)]
    public async Task Capture_trainer_screens(ThemePreference theme)
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
        shell.SelectedTrainerKey = "TRAINER_MIA";
        OpenTrainers(shell);
        Save("trainers");

        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.TrainerAi);
        Save("enemy-ai");
        window.Close();
    }
}
