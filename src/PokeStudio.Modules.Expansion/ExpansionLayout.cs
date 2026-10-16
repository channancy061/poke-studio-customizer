using System.Globalization;
using System.Text.RegularExpressions;
using PokeStudio.Core.SourceCode;

namespace PokeStudio.Modules.Expansion;

/// <summary>Where things live inside a pokeemerald-expansion folder, plus the project's settings.</summary>
internal sealed partial class ExpansionLayout
{
    public const string ExpansionHeader = "include/constants/expansion.h";
    public const string SpeciesInfoRoot = "src/data/pokemon/species_info.h";
    public const string SpeciesInfoDir = "src/data/pokemon/species_info";
    public const string EggMovesFile = "src/data/pokemon/egg_moves.h";
    public const string LearnablesJson = "src/data/pokemon/all_learnables.json";
    public const string TmsHmsFile = "include/constants/tms_hms.h";
    public const string GlobalConstants = "include/constants/global.h";

    public ExpansionLayout(string root)
    {
        Root = Path.GetFullPath(root);
        Version = ReadVersion(Root);
        Macros = new MacroTable();
        var configDir = Path.Combine(Root, "include", "config");
        if (Directory.Exists(configDir))
        {
            // general.h first: it defines GEN_1..GEN_9 and GEN_LATEST used by the others.
            var files = Directory.GetFiles(configDir, "*.h").OrderBy(f => Path.GetFileName(f) == "general.h" ? 0 : 1).ThenBy(f => f, StringComparer.Ordinal);
            foreach (var f in files)
            {
                Macros.LoadDefines(File.ReadAllText(f));
            }
        }

        foreach (var (name, value) in new[] { ("GEN_1", "0"), ("GEN_2", "1"), ("GEN_3", "2"), ("GEN_4", "3"), ("GEN_5", "4"), ("GEN_6", "5"), ("GEN_7", "6"), ("GEN_8", "7"), ("GEN_9", "8") })
        {
            if (!Macros.IsDefined(name))
            {
                Macros.Define(name, value);
            }
        }

        if (!Macros.IsDefined("GEN_LATEST"))
        {
            Macros.Define("GEN_LATEST", "GEN_9");
        }
    }

    public string Root { get; }
    public Version? Version { get; }
    public MacroTable Macros { get; }

    public string Full(string relative) => Path.Combine([Root, .. relative.Split('/')]);

    /// <summary>"firered" for pokefirered-expansion, "emerald" for pokeemerald-expansion (from the Makefile).</summary>
    public string BuildName
    {
        get
        {
            var makefile = Full("Makefile");
            var text = File.Exists(makefile) ? File.ReadAllText(makefile) : "";
            var version = Regex.Match(text, @"^GAME_VERSION\s*\??=\s*(\w+)", RegexOptions.Multiline);
            if (version.Success)
            {
                var block = Regex.Match(text, $@"ifeq\s*\(\$\(GAME_VERSION\),\s*{version.Groups[1].Value}\)[\s\S]*?BUILD_NAME\s*:?=\s*(\w+)");
                if (block.Success)
                {
                    return block.Groups[1].Value;
                }
            }

            var plain = Regex.Match(text, @"^BUILD_NAME\s*\??=\s*(\w+)", RegexOptions.Multiline);
            return plain.Success ? plain.Groups[1].Value : "emerald";
        }
    }

    public bool IsFireRedBase => BuildName is "firered" or "leafgreen";

    /// <summary>The built game's name without ".gba" (the Makefile's FILE_NAME, e.g. "pokefirered").</summary>
    public string FileName
    {
        get
        {
            var makefile = Full("Makefile");
            var text = File.Exists(makefile) ? File.ReadAllText(makefile) : "";
            var name = Regex.Match(text, @"^FILE_NAME\s*:=[ \t]*([^\s#]+)", RegexOptions.Multiline);
            return (name.Success ? name.Groups[1].Value : "poke$(BUILD_NAME)").Replace("$(BUILD_NAME)", BuildName, StringComparison.Ordinal);
        }
    }

    /// <summary>The standard message box width in pixels, from src/menu.c (27 tiles in Emerald, 26 in FireRed).</summary>
    public int TextBoxWidth
    {
        get
        {
            var menu = Full("src/menu.c");
            if (File.Exists(menu))
            {
                var text = File.ReadAllText(menu);
                var at = text.IndexOf("sStandardTextBox_WindowTemplates[]", StringComparison.Ordinal);
                if (at >= 0 && Regex.Match(text[at..], @"\.width\s*=\s*(\d+)") is { Success: true } m)
                {
                    return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 8;
                }
            }

            return IsFireRedBase ? 208 : 216;
        }
    }

    /// <summary>The generation-specific level-up file the game is built with (P_LVL_UP_LEARNSETS).</summary>
    public string LevelUpFile
    {
        get
        {
            var gen = Macros.TryEvaluate("P_LVL_UP_LEARNSETS") ?? 8;
            var n = (int)Math.Clamp(gen, 0, 8) + 1;
            for (; n >= 1; n--)
            {
                var rel = $"src/data/pokemon/level_up_learnsets/gen_{n}.h";
                if (File.Exists(Full(rel)))
                {
                    return rel;
                }
            }

            return "src/data/pokemon/level_up_learnsets/gen_9.h";
        }
    }

    /// <summary>species_info.h plus every species_info/*.h file (gen_1_families.h …).</summary>
    public IReadOnlyList<string> SpeciesFiles()
    {
        var list = new List<string>();
        if (File.Exists(Full(SpeciesInfoRoot)))
        {
            list.Add(SpeciesInfoRoot);
        }

        var dir = Full(SpeciesInfoDir);
        if (Directory.Exists(dir))
        {
            list.AddRange(Directory.GetFiles(dir, "*.h")
                .Select(f => SpeciesInfoDir + "/" + Path.GetFileName(f))
                .Where(f => f.Contains("families", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal));
        }

        return list;
    }

    public int NameLength(string macro, int fallback)
    {
        var path = Full(GlobalConstants);
        if (!File.Exists(path))
        {
            return fallback;
        }

        var table = new MacroTable();
        table.LoadDefines(File.ReadAllText(path));
        if (!table.IsDefined(macro))
        {
            return fallback; // an unknown name would otherwise evaluate to 0, as in C
        }

        return (int)(table.TryEvaluate(macro) ?? fallback);
    }

    public static bool LooksLikeExpansion(string root) =>
        File.Exists(Path.Combine(root, "include", "constants", "expansion.h"))
        && File.Exists(Path.Combine(root, "src", "data", "pokemon", "species_info.h"))
        && File.Exists(Path.Combine(root, "Makefile"));

    public static Version? ReadVersion(string root)
    {
        var path = Path.Combine(root, "include", "constants", "expansion.h");
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path);
        int Get(string name)
        {
            var m = Regex.Match(text, $@"#define\s+{name}\s+(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        }

        var major = Get("EXPANSION_VERSION_MAJOR");
        return major == 0 && Get("EXPANSION_VERSION_MINOR") == 0 ? null : new Version(major, Get("EXPANSION_VERSION_MINOR"), Get("EXPANSION_VERSION_PATCH"));
    }
}
