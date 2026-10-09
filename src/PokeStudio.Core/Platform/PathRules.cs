namespace PokeStudio.Core.Platform;

/// <summary>
/// Path helpers. Game archive contents (NARC/GARC names) are always compared case-sensitively;
/// containment checks follow the host file system so a path cannot escape a folder by changing case.
/// </summary>
public static class PathRules
{
    /// <summary>Comparison for names inside game archives and project-relative paths.</summary>
    public static StringComparison ArchiveNames => StringComparison.Ordinal;

    /// <summary>Comparison matching how the host OS resolves paths.</summary>
    public static StringComparison HostPaths =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>True when <paramref name="candidate"/> resolves to a location inside <paramref name="root"/>.</summary>
    public static bool IsInside(string root, string candidate)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(candidate);
        return full.StartsWith(fullRoot + Path.DirectorySeparatorChar, HostPaths);
    }

    /// <summary>Project-relative path using forward slashes, for manifests that move between OSes.</summary>
    public static string ToPortableRelative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    public static string FromPortableRelative(string root, string portable) =>
        Path.Combine([root, .. portable.Split('/', StringSplitOptions.RemoveEmptyEntries)]);

    /// <summary>Makes a string safe to use as a single folder name on Windows and Linux.</summary>
    public static string SafeFolderName(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        var cleaned = new string(name.Trim().Select(c => invalid.Contains(c) || char.IsControl(c) ? '-' : c).ToArray()).Trim('.', ' ');
        return string.IsNullOrEmpty(cleaned) ? "Untitled" : cleaned;
    }
}
