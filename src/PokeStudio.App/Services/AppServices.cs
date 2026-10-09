using Avalonia;
using Avalonia.Styling;
using Microsoft.Extensions.DependencyInjection;
using PokeStudio.App.ViewModels;
using PokeStudio.Core.Backups;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Platform;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Settings;
using PokeStudio.Modules.Demo;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.App.Services;

public static class AppServices
{
    public static IAppPaths DefaultPaths()
    {
        var paths = AppPaths.ForCurrentOs();
        paths.EnsureCreated();
        return paths;
    }

    /// <summary>Composition root. Register new game modules here.</summary>
    public static ServiceProvider Build(IAppPaths paths)
    {
        var services = new ServiceCollection();
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => new SettingsService(paths.SettingsFile));
        services.AddSingleton<SettingsStore>();
        services.AddSingleton(_ => new ModuleRegistry()
            .Register(new ExpansionModule())
            .Register(new DemoGameModule()));
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(30) });
        services.AddSingleton(sp => new ExpansionDownloader(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<ProjectService>();
        services.AddSingleton(sp => new BackupService(paths.BackupsDir, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ThemeService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider();
    }
}

/// <summary>Current settings plus save-on-change.</summary>
public sealed class SettingsStore(SettingsService service)
{
    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public async Task LoadAsync() => Current = await service.LoadAsync();

    public async Task UpdateAsync(Func<AppSettings, AppSettings> change)
    {
        Current = change(Current);
        Changed?.Invoke(this, EventArgs.Empty);
        await service.SaveAsync(Current);
    }
}

public sealed class ThemeService
{
    public void Apply(ThemePreference preference)
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = preference switch
            {
                ThemePreference.Light => ThemeVariant.Light,
                ThemePreference.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }
}
