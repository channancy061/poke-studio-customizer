using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PokeStudio.Core.Models;
using PokeStudio.Core.SourceCode;

namespace PokeStudio.Modules.Expansion;

/// <summary>A "Key: Value" line in a trainer's header.</summary>
internal sealed record HeaderField(string Key, string Value, int LineStart, int ValueStart, int LineEnd);

/// <summary>A group of lines separated from others by blank lines: one Pokémon, or a comment.</summary>
internal sealed record PartyChunk(int Start, int End, TrainerPokemon? Pokemon);

internal sealed class TrainerBlock
{
    /// <summary>Start of the "=== … ===" line and the start of the next trainer (or the end of the file).</summary>
    public int Start { get; set; }

    public int End { get; set; }

    public required string Key { get; init; }
    public required string Symbol { get; init; }
    public List<HeaderField> Fields { get; } = [];

    /// <summary>Offset just after the last header line (including its line break).</summary>
    public int HeaderEnd { get; set; }

    public List<PartyChunk> Chunks { get; } = [];
    public HeaderField? Get(string key) => Fields.LastOrDefault(f => f.Key == key);
}

/// <summary>
/// Reads and writes src/data/trainers.party (Showdown-style text, turned into C by the project's trainerproc tool).
/// Only trainers that changed are rewritten, and within them only changed fields and Pokémon.
/// </summary>
internal static partial class TrainerParty
{
    public const string File = "src/data/trainers.party";

    /// <summary>The pool pick function Poke Studio adds for the adaptive boss slot (POOL_PICK_POKE_STUDIO_ADAPTIVE).</summary>
    public const string AdaptivePick = "Poke Studio Adaptive";

    /// <summary>Tag marking the flexible member and its candidates.</summary>
    public const string FlexTag = "Tag7"; // becomes MON_POOL_TAG_TAG7 ("Tag 7" would not)

    private static bool IsFlex(TrainerPokemon p) => p.Extra.Any(kv => kv.Key == "Tags" && kv.Value == FlexTag);

    private static TrainerPokemon WithoutFlexTag(TrainerPokemon p) => p with { Extra = p.Extra.Where(kv => !(kv.Key == "Tags" && kv.Value == FlexTag)).ToList() };

    private static TrainerPokemon WithFlexTag(TrainerPokemon p) => IsFlex(p) ? p : p with { Extra = [.. p.Extra, new("Tags", FlexTag)] };

    /// <summary>Every Pokémon as written in the file: the party (flexible member tagged), then the candidates (tagged).</summary>
    internal static List<TrainerPokemon> FileOrder(Trainer t) => t.HasAdaptiveSlot
        ? [.. t.Party.Select((p, i) => i == t.FlexIndex ? WithFlexTag(p) : p), .. t.FlexCandidates.Select(WithFlexTag)]
        : [.. t.Party];

    [GeneratedRegex(@"^===\s*(\S+)\s*===\s*$")]
    private static partial Regex HeaderLine();

    [GeneratedRegex(@"^([A-Za-z][A-Za-z ]*?)\s*:\s?(.*)$")]
    private static partial Regex FieldLine();

    private sealed record Line(int Start, int End, int Next, string Text, bool InComment);

    private static List<Line> Lines(string text)
    {
        var list = new List<Line>();
        var inComment = false;
        for (var i = 0; i < text.Length || (i == text.Length && list.Count == 0 && text.Length > 0);)
        {
            var nl = text.IndexOf('\n', i);
            var end = nl < 0 ? text.Length : nl;
            var next = nl < 0 ? text.Length : nl + 1;
            var contentEnd = end > i && text[end - 1] == '\r' ? end - 1 : end;
            var line = text[i..contentEnd];
            var startsInComment = inComment;
            var t = line.TrimStart();
            var commentLine = startsInComment || t.StartsWith("/*", StringComparison.Ordinal);
            // track /* … */ across lines
            var scan = 0;
            var state = inComment;
            while (scan < line.Length)
            {
                if (!state && scan + 1 < line.Length && line[scan] == '/' && line[scan + 1] == '*')
                {
                    state = true;
                    scan += 2;
                }
                else if (state && scan + 1 < line.Length && line[scan] == '*' && line[scan + 1] == '/')
                {
                    state = false;
                    scan += 2;
                }
                else
                {
                    scan++;
                }
            }

            inComment = state;
            list.Add(new Line(i, contentEnd, next, line, commentLine));
            if (nl < 0)
            {
                break;
            }

            i = next;
        }

        return list;
    }

    public static List<(TrainerBlock Block, Trainer Trainer)> Parse(string text, Func<string, string> speciesFromName)
    {
        var result = new List<(TrainerBlock, Trainer)>();
        var lines = Lines(text);
        var i = 0;
        var id = 0;
        while (i < lines.Count)
        {
            var header = lines[i].InComment ? Match.Empty : HeaderLine().Match(lines[i].Text);
            if (!header.Success)
            {
                i++;
                continue;
            }

            var symbol = header.Groups[1].Value;
            var blockStart = lines[i].Start;
            i++;
            var fields = new List<HeaderField>();
            var headerEnd = lines[i - 1].Next;
            while (i < lines.Count && lines[i].Text.Trim().Length > 0 && !HeaderLine().IsMatch(lines[i].Text))
            {
                var l = lines[i];
                if (!l.InComment && FieldLine().Match(l.Text) is { Success: true } f)
                {
                    fields.Add(new HeaderField(f.Groups[1].Value.Trim(), f.Groups[2].Value.Trim(), l.Start, l.Start + f.Groups[2].Index, l.Next));
                }

                headerEnd = l.Next;
                i++;
            }

            var difficulty = fields.LastOrDefault(f => f.Key == "Difficulty")?.Value;
            var block = new TrainerBlock { Symbol = symbol, Key = difficulty is { Length: > 0 } && difficulty != "Normal" ? $"{symbol}@{difficulty}" : symbol, HeaderEnd = headerEnd, Start = blockStart };
            block.Fields.AddRange(fields);

            // party chunks until the next header
            while (i < lines.Count && !(HeaderLine().IsMatch(lines[i].Text) && !lines[i].InComment))
            {
                if (lines[i].Text.Trim().Length == 0)
                {
                    i++;
                    continue;
                }

                var first = i;
                var allComment = true;
                while (i < lines.Count && lines[i].Text.Trim().Length > 0 && !(HeaderLine().IsMatch(lines[i].Text) && !lines[i].InComment))
                {
                    allComment &= lines[i].InComment;
                    i++;
                }

                var chunkText = lines.Skip(first).Take(i - first).Select(l => l.Text).ToList();
                block.Chunks.Add(new PartyChunk(lines[first].Start, lines[i - 1].End, allComment ? null : ParsePokemon(chunkText, speciesFromName)));
            }

            block.End = i < lines.Count ? lines[i].Start : text.Length;
            result.Add((block, ToTrainer(block, id++)));
        }

        return result;
    }

    private static Trainer ToTrainer(TrainerBlock b, int id)
    {
        string V(string key) => b.Get(key)?.Value ?? "";
        var battle = b.Get("Battle Type") is { } bt
            ? (bt.Value.StartsWith("Double", StringComparison.OrdinalIgnoreCase) ? BattleType.Double : BattleType.Single)
            : V("Double Battle").Equals("Yes", StringComparison.OrdinalIgnoreCase) ? BattleType.Double : BattleType.Single;
        var mons = b.Chunks.Where(c => c.Pokemon is not null).Select(c => c.Pokemon!).ToList();
        var pick = V("Pool Pick Functions");
        var ours = pick == AdaptivePick && int.TryParse(V("Party Size"), CultureInfo.InvariantCulture, out var size) && size > 0 && size <= mons.Count
            && !b.Fields.Any(f => f.Key is "Pool Rules" or "Pool Prune" or "Copy Pool");
        int? flexIndex = null;
        var candidates = new List<TrainerPokemon>();
        if (ours)
        {
            var partySize = int.Parse(V("Party Size"), CultureInfo.InvariantCulture);
            var first = mons.FindIndex(IsFlex);
            flexIndex = first >= 0 && first < partySize ? first : null;
            candidates = mons.Skip(partySize).Select(WithoutFlexTag).ToList();
            mons = mons.Take(partySize).Select(WithoutFlexTag).ToList();
        }

        return new Trainer
        {
            Id = id,
            Key = b.Key,
            Name = V("Name"),
            TrainerClass = V("Class").Length == 0 ? "TRAINER_CLASS_PKMN_TRAINER_1" : ToConstant("TRAINER_CLASS", V("Class")),
            Pic = ToConstant("TRAINER_PIC", V("Pic")),
            Gender = V("Gender"),
            Music = V("Music").Length == 0 ? "" : ToConstant("TRAINER_ENCOUNTER_MUSIC", V("Music")),
            Items = SplitList(V("Items")).Select(x => ToConstant("ITEM", x)).ToList(),
            Battle = battle,
            AiFlags = SplitList(V("AI")).Select(x => ToConstant("AI_FLAG", x)).ToList(),
            Party = mons,
            FlexIndex = flexIndex,
            FlexCandidates = candidates,
            LockedReason = !ours && b.Fields.Any(f => f.Key.StartsWith("Pool", StringComparison.Ordinal) || f.Key == "Party Size")
                ? "This trainer picks its team from a pool at random, which the app can't edit yet. Edit it in trainers.party."
                : null,
        };
    }

    private static IEnumerable<string> SplitList(string value) =>
        value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static TrainerPokemon ParsePokemon(List<string> lines, Func<string, string> speciesFromName)
    {
        // "Nick (Species) (M) @ Item"
        var first = lines[0].Trim();
        string? item = null;
        var at = first.IndexOf(" @ ", StringComparison.Ordinal);
        if (at >= 0)
        {
            item = ToConstant("ITEM", first[(at + 3)..].Trim());
            first = first[..at].Trim();
        }

        string? gender = null;
        if (first.EndsWith("(M)", StringComparison.Ordinal) || first.EndsWith("(F)", StringComparison.Ordinal))
        {
            gender = first[^2..^1];
            first = first[..^3].Trim();
        }

        string? nickname = null;
        var speciesText = first;
        if (first.EndsWith(')') && first.LastIndexOf(" (", StringComparison.Ordinal) is var open and > 0)
        {
            nickname = first[..open].Trim();
            speciesText = first[(open + 2)..^1].Trim();
        }

        var mon = new TrainerPokemon { Species = speciesFromName(speciesText), Nickname = nickname, Gender = gender, HeldItem = item };
        var moves = new List<string>();
        var extra = new List<KeyValuePair<string, string>>();
        foreach (var raw in lines.Skip(1))
        {
            var l = raw.Trim();
            if (l.StartsWith("- ", StringComparison.Ordinal) || l == "-")
            {
                var move = l.TrimStart('-').Trim();
                if (move.Length > 0)
                {
                    moves.Add(ToConstant("MOVE", move));
                }

                continue;
            }

            var f = FieldLine().Match(l);
            if (!f.Success)
            {
                extra.Add(new(l, ""));
                continue;
            }

            var key = f.Groups[1].Value.Trim();
            var value = f.Groups[2].Value.Trim();
            mon = key switch
            {
                "Level" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var lv) => mon with { Level = lv },
                "Ability" => mon with { Ability = ToConstant("ABILITY", value) },
                "Nature" => mon with { Nature = ToConstant("NATURE", value) },
                "IVs" => mon with { Ivs = ParseStats(value, 31) },
                "EVs" => mon with { Evs = ParseStats(value, 0) },
                "Ball" => mon with { Ball = value },
                "Happiness" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h) => mon with { Happiness = h },
                "Shiny" => mon with { Shiny = value.Equals("Yes", StringComparison.OrdinalIgnoreCase) },
                "Tera Type" => mon with { TeraType = ToConstant("TYPE", value) },
                "Dynamax Level" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) => mon with { DynamaxLevel = d },
                "Gigantamax" => mon with { Gigantamax = value.Equals("Yes", StringComparison.OrdinalIgnoreCase) },
                _ => Keep(mon, key, value, extra),
            };
        }

        return mon with { Moves = moves, Extra = extra };
    }

    private static TrainerPokemon Keep(TrainerPokemon mon, string key, string value, List<KeyValuePair<string, string>> extra)
    {
        extra.Add(new(key, value));
        return mon;
    }

    private static readonly (string Label, Func<BaseStats, int> Get)[] StatLabels =
    [
        ("HP", s => s.Hp), ("Atk", s => s.Attack), ("Def", s => s.Defense), ("SpA", s => s.SpAttack), ("SpD", s => s.SpDefense), ("Spe", s => s.Speed),
    ];

    internal static BaseStats ParseStats(string value, int fallback)
    {
        int hp = fallback, atk = fallback, def = fallback, spa = fallback, spd = fallback, spe = fallback;
        foreach (var part in SplitList(value))
        {
            var bits = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length != 2 || !int.TryParse(bits[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            {
                continue;
            }

            switch (bits[1].ToUpperInvariant())
            {
                case "HP": hp = n; break;
                case "ATK": atk = n; break;
                case "DEF": def = n; break;
                case "SPA": spa = n; break;
                case "SPD": spd = n; break;
                case "SPE": spe = n; break;
            }
        }

        return new BaseStats(hp, atk, def, spa, spd, spe);
    }

    internal static string FormatStats(BaseStats s, bool skipZero) =>
        string.Join(" / ", StatLabels.Where(x => !skipZero || x.Get(s) != 0).Select(x => $"{x.Get(s).ToString(CultureInfo.InvariantCulture)} {x.Label}"));

    // ---------- names ↔ constants (the same rules as tools/trainerproc) ----------

    /// <summary>"Aroma Lady" → TRAINER_CLASS_AROMA_LADY; values already starting with the prefix are kept.</summary>
    public static string ToConstant(string prefix, string value)
    {
        if (value.StartsWith(prefix + "_", StringComparison.Ordinal))
        {
            return value;
        }

        if (value.Length == 0)
        {
            return prefix + "_NONE";
        }

        var sb = new StringBuilder(prefix).Append('_');
        foreach (var c in value)
        {
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                sb.Append(c);
            }
            else if (c is >= 'a' and <= 'z')
            {
                sb.Append(char.ToUpperInvariant(c));
            }
            else if (c != '\'')
            {
                sb.Append('_');
            }
        }

        return sb.ToString();
    }

    /// <summary>"Mr. Mime" → SPECIES_MR_MIME, "Nidoran♀" → SPECIES_NIDORAN_F.</summary>
    public static string SpeciesConstant(string value)
    {
        if (value.StartsWith("SPECIES_", StringComparison.Ordinal))
        {
            return value;
        }

        var sb = new StringBuilder("SPECIES_");
        var underscore = false;
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                if (underscore)
                {
                    sb.Append('_');
                }

                underscore = false;
                sb.Append(char.ToUpperInvariant(c));
            }
            else if (c is '\'' or '%' or '’')
            {
            }
            else if (c == '♂')
            {
                underscore = false;
                sb.Append("_M");
            }
            else if (c == '♀')
            {
                underscore = false;
                sb.Append("_F");
            }
            else if (c is 'é' or 'É')
            {
                if (underscore)
                {
                    sb.Append('_');
                }

                underscore = false;
                sb.Append('E');
            }
            else
            {
                underscore = true;
            }
        }

        return sb.ToString();
    }

    /// <summary>The friendly name when it converts back to the same constant, otherwise the constant itself.</summary>
    private static string Token(string prefix, string constant, GameCatalog catalog)
    {
        var name = catalog.NameOf(constant);
        return ToConstant(prefix, name) == constant ? name : constant;
    }

    /// <summary><paramref name="speciesNames"/> only holds names that convert back to the species (checked by the caller).</summary>
    private static string SpeciesToken(string constant, IReadOnlyDictionary<string, string> speciesNames) =>
        speciesNames.TryGetValue(constant, out var name) ? name : constant;

    // ---------- writing ----------

    public static string FormatPokemon(TrainerPokemon p, GameCatalog c, IReadOnlyDictionary<string, string> speciesNames, string nl)
    {
        var sb = new StringBuilder();
        var species = SpeciesToken(p.Species, speciesNames);
        sb.Append(p.Nickname is { Length: > 0 } nick ? $"{nick} ({species})" : species);
        if (p.Gender is { Length: > 0 } g)
        {
            sb.Append(" (").Append(g).Append(')');
        }

        if (p.HeldItem is { } item && item != "ITEM_NONE")
        {
            sb.Append(" @ ").Append(Token("ITEM", item, c));
        }

        void Line(string key, string value) => sb.Append(nl).Append(key).Append(": ").Append(value);
        Line("Level", p.Level.ToString(CultureInfo.InvariantCulture));
        if (p.Ability is { } ability)
        {
            Line("Ability", Token("ABILITY", ability, c));
        }

        if (p.Ivs is { } ivs)
        {
            Line("IVs", FormatStats(ivs, skipZero: false));
        }

        if (p.Evs is { } evs)
        {
            Line("EVs", FormatStats(evs, skipZero: true) is { Length: > 0 } e ? e : "0 HP");
        }

        if (p.Ball is { } ball)
        {
            Line("Ball", ball);
        }

        if (p.Happiness is { } happy)
        {
            Line("Happiness", happy.ToString(CultureInfo.InvariantCulture));
        }

        if (p.Nature is { } nature)
        {
            Line("Nature", Token("NATURE", nature, c));
        }

        if (p.Shiny)
        {
            Line("Shiny", "Yes");
        }

        if (p.DynamaxLevel is { } dmax)
        {
            Line("Dynamax Level", dmax.ToString(CultureInfo.InvariantCulture));
        }

        if (p.Gigantamax)
        {
            Line("Gigantamax", "Yes");
        }

        if (p.TeraType is { } tera)
        {
            Line("Tera Type", Token("TYPE", tera, c));
        }

        foreach (var (k, v) in p.Extra)
        {
            sb.Append(nl).Append(v.Length == 0 ? k : $"{k}: {v}");
        }

        foreach (var m in p.Moves)
        {
            sb.Append(nl).Append("- ").Append(Token("MOVE", m, c));
        }

        return sb.ToString();
    }

    public static List<TextEdit> EditsFor(string text, TrainerBlock block, Trainer before, Trainer after, GameCatalog c, IReadOnlyDictionary<string, string> speciesNames)
    {
        var edits = new List<TextEdit>();
        var nl = CScanner.LineEnding(text);
        var inserts = new StringBuilder();
        void Set(string key, string value)
        {
            var f = block.Get(key);
            if (f is not null)
            {
                edits.Add(value.Length == 0 && key != "Name"
                    ? new TextEdit(f.LineStart, f.LineEnd - f.LineStart, "")
                    : new TextEdit(f.ValueStart, LineContentEnd(text, f) - f.ValueStart, value));
            }
            else if (value.Length > 0)
            {
                inserts.Append(key).Append(": ").Append(value).Append(nl);
            }
        }

        if (before.Name != after.Name)
        {
            Set("Name", after.Name);
        }

        if (before.TrainerClass != after.TrainerClass)
        {
            Set("Class", Token("TRAINER_CLASS", after.TrainerClass, c));
        }

        if (before.Pic != after.Pic)
        {
            Set("Pic", Token("TRAINER_PIC", after.Pic, c));
        }

        if (before.Gender != after.Gender)
        {
            Set("Gender", after.Gender);
        }

        if (before.Music != after.Music)
        {
            Set("Music", after.Music.Length == 0 ? "" : Token("TRAINER_ENCOUNTER_MUSIC", after.Music, c));
        }

        if (!before.Items.SequenceEqual(after.Items))
        {
            Set("Items", string.Join(" / ", after.Items.Select(i => Token("ITEM", i, c))));
        }

        if (before.Battle != after.Battle)
        {
            if (block.Get("Battle Type") is not null)
            {
                Set("Battle Type", after.Battle == BattleType.Double ? "Doubles" : "Singles");
            }
            else
            {
                Set("Double Battle", after.Battle == BattleType.Double ? "Yes" : "No");
            }
        }

        if (!before.AiFlags.SequenceEqual(after.AiFlags))
        {
            Set("AI", string.Join(" / ", after.AiFlags.Select(f => Token("AI_FLAG", f, c))));
        }

        // adaptive slot: a pool with our pick function (party size = the team; candidates after it)
        if (before.HasAdaptiveSlot != after.HasAdaptiveSlot || (after.HasAdaptiveSlot && before.Party.Count != after.Party.Count))
        {
            Set("Party Size", after.HasAdaptiveSlot ? after.Party.Count.ToString(CultureInfo.InvariantCulture) : "");
            Set("Pool Pick Functions", after.HasAdaptiveSlot ? AdaptivePick : "");
        }

        if (inserts.Length > 0)
        {
            edits.Add(new TextEdit(block.HeaderEnd, 0, inserts.ToString()));
        }

        var beforeMons = FileOrder(before);
        var afterMons = FileOrder(after);
        if (!SameParty(beforeMons, afterMons))
        {
            edits.Add(PartyEdit(text, block, before with { Party = beforeMons }, after with { Party = afterMons }, c, speciesNames, nl));
        }

        return edits;
    }

    /// <summary>A whole new trainer block (used for difficulty versions), ending with a blank line.</summary>
    public static string FormatTrainer(Trainer t, GameCatalog c, IReadOnlyDictionary<string, string> speciesNames, string nl)
    {
        var symbol = t.Key.Split('@')[0];
        var difficulty = t.Key.Contains('@', StringComparison.Ordinal) ? t.Key.Split('@')[1] : null;
        var sb = new StringBuilder($"=== {symbol} ==={nl}");
        void Line(string key, string value) => sb.Append(key).Append(": ").Append(value).Append(nl);
        Line("Name", t.Name);
        Line("Class", Token("TRAINER_CLASS", t.TrainerClass, c));
        Line("Pic", Token("TRAINER_PIC", t.Pic, c));
        if (t.Gender.Length > 0)
        {
            Line("Gender", t.Gender);
        }

        if (t.Music.Length > 0)
        {
            Line("Music", Token("TRAINER_ENCOUNTER_MUSIC", t.Music, c));
        }

        if (t.Items.Count > 0)
        {
            Line("Items", string.Join(" / ", t.Items.Select(i => Token("ITEM", i, c))));
        }

        Line("Double Battle", t.Battle == BattleType.Double ? "Yes" : "No");
        if (t.AiFlags.Count > 0)
        {
            Line("AI", string.Join(" / ", t.AiFlags.Select(f => Token("AI_FLAG", f, c))));
        }

        if (difficulty is not null)
        {
            Line("Difficulty", difficulty);
        }

        if (t.HasAdaptiveSlot)
        {
            Line("Party Size", t.Party.Count.ToString(CultureInfo.InvariantCulture));
            Line("Pool Pick Functions", AdaptivePick);
        }

        foreach (var p in FileOrder(t))
        {
            sb.Append(nl).Append(FormatPokemon(p, c, speciesNames, nl)).Append(nl);
        }

        return sb.Append(nl).ToString();
    }

    private static int LineContentEnd(string text, HeaderField f)
    {
        var end = f.LineEnd;
        if (end > 0 && end <= text.Length && text[end - 1] == '\n')
        {
            end--;
        }

        if (end > 0 && text[end - 1] == '\r')
        {
            end--;
        }

        return Math.Max(end, f.ValueStart);
    }

    private static bool SameParty(IReadOnlyList<TrainerPokemon> a, IReadOnlyList<TrainerPokemon> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Equivalent(p.Second));

    private static TextEdit PartyEdit(string text, TrainerBlock block, Trainer before, Trainer after, GameCatalog c, IReadOnlyDictionary<string, string> speciesNames, string nl)
    {
        var pieces = new List<string>();
        var monIndex = 0;
        foreach (var chunk in block.Chunks)
        {
            if (chunk.Pokemon is null)
            {
                pieces.Add(text[chunk.Start..chunk.End]); // comments stay where they were
                continue;
            }

            if (monIndex < after.Party.Count)
            {
                var keep = monIndex < before.Party.Count && before.Party[monIndex].Equivalent(after.Party[monIndex]);
                pieces.Add(keep ? text[chunk.Start..chunk.End] : FormatPokemon(after.Party[monIndex], c, speciesNames, nl));
            }

            monIndex++;
        }

        for (var i = before.Party.Count; i < after.Party.Count; i++)
        {
            // new Pokémon go after the last one already there
            var lastMon = pieces.Count;
            for (var k = block.Chunks.Count - 1; k >= 0; k--)
            {
                if (block.Chunks[k].Pokemon is not null)
                {
                    break;
                }

                lastMon--;
            }

            pieces.Insert(Math.Max(0, lastMon), FormatPokemon(after.Party[i], c, speciesNames, nl));
        }

        var joined = string.Join(nl + nl, pieces);
        if (block.Chunks.Count == 0)
        {
            // no team yet: add one after the header, separated by a blank line
            return new TextEdit(block.HeaderEnd, 0, nl + joined + nl);
        }

        var start = block.Chunks[0].Start;
        var end = block.Chunks[^1].End;
        return new TextEdit(start, end - start, joined);
    }
}
