using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PokeStudio.Core.Models;
using PokeStudio.Core.SourceCode;

namespace PokeStudio.Modules.Expansion;

/// <summary>A C array like <c>static const struct LevelUpMove sFooLevelUpLearnset[] = { … };</c>.</summary>
internal sealed record ArrayBlock(string File, string Symbol, int Open, int Close, bool Locked);

internal static partial class LearnsetFiles
{
    [GeneratedRegex(@"static\s+const\s+struct\s+LevelUpMove\s+(\w+)\s*\[\s*\]\s*=\s*\{")]
    private static partial Regex LevelUpStart();

    [GeneratedRegex(@"static\s+const\s+u16\s+(\w+)\s*\[\s*\]\s*=\s*\{")]
    private static partial Regex EggStart();

    [GeneratedRegex(@"LEVEL_UP_MOVE\s*\(\s*(\d+)\s*,\s*(\w+)\s*\)")]
    private static partial Regex LevelUpEntry();

    [GeneratedRegex(@"\bF\((\w+)\)")]
    private static partial Regex MachineEntry();

    public static Dictionary<string, ArrayBlock> FindArrays(SourceFile file, bool levelUp)
    {
        var result = new Dictionary<string, ArrayBlock>(StringComparer.Ordinal);
        var regex = levelUp ? LevelUpStart() : EggStart();
        foreach (Match m in regex.Matches(file.Text))
        {
            var open = m.Index + m.Length - 1;
            var close = CScanner.FindMatching(file.Text, open);
            var locked = file.Map.HasDirectiveBetween(open, close);
            result[m.Groups[1].Value] = new ArrayBlock(file.Relative, m.Groups[1].Value, open, close, locked);
        }

        return result;
    }

    public static List<LevelUpMove> ReadLevelUp(SourceFile file, ArrayBlock block) =>
        LevelUpEntry().Matches(file.Text[block.Open..block.Close])
            .Select(m => new LevelUpMove(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value))
            .ToList();

    public static List<string> ReadEggMoves(SourceFile file, ArrayBlock block) =>
        CScanner.SplitTopLevel(file.Text, block.Open + 1, block.Close)
            .Select(p => CScanner.StripComments(file.Text[p.Start..p.End]))
            .Where(s => s.StartsWith("MOVE_", StringComparison.Ordinal) && s != "MOVE_UNAVAILABLE")
            .ToList();

    private static string EntryIndent(SourceFile file, ArrayBlock block)
    {
        var firstLine = file.Text.IndexOf('\n', block.Open);
        if (firstLine < 0 || firstLine >= block.Close)
        {
            return "    ";
        }

        var indent = CScanner.IndentAt(file.Text, firstLine + 1);
        return indent.Length == 0 ? "    " : indent;
    }

    public static TextEdit LevelUpEdit(SourceFile file, ArrayBlock block, IReadOnlyList<LevelUpMove> moves)
    {
        var nl = CScanner.LineEnding(file.Text);
        var indent = EntryIndent(file, block);
        var sb = new StringBuilder(nl);
        foreach (var m in moves)
        {
            sb.Append(indent).Append("LEVEL_UP_MOVE(").Append(m.Level.ToString(CultureInfo.InvariantCulture).PadLeft(2)).Append(", ").Append(m.Move).Append("),").Append(nl);
        }

        sb.Append(indent).Append("LEVEL_UP_END").Append(nl);
        return new TextEdit(block.Open + 1, block.Close - block.Open - 1, sb.ToString());
    }

    public static TextEdit EggEdit(SourceFile file, ArrayBlock block, IReadOnlyList<string> moves)
    {
        var nl = CScanner.LineEnding(file.Text);
        var indent = EntryIndent(file, block);
        var sb = new StringBuilder(nl);
        foreach (var m in moves)
        {
            sb.Append(indent).Append(m).Append(',').Append(nl);
        }

        sb.Append(indent).Append("MOVE_UNAVAILABLE,").Append(nl);
        return new TextEdit(block.Open + 1, block.Close - block.Open - 1, sb.ToString());
    }

    /// <summary>TM and HM moves in order, from tms_hms.h (FOREACH_TM / FOREACH_HM).</summary>
    public static List<(string Label, string Move)> ReadMachines(string text)
    {
        var list = new List<(string, string)>();
        foreach (var (macro, prefix) in new[] { ("FOREACH_TM", "TM"), ("FOREACH_HM", "HM") })
        {
            var start = text.IndexOf("#define " + macro, StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            var end = start;
            while (true)
            {
                var nl = text.IndexOf('\n', end);
                if (nl < 0)
                {
                    end = text.Length;
                    break;
                }

                var lineEndsWithBackslash = text[..nl].TrimEnd('\r').EndsWith('\\');
                end = nl + 1;
                if (!lineEndsWithBackslash)
                {
                    break;
                }
            }

            var n = 1;
            foreach (Match m in MachineEntry().Matches(text[start..end]))
            {
                list.Add(($"{prefix}{n++:00}", "MOVE_" + m.Groups[1].Value));
            }
        }

        return list;
    }
}

/// <summary>all_learnables.json: species key → moves it can be taught. Written back in the same layout.</summary>
internal sealed class LearnablesFile
{
    public required List<string> Order { get; init; }
    public required Dictionary<string, List<string>> Moves { get; init; }
    public required string Newline { get; init; }
    public required bool TrailingNewline { get; init; }

    public static LearnablesFile? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var text = File.ReadAllText(path);
        using var doc = JsonDocument.Parse(text);
        var order = new List<string>();
        var moves = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            order.Add(p.Name);
            moves[p.Name] = p.Value.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        }

        return new LearnablesFile
        {
            Order = order,
            Moves = moves,
            Newline = CScanner.LineEnding(text),
            TrailingNewline = text.EndsWith('\n'),
        };
    }

    public string Serialize()
    {
        var nl = Newline;
        var sb = new StringBuilder("{").Append(nl);
        for (var i = 0; i < Order.Count; i++)
        {
            var key = Order[i];
            var list = Moves[key];
            sb.Append("  ").Append(JsonSerializer.Serialize(key)).Append(": [");
            if (list.Count > 0)
            {
                sb.Append(nl);
                for (var j = 0; j < list.Count; j++)
                {
                    sb.Append("    ").Append(JsonSerializer.Serialize(list[j])).Append(j < list.Count - 1 ? "," : "").Append(nl);
                }

                sb.Append("  ");
            }

            sb.Append(']').Append(i < Order.Count - 1 ? "," : "").Append(nl);
        }

        sb.Append('}');
        if (TrailingNewline)
        {
            sb.Append(nl);
        }

        return sb.ToString();
    }
}

/// <summary>Reads move/ability/item/type lists and their real display names from the project.</summary>
internal static partial class Catalogs
{
    [GeneratedRegex(@"\.name\s*=\s*(?:\w+\s*\()?\s*""((?:[^""\\]|\\.)*)""")]
    private static partial Regex NameField();

    public static List<string> EnumMembers(string text, string prefix)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, $@"^\s*({Regex.Escape(prefix)}[A-Z0-9_]+)\s*(=\s*([^,\n]+))?\s*,", RegexOptions.Multiline))
        {
            var name = m.Groups[1].Value;
            var rhs = m.Groups[3].Value.Trim();
            if (name.Contains("COUNT", StringComparison.Ordinal) || name.EndsWith("_END", StringComparison.Ordinal) || name.StartsWith(prefix + "FIRST", StringComparison.Ordinal))
            {
                continue;
            }

            // skip aliases such as MOVE_DOUBLESLAP = MOVE_DOUBLE_SLAP
            if (rhs.StartsWith(prefix, StringComparison.Ordinal) && !rhs.Contains("COUNT", StringComparison.Ordinal))
            {
                continue;
            }

            if (seen.Add(name))
            {
                list.Add(name);
            }
        }

        return list;
    }

    /// <summary>Enum aliases such as "SPECIES_CASTFORM = SPECIES_CASTFORM_NORMAL," (alias → target).</summary>
    public static Dictionary<string, string> Aliases(string text, string prefix)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, $@"^\s*({Regex.Escape(prefix)}[A-Z0-9_]+)\s*=\s*({Regex.Escape(prefix)}[A-Z0-9_]+)\s*,", RegexOptions.Multiline))
        {
            map.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
        }

        return map;
    }

    /// <summary>"#define PREFIX_NAME value // comment" lines, in order (value and comment trimmed).</summary>
    public static List<(string Key, string Value, string? Comment)> DefineMembers(string text, string prefix)
    {
        var list = new List<(string, string, string?)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, $@"^\s*#define\s+({Regex.Escape(prefix)}[A-Z0-9_]+)\s+([^\r\n]*?)\s*(?://\s*([^\r\n]*))?\r?$", RegexOptions.Multiline))
        {
            var key = m.Groups[1].Value;
            if (seen.Add(key))
            {
                list.Add((key, m.Groups[2].Value.Trim(), m.Groups[3].Success && m.Groups[3].Value.Trim().Length > 0 ? m.Groups[3].Value.Trim() : null));
            }
        }

        return list;
    }

    /// <summary>Display names from a data file, e.g. [MOVE_TACKLE] = { .name = COMPOUND_STRING("Tackle"), … }.</summary>
    public static Dictionary<string, string> DisplayNames(string? text, string prefix)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (text is null)
        {
            return names;
        }

        foreach (Match m in Regex.Matches(text, $@"\[({Regex.Escape(prefix)}[A-Z0-9_]+)\]\s*=\s*\{{"))
        {
            var window = text.Substring(m.Index, Math.Min(700, text.Length - m.Index));
            var n = NameField().Match(window);
            if (n.Success)
            {
                names.TryAdd(m.Groups[1].Value, CStrings.DecodeLiterals("\"" + n.Groups[1].Value + "\""));
            }
        }

        return names;
    }

    public static List<CatalogEntry> Build(string? constants, string? data, string prefix, string noneName = "None")
    {
        if (constants is null)
        {
            return [];
        }

        var names = DisplayNames(data, prefix);
        return EnumMembers(constants, prefix)
            .Select(k => new CatalogEntry(k, k.EndsWith("_NONE", StringComparison.Ordinal) ? noneName : names.GetValueOrDefault(k) ?? GameCatalog.Prettify(k)))
            .ToList();
    }

    public static readonly (string Key, string Name, string Description)[] EvolutionMethods =
    [
        ("EVO_LEVEL", "Reaches a level", "Evolves after a level-up at or above the level you choose (0 = any level, combined with a condition)."),
        ("EVO_ITEM", "Uses an item", "Evolves when you use the chosen item on it from the bag."),
        ("EVO_TRADE", "Is traded", "Evolves when traded to another game."),
        ("EVO_LEVEL_BATTLE_ONLY", "Reaches a level in battle", "Evolves only when the level-up happens during a battle."),
        ("EVO_BATTLE_END", "After a battle", "Evolves when a battle ends, if its condition is met."),
        ("EVO_SPIN", "Player spins", "Evolves when the player spins around in the overworld."),
        ("EVO_SCRIPT_TRIGGER", "Special event", "Evolves when an event in the game triggers it."),
        ("EVO_SPLIT_FROM_EVO", "Appears alongside an evolution", "A copy appears when another evolution happens (like Shedinja)."),
        ("EVO_NONE", "Breeding only", "Not a real evolution; used so this form hatches from eggs."),
    ];

    public static readonly (string Key, string Name)[] EvolutionConditions =
    [
        ("IF_GENDER", "Is a certain gender"), ("IF_TIME", "At a time of day"), ("IF_NOT_TIME", "Not at a time of day"),
        ("IF_MIN_FRIENDSHIP", "Is friendly enough"), ("IF_ATK_GT_DEF", "Attack higher than Defense"), ("IF_ATK_EQ_DEF", "Attack equals Defense"),
        ("IF_ATK_LT_DEF", "Attack lower than Defense"), ("IF_HOLD_ITEM", "Holds an item"), ("IF_MIN_BEAUTY", "Beauty is high enough"),
        ("IF_SPECIES_IN_PARTY", "A Pokémon is in the party"), ("IF_IN_MAP", "In a certain place"), ("IF_IN_MAPSEC", "In a certain area"),
        ("IF_KNOWS_MOVE", "Knows a move"), ("IF_TRADE_PARTNER_SPECIES", "Traded for a certain Pokémon"), ("IF_TYPE_IN_PARTY", "A type is in the party"),
        ("IF_WEATHER", "In certain weather"), ("IF_KNOWS_MOVE_TYPE", "Knows a move of a type"), ("IF_NATURE", "Has a nature"),
        ("IF_REGION", "In a region"), ("IF_NOT_REGION", "Not in a region"), ("IF_USED_MOVE_X_TIMES", "Used a move several times"),
        ("IF_MIN_OVERWORLD_STEPS", "Walked enough steps"), ("IF_BAG_ITEM_COUNT", "Has enough of an item"),
    ];
}
