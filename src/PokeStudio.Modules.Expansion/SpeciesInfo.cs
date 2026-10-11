using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PokeStudio.Core.Models;
using PokeStudio.Core.SourceCode;

namespace PokeStudio.Modules.Expansion;

/// <summary>One "<c>.field = value</c>" inside a species block.</summary>
internal sealed record FieldOccurrence(
    string Name,
    int DesignatorStart,
    int ValueStart,
    int ValueEnd,
    int SegmentEnd,
    bool HasComma,
    bool Active,
    string? Condition,
    bool SpansDirectives);

internal sealed class SpeciesBlock
{
    public required string File { get; init; }
    public required string Symbol { get; init; }
    public required int Open { get; init; }
    public required int Close { get; init; }
    public required bool Active { get; init; }
    public required string? Condition { get; init; }

    /// <summary>The shared template the entry is built from (e.g. UNOWN_MISC_INFO), or null for a normal entry.</summary>
    public string? Template { get; init; }

    public List<FieldOccurrence> Fields { get; } = [];

    /// <summary>The occurrence the game is built with: the first active one.</summary>
    public FieldOccurrence? Get(string name) =>
        Fields.FirstOrDefault(f => f.Name == name && f.Active) ?? (Active ? null : Fields.FirstOrDefault(f => f.Name == name));

    public bool IsSettingDependent(string name) => Fields.Count(f => f.Name == name) > 1;
}

internal sealed class SourceFile
{
    public required string Relative { get; init; }
    public required string Text { get; init; }
    public required ConditionalMap Map { get; init; }
    public required MacroTable Macros { get; init; }
}

/// <summary>Reads species blocks from species_info files and turns edits back into minimal text changes.</summary>
internal static partial class SpeciesInfo
{
    [GeneratedRegex(@"\[(SPECIES_\w+)\]\s*=\s*\{")]
    private static partial Regex BlockStartRegex();

    // "[SPECIES_UNOWN] = UNOWN_MISC_INFO(A, …)," or "[SPECIES_MOTHIM_PLANT] = MOTHIM_SPECIES_INFO,"
    [GeneratedRegex(@"\[(SPECIES_\w+)\]\s*=\s*([A-Z_][A-Z0-9_]*)\s*[(,]")]
    private static partial Regex TemplateEntryRegex();

    // a block whose first content is a template call: "{ VIVILLON_MISC_INFO(…), … }"
    [GeneratedRegex(@"^\s*([A-Z_][A-Z0-9_]*)\s*\(")]
    private static partial Regex LeadingTemplateRegex();

    [GeneratedRegex(@"\[(MOVE_\w+)\]\s*=\s*\{")]
    private static partial Regex MoveBlockStartRegex();

    [GeneratedRegex(@"\[(ITEM_\w+)\]\s*=\s*\{")]
    private static partial Regex ItemBlockStartRegex();

    [GeneratedRegex(@"(?!^)([A-Z]+)")]
    private static partial Regex SnakifyRegex();

    [GeneratedRegex(@"^[A-Za-z_]\w*$")]
    private static partial Regex IdentifierRegex();

    /// <summary>
    /// A value written as a name the file defines (newer expansion versions share a family's types and abilities, e.g.
    /// <c>#define CLEFAIRY_FAMILY_TYPES { TYPE_FAIRY, TYPE_FAIRY }</c>) → what it stands for with the project's settings.
    /// Constants the game itself knows (TYPE_FAIRY…) are left as they are.
    /// </summary>
    public static string ExpandName(string value, MacroTable macros, Func<string, bool>? known = null)
    {
        var v = Unwrap(value);
        for (var depth = 0; depth < 8 && IdentifierRegex().IsMatch(v) && (known is null || !known(v)) && macros.Raw(v) is { } body; depth++)
        {
            v = Unwrap(Resolve(Unwrap(CScanner.StripComments(body)), macros, out _));
        }

        return Unwrap(Resolve(v, macros, out _));
    }

    /// <summary>"(a ? b : c)" → "a ? b : c": brackets around the whole value only.</summary>
    private static string Unwrap(string value)
    {
        var v = value.Trim();
        while (v.Length > 1 && v[0] == '(' && CScanner.FindMatching(v, 0) == v.Length - 1)
        {
            v = v[1..^1].Trim();
        }

        return v;
    }

    /// <param name="settings">Settings to read the file with (default: the project's own).</param>
    public static SourceFile LoadFile(ExpansionLayout layout, string relative, MacroTable? settings = null)
    {
        var baseMacros = settings ?? layout.Macros;
        var text = File.ReadAllText(layout.Full(relative));
        var map = new ConditionalMap(text, baseMacros);
        var macros = baseMacros.Clone();
        macros.LoadActiveDefines(text, map);
        return new SourceFile { Relative = relative, Text = text, Map = map, Macros = macros };
    }

    /// <summary>True inside a preprocessor line, including a #define continued with backslashes.</summary>
    private static bool InDirective(string text, int index)
    {
        var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        while (lineStart > 1)
        {
            var prevEnd = lineStart - 1; // the '\n'
            var e = prevEnd;
            while (e > 0 && text[e - 1] is '\r' or ' ' or '\t')
            {
                e--;
            }

            if (e == 0 || text[e - 1] != '\\')
            {
                break;
            }

            lineStart = text.LastIndexOf('\n', Math.Max(0, prevEnd - 1)) + 1;
        }

        return text.AsSpan(lineStart).TrimStart().StartsWith("#", StringComparison.Ordinal);
    }

    public static List<SpeciesBlock> ParseBlocks(SourceFile file, bool moves = false, bool items = false)
    {
        var text = file.Text;
        var blocks = new List<SpeciesBlock>();
        foreach (Match m in (items ? ItemBlockStartRegex() : moves ? MoveBlockStartRegex() : BlockStartRegex()).Matches(text))
        {
            var lineStart = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
            var before = text.AsSpan(lineStart, m.Index - lineStart).Trim();
            if (before.StartsWith("//", StringComparison.Ordinal) || before.Length > 0)
            {
                continue; // commented out, or part of a bigger expression
            }

            var open = m.Index + m.Length - 1;
            int close;
            try
            {
                close = CScanner.FindMatching(text, open);
            }
            catch (FormatException)
            {
                continue;
            }

            var block = new SpeciesBlock
            {
                File = file.Relative,
                Symbol = m.Groups[1].Value,
                Open = open,
                Close = close,
                Active = file.Map.IsActive(m.Index),
                Condition = file.Map.ConditionAt(m.Index),
            };
            ParseFields(file, block);
            blocks.Add(block);
        }

        if (!moves && !items)
        {
            foreach (Match m in TemplateEntryRegex().Matches(text))
            {
                var lineStart = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
                if (text.AsSpan(lineStart, m.Index - lineStart).Trim().Length > 0 || InDirective(text, m.Index))
                {
                    continue; // commented out, part of a bigger expression, or inside a #define
                }

                blocks.Add(new SpeciesBlock
                {
                    File = file.Relative,
                    Symbol = m.Groups[1].Value,
                    Open = m.Groups[2].Index,
                    Close = m.Groups[2].Index,
                    Active = file.Map.IsActive(m.Index),
                    Condition = file.Map.ConditionAt(m.Index),
                    Template = m.Groups[2].Value,
                });
            }

            blocks.Sort((a, b) => a.Open.CompareTo(b.Open));
        }

        return blocks;
    }

    private static void ParseFields(SourceFile file, SpeciesBlock block)
    {
        var text = file.Text;
        foreach (var (s, e) in CScanner.SplitTopLevel(text, block.Open + 1, block.Close, counts: block.Active ? file.Map.IsActive : null))
        {
            var designator = FindDesignator(text, s, e);
            if (designator is not { } d)
            {
                continue;
            }

            var valueStart = d.EqualsIndex + 1;
            while (valueStart < e && char.IsWhiteSpace(text[valueStart]))
            {
                valueStart++;
            }

            var valueEnd = e;
            while (valueEnd > valueStart && char.IsWhiteSpace(text[valueEnd - 1]))
            {
                valueEnd--;
            }

            block.Fields.Add(new FieldOccurrence(
                d.Name, d.Start, valueStart, valueEnd, e,
                HasComma: e < block.Close && text[e] == ',',
                Active: file.Map.IsActive(d.Start),
                Condition: file.Map.ConditionAt(d.Start),
                SpansDirectives: file.Map.HasDirectiveBetween(valueStart, valueEnd)));
        }
    }

    /// <summary>Last top-level ".name =" in [s, e) (macro calls like SHADOW(...) may precede it).</summary>
    private static (string Name, int Start, int EqualsIndex)? FindDesignator(string text, int s, int e)
    {
        (string, int, int)? found = null;
        var depth = 0;
        for (var i = s; i < e;)
        {
            var skipped = CScanner.SkipTrivia(text, i);
            if (skipped != i)
            {
                i = skipped;
                continue;
            }

            var ch = text[i];
            if (ch is '(' or '{' or '[')
            {
                depth++;
            }
            else if (ch is ')' or '}' or ']')
            {
                depth--;
            }
            else if (ch == '#' && depth == 0)
            {
                var nl = text.IndexOf('\n', i);
                i = nl < 0 || nl > e ? e : nl;
                continue;
            }
            else if (ch == '.' && depth == 0 && i + 1 < e && (char.IsLetter(text[i + 1]) || text[i + 1] == '_'))
            {
                var j = i + 1;
                while (j < e && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
                {
                    j++;
                }

                var name = text[(i + 1)..j];
                var k = j;
                while (k < e && char.IsWhiteSpace(text[k]))
                {
                    k++;
                }

                if (k < e && text[k] == '=' && (k + 1 >= e || text[k + 1] != '='))
                {
                    found = (name, i, k);
                    i = k + 1;
                    continue;
                }
            }

            i++;
        }

        return found;
    }

    // ---------- values ----------

    /// <summary>Picks the branch of a top-level "cond ? a : b" the settings select.</summary>
    public static string Resolve(string value, MacroTable macros, out string? condition)
    {
        condition = null;
        var q = TopLevelIndex(value, '?');
        if (q < 0)
        {
            return value.Trim();
        }

        var c = TopLevelIndex(value, ':', q + 1);
        if (c < 0)
        {
            return value.Trim();
        }

        var cond = value[..q].Trim();
        condition = cond.Trim('(', ')', ' ');
        var chosen = macros.IsTrue(cond) ? value[(q + 1)..c] : value[(c + 1)..];
        return Resolve(chosen, macros, out _);
    }

    /// <summary>
    /// The part of a value the settings use: for "cond ? a : b" the chosen branch (recursively), otherwise the whole
    /// value. Editing only this span keeps the other setting's value intact.
    /// </summary>
    public static (int Start, int End) ActiveSpan(string text, int start, int end, MacroTable macros)
    {
        var q = TopLevelIndex(text, '?', start, end);
        var c = q < 0 ? -1 : TopLevelIndex(text, ':', q + 1, end);
        if (q < 0 || c < 0)
        {
            return Trim(text, start, end);
        }

        return macros.IsTrue(text[start..q]) ? ActiveSpan(text, q + 1, c, macros) : ActiveSpan(text, c + 1, end, macros);
    }

    private static (int, int) Trim(string text, int s, int e)
    {
        while (s < e && char.IsWhiteSpace(text[s]))
        {
            s++;
        }

        while (e > s && char.IsWhiteSpace(text[e - 1]))
        {
            e--;
        }

        return (s, e);
    }

    private static int TopLevelIndex(string s, char target, int from = 0, int to = -1)
    {
        var depth = 0;
        var limit = to < 0 ? s.Length : to;
        for (var i = from; i < limit;)
        {
            var skipped = CScanner.SkipTrivia(s, i);
            if (skipped != i)
            {
                i = skipped;
                continue;
            }

            if (s[i] is '(' or '{' or '[')
            {
                depth++;
            }
            else if (s[i] is ')' or '}' or ']')
            {
                depth--;
            }
            else if (s[i] == target && depth == 0 && !(target == ':' && i + 1 < limit && s[i + 1] == ':'))
            {
                return i;
            }

            i++;
        }

        return -1;
    }

    /// <summary>Arguments of NAME(a, b, …) or the items of { a, b, … }.</summary>
    public static List<string> Args(string expr)
    {
        var t = expr.Trim();
        var open = t.IndexOfAny(['(', '{']);
        if (open < 0)
        {
            return [t];
        }

        var close = CScanner.FindMatching(t, open);
        return CScanner.SplitTopLevel(t, open + 1, close)
            .Select(p => CScanner.StripComments(t[p.Start..p.End]))
            .Where(a => a.Length > 0)
            .ToList();
    }

    public static string Raw(SourceFile file, FieldOccurrence f) => file.Text[f.ValueStart..f.ValueEnd];

    /// <summary>"sMukAlolaTeachableLearnset" → "MUK_ALOLA" (same rule the project's own build script uses).</summary>
    public static string LearnablesKey(string teachableSymbol)
    {
        var core = teachableSymbol;
        if (core.StartsWith('s'))
        {
            core = core[1..];
        }

        if (core.EndsWith("TeachableLearnset", StringComparison.Ordinal))
        {
            core = core[..^"TeachableLearnset".Length];
        }

        return SnakifyRegex().Replace(core, "_$1").ToUpperInvariant();
    }

    /// <summary>Builds the storage-independent Species for one block (<paramref name="nationalDex"/>: NATIONAL_DEX_* → number from include/constants/pokedex.h).</summary>
    public static Species ToSpecies(SourceFile file, SpeciesBlock block, int index, out string? levelUp, out string? teachable, out string? egg, out string? teachingType,
        IReadOnlyDictionary<string, int>? nationalDex = null)
    {
        var locked = new Dictionary<string, string>();
        var dependent = new Dictionary<string, string>();
        string? Value(string field, string modelField)
        {
            var f = block.Get(field);
            if (f is null)
            {
                return null;
            }

            if (f.SpansDirectives)
            {
                locked[modelField] = "This value is split across game settings in the source file, so it can only be edited there for now.";
                return null;
            }

            var resolved = Resolve(Raw(file, f), file.Macros, out var cond);
            if (cond is not null || block.IsSettingDependent(field))
            {
                dependent[modelField] = $"Depends on a game setting ({cond ?? f.Condition ?? "#if"}). Edits apply to the value your settings use.";
            }

            return resolved;
        }

        int Int(string field, string modelField, int fallback)
        {
            var v = Value(field, modelField);
            if (v is null)
            {
                return fallback;
            }

            var n = file.Macros.TryEvaluate(v);
            if (n is null)
            {
                locked[modelField] = $"Set with “{v}”, which the app can't calculate yet.";
                return fallback;
            }

            if (!Regex.IsMatch(v.Trim(), @"^\d+$"))
            {
                dependent.TryAdd(modelField, $"Calculated from “{v.Trim()}”. Editing replaces it with a plain number.");
            }

            return (int)n.Value;
        }

        // built from a shared template the app can't expand: shown, but edited only in the source file
        var template = block.Template ?? (block.Get("speciesName") is null && LeadingTemplateRegex().Match(file.Text[(block.Open + 1)..block.Close]) is { Success: true } lead
            ? lead.Groups[1].Value : null);
        if (template is not null)
        {
            locked["*"] = $"This Pokémon is built from the shared template “{template}” in the source file, so it can only be edited there.";
        }

        var name = Value("speciesName", "Name") is { } n ? CStrings.DecodeLiterals(n)
            : template is not null ? GameCatalog.Prettify(block.Symbol.Replace("SPECIES_", "", StringComparison.Ordinal)) : block.Symbol;
        var stats = new BaseStats(
            Int("baseHP", "Stats", 1), Int("baseAttack", "Stats", 1), Int("baseDefense", "Stats", 1),
            Int("baseSpAttack", "Stats", 1), Int("baseSpDefense", "Stats", 1), Int("baseSpeed", "Stats", 1));

        // (a family's shared name is expanded; editing then gives this Pokémon its own value)
        List<string> Listed(string field, string modelField)
        {
            if (Value(field, modelField) is not { } raw)
            {
                return [];
            }

            var whole = ExpandName(raw, file.Macros);
            var list = Args(whole).Select(x => ExpandName(x, file.Macros, n => n.StartsWith("TYPE_", StringComparison.Ordinal) || n.StartsWith("ABILITY_", StringComparison.Ordinal))).ToList();
            if (whole != raw.Trim() || list.Count != Args(raw).Count || !list.SequenceEqual(Args(whole)))
            {
                dependent.TryAdd(modelField, $"Shared through “{raw.Trim()}” in the source file. Editing gives this Pokémon its own value.");
            }

            return list;
        }

        var types = Listed("types", "Types");
        var abilities = Listed("abilities", "Abilities");
        while (abilities.Count is > 0 and < 3)
        {
            abilities.Add("ABILITY_NONE");
        }

        var description = "";
        if (Value("description", "DexEntry") is { } d)
        {
            if (d.StartsWith("COMPOUND_STRING", StringComparison.Ordinal) || d.StartsWith("_(", StringComparison.Ordinal))
            {
                description = CStrings.DecodeLiterals(d);
            }
            else
            {
                locked["DexEntry"] = "This Pokédex entry is shared with other forms; edit it in the source file for now.";
            }
        }

        GenderRatio? gender = Value("genderRatio", "Gender") switch
        {
            null => null,
            "MON_MALE" => GenderRatio.AlwaysMale,
            "MON_FEMALE" => GenderRatio.AlwaysFemale,
            "MON_GENDERLESS" => GenderRatio.NoGender,
            var g when g.StartsWith("PERCENT_FEMALE", StringComparison.Ordinal)
                && double.TryParse(Args(g)[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) => new GenderRatio(pct),
            _ => null,
        };

        levelUp = Value("levelUpLearnset", "Learnset");
        teachable = Value("teachableLearnset", "Learnset");
        egg = Value("eggMoveLearnset", "Learnset");
        teachingType = Value("teachingType", "TeachingType");

        if (!block.Active)
        {
            locked["*"] = "This Pokémon is switched off in your game's settings (include/config/species_enabled.h), so it isn't built into the game.";
        }

        var category = Value("categoryName", "Category") is { } c ? CStrings.DecodeLiterals(c) : "";
        var tags = SpeciesTags.None;
        foreach (var (field, tag) in TagFields)
        {
            if (Value(field, "Tags") is { } v && v is not ("FALSE" or "0"))
            {
                tags |= tag;
            }
        }
        return new Species
        {
            Id = index,
            Key = block.Symbol,
            Name = name,
            FormName = FormNameFor(block.Symbol, name),
            Stats = stats,
            Types = types,
            Abilities = abilities,
            HeldItemCommon = Value("itemCommon", "HeldItemCommon"),
            HeldItemRare = Value("itemRare", "HeldItemRare"),
            CatchRate = Int("catchRate", "CatchRate", 45),
            ExpYield = Int("expYield", "ExpYield", 0),
            Growth = Value("growthRate", "Growth") ?? "",
            Gender = gender,
            EggGroups = Value("eggGroups", "EggGroups") is { } eg ? Args(eg) : [],
            Category = category,
            DexEntry = description,
            IsEnabled = block.Active,
            Tags = tags,
            NationalDex = Value("natDexNum", "NationalDex") is { } dex && nationalDex is not null && nationalDex.TryGetValue(dex.Trim(), out var dexNumber) && dexNumber > 0 ? dexNumber : null,
            LearnsetKey = $"{levelUp}|{teachable}|{egg}",
            LockedFields = locked,
            SettingDependentFields = dependent,
        };
    }

    internal static readonly (string Field, SpeciesTags Tag)[] TagFields =
    [
        ("isRestrictedLegendary", SpeciesTags.RestrictedLegendary), ("isSubLegendary", SpeciesTags.SubLegendary),
        ("isMythical", SpeciesTags.Mythical), ("isUltraBeast", SpeciesTags.UltraBeast), ("isParadox", SpeciesTags.Paradox),
        ("isMegaEvolution", SpeciesTags.Mega), ("isPrimalReversion", SpeciesTags.Mega),
    ];

    /// <summary>"SPECIES_RATICATE_ALOLA" + "Raticate" → "Alola".</summary>
    public static string? FormNameFor(string symbol, string name)
    {
        var baseKey = "SPECIES_" + Regex.Replace(name.Normalize(NormalizationForm.FormD), @"[^A-Za-z0-9]+", "_").Trim('_').ToUpperInvariant();
        if (symbol == baseKey || !symbol.StartsWith(baseKey + "_", StringComparison.Ordinal))
        {
            return null;
        }

        return GameCatalog.Prettify(symbol[(baseKey.Length + 1)..]);
    }

    // ---------- evolutions ----------

    public static EvolutionSet ToEvolutions(SourceFile file, SpeciesBlock block)
    {
        var f = block.Get("evolutions");
        if (f is null)
        {
            return new EvolutionSet { FromSpecies = block.Symbol };
        }

        if (!block.Active)
        {
            return new EvolutionSet { FromSpecies = block.Symbol, LockedReason = "This Pokémon is switched off in your game's settings." };
        }

        if (f.SpansDirectives && !block.IsSettingDependent("evolutions"))
        {
            // split by #if: the entries the game uses can be replaced one for one
            var active = new List<Evolution>();
            foreach (var (start, end, isActive) in EvolutionEntries(file, f))
            {
                if (isActive && TryParseEvolutions("EVOLUTION(" + Resolve(file.Text[start..end], file.Macros, out _) + ")") is [var one])
                {
                    active.Add(one);
                }
                else if (isActive)
                {
                    return new EvolutionSet { FromSpecies = block.Symbol, LockedReason = "The app couldn't read these evolutions; edit them in the source file." };
                }
            }

            return new EvolutionSet
            {
                FromSpecies = block.Symbol,
                Evolutions = active,
                EntriesReplaceable = true,
                LockedReason = "These evolutions change with game settings (for example cross-generation evolutions), so only Fix evolutions can change them here; add or remove them in the source file.",
            };
        }

        if (f.SpansDirectives || block.IsSettingDependent("evolutions"))
        {
            return new EvolutionSet { FromSpecies = block.Symbol, Evolutions = TryParseEvolutions(Raw(file, f)) ?? [], LockedReason = "These evolutions change with game settings (for example cross-generation evolutions), so they can only be edited in the source file for now." };
        }

        var parsed = TryParseEvolutions(Resolve(Raw(file, f), file.Macros, out _));
        return parsed is null
            ? new EvolutionSet { FromSpecies = block.Symbol, LockedReason = "The app couldn't read these evolutions; edit them in the source file." }
            : new EvolutionSet { FromSpecies = block.Symbol, Evolutions = parsed };
    }

    private static List<Evolution>? TryParseEvolutions(string value)
    {
        try
        {
            var t = value.Trim();
            if (!t.StartsWith("EVOLUTION", StringComparison.Ordinal))
            {
                return null;
            }

            var list = new List<Evolution>();
            foreach (var item in Args(t))
            {
                if (!item.StartsWith('{'))
                {
                    continue;
                }

                var parts = Args(item);
                if (parts.Count < 3)
                {
                    return null;
                }

                var conditions = new List<EvolutionCondition>();
                foreach (var extra in parts.Skip(3))
                {
                    if (extra.StartsWith("CONDITIONS", StringComparison.Ordinal))
                    {
                        foreach (var c in Args(extra))
                        {
                            var cp = Args(c);
                            conditions.Add(new EvolutionCondition(cp[0], string.Join(", ", cp.Skip(1))));
                        }
                    }
                }

                list.Add(new Evolution(parts[0], parts[1], parts[2]) { Conditions = conditions });
            }

            return list;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string FormatEvolutions(IReadOnlyList<Evolution> evolutions, string newline, int alignColumn) =>
        "EVOLUTION(" + string.Join("," + newline + new string(' ', alignColumn), evolutions.Select(FormatEntry)) + ")";

    private static string FormatEntry(Evolution e)
    {
        var sb = new StringBuilder("{").Append(e.Method).Append(", ").Append(e.Parameter).Append(", ").Append(e.TargetSpecies);
        if (e.Conditions.Count > 0)
        {
            sb.Append(", CONDITIONS(").Append(string.Join(", ", e.Conditions.Select(c => $"{{{c.Kind}, {c.Value}}}"))).Append(')');
        }

        return sb.Append('}').ToString();
    }

    /// <summary>The top-level {…} entries of an EVOLUTION(…) value, and whether each is in a branch the game uses.</summary>
    internal static List<(int Start, int End, bool Active)> EvolutionEntries(SourceFile file, FieldOccurrence f)
    {
        var text = file.Text;
        var result = new List<(int, int, bool)>();
        var open = text.IndexOf('(', f.ValueStart);
        if (open < 0 || open >= f.ValueEnd)
        {
            return result;
        }

        var close = CScanner.FindMatching(text, open);
        for (var i = open + 1; i < close;)
        {
            var skipped = CScanner.SkipTrivia(text, i);
            if (skipped != i)
            {
                i = skipped;
            }
            else if (text[i] == '#')
            {
                var lineEnd = text.IndexOf('\n', i);
                i = lineEnd < 0 ? close : lineEnd + 1;
            }
            else if (text[i] == '{')
            {
                var end = CScanner.FindMatching(text, i) + 1;
                result.Add((i, end, file.Map.IsActive(i)));
                i = end;
            }
            else
            {
                i++;
            }
        }

        return result;
    }

    // ---------- writing ----------

    /// <summary>Text edits that turn <paramref name="before"/> into <paramref name="after"/> for one block.</summary>
    public static List<TextEdit> EditsFor(SourceFile file, SpeciesBlock block, Species before, Species after)
    {
        var edits = new List<TextEdit>();
        var nl = CScanner.LineEnding(file.Text);
        void Set(string field, string value, params string[] anchors)
        {
            var f = block.Get(field);
            if (f is not null)
            {
                var (s, e) = ActiveSpan(file.Text, f.ValueStart, f.ValueEnd, file.Macros);
                edits.Add(new TextEdit(s, e - s, value));
                return;
            }

            var anchor = anchors.Select(block.Get).LastOrDefault(x => x is not null) ?? block.Fields.LastOrDefault(x => x.Active);
            if (anchor is null)
            {
                return;
            }

            var indent = CScanner.IndentAt(file.Text, anchor.DesignatorStart);
            edits.Add(anchor.HasComma
                ? new TextEdit(anchor.SegmentEnd + 1, 0, $"{nl}{indent}.{field} = {value},")
                : new TextEdit(anchor.ValueEnd, 0, $",{nl}{indent}.{field} = {value},"));
        }

        if (before.Name != after.Name)
        {
            Set("speciesName", $"_(\"{CStrings.Escape(after.Name)}\")");
        }

        void Stat(string field, int b, int a)
        {
            if (b != a)
            {
                Set(field, a.ToString(CultureInfo.InvariantCulture));
            }
        }

        Stat("baseHP", before.Stats.Hp, after.Stats.Hp);
        Stat("baseAttack", before.Stats.Attack, after.Stats.Attack);
        Stat("baseDefense", before.Stats.Defense, after.Stats.Defense);
        Stat("baseSpAttack", before.Stats.SpAttack, after.Stats.SpAttack);
        Stat("baseSpDefense", before.Stats.SpDefense, after.Stats.SpDefense);
        Stat("baseSpeed", before.Stats.Speed, after.Stats.Speed);

        if (!before.Types.SequenceEqual(after.Types))
        {
            Set("types", $"MON_TYPES({string.Join(", ", after.Types)})");
        }

        if (!before.Abilities.SequenceEqual(after.Abilities))
        {
            Set("abilities", $"{{ {string.Join(", ", after.Abilities)} }}");
        }

        if (before.HeldItemCommon != after.HeldItemCommon)
        {
            Set("itemCommon", after.HeldItemCommon ?? "ITEM_NONE", "catchRate", "expYield", "evYield_HP", "evYield_Attack", "evYield_Defense", "evYield_Speed", "evYield_SpAttack", "evYield_SpDefense");
        }

        if (before.HeldItemRare != after.HeldItemRare)
        {
            Set("itemRare", after.HeldItemRare ?? "ITEM_NONE", "catchRate", "expYield", "evYield_HP", "evYield_Attack", "evYield_Defense", "evYield_Speed", "evYield_SpAttack", "evYield_SpDefense", "itemCommon");
        }

        if (before.CatchRate != after.CatchRate)
        {
            Set("catchRate", after.CatchRate.ToString(CultureInfo.InvariantCulture));
        }

        if (before.ExpYield != after.ExpYield)
        {
            Set("expYield", after.ExpYield.ToString(CultureInfo.InvariantCulture));
        }

        if (before.Growth != after.Growth)
        {
            Set("growthRate", after.Growth);
        }

        if (before.Gender != after.Gender && after.Gender is { } g)
        {
            Set("genderRatio", g.Genderless ? "MON_GENDERLESS"
                : g.PercentFemale is 0 ? "MON_MALE"
                : g.PercentFemale is 100 ? "MON_FEMALE"
                : $"PERCENT_FEMALE({g.PercentFemale.GetValueOrDefault().ToString("0.###", CultureInfo.InvariantCulture)})");
        }

        if (!before.EggGroups.SequenceEqual(after.EggGroups))
        {
            Set("eggGroups", $"MON_EGG_GROUPS({string.Join(", ", after.EggGroups)})");
        }

        if (before.Category != after.Category)
        {
            Set("categoryName", $"_(\"{CStrings.Escape(after.Category)}\")");
        }

        if (before.Tags != after.Tags)
        {
            foreach (var (field, tag) in TagFields.Where(t => t.Field is not ("isMegaEvolution" or "isPrimalReversion")))
            {
                var on = after.Tags.HasFlag(tag);
                if (on == before.Tags.HasFlag(tag))
                {
                    continue;
                }

                if (on)
                {
                    Set(field, "TRUE", "eggGroups", "abilities", "categoryName");
                }
                else if (block.Get(field) is { } f)
                {
                    // remove the whole line
                    var lineStart = file.Text.LastIndexOf('\n', Math.Max(0, f.DesignatorStart - 1)) + 1;
                    var end = f.HasComma ? f.SegmentEnd + 1 : f.ValueEnd;
                    end = file.Text.IndexOf('\n', end) is var n and >= 0 ? n + 1 : file.Text.Length;
                    edits.Add(new TextEdit(lineStart, end - lineStart, ""));
                }
            }
        }

        if (before.DexEntry != after.DexEntry && block.Get("description") is { } desc)
        {
            var indent = CScanner.IndentAt(file.Text, desc.DesignatorStart) + "    ";
            edits.Add(new TextEdit(desc.ValueStart, desc.ValueEnd - desc.ValueStart, "COMPOUND_STRING(" + CStrings.EncodeLines(after.DexEntry, indent, nl) + ")"));
        }

        return edits;
    }

    public static List<TextEdit> EvolutionEdits(SourceFile file, SpeciesBlock block, EvolutionSet after)
    {
        var nl = CScanner.LineEnding(file.Text);
        var f = block.Get("evolutions");
        if (f is null)
        {
            if (after.Evolutions.Count == 0)
            {
                return [];
            }

            var anchor = new[] { "levelUpLearnset", "teachableLearnset", "eggMoveLearnset" }.Select(block.Get).LastOrDefault(x => x is not null)
                ?? block.Fields.LastOrDefault(x => x.Active);
            if (anchor is null)
            {
                return [];
            }

            var indent = CScanner.IndentAt(file.Text, anchor.DesignatorStart);
            var column = indent.Length + ".evolutions = EVOLUTION(".Length;
            var text = FormatEvolutions(after.Evolutions, nl, column);
            return [anchor.HasComma
                ? new TextEdit(anchor.SegmentEnd + 1, 0, $"{nl}{indent}.evolutions = {text},")
                : new TextEdit(anchor.ValueEnd, 0, $",{nl}{indent}.evolutions = {text},")];
        }

        if (after.Evolutions.Count == 0)
        {
            // remove the whole line: from line start to just past the comma and newline
            var lineStart = file.Text.LastIndexOf('\n', Math.Max(0, f.DesignatorStart - 1)) + 1;
            var end = f.HasComma ? f.SegmentEnd + 1 : f.ValueEnd;
            if (end < file.Text.Length && file.Text[end] == '\r')
            {
                end++;
            }

            if (end < file.Text.Length && file.Text[end] == '\n')
            {
                end++;
            }

            return [new TextEdit(lineStart, end - lineStart, "")];
        }

        if (f.SpansDirectives)
        {
            // split by #if: replace changed entries one for one, leaving the #if lines alone
            var entries = EvolutionEntries(file, f).Where(e => e.Active).ToList();
            if (entries.Count != after.Evolutions.Count)
            {
                return [];
            }

            var edits = new List<TextEdit>();
            for (var i = 0; i < entries.Count; i++)
            {
                var (start, end, _) = entries[i];
                var formatted = FormatEntry(after.Evolutions[i]);
                var current = TryParseEvolutions("EVOLUTION(" + Resolve(file.Text[start..end], file.Macros, out _) + ")") is [var one] ? FormatEntry(one) : null;
                if (current != formatted)
                {
                    edits.Add(new TextEdit(start, end - start, formatted));
                }
            }

            return edits;
        }

        var valueLineStart = file.Text.LastIndexOf('\n', Math.Max(0, f.ValueStart - 1)) + 1;
        var col = f.ValueStart - valueLineStart + "EVOLUTION(".Length;
        return [new TextEdit(f.ValueStart, f.ValueEnd - f.ValueStart, FormatEvolutions(after.Evolutions, nl, col))];
    }
}
