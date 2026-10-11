using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PokeStudio.Core.Models;
using PokeStudio.Core.SourceCode;

namespace PokeStudio.Modules.Expansion;

/// <summary>src/data/wild_encounters.json, rewritten in the same 2-space layout the project uses.</summary>
internal sealed class EncountersFile
{
    public const string File = "src/data/wild_encounters.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly JsonNode _root;
    private readonly bool _trailingNewline;

    private EncountersFile(JsonNode root, bool trailingNewline, bool roundTrips)
    {
        _root = root;
        _trailingNewline = trailingNewline;
        RoundTrips = roundTrips;
    }

    /// <summary>False when writing the file back would change its layout; editing is then locked.</summary>
    public bool RoundTrips { get; }

    public static EncountersFile? Load(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return null;
        }

        var text = System.IO.File.ReadAllText(path);
        var root = JsonNode.Parse(text)!;
        var file = new EncountersFile(root, text.EndsWith('\n'), roundTrips: true);
        return file.Serialize() == text ? file : new EncountersFile(root, text.EndsWith('\n'), roundTrips: false);
    }

    public string Serialize()
    {
        var s = _root.ToJsonString(WriteOptions);
        return _trailingNewline ? s + "\n" : s;
    }

    public (List<EncounterTable> Tables, List<EncounterSlotChances> Chances) Read()
    {
        var tables = new List<EncounterTable>();
        var chances = new List<EncounterSlotChances>();
        foreach (var group in _root["wild_encounter_groups"]?.AsArray() ?? [])
        {
            var label = group?["label"]?.GetValue<string>() ?? "";
            if (chances.Count == 0)
            {
                foreach (var f in group?["fields"]?.AsArray() ?? [])
                {
                    var groups = new Dictionary<string, IReadOnlyList<int>>(StringComparer.Ordinal);
                    if (f?["groups"] is JsonObject g)
                    {
                        foreach (var (k, v) in g)
                        {
                            groups[k] = v!.AsArray().Select(x => x!.GetValue<int>()).ToList();
                        }
                    }

                    chances.Add(new EncounterSlotChances(f!["type"]!.GetValue<string>(), f["encounter_rates"]!.AsArray().Select(x => x!.GetValue<int>()).ToList(), groups));
                }
            }

            foreach (var e in group?["encounters"]?.AsArray() ?? [])
            {
                var methods = new List<EncounterMethodTable>();
                foreach (var (prop, value) in e!.AsObject())
                {
                    if (value is JsonObject m && m["mons"] is JsonArray mons)
                    {
                        methods.Add(new EncounterMethodTable
                        {
                            Type = prop,
                            Rate = m["encounter_rate"]?.GetValue<int>() ?? 0,
                            Slots = mons.Select(x => new EncounterSlot(x!["species"]!.GetValue<string>(), x["min_level"]!.GetValue<int>(), x["max_level"]!.GetValue<int>())).ToList(),
                        });
                    }
                }

                tables.Add(new EncounterTable
                {
                    Key = e["base_label"]?.GetValue<string>() ?? $"{label}#{tables.Count}",
                    Map = e["map"]?.GetValue<string>() ?? "",
                    Group = label,
                    Methods = methods,
                    LockedReason = RoundTrips ? null : "This file's layout couldn't be kept exactly, so wild encounters are read-only for now.",
                });
            }
        }

        return (tables, chances);
    }

    /// <summary>Writes changed tables into the JSON tree (only the values that changed).</summary>
    public void Update(IEnumerable<EncounterTable> changed)
    {
        var byKey = changed.ToDictionary(t => t.Key, StringComparer.Ordinal);
        foreach (var group in _root["wild_encounter_groups"]?.AsArray() ?? [])
        {
            foreach (var e in group?["encounters"]?.AsArray() ?? [])
            {
                if (e?["base_label"]?.GetValue<string>() is not { } key || !byKey.TryGetValue(key, out var table))
                {
                    continue;
                }

                foreach (var m in table.Methods)
                {
                    if (e[m.Type] is not JsonObject node)
                    {
                        continue;
                    }

                    if (node["encounter_rate"]?.GetValue<int>() != m.Rate)
                    {
                        node["encounter_rate"] = m.Rate;
                    }

                    var mons = node["mons"]!.AsArray();
                    for (var i = 0; i < m.Slots.Count && i < mons.Count; i++)
                    {
                        var s = m.Slots[i];
                        var o = mons[i]!;
                        if (o["min_level"]!.GetValue<int>() != s.MinLevel)
                        {
                            o["min_level"] = s.MinLevel;
                        }

                        if (o["max_level"]!.GetValue<int>() != s.MaxLevel)
                        {
                            o["max_level"] = s.MaxLevel;
                        }

                        if (o["species"]!.GetValue<string>() != s.Species)
                        {
                            o["species"] = s.Species;
                        }
                    }
                }
            }
        }
    }
}

/// <summary>Reads items from src/data/items.h and writes minimal edits.</summary>
internal static partial class ItemsInfo
{
    public const string File = "src/data/items.h";

    public static Item ToItem(SourceFile file, SpeciesBlock block)
    {
        var locked = new Dictionary<string, string>();
        var dependent = new Dictionary<string, string>();
        string? Value(string field, string model)
        {
            var f = block.Get(field);
            if (f is null)
            {
                return null;
            }

            if (f.SpansDirectives)
            {
                locked[model] = "This value is split across game settings in the source file.";
                return null;
            }

            var v = SpeciesInfo.Resolve(SpeciesInfo.Raw(file, f), file.Macros, out var cond);
            if (cond is not null || block.IsSettingDependent(field))
            {
                dependent[model] = $"Depends on a game setting ({cond ?? f.Condition ?? "#if"}). Edits change the value your settings use.";
            }

            return v;
        }

        int? Int(string field, string model)
        {
            if (Value(field, model) is not { } v)
            {
                return null;
            }

            var n = file.Macros.TryEvaluate(v);
            if (n is null)
            {
                locked[model] = $"Set with “{v}”, which the app can't calculate.";
            }

            return n is null ? null : (int)n.Value;
        }

        var name = Value("name", "Name") is { } n ? CStrings.DecodeLiterals(n) : GameCatalog.Prettify(block.Symbol);
        var description = "";
        if (Value("description", "Description") is { } d)
        {
            if (d.StartsWith("COMPOUND_STRING", StringComparison.Ordinal) || d.StartsWith('"'))
            {
                description = CStrings.DecodeLiterals(d);
            }
            else
            {
                locked["Description"] = "This description is shared with other items; edit it in the source file.";
            }
        }

        if (!block.Active)
        {
            locked["*"] = "This item is switched off by your game's settings.";
        }

        return new Item
        {
            Key = block.Symbol,
            Name = name,
            Price = Int("price", "Price") ?? 0,
            Description = description,
            EffectAmount = Int("holdEffectParam", "EffectAmount"),
            Pocket = Value("pocket", "Pocket") ?? "",
            HoldEffect = Value("holdEffect", "HoldEffect") ?? "",
            LockedFields = locked,
            SettingDependentFields = dependent,
        };
    }

    public static List<TextEdit> EditsFor(SourceFile file, SpeciesBlock block, Item before, Item after)
    {
        var edits = new List<TextEdit>();
        var nl = CScanner.LineEnding(file.Text);
        void Set(string field, string value)
        {
            if (block.Get(field) is { } f)
            {
                var (s, e) = SpeciesInfo.ActiveSpan(file.Text, f.ValueStart, f.ValueEnd, file.Macros);
                edits.Add(new TextEdit(s, e - s, value));
            }
        }

        if (before.Name != after.Name)
        {
            Set("name", $"ITEM_NAME(\"{CStrings.Escape(after.Name)}\")");
        }

        if (before.Price != after.Price)
        {
            Set("price", after.Price.ToString(CultureInfo.InvariantCulture));
        }

        if (before.EffectAmount != after.EffectAmount && after.EffectAmount is { } amount)
        {
            Set("holdEffectParam", amount.ToString(CultureInfo.InvariantCulture));
        }

        if (before.Description != after.Description && block.Get("description") is { } desc)
        {
            var indent = CScanner.IndentAt(file.Text, desc.DesignatorStart) + "    ";
            edits.Add(new TextEdit(desc.ValueStart, desc.ValueEnd - desc.ValueStart, "COMPOUND_STRING(" + CStrings.EncodeLines(after.Description, indent, nl) + ")"));
        }

        return edits;
    }
}

/// <summary>Shop lists in data/maps/*/scripts.inc (".2byte ITEM_…" lines ending in "pokemartlistend").</summary>
internal static partial class MartScripts
{
    public const string TierMarker = "@ Poke Studio badge tier";
    private const string BlockStart = "@ Poke Studio: badge-tier shops for ";
    private const string BlockEnd = "@ Poke Studio: end";

    [GeneratedRegex(@"^(\w+):{1,2}[ \t]*\r?\n((?:[ \t]*\.2byte[ \t]+\w+[ \t]*\r?\n)*)[ \t]*pokemartlistend", RegexOptions.Multiline)]
    private static partial Regex ListRegex();

    [GeneratedRegex(@"\.2byte[ \t]+(\w+)")]
    private static partial Regex ItemRegex();

    [GeneratedRegex(@"^[ \t]*pokemart[ \t]+(\w+)[ \t]*\r?$", RegexOptions.Multiline)]
    private static partial Regex CallRegex();

    [GeneratedRegex(@"^[ \t]*goto_if_set[ \t]+FLAG_BADGE0(\d)_GET,[ \t]*\w+[ \t]*@ Poke Studio badge tier[^\n]*\n", RegexOptions.Multiline)]
    private static partial Regex TierGotoRegex();

    public sealed record MartList(string File, string Label, int BodyStart, int BodyEnd, IReadOnlyList<string> Items);

    public sealed record ScriptFile(string Relative, string Text, List<MartList> Lists, Dictionary<string, int> CallSites, Dictionary<string, Dictionary<int, List<string>>> Tiers);

    public static ScriptFile Parse(string relative, string text)
    {
        // generated tier blocks are read for their stock, then ignored like any other text
        var tiers = new Dictionary<string, Dictionary<int, List<string>>>(StringComparer.Ordinal);
        var generated = new List<(int Start, int End)>();
        for (var i = text.IndexOf(BlockStart, StringComparison.Ordinal); i >= 0; i = text.IndexOf(BlockStart, i + 1, StringComparison.Ordinal))
        {
            var end = text.IndexOf(BlockEnd, i, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            end = text.IndexOf('\n', end) is var nl and >= 0 ? nl + 1 : text.Length;
            generated.Add((i, end));
            var block = text[i..end];
            foreach (Match m in Regex.Matches(block, @"^(\w+)_PokeStudioTier(\d)_Items:[ \t]*\r?\n((?:[ \t]*\.2byte[ \t]+\w+[ \t]*\r?\n)*)", RegexOptions.Multiline))
            {
                if (!tiers.TryGetValue(m.Groups[1].Value, out var t))
                {
                    tiers[m.Groups[1].Value] = t = [];
                }

                t[int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)] = ItemRegex().Matches(m.Groups[3].Value).Select(x => x.Groups[1].Value).ToList();
            }
        }

        bool InGenerated(int pos) => generated.Any(g => pos >= g.Start && pos < g.End);
        var lists = new List<MartList>();
        foreach (Match m in ListRegex().Matches(text))
        {
            if (InGenerated(m.Index))
            {
                continue;
            }

            var body = m.Groups[2];
            lists.Add(new MartList(relative, m.Groups[1].Value, body.Index, body.Index + body.Length,
                ItemRegex().Matches(body.Value).Select(x => x.Groups[1].Value).ToList()));
        }

        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in CallRegex().Matches(text))
        {
            if (!InGenerated(m.Index))
            {
                calls.TryAdd(m.Groups[1].Value, calls.ContainsKey(m.Groups[1].Value) ? -1 : m.Index);
            }
        }

        return new ScriptFile(relative, text, lists, calls, tiers);
    }

    public static string PlaceName(string relative, string label, int listsInFile)
    {
        var folder = relative.Split('/')[^2];
        var place = Regex.Replace(folder, "(?<=[a-z0-9])(?=[A-Z])|_", " ").Replace("  ", " ", StringComparison.Ordinal).Trim();
        if (listsInFile <= 1)
        {
            return place;
        }

        var suffix = label.Contains("Pokemart", StringComparison.Ordinal) ? label[(label.IndexOf("Pokemart", StringComparison.Ordinal) + 8)..].Trim('_') : label;
        return suffix.Length == 0 ? place : $"{place} ({Regex.Replace(suffix, "(?<=[a-z0-9])(?=[A-Z])|_", " ").Trim()})";
    }

    /// <summary>All edits for one file: changed stock lists, and the badge-tier shops regenerated if any mart in the file changed tiers.</summary>
    public static List<TextEdit> EditsFor(ScriptFile file, IReadOnlyList<(Mart Before, Mart After)> changed)
    {
        var edits = new List<TextEdit>();
        var text = file.Text;
        var nl = CScanner.LineEnding(text);
        foreach (var (before, after) in changed)
        {
            if (!before.Items.SequenceEqual(after.Items) && file.Lists.FirstOrDefault(l => l.Label == after.Key) is { } list)
            {
                var indent = IndentOf(text, list.BodyStart);
                edits.Add(new TextEdit(list.BodyStart, list.BodyEnd - list.BodyStart, string.Concat(after.Items.Select(i => $"{indent}.2byte {i}{nl}"))));
            }
        }

        var tiersChanged = changed.Any(c => !SameTiers(c.Before.BadgeTiers, c.After.BadgeTiers));
        if (!tiersChanged)
        {
            return edits;
        }

        // remove every generated piece in this file, then write the current tiers back
        foreach (Match m in TierGotoRegex().Matches(text))
        {
            edits.Add(new TextEdit(m.Index, m.Length, ""));
        }

        for (var i = text.IndexOf(BlockStart, StringComparison.Ordinal); i >= 0; i = text.IndexOf(BlockStart, i + 1, StringComparison.Ordinal))
        {
            var end = text.IndexOf(BlockEnd, i, StringComparison.Ordinal);
            end = end < 0 ? text.Length : (text.IndexOf('\n', end) is var e and >= 0 ? e + 1 : text.Length);
            var start = i;
            if (start >= nl.Length && text.AsSpan(start - nl.Length, nl.Length).SequenceEqual(nl))
            {
                start -= nl.Length; // the blank line we added before the block
            }

            edits.Add(new TextEdit(start, end - start, ""));
        }

        var appendix = new StringBuilder();
        var tierLookup = changed.ToDictionary(c => c.After.Key, c => c.After.BadgeTiers, StringComparer.Ordinal);
        foreach (var list in file.Lists)
        {
            IReadOnlyDictionary<int, IReadOnlyList<string>> tiers = tierLookup.TryGetValue(list.Label, out var t)
                ? t
                : file.Tiers.TryGetValue(list.Label, out var existing)
                    ? Additions(list.Items, existing)
                    : new Dictionary<int, IReadOnlyList<string>>();
            if (tiers.Count == 0 || !file.CallSites.TryGetValue(list.Label, out var call) || call < 0)
            {
                continue;
            }

            var baseItems = changed.FirstOrDefault(c => c.After.Key == list.Label).After?.Items ?? list.Items;
            var indent = IndentOf(text, call);
            var gotos = new StringBuilder();
            foreach (var badge in tiers.Keys.Order().Reverse())
            {
                gotos.Append($"{indent}goto_if_set FLAG_BADGE0{badge}_GET, {list.Label}_PokeStudioTier{badge} {TierMarker} {badge}{nl}");
            }

            edits.Add(new TextEdit(call, 0, gotos.ToString()));
            var after = AfterCall(text, call, nl);
            appendix.Append(nl).Append(BlockStart).Append(list.Label).Append(" (generated; edit them in the app)").Append(nl);
            var stock = baseItems.ToList();
            foreach (var badge in tiers.Keys.Order())
            {
                stock = [.. stock, .. tiers[badge].Where(i => !stock.Contains(i))];
                appendix.Append($"{list.Label}_PokeStudioTier{badge}::{nl}");
                appendix.Append($"{indent}pokemart {list.Label}_PokeStudioTier{badge}_Items{nl}");
                appendix.Append(after);
                appendix.Append(nl).Append($"{indent}.align 2{nl}");
                appendix.Append($"{list.Label}_PokeStudioTier{badge}_Items:{nl}");
                foreach (var item in stock)
                {
                    appendix.Append($"{indent}.2byte {item}{nl}");
                }

                appendix.Append($"{indent}pokemartlistend{nl}");
            }

            appendix.Append(BlockEnd).Append(nl);
        }

        if (appendix.Length > 0)
        {
            var end = text.Length;
            edits.Add(new TextEdit(end, 0, (text.EndsWith('\n') ? "" : nl) + appendix));
        }

        return edits;
    }

    /// <summary>Items each tier adds on top of the base list and lower tiers (generated lists hold the full stock).</summary>
    public static Dictionary<int, IReadOnlyList<string>> Additions(IReadOnlyList<string> baseItems, Dictionary<int, List<string>> full)
    {
        var result = new Dictionary<int, IReadOnlyList<string>>();
        var seen = baseItems.ToHashSet(StringComparer.Ordinal);
        foreach (var badge in full.Keys.Order())
        {
            var added = full[badge].Where(i => !seen.Contains(i)).ToList();
            seen.UnionWith(added);
            result[badge] = added;
        }

        return result;
    }

    private static bool SameTiers(IReadOnlyDictionary<int, IReadOnlyList<string>> a, IReadOnlyDictionary<int, IReadOnlyList<string>> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v.SequenceEqual(kv.Value));

    private static string IndentOf(string text, int pos)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, pos - 1)) + 1;
        var i = lineStart;
        while (i < text.Length && text[i] is ' ' or '\t')
        {
            i++;
        }

        return i > lineStart ? text[lineStart..i] : "\t";
    }

    /// <summary>The script lines after "pokemart X" up to and including "end" (message, release…), reused by tier shops.</summary>
    private static string AfterCall(string text, int call, string nl)
    {
        var lineEnd = text.IndexOf('\n', call);
        var sb = new StringBuilder();
        var pos = lineEnd + 1;
        while (pos > 0 && pos < text.Length)
        {
            var next = text.IndexOf('\n', pos);
            var line = next < 0 ? text[pos..] : text[pos..(next + 1)];
            sb.Append(line.TrimEnd('\r', '\n')).Append(nl);
            if (line.Trim() is "end" or "return" || next < 0)
            {
                break;
            }

            pos = next + 1;
        }

        return sb.ToString();
    }
}

/// <summary>Dialogue in data/maps/*/scripts.inc and data/text/*.inc: a label followed by .string lines.</summary>
internal static partial class TextScripts
{
    [GeneratedRegex(@"^(\w+):{1,2}[ \t]*\r?\n((?:[ \t]*\.string[ \t]+""(?:[^""\\]|\\.)*""[ \t]*\r?\n?)+)", RegexOptions.Multiline)]
    private static partial Regex EntryRegex();

    [GeneratedRegex(@"\.string[ \t]+""((?:[^""\\]|\\.)*)""")]
    private static partial Regex StringRegex();

    public sealed record Entry(string File, string Label, int Start, int End, string Indent, string Text, bool Terminated);

    public static IEnumerable<Entry> Parse(string relative, string text)
    {
        foreach (Match m in EntryRegex().Matches(text))
        {
            var body = m.Groups[2];
            var raw = string.Concat(StringRegex().Matches(body.Value).Select(x => x.Groups[1].Value));
            var terminated = raw.EndsWith('$');
            var end = body.Index + body.Length;
            if (end > 0 && text[end - 1] == '\n')
            {
                end--;
                if (end > 0 && text[end - 1] == '\r')
                {
                    end--;
                }
            }

            var indentEnd = body.Index;
            while (indentEnd < text.Length && text[indentEnd] is ' ' or '\t')
            {
                indentEnd++;
            }

            yield return new Entry(relative, m.Groups[1].Value, body.Index, end, text[body.Index..indentEnd], terminated ? raw[..^1] : raw, terminated);
        }
    }

    /// <summary>The .string lines for a text: one line per visible line, ending in its \n, \l or \p code.</summary>
    public static string Format(string text, string indent, bool terminated, string nl)
    {
        // a real line break can't go inside a .string: write it as the \n code
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        var sb = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var next = -1;
            for (var j = i; j < text.Length - 1; j++)
            {
                if (text[j] == '\\' && text[j + 1] is 'n' or 'l' or 'p')
                {
                    next = j + 2;
                    break;
                }

                if (text[j] == '\\')
                {
                    j++; // skip escaped character
                }
            }

            var piece = next < 0 ? text[i..] : text[i..next];
            if (sb.Length > 0)
            {
                sb.Append(nl);
            }

            sb.Append(indent).Append(".string \"").Append(piece).Append(next < 0 && terminated ? "$" : "").Append('"');
            if (next < 0)
            {
                return sb.ToString();
            }

            i = next;
        }

        if (terminated)
        {
            sb.Append(sb.Length > 0 ? nl : "").Append(indent).Append(".string \"$\"");
        }

        return sb.ToString();
    }

    /// <summary>Friendly form for editing: real line breaks, a blank line for a new text box.</summary>
    public static string ToEditable(string raw) =>
        raw.Replace("\\p", "\n\n", StringComparison.Ordinal).Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\l", "\n", StringComparison.Ordinal);

    /// <summary>Back to the game's codes: first break in a box is \n, later ones scroll (\l), a blank line starts a new box (\p).</summary>
    public static string FromEditable(string editable)
    {
        var boxes = editable.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n");
        return string.Join("\\p", boxes.Select(box =>
        {
            var lines = box.Split('\n');
            var sb = new StringBuilder(lines[0]);
            for (var i = 1; i < lines.Length; i++)
            {
                sb.Append(i == 1 ? "\\n" : "\\l").Append(lines[i]);
            }

            return sb.ToString();
        }));
    }

    public static string PlaceName(string relative)
    {
        var parts = relative.Split('/');
        var name = parts[^1] is "scripts.inc" or "text.inc" ? parts[^2] : Path.GetFileNameWithoutExtension(parts[^1]);
        var spaced = Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])|_", " ").Replace("  ", " ", StringComparison.Ordinal).Trim();
        return string.Join(' ', spaced.Split(' ').Select(w => w.Length > 0 && char.IsLower(w[0]) ? char.ToUpperInvariant(w[0]) + w[1..] : w));
    }
}

/// <summary>Measures text in pixels with the project's own font widths (src/fonts.c + charmap.txt).</summary>
internal sealed partial class FontMetrics
{
    private readonly Dictionary<string, byte> _chars;
    private readonly byte[] _widths;

    private FontMetrics(Dictionary<string, byte> chars, byte[] widths)
    {
        _chars = chars;
        _widths = widths;
    }

    [GeneratedRegex(@"^'(.+?)'\s*=\s*([0-9A-Fa-f]{2})\s*$", RegexOptions.Multiline)]
    private static partial Regex CharLine();

    public static FontMetrics? Load(string root)
    {
        var charmap = Path.Combine(root, "charmap.txt");
        var fonts = Path.Combine(root, "src", "fonts.c");
        if (!System.IO.File.Exists(charmap) || !System.IO.File.Exists(fonts))
        {
            return null;
        }

        var chars = new Dictionary<string, byte>(StringComparer.Ordinal);
        foreach (Match m in CharLine().Matches(System.IO.File.ReadAllText(charmap)))
        {
            chars.TryAdd(m.Groups[1].Value.Replace("\\'", "'", StringComparison.Ordinal), byte.Parse(m.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        var fontText = System.IO.File.ReadAllText(fonts);
        var start = fontText.IndexOf("gFontNormalLatinGlyphWidths[]", StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var open = fontText.IndexOf('{', start);
        var close = fontText.IndexOf('}', open);
        var widths = fontText[(open + 1)..close].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => byte.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : (byte)6).ToArray();
        return widths.Length < 200 ? null : new FontMetrics(chars, widths);
    }

    /// <summary>Pixel width of one line; placeholders count at a typical worst case (e.g. {PLAYER} = 7 letters).</summary>
    public int Measure(string line)
    {
        var px = 0;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '{')
            {
                var close = line.IndexOf('}', i);
                if (close > i)
                {
                    var token = line[(i + 1)..close];
                    px += token switch
                    {
                        "PLAYER" or "RIVAL" => 7 * 6,
                        _ when token.StartsWith("STR_VAR", StringComparison.Ordinal) => 4 * 6, // usually a number or a short word
                        _ => 0, // colour and control codes take no space
                    };
                    i = close;
                    continue;
                }
            }

            if (line[i] == '\\' && i + 1 < line.Length)
            {
                i++;
            }

            var ch = line[i].ToString();
            px += _chars.TryGetValue(ch, out var code) && code < _widths.Length ? _widths[code] : 6;
        }

        return px;
    }
}

/// <summary>Level caps: src/caps.c (sLevelCapFlagMap) and include/config/caps.h.</summary>
internal static partial class CapsFiles
{
    public const string Source = "src/caps.c";
    public const string Config = "include/config/caps.h";

    [GeneratedRegex(@"\{\s*(\w+)\s*,\s*(\d+)\s*\}")]
    private static partial Regex Entry();

    public sealed record CapEntry(string Flag, int Level, int NumberStart, int NumberEnd);

    public static List<CapEntry> ReadTable(string text)
    {
        var start = text.IndexOf("sLevelCapFlagMap", StringComparison.Ordinal);
        if (start < 0)
        {
            return [];
        }

        var open = text.IndexOf('{', text.IndexOf('=', start));
        var close = CScanner.FindMatching(text, open);
        var list = new List<CapEntry>();
        foreach (Match m in Entry().Matches(text, open + 1))
        {
            if (m.Index >= close)
            {
                break;
            }

            list.Add(new CapEntry(m.Groups[1].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), m.Groups[2].Index, m.Groups[2].Index + m.Groups[2].Length));
        }

        return list;
    }

    public static string Label(string flag) => flag switch
    {
        _ when Regex.IsMatch(flag, @"^FLAG_BADGE0(\d)_GET$") => "Gym " + flag[11],
        "FLAG_IS_CHAMPION" or "FLAG_SYS_GAME_CLEAR" => "Champion",
        _ => GameCatalog.Prettify(flag.Replace("FLAG_", "", StringComparison.Ordinal)),
    };

    /// <summary>Span of a #define's value in the config text.</summary>
    public static (int Start, int End, string Value)? Define(string text, string name)
    {
        var m = Regex.Match(text, $@"^[ \t]*#define[ \t]+{name}[ \t]+(\w+)", RegexOptions.Multiline);
        return m.Success ? (m.Groups[1].Index, m.Groups[1].Index + m.Groups[1].Length, m.Groups[1].Value) : null;
    }
}
