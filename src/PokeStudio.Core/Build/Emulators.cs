using PokeStudio.Core.Models;

namespace PokeStudio.Core.Build;

/// <summary>An emulator the app knows how to recognise and launch. None are bundled; the user installs their own.</summary>
/// <param name="Note">Shown when this emulator is picked (e.g. why an old one may misbehave).</param>
public sealed record EmulatorKind(
    string Id,
    string Name,
    IReadOnlyList<GamePlatform> Platforms,
    IReadOnlyList<string> FileNames,
    string Website,
    bool Suggested = false,
    string? Note = null,
    bool NoteIsWarning = false);

public sealed record EmulatorCheck(ToolState State, string Message, EmulatorKind? Kind);

/// <summary>An emulator found on this computer.</summary>
public sealed record EmulatorCandidate(string Path, EmulatorKind Kind);

public static class Emulators
{
    public static readonly IReadOnlyList<EmulatorKind> Known =
    [
        new("mgba", "mGBA", [GamePlatform.Gba], ["mgba.exe", "mgba-qt.exe", "mgba-qt", "mgba", "io.mgba.mGBA"],
            "https://mgba.io", Suggested: true,
            Note: "Accurate and actively maintained. The best choice for testing GBA hacks."),
        new("vbam", "VisualBoyAdvance-M", [GamePlatform.Gba], ["visualboyadvance-m.exe", "visualboyadvance-m", "vbam.exe"],
            "https://visualboyadvance-m.org", Note: "Works well. mGBA is a little more accurate."),
        new("vba", "VisualBoyAdvance 1.8", [GamePlatform.Gba], ["visualboyadvance.exe", "vba.exe"],
            "https://mgba.io",
            Note: "This is a very old emulator (2005). Modern hacks can freeze, glitch or lose saves in it even when the game itself is fine. We suggest mGBA for testing.",
            NoteIsWarning: true),
        new("melonds", "melonDS", [GamePlatform.Nds], ["melonds.exe", "melonds", "net.kuribo64.melonDS"],
            "https://melonds.kuribo64.net", Suggested: true,
            Note: "Accurate and actively maintained. The best choice for testing DS hacks."),
        new("desmume", "DeSmuME", [GamePlatform.Nds], ["desmume.exe", "desmume", "desmume-cli", "org.desmume.DeSmuME"],
            "https://desmume.org", Note: "Works for most DS games. melonDS is more accurate."),
        new("azahar", "Azahar", [GamePlatform.N3ds], ["azahar.exe", "azahar", "org.azahar_emu.Azahar"],
            "https://azahar-emu.org", Suggested: true,
            Note: "The actively maintained continuation of Citra."),
        new("lime3ds", "Lime3DS", [GamePlatform.N3ds], ["lime3ds-gui.exe", "lime3ds.exe", "lime3ds-gui", "lime3ds"],
            "https://azahar-emu.org", Note: "Works. Its development moved to Azahar."),
        new("citra", "Citra", [GamePlatform.N3ds], ["citra-qt.exe", "citra.exe", "citra-qt", "citra"],
            "https://azahar-emu.org", Note: "Works, but Citra is no longer updated. Azahar is its maintained continuation."),
    ];

    public static IReadOnlyList<EmulatorKind> For(GamePlatform platform) =>
        Known.Where(k => k.Platforms.Contains(platform)).ToList();

    public static EmulatorKind? Suggested(GamePlatform platform) => For(platform).FirstOrDefault(k => k.Suggested);

    public static EmulatorKind? Identify(string path)
    {
        var name = Path.GetFileName(path);
        return Known.FirstOrDefault(k => k.FileNames.Any(f => f.Equals(name, StringComparison.OrdinalIgnoreCase)))
            ?? (name.StartsWith("desmume", StringComparison.OrdinalIgnoreCase) ? Known.First(k => k.Id == "desmume") : null)
            ?? (name.StartsWith("mgba", StringComparison.OrdinalIgnoreCase) ? Known.First(k => k.Id == "mgba") : null)
            ?? (name.StartsWith("melonds", StringComparison.OrdinalIgnoreCase) ? Known.First(k => k.Id == "melonds") : null);
    }

    /// <summary>The "Test" button in Settings and the setup wizard.</summary>
    public static EmulatorCheck Check(string? path, GamePlatform platform)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new EmulatorCheck(ToolState.Warning, "No emulator chosen yet.", null);
        }

        if (Directory.Exists(path))
        {
            return new EmulatorCheck(ToolState.Missing, "That's a folder. Pick the emulator's program file inside it.", null);
        }

        if (!File.Exists(path))
        {
            return new EmulatorCheck(ToolState.Missing, "This file doesn't exist any more. Was the emulator moved or uninstalled?", null);
        }

        var kind = Identify(path);
        if (kind is null)
        {
            return OperatingSystem.IsWindows() && !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? new EmulatorCheck(ToolState.Missing, "This isn't a program. Pick the emulator's .exe file.", null)
                : new EmulatorCheck(ToolState.Warning, "We don't recognise this program, but we'll try opening games with it.", null);
        }

        if (!kind.Platforms.Contains(platform))
        {
            return new EmulatorCheck(ToolState.Missing, $"{kind.Name} doesn't play {Label(platform)} games.", kind);
        }

        return kind.NoteIsWarning
            ? new EmulatorCheck(ToolState.Warning, $"{kind.Name} found. {kind.Note}", kind)
            : new EmulatorCheck(ToolState.Ready, $"{kind.Name} found and ready.", kind);
    }

    /// <summary>How to open <paramref name="gameFile"/> in the emulator. All known emulators take the game as their only argument.</summary>
    public static ProcessSpec LaunchSpec(string emulatorPath, string gameFile) =>
        new(emulatorPath, [gameFile], Path.GetDirectoryName(emulatorPath));

    public static string Label(GamePlatform platform) => platform switch
    {
        GamePlatform.Gba => "GBA",
        GamePlatform.Nds => "DS",
        GamePlatform.N3ds => "3DS",
        _ => platform.ToString(),
    };

    /// <summary>Usual places emulators live on this OS, plus a "Games" folder on each drive.</summary>
    public static IReadOnlyList<string> DefaultSearchRoots()
    {
        var roots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            roots.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "Games"));
                roots.Add(Path.Combine(drive.RootDirectory.FullName, "Emulators"));
            }
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.AddRange(["/usr/bin", "/usr/local/bin", "/var/lib/flatpak/exports/bin",
                Path.Combine(home, ".local", "share", "flatpak", "exports", "bin"),
                Path.Combine(home, ".local", "bin"), Path.Combine(home, "Applications"), Path.Combine(home, "Games")]);
        }

        return roots.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Looks for known emulators under <paramref name="roots"/> (a few folders deep, bounded so it stays quick).</summary>
    public static IReadOnlyList<EmulatorCandidate> Find(IEnumerable<string> roots, int maxDepth = 5, int maxFolders = 20_000, CancellationToken cancellationToken = default)
    {
        var found = new List<EmulatorCandidate>();
        var visited = 0;
        var queue = new Queue<(string Dir, int Depth)>(roots.Where(Directory.Exists).Select(r => (r, 0)));
        while (queue.Count > 0 && visited < maxFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (dir, depth) = queue.Dequeue();
            visited++;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (Identify(file) is { } kind && (!OperatingSystem.IsWindows() || file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                    {
                        found.Add(new EmulatorCandidate(file, kind));
                    }
                }

                if (depth < maxDepth)
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        var name = Path.GetFileName(sub);
                        if (!name.StartsWith('.') && !SkipFolders.Contains(name))
                        {
                            queue.Enqueue((sub, depth + 1));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // unreadable folder: skip
            }
        }

        // Prefer the GUI build when a folder has both (citra-qt over citra).
        return found
            .GroupBy(c => (Path.GetDirectoryName(c.Path), c.Kind.Id))
            .Select(g => g.OrderBy(c => Path.GetFileName(c.Path).Contains("qt", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(c.Path).Contains("gui", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First())
            .OrderBy(c => c.Kind.Suggested ? 0 : 1)
            .ThenBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "node_modules", "Common Files", "WindowsApps", "Microsoft", "dotnet", "Steam", "steamapps",
        "Roms", "Battery", "Cheats", "StateSlots", "plugins", "dist", "scripting",
    };
}
