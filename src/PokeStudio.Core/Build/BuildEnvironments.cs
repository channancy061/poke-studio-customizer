using System.Text;

namespace PokeStudio.Core.Build;

/// <summary>What a module needs to turn a project into a game file.</summary>
/// <param name="Command">Shell command run inside the project folder, e.g. "make -j8".</param>
/// <param name="OutputFile">Project-relative path of the finished game, e.g. "pokeemerald.gba".</param>
/// <param name="RequiredTools">Programs that must exist (checked by the setup wizard).</param>
/// <param name="RequiredFiles">Files that must exist, e.g. "/usr/include/png.h" (libpng).</param>
public sealed record BuildDefinition(string Command, string OutputFile, IReadOnlyList<string> RequiredTools, IReadOnlyList<string> RequiredFiles)
{
    /// <summary>Plain-English name of the package set, for messages.</summary>
    public string ToolsDescription { get; init; } = "build tools";

    /// <summary>Roughly how many files a full build compiles, for the progress bar.</summary>
    public int EstimatedSteps { get; init; } = 500;
}

public enum ToolState
{
    Ready,
    Warning,
    Missing,
}

/// <summary>Something the setup wizard can do for the user.</summary>
public enum ToolFix
{
    None,
    InstallWsl,
    InstallUbuntu,
    InstallBuildTools,
}

/// <summary>One line in the setup wizard: green/amber/red plus a plain-English explanation and a fix.</summary>
public sealed record ToolStatus(string Name, ToolState State, string Detail, string? HowToFix = null, ToolFix Fix = ToolFix.None);

/// <summary>Where builds run: WSL on Windows, or the system itself on Linux.</summary>
public interface IBuildEnvironment
{
    string Name { get; }

    Task<IReadOnlyList<ToolStatus>> CheckAsync(BuildDefinition build, CancellationToken cancellationToken = default);

    /// <summary>The program to run for <paramref name="command"/> inside <paramref name="projectDir"/>.</summary>
    ProcessSpec Command(string projectDir, string command);

    /// <summary>A command that performs <paramref name="fix"/>, or null if the user must do it themselves.</summary>
    ProcessSpec? FixCommand(ToolFix fix, BuildDefinition build);
}

/// <summary>Builds inside WSL on Windows (pokeemerald-expansion only builds under WSL on Windows).</summary>
public sealed class WslBuildEnvironment(IProcessRunner runner, string distro = "Ubuntu") : IBuildEnvironment
{
    public static readonly string[] UbuntuPackages =
        ["build-essential", "binutils-arm-none-eabi", "gcc-arm-none-eabi", "libnewlib-arm-none-eabi", "git", "libpng-dev", "python3"];

    /// <summary>The user installs WSL themselves; the app never changes system settings.</summary>
    public const string WslInstructions =
        "1. Open the Start menu, type PowerShell, right-click it and choose “Run as administrator”.\n" +
        "2. Run:  wsl --install -d Ubuntu\n" +
        "3. Restart your PC when asked. Ubuntu opens once — pick a user name and password.\n" +
        "4. Come back here and click “Check again”.\n" +
        "Everything else in the app works without WSL; only “Build my game” needs it.";

    public string Name => $"WSL ({distro})";
    public string Distro => distro;

    public async Task<IReadOnlyList<ToolStatus>> CheckAsync(BuildDefinition build, CancellationToken cancellationToken = default)
    {
        var list = new List<ToolStatus>();
        ProcessResult listing;
        try
        {
            listing = await runner.RunAsync(new ProcessSpec("wsl.exe", ["--list", "--quiet"]) { OutputEncoding = Encoding.Unicode }, cancellationToken: cancellationToken);
        }
        catch (ProgramNotFoundException)
        {
            listing = new ProcessResult(-1, "");
        }

        var text = listing.Output.Replace("\0", "", StringComparison.Ordinal);
        var distros = listing.Succeeded
            ? text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : [];

        // Windows ships a wsl.exe stub that prints "not installed" and fails; WSL without any Linux
        // also fails, but says it has no distributions.
        var wslOn = listing.Succeeded || text.Contains("no installed distributions", StringComparison.OrdinalIgnoreCase);
        if (!wslOn)
        {
            list.Add(new ToolStatus("WSL (Windows Subsystem for Linux)", ToolState.Missing,
                "Not turned on. Windows needs it to build pokeemerald-expansion games — the project's tools don't run on plain Windows.",
                WslInstructions,
                ToolFix.InstallWsl));
            return list;
        }

        list.Add(new ToolStatus("WSL (Windows Subsystem for Linux)", ToolState.Ready, "Turned on."));
        if (!distros.Any(d => d.Equals(distro, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(new ToolStatus($"{distro} inside WSL", ToolState.Missing,
                $"WSL is on, but {distro} isn't installed in it.",
                $"Open PowerShell and run:  wsl --install -d {distro}\nThen finish {distro}'s first-time setup (pick a user name and password) and click “Check again”.", ToolFix.InstallUbuntu));
            return list;
        }

        list.Add(new ToolStatus($"{distro} inside WSL", ToolState.Ready, "Installed."));
        var probe = ProbeScript(build);
        var result = await runner.RunAsync(new ProcessSpec("wsl.exe", ["-d", distro, "--", "sh", "-c", probe]), cancellationToken: cancellationToken);
        list.AddRange(ParseProbe(result.Output, build, $"Click “Install {build.ToolsDescription}” — the app installs them inside {distro} only (about 1 GB download)."));
        return list;
    }

    public ProcessSpec Command(string projectDir, string command) =>
        new("wsl.exe", ["-d", distro, "--cd", projectDir, "--", "sh", "-c", command]);

    public ProcessSpec? FixCommand(ToolFix fix, BuildDefinition build) => fix switch
    {
        // Turning on WSL changes Windows itself, so the user does that step (see WslInstructions).
        ToolFix.InstallBuildTools => new ProcessSpec("wsl.exe",
            ["-d", distro, "-u", "root", "--", "sh", "-c",
             "apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y " + string.Join(' ', UbuntuPackages)]),
        _ => null,
    };

    internal static string ProbeScript(BuildDefinition build)
    {
        var sb = new StringBuilder();
        foreach (var t in build.RequiredTools)
        {
            sb.Append($"command -v {t} >/dev/null 2>&1 && echo OK:{t} || echo MISSING:{t}; ");
        }

        foreach (var f in build.RequiredFiles)
        {
            sb.Append($"[ -e {f} ] && echo OK:{f} || echo MISSING:{f}; ");
        }

        return sb.ToString();
    }

    internal static IEnumerable<ToolStatus> ParseProbe(string output, BuildDefinition build, string howToFix)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries);
        var missing = build.RequiredTools.Concat(build.RequiredFiles)
            .Where(t => !lines.Contains("OK:" + t))
            .Select(Friendly)
            .ToList();
        yield return missing.Count == 0
            ? new ToolStatus(char.ToUpperInvariant(build.ToolsDescription[0]) + build.ToolsDescription[1..], ToolState.Ready, "All installed.")
            : new ToolStatus(char.ToUpperInvariant(build.ToolsDescription[0]) + build.ToolsDescription[1..], ToolState.Missing,
                "Missing: " + string.Join(", ", missing) + ".", howToFix, ToolFix.InstallBuildTools);
    }

    private static string Friendly(string tool) => tool switch
    {
        "arm-none-eabi-gcc" => "GBA compiler (arm-none-eabi-gcc)",
        "make" => "make",
        "gcc" => "C compiler (gcc)",
        "python3" => "Python 3",
        "/usr/include/png.h" => "PNG library (libpng)",
        _ => tool,
    };
}

/// <summary>Builds with the system's own tools (Linux). Linux-specific polish is planned for the final phase.</summary>
public sealed class NativeBuildEnvironment(IProcessRunner runner) : IBuildEnvironment
{
    public string Name => "This computer";

    public async Task<IReadOnlyList<ToolStatus>> CheckAsync(BuildDefinition build, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(new ProcessSpec("sh", ["-c", WslBuildEnvironment.ProbeScript(build)]), cancellationToken: cancellationToken);
        var hint = "Install them with your package manager, e.g. on Ubuntu: sudo apt install " + string.Join(' ', WslBuildEnvironment.UbuntuPackages);
        return WslBuildEnvironment.ParseProbe(result.Output, build, hint).Select(s => s with { Fix = ToolFix.None }).ToList();
    }

    public ProcessSpec Command(string projectDir, string command) => new("sh", ["-c", command], projectDir);

    public ProcessSpec? FixCommand(ToolFix fix, BuildDefinition build) => null;
}
