using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Settings;

namespace PokeStudio.App.Tests;

public class GuideUiTests
{
    [AvaloniaTheory]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Light)]
    public async Task The_guide_is_written_to_the_project_folder(ThemePreference theme)
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "screens");
        Directory.CreateDirectory(outDir);
        using var env = new TestEnvironment();
        env.Main.Theme.Apply(theme);
        var window = new MainWindow { DataContext = env.Main, Width = 1440, Height = 900 };
        window.Show();
        var root = Path.Combine(env.Root, "my-hack");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(root));
        var shell = Assert.IsType<ShellViewModel>(env.Main.CurrentView);
        shell.IsChangesOpen = false;
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Guide);
        var page = Assert.IsType<GuidePageViewModel>(shell.CurrentPage);
        Assert.EndsWith("Player's Guide", page.Title, StringComparison.Ordinal);

        await page.CreateCommand.ExecuteAsync(null);
        Assert.True(page.HasGuide, page.Status);
        var html = await File.ReadAllTextAsync(page.HtmlFile);
        Assert.Contains("Fern", html, StringComparison.Ordinal);
        Assert.Contains("Where to catch Pokémon", html, StringComparison.Ordinal);
        Assert.True(File.Exists(page.MarkdownFile));

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(outDir, $"players-guide-{theme}.png"));
        window.Close();
    }
}
