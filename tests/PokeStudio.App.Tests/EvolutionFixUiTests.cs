using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;
using PokeStudio.Core.Progression;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Settings;

namespace PokeStudio.App.Tests;

public class EvolutionFixUiTests
{
    private static async Task<(ShellViewModel Shell, string Root)> OpenFixtureAsync(TestEnvironment env)
    {
        var root = Path.Combine(env.Root, "my-hack");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(root));
        return (Assert.IsType<ShellViewModel>(env.Main.CurrentView), root);
    }

    private static EvolutionFixPageViewModel Open(ShellViewModel shell)
    {
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.EvolutionFixes);
        return Assert.IsType<EvolutionFixPageViewModel>(shell.CurrentPage);
    }

    [AvaloniaFact]
    public async Task Fixes_apply_in_one_undo_step_and_keep_the_settings_lines_in_the_source()
    {
        using var env = new TestEnvironment();
        var (shell, root) = await OpenFixtureAsync(env);
        var page = Open(shell);
        Assert.Equal(2, page.Rows.Count);
        var trade = page.Rows.Single(r => r.Row.Problem == EvolutionProblem.Trade);
        Assert.Equal("Trade", trade.Before);
        Assert.Equal("Level 36", trade.After);
        page.TradeLevel = 30;
        Assert.Equal("Level 30", trade.After);
        var time = page.Rows.Single(r => r.Row.Problem == EvolutionProblem.TimeOfDay);
        Assert.Equal("Use Sun Stone in the night", time.Before);

        page.ApplyCommand.Execute(null);
        Assert.False(page.HasRows);
        Assert.Single(shell.Changes);
        shell.UndoCommand.Execute(null);
        Assert.Equal(2, EvolutionFixer.Find(shell.Session.Data).Count);
        shell.RedoCommand.Execute(null);

        await shell.SaveCommand.ExecuteAsync(null);
        if (shell.IsValidationOpen)
        {
            await shell.SaveAnywayCommand.ExecuteAsync(null);
        }

        var text = File.ReadAllText(Path.Combine(root, "src", "data", "pokemon", "species_info", "gen_1_families.h"));
        var expected = string.Join("\n",
            "        .evolutions = EVOLUTION({EVO_LEVEL, 20, SPECIES_SPROUTLE},",
            "                            #if P_GEN_4_CROSS_EVOS",
            "                                {EVO_ITEM, ITEM_SUN_STONE, SPECIES_BLOOMTLE},",
            "                            #endif",
            "                                {EVO_LEVEL, 30, SPECIES_SPROUTLE_FROST}),");
        Assert.Contains(expected, text.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("EVOLUTION({EVO_ITEM, ITEM_SUN_STONE, SPECIES_SPROUTLE_FROST},", text, StringComparison.Ordinal);
    }

    [AvaloniaTheory]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Light)]
    public async Task Capture_fix_evolutions_screen(ThemePreference theme)
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "screens");
        Directory.CreateDirectory(outDir);
        using var env = new TestEnvironment();
        env.Main.Theme.Apply(theme);
        var window = new MainWindow { DataContext = env.Main, Width = 1440, Height = 900 };
        window.Show();
        var (shell, _) = await OpenFixtureAsync(env);
        shell.IsChangesOpen = false;
        Open(shell);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(outDir, $"fix-evolutions-{theme}.png"));
        window.Close();
    }
}
