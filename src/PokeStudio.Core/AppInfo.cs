namespace PokeStudio.Core;

/// <summary>
/// Single place for the product's names. "Poke Studio Customizer" is a working title:
/// choose a trademark-safe name before any public release and change it here
/// (plus &lt;Product&gt; in Directory.Build.props and the installer scripts).
/// </summary>
public static class AppInfo
{
    public const string DisplayName = "Poke Studio Customizer";

    /// <summary>Folder name on Windows (%APPDATA%\FolderName).</summary>
    public const string FolderName = "PokeStudio";

    /// <summary>Lower-case folder name for Linux XDG directories.</summary>
    public const string LinuxFolderName = "poke-studio";

    /// <summary>Default folder for user-visible projects inside Documents.</summary>
    public const string ProjectsFolderName = "Poke Studio";

    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
