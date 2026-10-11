using System.Globalization;
using System.Text.RegularExpressions;

namespace PokeStudio.Core.Build;

public enum BuildOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    /// <summary>Tools are missing; nothing was run.</summary>
    NotReady,
}

/// <summary>A finished game kept in the project's builds folder.</summary>
public sealed record BuildRecord(string FilePath, DateTimeOffset CreatedUtc, long Size)
{
    public string FileName => Path.GetFileName(FilePath);
}

public sealed record BuildResult(BuildOutcome Outcome, string Log, IReadOnlyList<BuildProblem> Problems, BuildRecord? Output, IReadOnlyList<ToolStatus> Tools)
{
    public bool Succeeded => Outcome == BuildOutcome.Succeeded;
}

/// <summary>Progress for the build page: a 0..1 estimate plus the latest step in plain words.</summary>
public sealed record BuildProgress(double Fraction, string Step, string? LogLine);

/// <summary>
/// Runs a module's build in the right environment, translates errors, and keeps the last few
/// finished games as dated copies in the project's builds folder.
/// </summary>
public sealed partial class BuildService(IProcessRunner runner, IBuildEnvironment environment, TimeProvider? clock = null)
{
    public const string BuildsFolderName = "builds";
    public const int DefaultKeep = 5;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IBuildEnvironment Environment => environment;

    public static string BuildsDir(string projectDir) => Path.Combine(projectDir, BuildsFolderName);

    public Task<IReadOnlyList<ToolStatus>> CheckAsync(BuildDefinition build, CancellationToken cancellationToken = default) =>
        environment.CheckAsync(build, cancellationToken);

    /// <param name="sourceDir">Folder the build runs in (the decomp project).</param>
    /// <param name="projectDir">Poke Studio project folder; finished games go in its builds folder.</param>
    public async Task<BuildResult> BuildAsync(
        BuildDefinition build,
        string sourceDir,
        string projectDir,
        int keep = DefaultKeep,
        IProgress<BuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new BuildProgress(0, "Checking your build tools…", null));
        var tools = await environment.CheckAsync(build, cancellationToken);
        if (tools.Any(t => t.State == ToolState.Missing))
        {
            return new BuildResult(BuildOutcome.NotReady, "", [], null, tools);
        }

        var output = Path.Combine(sourceDir, build.OutputFile);
        var before = File.Exists(output) ? File.GetLastWriteTimeUtc(output) : DateTime.MinValue;
        var compiled = 0;
        var estimate = Math.Max(1, build.EstimatedSteps);
        void OnLine(string line)
        {
            var step = StepFor(line);
            if (step is not null)
            {
                compiled++;
            }

            progress?.Report(new BuildProgress(Math.Min(0.95, 0.05 + (0.9 * compiled / estimate)), step ?? "Building…", line));
        }

        ProcessResult result;
        try
        {
            result = await runner.RunAsync(environment.Command(sourceDir, build.Command), OnLine, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return new BuildResult(BuildOutcome.Cancelled, "", [], null, tools);
        }
        catch (ProgramNotFoundException ex)
        {
            var problem = new BuildProblem("The build couldn't start", $"“{ex.Program}” isn't available. Open the setup wizard to check your build tools.", null, null, ex.Message);
            return new BuildResult(BuildOutcome.Failed, ex.Message, [problem], null, tools);
        }

        if (!result.Succeeded || !File.Exists(output) || File.GetLastWriteTimeUtc(output) < before)
        {
            return new BuildResult(BuildOutcome.Failed, result.Output, BuildErrorTranslator.Translate(result.Output), null, tools);
        }

        progress?.Report(new BuildProgress(1, "Done!", null));
        var record = Keep(output, projectDir, keep);
        return new BuildResult(BuildOutcome.Succeeded, result.Output, [], record, tools);
    }

    /// <summary>Copies a finished game into the builds folder with a date in its name and prunes old ones.</summary>
    public BuildRecord Keep(string gameFile, string projectDir, int keep = DefaultKeep)
    {
        var dir = BuildsDir(projectDir);
        Directory.CreateDirectory(dir);
        var now = _clock.GetUtcNow();
        var stem = Path.GetFileNameWithoutExtension(gameFile);
        var ext = Path.GetExtension(gameFile);
        var name = $"{now.ToLocalTime():yyyy-MM-dd_HHmmss}_{stem}{ext}";
        var target = Path.Combine(dir, name);
        for (var i = 2; File.Exists(target); i++)
        {
            target = Path.Combine(dir, $"{now.ToLocalTime():yyyy-MM-dd_HHmmss}_{stem}-{i}{ext}");
        }

        File.Copy(gameFile, target);
        File.SetLastWriteTimeUtc(target, now.UtcDateTime);
        foreach (var old in History(projectDir).Skip(Math.Max(1, keep)))
        {
            File.Delete(old.FilePath);
            foreach (var companion in GuideFiles(old.FilePath).Append(PatchPath(old.FilePath)).Where(File.Exists))
            {
                File.Delete(companion);
            }
        }

        return new BuildRecord(target, now, new FileInfo(target).Length);
    }

    public const string GuideSuffix = "-players-guide";

    /// <summary>The Player's Guide written next to a build: (HTML, Markdown).</summary>
    public static (string Html, string Markdown) GuidePaths(string buildFile)
    {
        var stem = Path.Combine(Path.GetDirectoryName(buildFile)!, Path.GetFileNameWithoutExtension(buildFile) + GuideSuffix);
        return (stem + ".html", stem + ".md");
    }

    /// <summary>The patch (.bps) made from a build, next to it.</summary>
    public static string PatchPath(string buildFile) => Path.ChangeExtension(buildFile, ".bps");

    private static string[] GuideFiles(string buildFile) => GuidePaths(buildFile) is var (h, m) ? [h, m] : [];

    private static bool IsGuide(string file) => Path.GetFileNameWithoutExtension(file).EndsWith(GuideSuffix, StringComparison.Ordinal);

    /// <summary>Kept builds, newest first.</summary>
    public static IReadOnlyList<BuildRecord> History(string projectDir)
    {
        var dir = BuildsDir(projectDir);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        return Directory.EnumerateFiles(dir)
            .Where(f => DatedName().IsMatch(Path.GetFileName(f)) && !IsGuide(f) && !f.EndsWith(".bps", StringComparison.OrdinalIgnoreCase))
            .Select(f => new FileInfo(f))
            .Select(f => new BuildRecord(f.FullName, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero), f.Length))
            .OrderByDescending(r => r.CreatedUtc)
            .ThenByDescending(r => r.FileName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A short "what's happening" line for a build log line, or null if it's not a step.</summary>
    internal static string? StepFor(string line)
    {
        var m = CompileStep().Match(line);
        if (!m.Success)
        {
            return line.Contains("arm-none-eabi-ld", StringComparison.Ordinal) || line.StartsWith("LD ", StringComparison.Ordinal)
                ? "Putting the game together…"
                : null;
        }

        var file = Path.GetFileName(m.Groups["file"].Value);
        return string.Create(CultureInfo.InvariantCulture, $"Building {file}");
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}_\d{6}_")]
    private static partial Regex DatedName();

    [GeneratedRegex(@"(?:^|\s)(?:CC|AS|cc1|arm-none-eabi-gcc|arm-none-eabi-as|tools/\S+/\S+)\s.*?(?<file>[\w./-]+\.(?:c|s|inc|png|json))\b")]
    private static partial Regex CompileStep();
}
