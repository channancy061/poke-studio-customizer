using PokeStudio.Core.Models;
using PokeStudio.Core.Platform;
using PokeStudio.Core.Settings;

namespace PokeStudio.Core.Tests;

public class SettingsAndPathsTests
{
    [Fact]
    public void Windows_paths_use_appdata()
    {
        var env = new Dictionary<string, string> { ["APPDATA"] = @"C:\Users\amy\AppData\Roaming", ["LOCALAPPDATA"] = @"C:\Users\amy\AppData\Local" };
        var paths = new AppPaths(HostOs.Windows, k => env.GetValueOrDefault(k), @"C:\Users\amy", @"C:\Users\amy\Documents");

        Assert.Equal(Path.Combine(@"C:\Users\amy\AppData\Roaming", "PokeStudio"), paths.ConfigDir);
        Assert.Equal(Path.Combine(@"C:\Users\amy\AppData\Local", "PokeStudio", "Backups"), paths.BackupsDir);
        Assert.Equal(Path.Combine(@"C:\Users\amy\Documents", "Poke Studio"), paths.DefaultProjectsDir);
    }

    [Fact]
    public void Linux_paths_follow_xdg_with_fallbacks()
    {
        var defaults = new AppPaths(HostOs.Linux, _ => null, "/home/amy", null);
        Assert.Equal(Path.Combine("/home/amy", ".config", "poke-studio"), defaults.ConfigDir);
        Assert.Equal(Path.Combine("/home/amy", ".local", "share", "poke-studio", "backups"), defaults.BackupsDir);
        Assert.Equal(Path.Combine("/home/amy", ".local", "state", "poke-studio", "logs"), defaults.LogsDir);
        Assert.Equal(Path.Combine("/home/amy", "Documents", "Poke Studio"), defaults.DefaultProjectsDir);

        var env = new Dictionary<string, string> { ["XDG_CONFIG_HOME"] = "/cfg", ["XDG_DATA_HOME"] = "/data" };
        var custom = new AppPaths(HostOs.Linux, k => env.GetValueOrDefault(k), "/home/amy", "/home/amy/Docs");
        Assert.Equal(Path.Combine("/cfg", "poke-studio"), custom.ConfigDir);
        Assert.Equal(Path.Combine("/data", "poke-studio"), custom.DataDir);
    }

    [Fact]
    public async Task Settings_round_trip()
    {
        using var tmp = new TempDir();
        var service = new SettingsService(tmp.Combine("cfg", "settings.json"));
        var settings = new AppSettings
        {
            Theme = ThemePreference.Light,
            TextScale = 1.15,
            AdvancedMode = true,
            EmulatorPaths = new Dictionary<GamePlatform, string> { [GamePlatform.Gba] = "/usr/bin/mgba-qt" },
        }.WithRecent(new RecentProject("/p/one", "One", GamePlatform.Gba, DateTimeOffset.UnixEpoch));

        await service.SaveAsync(settings);
        var loaded = await service.LoadAsync();

        Assert.Equal(ThemePreference.Light, loaded.Theme);
        Assert.Equal(1.15, loaded.TextScale);
        Assert.Equal("/usr/bin/mgba-qt", loaded.EmulatorPaths[GamePlatform.Gba]);
        Assert.Equal("One", Assert.Single(loaded.RecentProjects).Name);
    }

    [Fact]
    public async Task Missing_or_damaged_settings_fall_back_to_defaults()
    {
        using var tmp = new TempDir();
        var file = tmp.Combine("settings.json");
        var service = new SettingsService(file);

        Assert.Equal(ThemePreference.Dark, (await service.LoadAsync()).Theme);

        await File.WriteAllTextAsync(file, "{ not json");
        var loaded = await service.LoadAsync();
        Assert.Equal(new AppSettings().Theme, loaded.Theme);
        Assert.True(File.Exists(file + ".broken"));
    }

    [Fact]
    public void Recent_projects_move_to_top_without_duplicates()
    {
        var s = new AppSettings()
            .WithRecent(new RecentProject("/a", "A", GamePlatform.Gba, DateTimeOffset.UnixEpoch))
            .WithRecent(new RecentProject("/b", "B", GamePlatform.Nds, DateTimeOffset.UnixEpoch))
            .WithRecent(new RecentProject("/a", "A", GamePlatform.Gba, DateTimeOffset.UnixEpoch));
        Assert.Equal(["/a", "/b"], s.RecentProjects.Select(r => r.Path));
    }
}
