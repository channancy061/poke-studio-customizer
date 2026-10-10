using System.Text.Json;
using PokeStudio.Core.Models;
using PokeStudio.Core.Storage;

namespace PokeStudio.Core.Settings;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public sealed record RecentProject(string Path, string Name, GamePlatform Platform, DateTimeOffset LastOpenedUtc);

public sealed record AppSettings
{
    public int SchemaVersion { get; init; } = 1;
    public ThemePreference Theme { get; init; } = ThemePreference.Dark;

    /// <summary>1.0 = default; 0.9 small … 1.3 larger.</summary>
    public double TextScale { get; init; } = 1.0;

    public bool ReduceMotion { get; init; }
    public bool AdvancedMode { get; init; }
    public bool FirstRunCompleted { get; init; }
    public string? ProjectsFolder { get; init; }

    /// <summary>Null = the app's default backups folder.</summary>
    public string? BackupsFolder { get; init; }
    public int BackupKeepCount { get; init; } = 20;
    public int AutoBackupMinutes { get; init; } = 10;
    public bool BackupBeforeBuild { get; init; } = true;

    /// <summary>Finished games kept per project (older dated builds are removed).</summary>
    public int BuildsKeepCount { get; init; } = 5;

    public bool TourCompleted { get; init; }

    /// <summary>Emulator executable per platform, configured per OS by the user.</summary>
    public IReadOnlyDictionary<GamePlatform, string> EmulatorPaths { get; init; } = new Dictionary<GamePlatform, string>();

    public IReadOnlyList<RecentProject> RecentProjects { get; init; } = [];

    public AppSettings WithRecent(RecentProject project, int max = 12) => this with
    {
        RecentProjects = [project, .. RecentProjects.Where(r => r.Path != project.Path).Take(max - 1)],
    };
}

/// <summary>
/// Loads and saves settings.json atomically. A damaged file is kept as settings.json.broken
/// and defaults are used, so the app always starts.
/// </summary>
public sealed class SettingsService(string settingsFile)
{
    public string SettingsFile { get; } = settingsFile;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(SettingsFile))
        {
            return new AppSettings();
        }

        try
        {
            return await JsonStore.ReadAsync<AppSettings>(SettingsFile, cancellationToken) ?? new AppSettings();
        }
        catch (JsonException)
        {
            File.Copy(SettingsFile, SettingsFile + ".broken", overwrite: true);
            return new AppSettings();
        }
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(SettingsFile))!);
        return JsonStore.WriteAsync(SettingsFile, settings, cancellationToken);
    }
}
