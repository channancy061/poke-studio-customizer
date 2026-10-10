using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;
using PokeStudio.Core.Models;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Settings;

namespace PokeStudio.App.Tests;

public class MovesUiTests
{
    private static async Task<(ShellViewModel Shell, string Root)> OpenFixtureAsync(TestEnvironment env)
    {
        var root = Path.Combine(env.Root, "my-hack");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(root));
        return (Assert.IsType<ShellViewModel>(env.Main.CurrentView), root);
    }

    private static MovesPageViewModel OpenMoves(ShellViewModel shell)
    {
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Moves);
        return Assert.IsType<MovesPageViewModel>(shell.CurrentPage);
    }

    [AvaloniaFact]
    public async Task Moves_page_lists_filters_and_edits_with_undo()
    {
        using var env = new TestEnvironment();
        var (shell, root) = await OpenFixtureAsync(env);
        var page = OpenMoves(shell);

        Assert.Equal(10, page.Items.Count);
        page.Search = "seed";
        Assert.Equal("Seed Bomb", Assert.Single(page.Items).Title);
        page.Search = "";
        page.TypeFilter = page.TypeFilters.Single(t => t.Key == "TYPE_GRASS");
        Assert.Equal(4, page.Items.Count);

        page.SelectedItem = page.Items.Single(i => i.Key == "MOVE_SEED_BOMB");
        var editor = Assert.IsType<MoveEditorViewModel>(page.Editor);
        editor.Power = 90;
        editor.Power = 95; // a quick second edit merges into one change
        Assert.Equal("Seed Bomb · power 80 → 95", Assert.Single(shell.Changes).Description);

        editor.NameDraft = "Seed Barrage";
        Assert.Equal("Seed Barrage", page.Items.Single(i => i.Key == "MOVE_SEED_BOMB").Title);
        Assert.Equal("Seed Barrage", shell.Session.Data.Catalog.NameOf("MOVE_SEED_BOMB")); // pickers everywhere use the new name

        editor.NameDraft = "An Extremely Long Seed Name";
        Assert.NotNull(editor.NameError);
        Assert.Equal("Seed Barrage", editor.Title); // not applied

        editor.Category = editor.CategoryOptions.Single(c => c.Category == MoveCategory.Status);
        Assert.Equal(0, editor.Power);
        Assert.False(editor.CanEditPower);

        shell.UndoCommand.Execute(null);
        Assert.Equal(95, editor.Power);

        await shell.SaveCommand.ExecuteAsync(null);
        Assert.False(shell.HasErrors); // only the fixture's existing "check this" warning
        await shell.SaveAnywayCommand.ExecuteAsync(null);
        var text = File.ReadAllText(Path.Combine(root, "src", "data", "moves_info.h"));
        Assert.Contains("COMPOUND_STRING(\"Seed Barrage\")", text, StringComparison.Ordinal);
        Assert.Contains(".power = 95,", text, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Move_problems_take_you_to_the_move()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        var data = shell.Session.Data;
        var i = data.Moves.FindIndex(m => m.Key == "MOVE_GROWL");
        shell.Execute(new Core.Changes.ValueChange<Move>("Moves", "test", v => data.Moves[i] = v, data.Moves[i], data.Moves[i] with { Pp = 0 }));

        await shell.SaveCommand.ExecuteAsync(null);
        var issue = Assert.Single(shell.Issues, x => x.Text.Contains("Growl", StringComparison.Ordinal));
        issue.GoCommand.Execute(null);

        var page = Assert.IsType<MovesPageViewModel>(shell.CurrentPage);
        Assert.Equal("MOVE_GROWL", page.SelectedItem!.Key);
    }

    [AvaloniaFact]
    public async Task Modernize_previews_lets_you_untick_and_applies_as_one_undo_step()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Modernize);
        var page = Assert.IsType<ModernizePageViewModel>(shell.CurrentPage);

        Assert.Equal(9, page.Generation!.Generation);
        await page.PreviewCommand.ExecuteAsync(null);
        Assert.True(page.NothingToChange); // the project already uses Gen 9 level-up moves

        page.Generation = page.Generations.Single(g => g.Generation == 3);
        await page.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(["Sproutle", "Emberpup"], page.Rows.Select(r => r.Title));
        Assert.Equal("Also changes: Sproutle (Frost)", page.Rows[0].SharedText);
        Assert.Contains("Protect", page.Rows[1].Details, StringComparison.Ordinal);

        page.Rows[0].IsIncluded = false;
        Assert.Equal("Apply to 1 Pokémon", page.ApplyText);
        page.ApplyCommand.Execute(null);

        var change = Assert.Single(shell.Changes);
        Assert.Equal("Modernized 1 Pokémon to Gen 3", change.Description);
        var ember = shell.Session.Data.Species.Single(s => s.Key == "SPECIES_EMBERPUP");
        Assert.Equal(3, shell.Session.Data.LearnsetFor(ember)!.LevelUp.Count);
        shell.UndoCommand.Execute(null);
        Assert.Equal(2, shell.Session.Data.LearnsetFor(ember)!.LevelUp.Count);
    }

    [AvaloniaFact]
    public async Task Modernize_can_also_update_types()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        shell.Navigate(NavKey.Modernize);
        var page = Assert.IsType<ModernizePageViewModel>(shell.CurrentPage);
        page.IncludeMoves = false;
        page.IncludeSpecies = true;

        await page.PreviewCommand.ExecuteAsync(null);

        var row = Assert.Single(page.Rows);
        Assert.Equal("type Grass → Grass/Fairy", row.Summary);
    }

    [AvaloniaFact]
    public async Task Global_search_jumps_to_pokemon_moves_and_tools()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        var search = shell.Search;

        search.OpenCommand.Execute(null);
        Assert.True(search.IsOpen);
        Assert.Contains(search.Results, r => r.Kind == "Tool" && r.Title == "Backups"); // empty query lists tools

        search.Query = "bloom";
        Assert.Equal("Bloomtle", search.Selected!.Title);
        search.Activate();
        Assert.False(search.IsOpen);
        var pokemon = Assert.IsType<PokemonPageViewModel>(shell.CurrentPage);
        Assert.Equal("SPECIES_BLOOMTLE", pokemon.SelectedItem!.Key);

        search.Open();
        search.Query = "frost";
        Assert.Equal("Frost Breath", search.Selected!.Title); // starts with the text, so it ranks first
        Assert.Contains(search.Results, r => r.Kind == "Pokémon" && r.Title == "Sproutle (Frost)");
        search.MoveSelection(1);
        Assert.Equal("Sproutle (Frost)", search.Selected!.Title);
        search.MoveSelection(-1);
        search.Activate();
        var moves = Assert.IsType<MovesPageViewModel>(shell.CurrentPage);
        Assert.Equal("MOVE_FROST_BREATH", moves.SelectedItem!.Key);

        search.Open();
        search.Query = "zzzz";
        Assert.True(search.HasNoResults);
    }

    [AvaloniaFact]
    public async Task Level_up_rows_can_be_dragged_to_a_new_position()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        shell.SelectedSpeciesKey = "SPECIES_SPROUTLE";
        shell.Navigate(NavKey.Learnsets);
        var editor = Assert.IsType<LearnsetEditorViewModel>(((LearnsetPageViewModel)shell.CurrentPage!).Editor);

        editor.MoveRowTo(3, 0);

        Assert.Equal(["MOVE_SLEEP_DUST", "MOVE_TACKLE", "MOVE_GROWL", "MOVE_VINE_LASH"], editor.LevelUp.Select(r => r.Move!.Key));
        Assert.Equal("Sproutle · moved Sleep Dust to position 1", Assert.Single(shell.Changes).Description);
    }

    [AvaloniaFact]
    public async Task Dropping_a_folder_on_the_hub_opens_it()
    {
        using var env = new TestEnvironment();
        var root = Path.Combine(env.Root, "dropped");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);

        await env.Main.Hub.OpenDroppedAsync(root);

        var shell = Assert.IsType<ShellViewModel>(env.Main.CurrentView);
        Assert.Equal("dropped", shell.ProjectName);

        // dropping the Poke Studio project folder itself reopens it instead of making a new one
        var projectDir = shell.Session.Project.Directory;
        env.Main.GoHome();
        await env.Main.Hub.OpenDroppedAsync(projectDir);
        Assert.Equal(projectDir, Assert.IsType<ShellViewModel>(env.Main.CurrentView).Session.Project.Directory);
    }

    [AvaloniaFact]
    public async Task Demo_game_keeps_moves_locked_and_has_no_modernize()
    {
        using var env = new TestEnvironment();
        Assert.Null(await env.Main.CreateProjectFromGameAsync(HubViewModel.DemoGamePath));
        var shell = Assert.IsType<ShellViewModel>(env.Main.CurrentView);

        Assert.True(shell.NavItems.Single(n => n.Key == NavKey.Moves).IsLocked);
        Assert.DoesNotContain(shell.NavItems, n => n.Key == NavKey.Modernize);
        shell.Search.Open();
        shell.Search.Query = "tackle";
        Assert.DoesNotContain(shell.Search.Results, r => r.Kind == "Move");
    }

    [AvaloniaTheory]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Light)]
    public async Task Capture_moves_modernize_and_search_screens(ThemePreference theme)
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
        var moves = OpenMoves(shell);
        moves.SelectedItem = moves.Items.Single(i => i.Key == "MOVE_TACKLE");
        Save("moves");

        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Modernize);
        var page = (ModernizePageViewModel)shell.CurrentPage!;
        page.Generation = page.Generations.Single(g => g.Generation == 3);
        page.IncludeSpecies = true;
        await page.PreviewCommand.ExecuteAsync(null);
        Save("modernize-preview");

        shell.Search.Open();
        shell.Search.Query = "s";
        Save("global-search");
        window.Close();
    }
}
