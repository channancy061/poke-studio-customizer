using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PokeStudio.Core.GameOptions;
using PokeStudio.Core.SourceCode;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// Writes the new-game options into a pokeemerald-expansion / pokefirered-expansion project: a generated script with the
/// options menu, a header naming the flags and variables it uses, and small clearly marked code blocks. Every block is
/// added once; settings the creator doesn't use add nothing.
/// </summary>
internal static partial class GameOptionsWriter
{
    public const string Header = "include/constants/poke_studio.h";
    public const string Script = "data/scripts/poke_studio_options.inc";
    public const string FlagsFile = "include/constants/flags.h";
    public const string VarsFile = "include/constants/vars.h";
    public const string EventScripts = "data/event_scripts.s";
    public const string Pokemon = "src/pokemon.c";
    public const string PartyMenu = "src/party_menu.c";
    public const string ItemsEnum = "include/constants/items.h";
    public const string Mark = "Poke Studio";

    public static IReadOnlyList<string> Files(ExpansionLayout layout) =>
        [Header, Script, FlagsFile, EventScripts, Pokemon, PartyMenu, ItemsEnum, StartMapScripts(layout), .. CodeFiles, .. WhiteOutFiles, .. CenterPlacement.CenterMaps(layout.Root)];

    /// <summary>The scripts the game runs after blacking out (Pokémon Center, or home before any Center).</summary>
    public static IReadOnlyList<string> WhiteOutFiles { get; } = ["data/event_scripts.s", "data/scripts/white_out.inc", "data/scripts/pkmn_center_nurse_frlg.inc"];

    private static readonly string[] WhiteOutLabels = ["EventScript_AfterWhiteOutHeal", "EventScript_AfterWhiteOutHeal_Frlg", "EventScript_AfterWhiteOutMomHeal"];

    /// <summary>Whether blacking out is handled by the Nuzlocke code (the "fainted are gone" rule exists).</summary>
    private static bool WipeHandled(GameOptionsSettings o) => CodeUsed(o) && o.NuzRuleUsed(o.NuzFaintedGone);

    /// <summary>A whiteout script that goes to our handler first in a Nuzlocke (or, with handled false, as it was).</summary>
    internal static string WhiteOutScript(string text, bool handled)
    {
        var clean = Regex.Replace(text, $@"^[^\n]*@ {Mark}: Nuzlocke wipe[^\n]*\r?\n", "", RegexOptions.Multiline);
        if (!handled)
        {
            return clean;
        }

        var nl = CScanner.LineEnding(clean);
        foreach (var label in WhiteOutLabels)
        {
            var at = Regex.Match(clean, $@"^{label}::[^\n]*\n", RegexOptions.Multiline);
            if (at.Success)
            {
                clean = clean.Insert(at.Index + at.Length, $"\tgoto_if_set FLAG_PS_NUZ_FAINT, PokeStudio_EventScript_NuzWipe @ {Mark}: Nuzlocke wipe{nl}");
            }
        }

        return clean;
    }

    /// <summary>The map a new game starts on (Emerald: the truck; FireRed: the player's room).</summary>
    public static string StartMapScripts(ExpansionLayout layout) =>
        layout.IsFireRedBase ? "data/maps/PalletTown_PlayersHouse_2F/scripts.inc" : "data/maps/InsideOfTruck/scripts.inc";

    private static string StartMapLabel(ExpansionLayout layout) => layout.IsFireRedBase ? "PalletTown_PlayersHouse_2F_MapScripts" : "InsideOfTruck_MapScripts";

    /// <summary>Names the options use, mapped to free flags/variables of the project (kept stable once chosen).</summary>
    public sealed record Ids(IReadOnlyDictionary<string, string> Flags, IReadOnlyDictionary<string, string> Vars);

    private static readonly string[] FlagNames = ["FLAG_PS_TRAINING_OFF", "FLAG_PS_LEVEL_CAP_OFF"];
    private static readonly string[] VarNames = ["VAR_PS_OPTIONS_STATE", "VAR_PS_DIFFICULTY", "VAR_PS_NO_BAG"];

    // Emerald names free ones FLAG_UNUSED_0x020 / VAR_UNUSED_0x404E; FireRed leaves them as FLAG_0x4AB / VAR_0x4029
    [GeneratedRegex(@"^#define\s+(FLAG_(?:UNUSED_)?0x[0-9A-Fa-f]+)\s+(0x[0-9A-Fa-f]+)\b", RegexOptions.Multiline)]
    private static partial Regex UnusedFlag();

    [GeneratedRegex(@"^#define\s+(VAR_(?:UNUSED_)?0x[0-9A-Fa-f]+)\s+(0x[0-9A-Fa-f]+)\b", RegexOptions.Multiline)]
    private static partial Regex UnusedVar();

    // saved flags between the temporary flags and the trainer flags; saved variables after the temporary ones
    private static bool SafeFlag(string hex) => Convert.ToInt32(hex, 16) is >= 0x20 and < 0x500;

    private static bool SafeVar(string hex) => Convert.ToInt32(hex, 16) is >= 0x4010 and < 0x4100;

    [GeneratedRegex(@"^#define\s+((?:FLAG|VAR)_PS_\w+)\s+(0x[0-9A-Fa-f]+)", RegexOptions.Multiline)]
    private static partial Regex Existing();

    /// <summary>Reuses the ids already in the generated header; picks new ones from the project's unused flags and variables.</summary>
    public static Ids AllocateIds(Func<string, string?> read, Func<string, bool> usedElsewhere, IEnumerable<string>? extraFlags = null)
    {
        var existing = read(Header) is { } h ? Existing().Matches(h).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value) : [];
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        var takenValues = existing.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var freeFlags = UnusedFlag().Matches(read(FlagsFile) ?? "")
            .Where(m => SafeFlag(m.Groups[2].Value) && !takenValues.Contains(m.Groups[2].Value) && !usedElsewhere(m.Groups[1].Value)).Select(m => m.Groups[2].Value).ToQueue();
        var freeVars = UnusedVar().Matches(read(VarsFile) ?? "")
            .Where(m => SafeVar(m.Groups[2].Value) && !takenValues.Contains(m.Groups[2].Value) && !usedElsewhere(m.Groups[1].Value)).Select(m => m.Groups[2].Value).ToQueue();
        // names already in the header keep their numbers even when an option is switched off for now
        var wanted = FlagNames.Concat(extraFlags ?? []).Concat(existing.Keys.Where(k => k.StartsWith("FLAG_", StringComparison.Ordinal))).Distinct(StringComparer.Ordinal);
        foreach (var name in wanted)
        {
            flags[name] = existing.GetValueOrDefault(name) ?? (freeFlags.Count > 0 ? freeFlags.Dequeue() : throw new InvalidOperationException("The project has no unused flags left for the new-game options."));
        }

        foreach (var name in VarNames)
        {
            vars[name] = existing.GetValueOrDefault(name) ?? (freeVars.Count > 0 ? freeVars.Dequeue() : throw new InvalidOperationException("The project has no unused variables left for the new-game options."));
        }

        return new Ids(flags, vars);
    }

    private static Queue<string> ToQueue(this IEnumerable<string> items) => new(items);

    /// <summary>Flags for other generated code (the final chapter), kept in the same header as the options' ones.</summary>
    internal static Ids AllocateFor(string root, Func<string, string?> read, IEnumerable<string> extraFlags) =>
        AllocateIds(read, UsageScanner(root), extraFlags);

    /// <summary>include/constants/poke_studio.h: our flag and variable numbers.</summary>
    internal static string HeaderText(Ids ids)
    {
        var header = new StringBuilder();
        header.Append("// Generated by Poke Studio: flags and variables used by the new-game options and the story. Edit them in the app.\n");
        header.Append("#ifndef GUARD_CONSTANTS_POKE_STUDIO_H\n#define GUARD_CONSTANTS_POKE_STUDIO_H\n\n");
        foreach (var (k, v) in ids.Flags)
        {
            header.Append(CultureInfo.InvariantCulture, $"#define {k} {v}\n");
        }

        foreach (var (k, v) in ids.Vars)
        {
            header.Append(CultureInfo.InvariantCulture, $"#define {k} {v}\n");
        }

        header.Append("\n#endif // GUARD_CONSTANTS_POKE_STUDIO_H\n");
        return header.ToString();
    }

    /// <summary>flags.h with the include of our header, or null when it already has it (or has no guard to put it before).</summary>
    internal static string? WithHeaderInclude(string flags)
    {
        var guardEnd = flags.LastIndexOf("#endif", StringComparison.Ordinal);
        return flags.Contains("constants/poke_studio.h", StringComparison.Ordinal) || guardEnd < 0
            ? null
            : flags.Insert(guardEnd, $"#include \"constants/poke_studio.h\" // {Mark}\n\n");
    }

    /// <summary>Reads the project from disk and plans the changes (ids chosen from flags and variables nothing else uses).</summary>
    public static Dictionary<string, string> PlanFor(ExpansionLayout layout, GameOptionsSettings options, Core.Modules.GameData? data = null, List<string>? notes = null)
    {
        string? Read(string rel) => File.Exists(layout.Full(rel)) ? File.ReadAllText(layout.Full(rel)) : null;
        byte[]? ReadBytes(string rel) => File.Exists(layout.Full(rel)) ? File.ReadAllBytes(layout.Full(rel)) : null;
        var extras = BuildExtras(layout, data);
        var ids = AllocateIds(Read, UsageScanner(layout.Root), ExtraFlagNames(options, extras));
        return Plan(layout, options, ids, Read, extras, ReadBytes, notes);
    }

    /// <summary>"Is this name used anywhere but its definition?" (scans the code, scripts and map data once, on first ask).</summary>
    private static Func<string, bool> UsageScanner(string root)
    {
        HashSet<string>? used = null;
        return name =>
        {
            if (used is null)
            {
                used = new HashSet<string>(StringComparer.Ordinal);
                var words = new Regex(@"\b(?:FLAG|VAR)_(?:UNUSED_)?0x[0-9A-Fa-f]+\b");
                string[] skip = [Path.Combine("include", "constants", "flags.h"), Path.Combine("include", "constants", "vars.h")];
                foreach (var dir in new[] { "src", "include", "data" }.Select(d => Path.Combine(root, d)).Where(Directory.Exists))
                {
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        var ext = Path.GetExtension(file);
                        if (ext is not (".c" or ".h" or ".inc" or ".s" or ".json" or ".pory" or ".party") || skip.Any(s => file.EndsWith(s, StringComparison.Ordinal)))
                        {
                            continue;
                        }

                        foreach (Match m in words.Matches(File.ReadAllText(file)))
                        {
                            used.Add(m.Value);
                        }
                    }
                }
            }

            return used.Contains(name);
        };
    }

    /// <summary>New contents for every file that needs to change (files already up to date are left out).</summary>
    public static Dictionary<string, string> Plan(ExpansionLayout layout, GameOptionsSettings o, Ids ids, Func<string, string?> read,
        Extras? extras = null, Func<string, byte[]?>? readBytes = null, List<string>? notes = null)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string file, string? before, string after)
        {
            if (before != after)
            {
                result[file] = after;
            }
        }

        // 1. the header with our flag and variable names (numbers, so it needs no other header)
        Put(Header, read(Header), HeaderText(ids));

        // 2. flags.h includes the header (so scripts and C see the names)
        if (read(FlagsFile) is { } flags && WithHeaderInclude(flags) is { } withInclude)
        {
            Put(FlagsFile, flags, withInclude);
        }

        // 3. the generated script, included by event_scripts.s
        Put(Script, read(Script), BuildScript(o, ids, layout.IsFireRedBase));
        if (read(EventScripts) is { } events && !events.Contains("poke_studio_options.inc", StringComparison.Ordinal))
        {
            var nl = CScanner.LineEnding(events);
            Put(EventScripts, events, events.TrimEnd('\r', '\n') + nl + nl + $"\t.include \"{Script}\" @ {Mark}" + nl);
        }

        // Hard and Extra Hard: a gym leader waits until every trainer in the gym is beaten (lines removed when off)
        var gyms = (extras ?? Extras.None).Gyms;
        foreach (var file in gyms.Select(g => g.LeaderScript).Distinct(StringComparer.Ordinal))
        {
            if ((result.TryGetValue(file, out var planned) ? planned : read(file)) is not { } gymScript)
            {
                continue;
            }

            var clean = Regex.Replace(gymScript, $@"^[^\n]*@ {Mark}: gym lock[^\n]*\r?\n", "", RegexOptions.Multiline);
            if (TrainerRules(o))
            {
                for (var g = 0; g < gyms.Count; g++)
                {
                    if (gyms[g].LeaderScript != file)
                    {
                        continue;
                    }

                    var at = Regex.Match(clean, $@"^([ \t]*)trainerbattle\w*\s+{gyms[g].Leader}\b", RegexOptions.Multiline);
                    if (at.Success)
                    {
                        var nl = CScanner.LineEnding(clean);
                        var ind = at.Groups[1].Value;
                        clean = clean.Insert(at.Index,
                            $"{ind}setvar VAR_0x8004, {g} @ {Mark}: gym lock{nl}{ind}callnative PokeStudio_GymTrainersLeft @ {Mark}: gym lock{nl}{ind}goto_if_ne VAR_RESULT, 0, PokeStudio_EventScript_GymLocked @ {Mark}: gym lock{nl}");
                    }
                }
            }

            Put(file, gymScript, clean);
        }

        // after blacking out in a Nuzlocke: the wipe handler first (the run goes on from the PC, or it's over)
        foreach (var file in WhiteOutFiles)
        {
            if ((result.TryGetValue(file, out var planned) ? planned : read(file)) is { } whiteOut) // event_scripts.s also gets the include above
            {
                Put(file, whiteOut, WhiteOutScript(whiteOut, WipeHandled(o)));
            }
        }

        // 4. the start map runs the options once (when VAR_PS_OPTIONS_STATE is still 0)
        var startMap = StartMapScripts(layout);
        if (read(startMap) is { } map && !map.Contains("PokeStudio_EventScript_NewGameOptions", StringComparison.Ordinal) && !map.Contains("PokeStudio_OnFrameTable", StringComparison.Ordinal))
        {
            Put(startMap, map, HookStartMap(map, StartMapLabel(layout)));
        }

        // 5. settings in the battle and caps configs
        if (read(BattleConfig) is { } battle)
        {
            var updated = battle;
            if (o.Difficulty != OptionMode.NotUsed)
            {
                updated = SetDefine(updated, "B_VAR_DIFFICULTY", ids.Vars["VAR_PS_DIFFICULTY"]);
            }

            if (o.NoItemsInBattle != OptionMode.NotUsed)
            {
                updated = SetDefine(updated, "B_VAR_NO_BAG_USE", ids.Vars["VAR_PS_NO_BAG"]);
            }

            Put(BattleConfig, battle, updated);
        }

        // a Nuzlocke: the PC mustn't heal Pokémon moved in and out of it (Gen 8+ behaviour)
        if (o.NuzlockeUsed && read(OverworldConfig) is { } overworld && Define(overworld, "OW_PC_HEAL") is { } pcHeal
            && pcHeal != "GEN_LATEST" && Regex.IsMatch(pcHeal, @"^GEN_[1-7]$"))
        {
            Put(OverworldConfig, overworld, SetDefine(overworld, "OW_PC_HEAL", "GEN_LATEST"));
            notes?.Add("The PC healed Pokémon put in it; for the Nuzlocke it now doesn't (OW_PC_HEAL).");
        }

        if (o.LevelCapCodeUsed && read(CapsConfig) is { } capsCfg)
        {
            var updated = capsCfg;
            if (Define(updated, "B_LEVEL_CAP_TYPE") == "LEVEL_CAP_NONE")
            {
                updated = SetDefine(updated, "B_LEVEL_CAP_TYPE", "LEVEL_CAP_FLAG_LIST");
            }

            if (Define(updated, "B_EXP_CAP_TYPE") == "EXP_CAP_NONE")
            {
                updated = SetDefine(updated, "B_EXP_CAP_TYPE", "EXP_CAP_HARD");
            }

            // candies stop at the cap too (with the cap switched off the cap is level 100, so they always work)
            updated = SetDefine(updated, "B_RARE_CANDY_CAP", "TRUE");

            Put(CapsConfig, capsCfg, updated);
        }

        // 6. small code blocks
        if (o.LevelCapCodeUsed && read(CapsSource) is { } caps && !caps.Contains("FLAG_PS_LEVEL_CAP_OFF", StringComparison.Ordinal))
        {
            var m = Regex.Match(caps, @"u32 GetCurrentLevelCap\(void\)\s*\{\r?\n");
            if (m.Success)
            {
                var nl = CScanner.LineEnding(caps);
                Put(CapsSource, caps, caps.Insert(m.Index + m.Length,
                    $"    if (FlagGet(FLAG_PS_LEVEL_CAP_OFF)) // {Mark}: the player switched the hard level cap off{nl}        return MAX_LEVEL;{nl}{nl}"));
            }
        }

        if (o.TrainingOff != OptionMode.NotUsed && read(Pokemon) is { } pokemon && !pokemon.Contains("FLAG_PS_TRAINING_OFF", StringComparison.Ordinal))
        {
            var after = TrainingOffPatch(pokemon);
            if (after is not null)
            {
                Put(Pokemon, pokemon, after);
            }
        }

        if (o.InfiniteCandy != OptionMode.NotUsed)
        {
            // the new item copies the Rare Candy, so it needs one to copy
            var hasRareCandy = read(ItemsInfo.File)?.Contains("[ITEM_RARE_CANDY]", StringComparison.Ordinal) == true;
            if (hasRareCandy && read(ItemsEnum) is { } items && !items.Contains("ITEM_INFINITE_CANDY", StringComparison.Ordinal))
            {
                var m = Regex.Match(items, @"^([ \t]*)ITEMS_COUNT,", RegexOptions.Multiline);
                if (m.Success)
                {
                    Put(ItemsEnum, items, items.Insert(m.Index, $"{m.Groups[1].Value}ITEM_INFINITE_CANDY, // {Mark}{CScanner.LineEnding(items)}"));
                }
            }

            if (read(ItemsInfo.File) is { } info && !info.Contains("[ITEM_INFINITE_CANDY]", StringComparison.Ordinal))
            {
                var at = info.IndexOf("[ITEM_RARE_CANDY]", StringComparison.Ordinal);
                if (at >= 0)
                {
                    var open = info.IndexOf('{', at);
                    var close = CScanner.FindMatching(info, open);
                    var end = info.IndexOf('\n', close) + 1;
                    var nl = CScanner.LineEnding(info);
                    var block = string.Join(nl,
                        "",
                        $"    // {Mark}: the Infinite Candy (a Rare Candy that is never used up)",
                        "    [ITEM_INFINITE_CANDY] =",
                        "    {",
                        "        .name = ITEM_NAME(\"Infinite Candy\"),",
                        "        .price = 0,",
                        "        .importance = 1,",
                        "        .description = COMPOUND_STRING(",
                        "            \"Never runs out.\\n\"",
                        "            \"Raises a Pokémon's\\n\"",
                        "            \"level by one.\"),",
                        "        .pocket = POCKET_KEY_ITEMS,",
                        "        .sortType = ITEM_TYPE_LEVEL_UP_ITEM,",
                        "        .type = ITEM_USE_PARTY_MENU,",
                        "        .fieldUseFunc = ItemUseOutOfBattle_RareCandy,",
                        "        .effect = gItemEffect_RareCandy,",
                        "        .iconPic = gItemIcon_RareCandy,",
                        "        .iconPalette = gItemIconPalette_RareCandy,",
                        "    },") + nl;
                    Put(ItemsInfo.File, info, info.Insert(end, block));
                }
            }

            if (read(PartyMenu) is { } party && !party.Contains("ITEM_INFINITE_CANDY", StringComparison.Ordinal))
            {
                Put(PartyMenu, party, InfiniteCandyPatch(party));
            }
        }

        // 7. Nuzlocke rules, adaptive boss slot, Pokémon Center helper
        // (reads see the changes planned above: two steps can touch the same file, e.g. FireRed's battle.h)
        string? Current(string file) => result.TryGetValue(file, out var planned) ? planned : read(file);
        PlanCode(layout, o, ids, Current, readBytes ?? (_ => null), (f, _, after) => Put(f, read(f), after), extras ?? Extras.None, notes ?? []);

        return result;
    }

    private const string BattleConfig = "include/config/battle.h";
    private const string CapsConfig = "include/config/caps.h";
    private const string OverworldConfig = "include/config/overworld.h";
    private const string CapsSource = "src/caps.c";

    private static string? Define(string text, string name) => CapsFiles.Define(text, name)?.Value;

    private static string SetDefine(string text, string name, string value) =>
        CapsFiles.Define(text, name) is { } d && d.Value != value ? text[..d.Start] + value + text[d.End..] : text;

    /// <summary>After the loop that reads IVs and EVs in CalculateMonStats: perfect IVs and no EVs when the flag is set.</summary>
    internal static string? TrainingOffPatch(string pokemon)
    {
        var fn = pokemon.IndexOf("void CalculateMonStats(", StringComparison.Ordinal);
        var read = fn < 0 ? -1 : pokemon.IndexOf("MON_DATA_HP_EV + i", fn, StringComparison.Ordinal);
        if (read < 0)
        {
            return null;
        }

        var loop = pokemon.LastIndexOf("for (", read, StringComparison.Ordinal);
        var open = pokemon.IndexOf('{', loop);
        var close = CScanner.FindMatching(pokemon, open);
        var lineEnd = pokemon.IndexOf('\n', close) + 1;
        var nl = CScanner.LineEnding(pokemon);
        var block = string.Join(nl,
            "",
            $"    // {Mark}: EVs and IVs switched off in the new-game options (perfect IVs, no EVs)",
            "    if (FlagGet(FLAG_PS_TRAINING_OFF))",
            "    {",
            "        for (u32 i = 0; i < NUM_STATS; i++)",
            "        {",
            "            iv[i] = MAX_PER_STAT_IVS;",
            "            ev[i] = 0;",
            "        }",
            "    }") + nl;
        return pokemon.Insert(lineEnd, block);
    }

    /// <summary>Inside the Rare Candy use functions, the Infinite Candy isn't removed from the bag.</summary>
    internal static string InfiniteCandyPatch(string party)
    {
        var result = new StringBuilder(party);
        var offset = 0;
        foreach (Match fn in Regex.Matches(party, @"\bvoid ItemUseCB_RareCandy\w*\(u8 taskId, TaskFunc \w+\)\s*\{"))
        {
            var open = fn.Index + fn.Length - 1;
            var close = CScanner.FindMatching(party, open);
            var body = party[open..close];
            var patched = body.Replace("RemoveBagItem(gSpecialVar_ItemId, 1);",
                $"if (gSpecialVar_ItemId != ITEM_INFINITE_CANDY) RemoveBagItem(gSpecialVar_ItemId, 1); // {Mark}: never used up", StringComparison.Ordinal);
            result.Remove(open + offset, body.Length).Insert(open + offset, patched);
            offset += patched.Length - body.Length;
        }

        return result.ToString();
    }

    /// <summary>Adds an "on frame" entry that runs the options menu once.</summary>
    internal static string HookStartMap(string map, string label)
    {
        var nl = CScanner.LineEnding(map);
        var at = map.IndexOf(label + "::", StringComparison.Ordinal);
        if (at < 0)
        {
            return map;
        }

        var table = Regex.Match(map[at..], @"map_script\s+MAP_SCRIPT_ON_FRAME_TABLE,\s*(\w+)");
        if (table.Success)
        {
            // the map already has a frame table: our entry goes first
            var tableLabel = table.Groups[1].Value;
            var t = map.IndexOf(tableLabel + ":", at + table.Index + table.Length, StringComparison.Ordinal);
            if (t >= 0)
            {
                var lineEnd = map.IndexOf('\n', t) + 1;
                return map.Insert(lineEnd, $"\tmap_script_2 VAR_PS_OPTIONS_STATE, 0, PokeStudio_EventScript_NewGameOptions @ {Mark}{nl}");
            }
        }

        var terminator = Regex.Match(map[at..], @"^[ \t]*\.byte 0", RegexOptions.Multiline);
        return terminator.Success
            ? map.Insert(at + terminator.Index, $"\tmap_script MAP_SCRIPT_ON_FRAME_TABLE, PokeStudio_OnFrameTable @ {Mark}{nl}")
            : map;
    }

    /// <summary>The options script: settings the creator fixed are applied silently; the rest are asked.</summary>
    internal static string BuildScript(GameOptionsSettings o, Ids ids, bool fireRed = false)
    {
        var sb = new StringBuilder();
        var texts = new StringBuilder();
        var subs = new StringBuilder();
        void L(string line) => sb.Append(line).Append('\n');
        void Text(string label, string text) => texts.Append($"{label}:\n\t.string \"{text}$\"\n\n");

        sb.Append("@ Generated by Poke Studio: the new-game options. Edit them in the app; changes here are replaced.\n\n");
        L("PokeStudio_OnFrameTable::");
        L("\tmap_script_2 VAR_PS_OPTIONS_STATE, 0, PokeStudio_EventScript_NewGameOptions");
        L("\t.2byte 0");
        L("");
        L("PokeStudio_EventScript_NewGameOptions::");
        L("\tlockall");
        L("\tsetvar VAR_PS_OPTIONS_STATE, 1");

        // fixed settings
        if (o.Difficulty == OptionMode.AlwaysOn)
        {
            L($"\tsetvar VAR_PS_DIFFICULTY, {(int)o.FixedDifficulty}");
        }

        if (o.TrainingOff == OptionMode.AlwaysOn)
        {
            L("\tsetflag FLAG_PS_TRAINING_OFF");
        }

        if (o.LevelCap == OptionMode.AlwaysOff)
        {
            L("\tsetflag FLAG_PS_LEVEL_CAP_OFF");
        }

        if (o.NoItemsInBattle == OptionMode.AlwaysOn)
        {
            L($"\tsetvar VAR_PS_NO_BAG, {(o.NoItemsAlsoWild ? 2 : 1)}");
        }

        if (o.InfiniteCandy == OptionMode.AlwaysOn)
        {
            L("\tgiveitem ITEM_INFINITE_CANDY");
        }

        if (o.AdaptiveSlot == OptionMode.AlwaysOn)
        {
            L("\tsetflag FLAG_PS_ADAPTIVE");
        }

        // the cap code exists only for the Nuzlocke rule: off unless that rule switches it on
        if (o.LevelCap == OptionMode.NotUsed && o.LevelCapCodeUsed)
        {
            L("\tsetflag FLAG_PS_LEVEL_CAP_OFF");
        }

        var nuzlocke = o.Nuzlocke is OptionMode.PlayerChooses or OptionMode.AlwaysOn;
        var nuzRules = new[] { o.NuzFirstEncounter, o.NuzFaintedGone, o.NuzDupes, o.NuzEeveeException, o.NuzShiny, o.NuzStatic, o.NuzNicknames, o.NuzLevelCap };
        var asks = new[] { o.Difficulty, o.TrainingOff, o.LevelCap, o.NoItemsInBattle, o.InfiniteCandy, o.AdaptiveSlot, o.Nuzlocke }.Count(m => m == OptionMode.PlayerChooses)
            + (nuzlocke ? nuzRules.Count(m => m == OptionMode.PlayerChooses) : 0);
        if (asks > 0)
        {
            L("\tmsgbox PokeStudio_Text_Intro, MSGBOX_DEFAULT");
            Text("PokeStudio_Text_Intro", "Before you begin, choose how\\nyou want to play.");
            L("\tmsgbox PokeStudio_Text_Careful, MSGBOX_DEFAULT");
            Text("PokeStudio_Text_Careful", "IMPORTANT: read each question.\\nStop pressing A now!\\pYes is picked first. Press B\\nto answer No.\\pChoices can't be changed\\nlater in this save.");
        }

        if (o.Difficulty == OptionMode.PlayerChooses)
        {
            var labels = new List<string> { "PokeStudio_Text_Easy", "PokeStudio_Text_Normal", "PokeStudio_Text_Hard" };
            Text("PokeStudio_Text_Easy", "Easy");
            Text("PokeStudio_Text_Normal", "Normal");
            Text("PokeStudio_Text_Hard", "Hard");
            if (o.OfferExtraHard)
            {
                labels.Add("PokeStudio_Text_ExtraHard");
                Text("PokeStudio_Text_ExtraHard", "Extra Hard");
            }

            L("\tmessage PokeStudio_Text_AskDifficulty");
            L("\twaitmessage");
            L($"\tdynmultichoice 0, 0, TRUE, 4, {(int)o.DefaultDifficulty}, DYN_MULTICHOICE_CB_NONE, {string.Join(", ", labels)}");
            L("\tcopyvar VAR_PS_DIFFICULTY, VAR_RESULT");
            L("\tclosemessage");
            Text("PokeStudio_Text_AskDifficulty", "Which difficulty?");
        }

        void Ask(OptionMode mode, string name, string question, string ifYes, string? ifNo = null)
        {
            if (mode != OptionMode.PlayerChooses)
            {
                return;
            }

            L($"\tmsgbox PokeStudio_Text_Ask{name}, MSGBOX_YESNO");
            L($"\tcall_if_eq VAR_RESULT, YES, PokeStudio_EventScript_{name}Yes");
            if (ifNo is not null)
            {
                L($"\tcall_if_eq VAR_RESULT, NO, PokeStudio_EventScript_{name}No");
            }

            Text($"PokeStudio_Text_Ask{name}", question);
            subs.Append($"PokeStudio_EventScript_{name}Yes::\n{ifYes}\treturn\n\n");
            if (ifNo is not null)
            {
                subs.Append($"PokeStudio_EventScript_{name}No::\n{ifNo}\treturn\n\n");
            }
        }

        Ask(o.TrainingOff, "TrainingOff", "Switch off EVs and IVs?\\nEveryone gets equal stats.", "\tsetflag FLAG_PS_TRAINING_OFF\n");
        Ask(o.LevelCap, "LevelCap", "Use a hard level cap?\\nNo EXP above the next cap.", "\tclearflag FLAG_PS_LEVEL_CAP_OFF\n", "\tsetflag FLAG_PS_LEVEL_CAP_OFF\n");
        Ask(o.NoItemsInBattle, "NoItems", o.NoItemsAlsoWild ? "No items in any battle?" : "No items in trainer battles?",
            $"\tsetvar VAR_PS_NO_BAG, {(o.NoItemsAlsoWild ? 2 : 1)}\n");
        Ask(o.InfiniteCandy, "Candy", "Take an Infinite Candy?\\nIt never runs out.", "\tgiveitem ITEM_INFINITE_CANDY\n");
        Ask(o.AdaptiveSlot, "Adaptive", "Bosses adapt one Pokémon\\nto your team?", "\tsetflag FLAG_PS_ADAPTIVE\n");

        if (nuzlocke)
        {
            void Rule(OptionMode mode, string name, string question, string set)
            {
                if (mode == OptionMode.AlwaysOn)
                {
                    L(set.TrimEnd('\n'));
                }

                Ask(mode, name, question, set);
            }

            if (o.Nuzlocke == OptionMode.PlayerChooses)
            {
                L("\tmsgbox PokeStudio_Text_AskNuzlocke, MSGBOX_YESNO");
                L("\tgoto_if_eq VAR_RESULT, NO, PokeStudio_EventScript_NuzlockeDone");
                Text("PokeStudio_Text_AskNuzlocke", "Play a Nuzlocke?\\nThe rules come one by one.");
            }

            Rule(o.NuzFirstEncounter, "NuzFirst", "Catch only the first\\nPokémon in each area?", "\tsetflag FLAG_PS_NUZ_FIRST\n");
            Rule(o.NuzFaintedGone, "NuzFaint", "Fainted Pokémon are\\ngone for good?", "\tsetflag FLAG_PS_NUZ_FAINT\n");
            if (o.NuzRuleUsed(o.NuzFaintedGone))
            {
                // the whole team fainted: go on with healthy Pokémon from the PC, or the run is over
                L("\tgoto_if_unset FLAG_PS_NUZ_FAINT, PokeStudio_EventScript_NuzWipeAsked");
                L("\tmsgbox PokeStudio_Text_AskNuzWipe, MSGBOX_YESNO");
                L("\tcall_if_eq VAR_RESULT, NO, PokeStudio_EventScript_NuzWipeEnds");
                L("PokeStudio_EventScript_NuzWipeAsked:");
                Text("PokeStudio_Text_AskNuzWipe", "If your whole team faints,\\ngo on with PC Pokémon?\\pNo: the run is over then.");
                subs.Append("PokeStudio_EventScript_NuzWipeEnds::\n\tsetflag FLAG_PS_NUZ_WIPE_ENDS\n\treturn\n\n");
            }
            if (o.NuzFirstEncounter != OptionMode.NotUsed && o.NuzFirstEncounter != OptionMode.AlwaysOff)
            {
                L("\tgoto_if_unset FLAG_PS_NUZ_FIRST, PokeStudio_EventScript_NuzlockeClausesDone");
                Rule(o.NuzDupes, "NuzDupes", "Duplicates clause?\\nRepeats don't use up areas.", "\tsetflag FLAG_PS_NUZ_DUPES\n");
                if (o.NuzDupes is OptionMode.AlwaysOn or OptionMode.PlayerChooses)
                {
                    L("\tgoto_if_unset FLAG_PS_NUZ_DUPES, PokeStudio_EventScript_NuzlockeEeveeDone");
                    Rule(o.NuzEeveeException, "NuzEevee", "Eevee clause? Eevee is\\nnever a duplicate.", "\tsetflag FLAG_PS_NUZ_EEVEE\n");
                    L("PokeStudio_EventScript_NuzlockeEeveeDone:");
                }

                Rule(o.NuzShiny, "NuzShiny", "Shiny clause? Shinies can\\nalways be caught.", "\tsetflag FLAG_PS_NUZ_SHINY\n");

                // static encounters: a "yes" is warned about and asked again (a legendary can be lost)
                if (o.NuzStatic == OptionMode.AlwaysOn)
                {
                    L("\tsetflag FLAG_PS_NUZ_STATIC");
                }

                Ask(o.NuzStatic, "NuzStatic", "Do static encounters count\\nas the area's first?",
                    "\tmsgbox PokeStudio_Text_StaticWarning, MSGBOX_YESNO\n\tcall_if_eq VAR_RESULT, YES, PokeStudio_EventScript_NuzStaticSure\n");
                if (o.NuzStatic == OptionMode.PlayerChooses)
                {
                    Text("PokeStudio_Text_StaticWarning", "Careful: legendaries count too.\\nIf you already caught something\\pthere, or it faints, it's lost\\nfor this run. Are you sure?");
                    subs.Append("PokeStudio_EventScript_NuzStaticSure::\n\tsetflag FLAG_PS_NUZ_STATIC\n\treturn\n\n");
                }
                L("PokeStudio_EventScript_NuzlockeClausesDone:");
            }

            Rule(o.NuzNicknames, "NuzNick", "Nickname every Pokémon\\nyou catch?", "\tsetflag FLAG_PS_NUZ_NICK\n");
            if (o.LevelCapCodeUsed)
            {
                Rule(o.NuzLevelCap, "NuzCap", "Respect the level cap\\nduring the Nuzlocke?", "\tclearflag FLAG_PS_LEVEL_CAP_OFF\n");
            }

            L("PokeStudio_EventScript_NuzlockeDone:");
        }

        if (asks > 0)
        {
            L("\tmsgbox PokeStudio_Text_Done, MSGBOX_DEFAULT");
            Text("PokeStudio_Text_Done", "All set. Have fun!");
        }

        L("\treleaseall");
        L("\tend");
        L("");
        if (o.CenterHelper.AnyService)
        {
            CenterHelperScript(o.CenterHelper, fireRed, L, Text);
        }

        if (TrainerRules(o))
        {
            L("PokeStudio_EventScript_GymLocked::");
            L("\tmsgbox PokeStudio_Text_GymLocked, MSGBOX_DEFAULT");
            L("\trelease");
            L("\tend");
            L("");
            Text("PokeStudio_Text_GymLocked", "Not yet! Beat every trainer in\\nthis Gym first, then come back.");
        }

        if (WipeHandled(o))
        {
            // after blacking out (see WhiteOutScripts): fainted Pokémon can't be healed
            L("PokeStudio_EventScript_NuzWipe::");
            L("\tlockall");
            L("\tcallnative PokeStudio_NuzAfterWipe");
            L("\tgoto_if_eq VAR_RESULT, FALSE, PokeStudio_EventScript_NuzRunOver");
            L("\tmsgbox PokeStudio_Text_NuzWipe, MSGBOX_DEFAULT");
            L("\tfadedefaultbgm");
            L("\treleaseall");
            L("\tend");
            L("PokeStudio_EventScript_NuzRunOver::");
            L("\tmsgbox PokeStudio_Text_NuzRunOver, MSGBOX_DEFAULT");
            L("\tfadedefaultbgm");
            L("\treleaseall");
            L("\tend");
            L("");
            Text("PokeStudio_Text_NuzWipe", "Your whole team fainted.\\nFainted Pokémon can't be healed\\lin a Nuzlocke.\\pTake a healthy Pokémon from\\nthe PC to go on.");
            Text("PokeStudio_Text_NuzRunOver", "Your whole team fainted.\\nYour Nuzlocke run is over.\\pNo more battles can start in\\nthis save. Start a new game to\\ltry again.");
        }

        sb.Append(subs).Append(texts);
        return sb.ToString();
    }

    private static readonly (string Name, string Effect)[] Natures =
    [
        ("Hardy", ""), ("Lonely", "+Atk -Def"), ("Brave", "+Atk -Spe"), ("Adamant", "+Atk -SpA"), ("Naughty", "+Atk -SpD"),
        ("Bold", "+Def -Atk"), ("Docile", ""), ("Relaxed", "+Def -Spe"), ("Impish", "+Def -SpA"), ("Lax", "+Def -SpD"),
        ("Timid", "+Spe -Atk"), ("Hasty", "+Spe -Def"), ("Serious", ""), ("Jolly", "+Spe -SpA"), ("Naive", "+Spe -SpD"),
        ("Modest", "+SpA -Atk"), ("Mild", "+SpA -Def"), ("Quiet", "+SpA -Spe"), ("Bashful", ""), ("Rash", "+SpA -SpD"),
        ("Calm", "+SpD -Atk"), ("Gentle", "+SpD -Def"), ("Sassy", "+SpD -Spe"), ("Careful", "+SpD -SpA"), ("Quirky", ""),
    ];

    private static readonly string[] EvSpreads =
        ["Reset to 0", "Attack + Speed", "Sp. Atk + Speed", "HP + Attack", "HP + Sp. Atk", "HP + Defense", "HP + Sp. Def"];

    /// <summary>The Pokémon Center helper: pick a service, a party Pokémon, then the nature or spread.</summary>
    private static void CenterHelperScript(CenterHelperSettings h, bool fireRed, Action<string> L, Action<string, string> text)
    {
        var services = new List<(string Name, string Label, int Price)>();
        if (h.Nature)
        {
            services.Add(("Nature", "Change nature", h.NaturePrice));
        }

        if (h.PerfectIvs)
        {
            services.Add(("Ivs", "Perfect IVs", h.IvPrice));
        }

        if (h.EvSpreads)
        {
            services.Add(("Evs", "Set EVs", h.EvPrice));
        }

        L("PokeStudio_EventScript_CenterHelper::");
        L("\tlock");
        L("\tfaceplayer");
        L("\tmessage PokeStudio_Text_HelperAsk");
        L("\twaitmessage");
        L($"\tdynmultichoice 0, 0, FALSE, 6, 0, DYN_MULTICHOICE_CB_NONE, {string.Join(", ", services.Select(s => $"PokeStudio_Text_Helper{s.Name}"))}, PokeStudio_Text_HelperBye");
        L("\tswitch VAR_RESULT");
        for (var i = 0; i < services.Count; i++)
        {
            L($"\tcase {i}, PokeStudio_EventScript_Helper{services[i].Name}");
        }

        L("\tgoto PokeStudio_EventScript_HelperEnd");
        L("");
        text("PokeStudio_Text_HelperAsk", "I can tune your Pokémon.\\nWhat would you like?");
        text("PokeStudio_Text_HelperBye", "No, thanks");
        foreach (var s in services)
        {
            text($"PokeStudio_Text_Helper{s.Name}", s.Price > 0 ? $"{s.Label} (¥{s.Price})" : s.Label);
        }

        foreach (var s in services)
        {
            L($"PokeStudio_EventScript_Helper{s.Name}:");
            if (s.Price > 0)
            {
                L($"\tcheckmoney {s.Price}");
                L("\tgoto_if_eq VAR_RESULT, FALSE, PokeStudio_EventScript_HelperNoMoney");
            }

            L("\tmsgbox PokeStudio_Text_HelperWhich, MSGBOX_DEFAULT");
            L("\tspecial ChoosePartyMon");
            if (fireRed)
            {
                L("\twaitstate"); // FireRed's ChoosePartyMon doesn't wait by itself
            }

            L("\tgoto_if_ge VAR_0x8004, PARTY_SIZE, PokeStudio_EventScript_HelperEnd");
            L("\tspecialvar VAR_RESULT, ScriptGetPartyMonSpecies");
            L("\tgoto_if_eq VAR_RESULT, SPECIES_EGG, PokeStudio_EventScript_HelperEgg");
            switch (s.Name)
            {
                case "Nature":
                    L("\tmessage PokeStudio_Text_HelperWhichNature");
                    L("\twaitmessage");
                    L($"\tdynmultichoice 0, 0, FALSE, 6, 0, DYN_MULTICHOICE_CB_NONE, {string.Join(", ", Natures.Select(n => $"PokeStudio_Text_Nature{n.Name}"))}");
                    L("\tgoto_if_eq VAR_RESULT, MULTI_B_PRESSED, PokeStudio_EventScript_HelperEnd");
                    L("\tspecial SetHiddenNature");
                    break;
                case "Ivs":
                    L("\tcallnative PokeStudio_MaxIvs");
                    break;
                default:
                    L("\tmessage PokeStudio_Text_HelperWhichEvs");
                    L("\twaitmessage");
                    L($"\tdynmultichoice 0, 0, FALSE, 6, 0, DYN_MULTICHOICE_CB_NONE, {string.Join(", ", EvSpreads.Select((_, i) => $"PokeStudio_Text_Evs{i}"))}");
                    L("\tgoto_if_eq VAR_RESULT, MULTI_B_PRESSED, PokeStudio_EventScript_HelperEnd");
                    L("\tcallnative PokeStudio_SetEvSpread");
                    break;
            }

            if (s.Price > 0)
            {
                L($"\tremovemoney {s.Price}");
            }

            L("\tplayse SE_SUCCESS");
            L("\tmsgbox PokeStudio_Text_HelperDone, MSGBOX_DEFAULT");
            L("\tgoto PokeStudio_EventScript_HelperEnd");
            L("");
        }

        L("PokeStudio_EventScript_HelperNoMoney:");
        L("\tmsgbox PokeStudio_Text_HelperNoMoney, MSGBOX_DEFAULT");
        L("\tgoto PokeStudio_EventScript_HelperEnd");
        L("");
        L("PokeStudio_EventScript_HelperEgg:");
        L("\tmsgbox PokeStudio_Text_HelperEgg, MSGBOX_DEFAULT");
        L("PokeStudio_EventScript_HelperEnd:");
        L("\tclosemessage");
        L("\trelease");
        L("\tend");
        L("");
        text("PokeStudio_Text_HelperWhich", "Which Pokémon?");
        text("PokeStudio_Text_HelperWhichNature", "Which nature?");
        text("PokeStudio_Text_HelperWhichEvs", "Which EV spread?");
        text("PokeStudio_Text_HelperDone", "All done! Take good care\\nof it.");
        text("PokeStudio_Text_HelperNoMoney", "You don't have enough\\nmoney.");
        text("PokeStudio_Text_HelperEgg", "I can't do that for an Egg.");
        foreach (var n in Natures)
        {
            text($"PokeStudio_Text_Nature{n.Name}", n.Effect.Length == 0 ? n.Name : $"{n.Name} {n.Effect}");
        }

        for (var i = 0; i < EvSpreads.Length; i++)
        {
            text($"PokeStudio_Text_Evs{i}", EvSpreads[i]);
        }
    }
}
