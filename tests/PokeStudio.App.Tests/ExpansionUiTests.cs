using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;
using PokeStudio.Core.Projects;

namespace PokeStudio.App.Tests;

/// <summary>Opens the invented pokeemerald-expansion fixture through the real UI flow.</summary>
public class ExpansionUiTests
{
    private const string SpeciesFile = "src/data/pokemon/species_info/gen_1_families.h";

    private static async Task<(ShellViewModel Shell, string Root)> OpenFixtureAsync(TestEnvironment env)
    {
        var root = Path.Combine(env.Root, "my-hack");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(root));
        return (Assert.IsType<ShellViewModel>(env.Main.CurrentView), root);
    }

    private static string Read(string root, string rel) => File.ReadAllText(Path.Combine([root, .. rel.Split('/')]));

    [AvaloniaFact]
    public async Task Decomp_project_opens_with_editors_for_what_it_supports()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);

        Assert.Equal("my-hack", shell.ProjectName);
        Assert.True(shell.NavItems.Single(n => n.Key == NavKey.Species).IsEnabled);
        Assert.True(shell.NavItems.Single(n => n.Key == NavKey.Learnsets).IsEnabled);
        Assert.True(shell.NavItems.Single(n => n.Key == NavKey.Evolutions).IsEnabled);
        Assert.True(shell.NavItems.Single(n => n.Key == NavKey.Trainers).IsEnabled);
        Assert.True(shell.NavItems.Single(n => n.Key == NavKey.Encounters).IsEnabled);
        Assert.True(shell.NavItems.Single(n => n.Key == NavKey.Graphics).IsEnabled);

        var page = Assert.IsType<PokemonPageViewModel>(shell.CurrentPage);
        Assert.Equal(5, page.Items.Count);
        page.Search = "bloom";
        Assert.Equal("Bloomtle", Assert.Single(page.Items).Title);
    }

    [AvaloniaFact]
    public async Task Saving_runs_checks_first_and_fix_it_for_me_clears_them()
    {
        using var env = new TestEnvironment();
        var (shell, root) = await OpenFixtureAsync(env);
        var before = Read(root, SpeciesFile);
        var editor = Assert.IsType<PokemonEditorViewModel>(Assert.IsType<PokemonPageViewModel>(shell.CurrentPage).Editor);
        editor.Stats.Single(s => s.Label == "Speed").Value = 77;

        await shell.SaveCommand.ExecuteAsync(null);

        Assert.True(shell.IsValidationOpen); // Bloomtle learns 5 moves at level 1
        var issue = Assert.Single(shell.Issues, i => i.Text.StartsWith("Bloomtle", StringComparison.Ordinal)); // the fixture also has a deliberately long sign
        Assert.True(issue.HasFix);
        Assert.Equal(before, Read(root, SpeciesFile)); // nothing written yet

        issue.FixCommand.Execute(null);
        Assert.DoesNotContain(shell.Issues, i => i.IsError || i.Text.StartsWith("Bloomtle", StringComparison.Ordinal));
        await shell.SaveAnywayCommand.ExecuteAsync(null);

        Assert.False(shell.IsValidationOpen);
        Assert.Contains(".baseSpeed     = 77,", Read(root, SpeciesFile), StringComparison.Ordinal);
        Assert.False(shell.IsDirty);
        Assert.NotEmpty(await shell.Session.ListBackupsAsync()); // backed up before writing
    }

    [AvaloniaFact]
    public async Task Learnset_and_evolution_editors_make_undoable_changes()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);

        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Learnsets);
        var learn = Assert.IsType<LearnsetEditorViewModel>(Assert.IsType<LearnsetPageViewModel>(shell.CurrentPage).Editor);
        Assert.NotNull(learn.SharedNote); // Sproutle shares its level-up list with its Frost form
        learn.LevelUp[2].MoveDownCommand.Execute(null);
        Assert.Equal("MOVE_SLEEP_DUST", learn.LevelUp[2].Move!.Key);
        learn.NewEggMove = shell.Session.Data.Catalog.Moves.Single(m => m.Key == "MOVE_PROTECT");
        learn.AddEggMoveCommand.Execute(null);
        Assert.Contains(learn.EggMoves, e => e.Key == "MOVE_PROTECT");
        learn.Machines.Single(m => m.Label == "TM01").IsChecked = false;
        Assert.DoesNotContain("MOVE_PROTECT", shell.Session.Data.LearnsetFor(shell.Session.Data.Species[0])!.Teachable);

        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Evolutions);
        var evo = Assert.IsType<EvolutionEditorViewModel>(Assert.IsType<EvolutionPageViewModel>(shell.CurrentPage).Editor);
        Assert.Equal("Sproutle evolves into Bloomtle when it reaches level 16.", Assert.Single(evo.Rows).Sentence);
        evo.Rows[0].ParameterLevel = 18;
        Assert.Equal("18", shell.Session.Data.Evolutions.Single(e => e.FromSpecies == "SPECIES_SPROUTLE").Evolutions[0].Parameter);

        Assert.Equal(4, shell.Changes.Count);
        for (var i = 0; i < 4; i++)
        {
            shell.UndoCommand.Execute(null);
        }

        Assert.Empty(shell.Changes);
        Assert.Equal("16", shell.Session.Data.Evolutions.Single(e => e.FromSpecies == "SPECIES_SPROUTLE").Evolutions[0].Parameter);
    }

    [AvaloniaFact]
    public async Task Locked_parts_say_why()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        var page = Assert.IsType<PokemonPageViewModel>(shell.CurrentPage);

        page.SelectedItem = page.Items.Single(i => i.Key == "SPECIES_GHOSTLING");
        var ghost = Assert.IsType<PokemonEditorViewModel>(page.Editor);
        Assert.True(ghost.IsReadOnly);
        Assert.Contains(ghost.LockNotes, n => n.Contains("switched off", StringComparison.Ordinal));

        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Evolutions);
        var evoPage = Assert.IsType<EvolutionPageViewModel>(shell.CurrentPage);
        evoPage.SelectedItem = evoPage.Items.Single(i => i.Key == "SPECIES_EMBERPUP");
        var evo = Assert.IsType<EvolutionEditorViewModel>(evoPage.Editor);
        Assert.False(evo.IsEditable);
        Assert.Contains("game settings", evo.LockedReason, StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public async Task Capture_expansion_screens(string theme)
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "screens");
        Directory.CreateDirectory(outDir);
        using var env = new TestEnvironment();
        env.Main.Theme.Apply(theme == "Light" ? Core.Settings.ThemePreference.Light : Core.Settings.ThemePreference.Dark);
        var window = new MainWindow { DataContext = env.Main, Width = 1440, Height = 900 };
        window.Show();
        var (shell, _) = await OpenFixtureAsync(env);

        void Save(string name)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame()!.Save(Path.Combine(outDir, $"{name}-{theme}.png"));
        }

        var page = (PokemonPageViewModel)shell.CurrentPage!;
        page.SelectedItem = page.Items[1];
        ((PokemonEditorViewModel)page.Editor!).Stats[0].Value = 80;
        Save("expansion-pokemon");
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Learnsets);
        Save("expansion-learnset");
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Evolutions);
        Save("expansion-evolution");
        await shell.SaveCommand.ExecuteAsync(null);
        Save("expansion-checks");
        window.Close();
    }
}
