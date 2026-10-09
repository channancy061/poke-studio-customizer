using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using PokeStudio.App.Services;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;

namespace PokeStudio.App;

public partial class App : Application
{
    public IServiceProvider? Services { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Services = AppServices.Build(AppServices.DefaultPaths());
            var settings = Services.GetRequiredService<SettingsStore>();
            settings.LoadAsync().GetAwaiter().GetResult();
            Services.GetRequiredService<ThemeService>().Apply(settings.Current.Theme);

            var window = new MainWindow { DataContext = Services.GetRequiredService<MainWindowViewModel>() };
            Services.GetRequiredService<DialogService>().Owner = window;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => (window.DataContext as MainWindowViewModel)?.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
