namespace PokeStudio.Core.Platform;

public enum HostOs
{
    Windows,
    Linux,
    MacOs,
}

/// <summary>OS-appropriate folders for settings, data, backups, logs and projects.</summary>
public interface IAppPaths
{
    string ConfigDir { get; }
    string DataDir { get; }
    string BackupsDir { get; }
    string LogsDir { get; }
    string DefaultProjectsDir { get; }
    string SettingsFile { get; }
}

public sealed class AppPaths : IAppPaths
{
    public string ConfigDir { get; }
    public string DataDir { get; }
    public string BackupsDir { get; }
    public string LogsDir { get; }
    public string DefaultProjectsDir { get; }
    public string SettingsFile => Path.Combine(ConfigDir, "settings.json");

    /// <param name="os">Which OS conventions to follow.</param>
    /// <param name="getEnv">Environment lookup (injected so tests can fake any OS).</param>
    /// <param name="home">User home directory.</param>
    /// <param name="documents">User documents directory, if the OS reports one.</param>
    public AppPaths(HostOs os, Func<string, string?> getEnv, string home, string? documents)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        var docs = string.IsNullOrEmpty(documents) ? Path.Combine(home, "Documents") : documents;

        if (os == HostOs.Windows)
        {
            var roaming = NonEmpty(getEnv("APPDATA")) ?? Path.Combine(home, "AppData", "Roaming");
            var local = NonEmpty(getEnv("LOCALAPPDATA")) ?? Path.Combine(home, "AppData", "Local");
            ConfigDir = Path.Combine(roaming, AppInfo.FolderName);
            DataDir = Path.Combine(local, AppInfo.FolderName);
            LogsDir = Path.Combine(DataDir, "Logs");
            BackupsDir = Path.Combine(DataDir, "Backups");
        }
        else
        {
            // XDG Base Directory spec (also used on macOS for simplicity; macOS is not a target).
            var config = NonEmpty(getEnv("XDG_CONFIG_HOME")) ?? Path.Combine(home, ".config");
            var data = NonEmpty(getEnv("XDG_DATA_HOME")) ?? Path.Combine(home, ".local", "share");
            var state = NonEmpty(getEnv("XDG_STATE_HOME")) ?? Path.Combine(home, ".local", "state");
            ConfigDir = Path.Combine(config, AppInfo.LinuxFolderName);
            DataDir = Path.Combine(data, AppInfo.LinuxFolderName);
            LogsDir = Path.Combine(state, AppInfo.LinuxFolderName, "logs");
            BackupsDir = Path.Combine(DataDir, "backups");
        }

        DefaultProjectsDir = Path.Combine(docs, AppInfo.ProjectsFolderName);
    }

    public static AppPaths ForCurrentOs() => new(
        CurrentOs(),
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    public static HostOs CurrentOs() =>
        OperatingSystem.IsWindows() ? HostOs.Windows
        : OperatingSystem.IsMacOS() ? HostOs.MacOs
        : HostOs.Linux;

    public void EnsureCreated()
    {
        foreach (var dir in new[] { ConfigDir, DataDir, BackupsDir, LogsDir })
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
