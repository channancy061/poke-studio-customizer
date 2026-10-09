using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;
using PokeStudio.Core.Settings;

namespace PokeStudio.App.Tests;

/// <summary>
/// Renders key screens to PNG (TestResults/screens) in light and dark. CI uploads them as
/// artifacts so UI changes can be reviewed on both OSes without running the app.
/// </summary>
public class ScreenshotTests
{
    private static readonly string OutDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "screens");

    [AvaloniaTheory]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Light)]
    public async Task Capture_key_screens(ThemePreference theme)
    {
        Directory.CreateDirectory(OutDir);
        using var env = new TestEnvironment();
        env.Main.Theme.Apply(theme);
        var window = new MainWindow { DataContext = env.Main, Width = 1440, Height = 900 };
        window.Show();

        Save(window, $"hub-{theme}");

        Assert.Null(await env.Main.CreateProjectFromGameAsync(HubViewModel.DemoGamePath));
        var shell = Assert.IsType<ShellViewModel>(env.Main.CurrentView);
        ((SpeciesPageViewModel)shell.CurrentPage!).Selected!.Stats[5].Value = 90;
        Save(window, $"creatures-{theme}");

        shell.Navigate(NavKey.Overview);
        Save(window, $"overview-{theme}");

        shell.Navigate(NavKey.Settings);
        Save(window, $"settings-{theme}");

        window.Close();
    }

    private static void Save(MainWindow window, string name)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(OutDir, name + ".png"));
    }
}
