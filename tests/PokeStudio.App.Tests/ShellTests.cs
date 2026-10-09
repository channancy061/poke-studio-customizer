using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using PokeStudio.App;
using PokeStudio.App.Services;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;
using PokeStudio.Core.Platform;

[assembly: AvaloniaTestApplication(typeof(PokeStudio.App.Tests.TestAppBuilder))]

namespace PokeStudio.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia() // real text shaping, so bundled fonts are exercised
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Isolated app-data and projects folders per test.</summary>
public sealed class TestEnvironment : IDisposable
{
    public TestEnvironment()
    {
        Root = Path.Combine(Path.GetTempPath(), "pokestudio-ui-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(HostOs.Linux, _ => null, Path.Combine(Root, "home"), Path.Combine(Root, "docs"));
        paths.EnsureCreated();
        Services = AppServices.Build(paths);
        Main = Services.GetRequiredService<MainWindowViewModel>();
    }

    public string Root { get; }
    public ServiceProvider Services { get; }
    public MainWindowViewModel Main { get; }

    public void Dispose()
    {
        Main.Dispose();
        Services.Dispose();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class ShellTests
{
    private static async Task<ShellViewModel> OpenDemoAsync(TestEnvironment env)
    {
        var message = await env.Main.CreateProjectFromGameAsync(HubViewModel.DemoGamePath);
        Assert.Null(message);
        return Assert.IsType<ShellViewModel>(env.Main.CurrentView);
    }

    [AvaloniaFact]
    public async Task Navigation_follows_module_capabilities()
    {
        using var env = new TestEnvironment();
        var shell = await OpenDemoAsync(env);

        var pokemon = shell.NavItems.Single(n => n.Key == NavKey.Species);
        Assert.True(pokemon.IsEnabled);
        Assert.NotNull(pokemon.Icon); // icon geometries load from Theme/Icons.axaml

        var moves = shell.NavItems.Single(n => n.Key == NavKey.Moves);
        Assert.True(moves.IsLocked);
        Assert.False(string.IsNullOrWhiteSpace(moves.Reason));

        // the demo can never do 3D models, graphics, learnsets, evolutions, encounters or items: hidden and counted
        Assert.DoesNotContain(shell.NavItems, n => n.Key == NavKey.Models3d);
        Assert.Equal(4, shell.HiddenCount);
        Assert.Equal("4 tools hidden for this game", shell.HiddenText);
        Assert.IsType<PokemonPageViewModel>(shell.CurrentPage);
    }

    [AvaloniaFact]
    public async Task Editing_a_stat_shows_in_changes_and_can_be_undone()
    {
        using var env = new TestEnvironment();
        var shell = await OpenDemoAsync(env);
        var page = Assert.IsType<PokemonPageViewModel>(shell.CurrentPage);
        var speed = Assert.IsType<PokemonEditorViewModel>(page.Editor).Stats.Single(s => s.Label == "Speed");

        speed.Value = 90;

        Assert.Equal(90, shell.Session.Data.Species[0].Stats.Speed);
        var change = Assert.Single(shell.Changes);
        Assert.Equal("Leafling · Speed 45 → 90", change.Description);
        Assert.Equal("Pokémon", change.Area);
        Assert.Equal("1 unsaved change", shell.UnsavedText);

        shell.UndoCommand.Execute(null);
        Assert.Equal(45, shell.Session.Data.Species[0].Stats.Speed);
        Assert.Empty(shell.Changes);

        speed.Value = 100;
        await shell.SaveCommand.ExecuteAsync(null);
        Assert.False(shell.IsDirty);
    }

    [AvaloniaFact]
    public async Task Name_longer_than_the_game_allows_is_rejected_with_a_plain_message()
    {
        using var env = new TestEnvironment();
        var shell = await OpenDemoAsync(env);
        var editor = Assert.IsType<PokemonEditorViewModel>(Assert.IsType<PokemonPageViewModel>(shell.CurrentPage).Editor);

        editor.NameDraft = "Leaflingtonia";

        Assert.Equal("Leafling", shell.Session.Data.Species[0].Name);
        Assert.Contains("too long for this game", editor.NameError, StringComparison.Ordinal);
        Assert.Empty(shell.Changes);
    }

    [AvaloniaFact]
    public async Task Unknown_files_get_a_friendly_message()
    {
        using var env = new TestEnvironment();
        var file = Path.Combine(env.Root, "random.bin");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);

        var message = await env.Main.CreateProjectFromGameAsync(file);

        Assert.Contains("don't recognise", message, StringComparison.Ordinal);
        Assert.IsType<HubViewModel>(env.Main.CurrentView);
    }

    [AvaloniaFact]
    public async Task Shell_renders_every_page_without_errors()
    {
        using var env = new TestEnvironment();
        var shell = await OpenDemoAsync(env);
        var window = new MainWindow { DataContext = env.Main };
        window.Show();

        foreach (var item in shell.NavItems.Where(n => n.IsEnabled))
        {
            shell.SelectedNav = item;
            Dispatcher();
            Assert.NotNull(shell.CurrentPage);
        }

        Assert.NotNull(window.GetVisualDescendantsOfType<ShellView>());
        window.Close();
    }

    [AvaloniaFact]
    public void Theme_setting_switches_light_and_dark()
    {
        using var env = new TestEnvironment();
        var settings = new SettingsPageViewModel(env.Main);

        settings.IsLight = true;
        Assert.Equal(ThemeVariant.Light, Application.Current!.ActualThemeVariant);

        settings.IsDark = true;
        Assert.Equal(ThemeVariant.Dark, Application.Current.ActualThemeVariant);
    }

    [AvaloniaFact]
    public async Task Recent_projects_are_remembered_and_reopen()
    {
        using var env = new TestEnvironment();
        await OpenDemoAsync(env);
        env.Main.GoHome();

        var recent = Assert.Single(env.Main.Hub.Recent);
        await recent.OpenCommand.ExecuteAsync(null);
        Assert.IsType<ShellViewModel>(env.Main.CurrentView);
    }

    private static void Dispatcher() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
}

internal static class VisualExtensions
{
    public static T? GetVisualDescendantsOfType<T>(this Control root)
        where T : Control =>
        Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<T>().FirstOrDefault();
}
