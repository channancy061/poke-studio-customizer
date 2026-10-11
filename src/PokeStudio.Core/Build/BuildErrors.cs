using System.Text.RegularExpressions;

namespace PokeStudio.Core.Build;

/// <summary>A build failure explained in plain words, with the original line kept for Advanced mode.</summary>
public sealed record BuildProblem(string Title, string Explanation, string? File, int? Line, string RawLine);

/// <summary>Turns compiler and make output into plain-English problems.</summary>
public static partial class BuildErrorTranslator
{
    private sealed record Rule(Regex Pattern, Func<Match, (string Title, string Explanation)> Explain);

    private static readonly Rule[] Rules =
    [
        new(Undeclared(), m => ($"“{m.Groups["name"].Value}” doesn't exist",
            NameHint(m.Groups["name"].Value) + " Check the spelling, or add it first.")),
        new(UndefinedReference(), m => ($"“{m.Groups["name"].Value}” is used but never defined",
            "Something refers to this name, but no file creates it. A data file may be missing an entry.")),
        new(MultipleDefinition(), m => ($"“{m.Groups["name"].Value}” is defined twice",
            "Two places create the same thing. Remove or rename one of them.")),
        new(RomTooBig(), _ => ("Your game is too big",
            "The finished game no longer fits in a GBA cartridge (32 MB). Remove some added graphics, music or data.")),
        new(MissingHeader(), m => ($"A required file is missing: {m.Groups["name"].Value}",
            m.Groups["name"].Value == "png.h"
                ? "The PNG library (libpng) isn't installed. Open the setup wizard and install the build tools."
                : "A file the game's code needs can't be found. Did a file get moved or deleted?")),
        new(NoRule(), m => ($"A file is missing: {m.Groups["name"].Value}",
            "The project needs this file to build but it isn't there. Restore it from a backup, or re-download the project.")),
        new(CommandNotFound(), m => ($"A build tool is missing: {m.Groups["name"].Value}",
            "Open the setup wizard and install the build tools, then try again.")),
        new(SyntaxError(), _ => ("There's a typo in a data file",
            "Something is written in a way the compiler can't read — often a missing comma, bracket or quote near this line.")),
        new(PermissionDenied(), _ => ("A file couldn't be written",
            "Another program may have the file open, or the folder is read-only. Close other programs (including the emulator) and try again.")),
        new(OutOfSpace(), _ => ("Your disk is full",
            "Free up some space and build again.")),
        new(NoGitHistory(), _ => ("The project has no git history",
            "pokeemerald-expansion checks that it was downloaded with git (not as a ZIP). Download it again with git, or create an empty file named “.histignore” in the project folder to build anyway.")),
    ];

    /// <summary>Plain-English problems in <paramref name="output"/>, most important first, without duplicates.</summary>
    public static IReadOnlyList<BuildProblem> Translate(string output)
    {
        var problems = new List<BuildProblem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (TryTranslate(line) is { } problem && seen.Add(problem.Title + problem.File + problem.Line))
            {
                problems.Add(problem);
            }
        }

        if (problems.Count == 0)
        {
            var last = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)) ?? "";
            problems.Add(new BuildProblem("The build stopped with an error",
                "We couldn't work out the exact cause. Turn on Advanced mode to see the full build log.", null, null, last));
        }

        return problems;
    }

    public static BuildProblem? TryTranslate(string line)
    {
        foreach (var rule in Rules)
        {
            var m = rule.Pattern.Match(line);
            if (m.Success)
            {
                var (title, explanation) = rule.Explain(m);
                var loc = Location().Match(line);
                return new BuildProblem(title, explanation,
                    loc.Success ? loc.Groups["file"].Value : null,
                    loc.Success ? int.Parse(loc.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture) : null,
                    line.Trim());
            }
        }

        return null;
    }

    private static string NameHint(string name) => name switch
    {
        _ when name.StartsWith("SPECIES_", StringComparison.Ordinal) => "No Pokémon has this name.",
        _ when name.StartsWith("MOVE_", StringComparison.Ordinal) => "No move has this name.",
        _ when name.StartsWith("ITEM_", StringComparison.Ordinal) => "No item has this name.",
        _ when name.StartsWith("ABILITY_", StringComparison.Ordinal) => "No ability has this name.",
        _ when name.StartsWith("TYPE_", StringComparison.Ordinal) => "No type has this name.",
        _ => "The game's code uses a name it doesn't know.",
    };

    [GeneratedRegex(@"^(?<file>[^:\s][^:]*?\.[A-Za-z0-9]+):(?<line>\d+)(?::\d+)?:")]
    private static partial Regex Location();

    [GeneratedRegex(@"error: [‘'`](?<name>\w+)['’] undeclared")]
    private static partial Regex Undeclared();

    [GeneratedRegex(@"undefined reference to [`‘'](?<name>\w+)['’]")]
    private static partial Regex UndefinedReference();

    [GeneratedRegex(@"multiple definition of [`‘'](?<name>\w+)['’]")]
    private static partial Regex MultipleDefinition();

    [GeneratedRegex(@"region [`‘']?ROM['’]? overflowed|will not fit in region")]
    private static partial Regex RomTooBig();

    [GeneratedRegex(@"fatal error: (?<name>[\w./-]+): No such file or directory")]
    private static partial Regex MissingHeader();

    [GeneratedRegex(@"No rule to make target [`‘'](?<name>[^'’]+)['’]")]
    private static partial Regex NoRule();

    [GeneratedRegex(@"(?:sh: \d+: |/bin/sh: )?(?<name>[\w.+-]+): (?:command )?not found")]
    private static partial Regex CommandNotFound();

    [GeneratedRegex(@"error: (?:expected|syntax error|stray|missing terminating)")]
    private static partial Regex SyntaxError();

    [GeneratedRegex(@"Permission denied")]
    private static partial Regex PermissionDenied();

    [GeneratedRegex(@"No space left on device")]
    private static partial Regex OutOfSpace();

    [GeneratedRegex(@"no git history found")]
    private static partial Regex NoGitHistory();
}
