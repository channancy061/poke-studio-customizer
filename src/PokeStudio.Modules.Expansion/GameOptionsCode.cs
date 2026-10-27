using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PokeStudio.Core.GameOptions;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.SourceCode;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// The game-code half of the new-game options: Nuzlocke rules, the adaptive boss slot and the Pokémon Center helper.
/// Everything lives in a generated src/poke_studio.c; the game's own files get one marked line each where it hooks in.
/// </summary>
internal static partial class GameOptionsWriter
{
    public const string CodeFile = "src/poke_studio.c";
    public const string CodeHeader = "include/poke_studio.h";
    public const string BattleSetup = "src/battle_setup.c";
    public const string BattleScripts = "src/battle_script_commands.c";
    public const string PoolsSource = "src/trainer_pools.c";
    public const string PoolsHeader = "include/trainer_pools.h";
    public const string WildConfig = "include/config/wild_encounter.h";
    public const string ItemUse = "src/item_use.c";
    public const string DataSource = "src/data.c";

    /// <summary>What the code needs from the rest of the project.</summary>
    public sealed record Extras(
        IReadOnlyDictionary<string, string> FamilyRoots,
        IReadOnlyList<string> EncounterAreas,
        IReadOnlyList<string> CenterMaps)
    {
        public static readonly Extras None = new(new Dictionary<string, string>(), [], []);

        /// <summary>Expansion 1.17+ keeps parties in gParties[…] (the old gPlayerParty/gEnemyParty names are deprecated).</summary>
        public bool HasParties { get; init; }

        /// <summary>Trainers that always stop the player (bosses and story battles), for Easy.</summary>
        public IReadOnlyList<string> MajorTrainers { get; init; } = [];

        /// <summary>The gyms, for Hard's gym lock.</summary>
        public IReadOnlyList<GymInfo> Gyms { get; init; } = [];

        /// <summary>The Mart Specialist's stock for 0…8 badges.</summary>
        public IReadOnlyList<IReadOnlyList<string>> SpecialistTiers { get; init; } = [];

        /// <summary>Each area's night table, for <see cref="GameOptionsSettings.DayAndNight"/>.</summary>
        public IReadOnlyList<Core.Models.EncounterTable> NightTables { get; init; } = [];

        /// <summary>The playthrough's order of places (for the area lock's "onward"), or null without one.</summary>
        public IReadOnlyList<string>? StoryOrder { get; init; }

        /// <summary>In-game trades' new requests (trade id → species), for <see cref="GameOptionsSettings.TradesAskForWildPokemon"/>.</summary>
        public IReadOnlyDictionary<string, string> TradeRequests { get; init; } = new Dictionary<string, string>();
    }

    public static IReadOnlyList<string> CodeFiles => [CodeFile, CodeHeader, BattleSetup, BattleScripts, PoolsSource, PoolsHeader, WildConfig, ItemUse, DataSource, Healing, WildEncounters, TrainerSee];

    public const string WildEncounters = "src/wild_encounter.c";
    public const string TrainerSee = "src/trainer_see.c";

    /// <summary>Where Pokémon Centers, healing NPCs and blacking out heal the party and the PC boxes.</summary>
    public const string Healing = "src/script_pokemon_util.c";

    private static readonly string[] CodeFlagNames =
        ["FLAG_PS_NUZ_FIRST", "FLAG_PS_NUZ_FAINT", "FLAG_PS_NUZ_DUPES", "FLAG_PS_NUZ_EEVEE", "FLAG_PS_NUZ_SHINY", "FLAG_PS_NUZ_NICK", "FLAG_PS_NUZ_STATIC", "FLAG_PS_ADAPTIVE", "FLAG_PS_NO_CATCH", "FLAG_PS_NUZ_WIPE_ENDS", "FLAG_PS_NUZ_OVER"];

    private static bool CodeUsed(GameOptionsSettings o) => o.PlayerTools || o.NuzlockeUsed || o.AdaptiveSlot != OptionMode.NotUsed || o.CenterHelper.AnyService || TrainerRules(o);

    /// <summary>Trainers behave by difficulty (needs the difficulty option).</summary>
    internal static bool TrainerRules(GameOptionsSettings o) => o.TrainerRulesByDifficulty && o.Difficulty != OptionMode.NotUsed;

    /// <summary>A gym: its leader, the script file the leader's battle is in, and the other trainers on its floors.</summary>
    public sealed record GymInfo(string Leader, string LeaderScript, IReadOnlyList<string> Trainers);

    public static string AreaFlag(string mapsec) => "FLAG_PS_AREA_" + mapsec.Replace("MAPSEC_", "", StringComparison.Ordinal);

    /// <summary>Flag names the code needs beyond the base options.</summary>
    internal static IEnumerable<string> ExtraFlagNames(GameOptionsSettings o, Extras x)
    {
        string[] dexNav = o.DexNav ? ["FLAG_PS_DEXNAV_SEARCHING", "FLAG_PS_DEXNAV_DETECTOR", "VAR_PS_DEXNAV_SPECIES", "VAR_PS_DEXNAV_STEPS"] : [];
        if (!CodeUsed(o))
        {
            return dexNav;
        }

        var areas = o.NuzRuleUsed(o.NuzFirstEncounter) ? x.EncounterAreas.Select(AreaFlag) : [];
        string[] ability = o.CenterHelper.AnyService && o.CenterHelper.AbilityService ? ["FLAG_PS_ABILITY_ON"] : [];
        return [.. CodeFlagNames, .. ability, .. dexNav, .. areas];
    }

    /// <summary>Areas with wild Pokémon (region map sections of the maps in wild_encounters.json) and evolution families.</summary>
    internal static Extras BuildExtras(ExpansionLayout layout, GameData? data)
    {
        var roots = new Dictionary<string, string>(StringComparer.Ordinal);
        if (data is not null)
        {
            var known = data.Species.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
            var parent = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var set in data.Evolutions)
            {
                foreach (var e in set.Evolutions)
                {
                    if (e.TargetSpecies != set.FromSpecies && known.Contains(e.TargetSpecies) && known.Contains(set.FromSpecies))
                    {
                        parent.TryAdd(e.TargetSpecies, set.FromSpecies);
                    }
                }
            }

            foreach (var species in parent.Keys)
            {
                var root = species;
                var seen = new HashSet<string>(StringComparer.Ordinal) { root };
                while (parent.TryGetValue(root, out var up) && seen.Add(up))
                {
                    root = up;
                }

                if (root != species)
                {
                    roots[species] = root;
                }
            }
        }

        var mapsecByMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var mapsDir = layout.Full("data/maps");
        if (Directory.Exists(mapsDir))
        {
            foreach (var file in Directory.GetDirectories(mapsDir).Select(d => Path.Combine(d, "map.json")).Where(File.Exists))
            {
                var text = File.ReadAllText(file);
                var id = Regex.Match(text, "\"id\"\\s*:\\s*\"(\\w+)\"");
                var sec = Regex.Match(text, "\"region_map_section\"\\s*:\\s*\"(\\w+)\"");
                if (id.Success && sec.Success)
                {
                    mapsecByMap[id.Groups[1].Value] = sec.Groups[1].Value;
                }
            }
        }

        var areas = new SortedSet<string>(StringComparer.Ordinal);
        if (File.Exists(layout.Full(EncountersFile.File)))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(layout.Full(EncountersFile.File)));
            foreach (var group in doc.RootElement.GetProperty("wild_encounter_groups").EnumerateArray())
            {
                if (!group.TryGetProperty("for_maps", out var forMaps) || !forMaps.GetBoolean())
                {
                    continue;
                }

                foreach (var enc in group.GetProperty("encounters").EnumerateArray())
                {
                    if (enc.TryGetProperty("map", out var map) && mapsecByMap.TryGetValue(map.GetString()!, out var sec) && sec != "MAPSEC_NONE")
                    {
                        areas.Add(sec);
                    }
                }
            }
        }

        var pokemonHeader = layout.Full("include/pokemon.h");
        return new Extras(roots, [.. areas], [.. CenterPlacement.CenterMaps(layout.Root)])
        {
            HasParties = File.Exists(pokemonHeader) && File.ReadAllText(pokemonHeader).Contains("gParties[", StringComparison.Ordinal),
            MajorTrainers = data is null ? [] : MajorTrainers(data),
            Gyms = data is null ? [] : Gyms(layout, data),
            SpecialistTiers = data is null ? [] : Core.Progression.SpecialistStock.Tiers(data),
            StoryOrder = data is null ? null : Core.Progression.StoryOrder.For(data),
            NightTables = data is null ? [] : Core.Progression.EncounterPlanner.NightTables(data, data.Lab ?? new Core.ChampionLab.LabSettings()),
            TradeRequests = data is null ? new Dictionary<string, string>() : TradeWriter.Requests(layout, data),
        };
    }

    /// <summary>Bosses (every group but regular trainers) and story battles: they stop the player even on Easy.</summary>
    private static List<string> MajorTrainers(GameData data)
    {
        var lab = data.Lab ?? new Core.ChampionLab.LabSettings();
        return data.Trainers.Where(t => !t.Key.Contains('@', StringComparison.Ordinal) && t.Party.Count > 0
                && (t.Key.StartsWith(Core.Story.StoryBattles.TrainerPrefix, StringComparison.Ordinal) || Core.ChampionLab.Lab.GroupOf(data, lab, t) != Core.ChampionLab.TrainerGroup.Regular))
            .Select(t => t.Key).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// <summary>Every gym (maps named …_Gym…, all floors together) with its leader and the other trainers in it.</summary>
    private static List<GymInfo> Gyms(ExpansionLayout layout, GameData data)
    {
        var maps = layout.Full("data/maps");
        if (!Directory.Exists(maps))
        {
            return [];
        }

        var known = data.Trainers.ToDictionary(t => t.Key, StringComparer.Ordinal);
        var gyms = new List<GymInfo>();
        foreach (var gym in Directory.GetDirectories(maps).Select(Path.GetFileName).OfType<string>()
                     .Where(d => d.Contains("_Gym", StringComparison.Ordinal) && (layout.IsFireRedBase || !d.EndsWith("_Frlg", StringComparison.Ordinal)))
                     .GroupBy(d => d[..(d.IndexOf("_Gym", StringComparison.Ordinal) + 4)])
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var battles = new List<(string Trainer, string File)>();
            foreach (var dir in gym.Order(StringComparer.Ordinal))
            {
                var file = $"data/maps/{dir}/scripts.inc";
                if (File.Exists(layout.Full(file)))
                {
                    battles.AddRange(Regex.Matches(File.ReadAllText(layout.Full(file)), @"\btrainerbattle\w*\s+(?:\w+,\s*)?(TRAINER_\w+)")
                        .Select(m => (m.Groups[1].Value, file)));
                }
            }

            bool IsLeader(string key) => Core.Progression.StoryOrder.BadgeOf(key) is not null
                || known.TryGetValue(key, out var t) && t.TrainerClass.Contains("LEADER", StringComparison.Ordinal);
            if (battles.FirstOrDefault(b => IsLeader(b.Trainer)) is not { Trainer: not null } leader)
            {
                continue;
            }

            var others = battles.Select(b => b.Trainer).Distinct(StringComparer.Ordinal)
                .Where(k => !IsLeader(k) && known.TryGetValue(k, out var t) && !Core.ChampionLab.Lab.IsRematch(data, t)).ToList();
            if (others.Count > 0)
            {
                gyms.Add(new GymInfo(leader.Trainer, leader.File, others));
            }
        }

        return gyms;
    }

    /// <summary>Plans the code files and hooks (called from <see cref="Plan"/>).</summary>
    private static void PlanCode(ExpansionLayout layout, GameOptionsSettings o, Ids ids, Func<string, string?> read, Func<string, byte[]?> readBytes,
        Action<string, string?, string> put, Extras x, List<string> notes)
    {
        if (!CodeUsed(o))
        {
            return;
        }

        put(CodeHeader, read(CodeHeader), BuildCodeHeader());
        put(CodeFile, read(CodeFile), BuildCode(o, ids, x));

        // the "can't catch" flag: expansion's own setting, pointed at our flag unless the creator already set one
        var (cfgFile, cfgName) = read(WildConfig)?.Contains("WE_FLAG_NO_CATCHING", StringComparison.Ordinal) == true
            ? (WildConfig, "WE_FLAG_NO_CATCHING") : ("include/config/battle.h", "B_FLAG_NO_CATCHING");
        if (read(cfgFile) is { } cfg && Define(cfg, cfgName) == "0")
        {
            put(cfgFile, cfg, SetDefine(cfg, cfgName, ids.Flags["FLAG_PS_NO_CATCH"]));
        }

        // battle_setup.c: before a wild battle and after every battle
        if (read(BattleSetup) is { } setup)
        {
            var updated = setup;
            if (!setup.Contains("PokeStudio_", StringComparison.Ordinal))
            {
                updated = AddInclude(updated);
                updated = InsertAtBodyStart(updated, "static void DoStandardWildBattle(bool32 isDouble)", "PokeStudio_BeforeWildBattle(); // Poke Studio: Nuzlocke area rules");
                updated = InsertAfterFollowerBlock(updated, "static void CB2_EndWildBattle(void)");
                updated = InsertAfterFollowerBlock(updated, "static void CB2_EndTrainerBattle(void)");
            }

            // static encounters (scripted and legendary battles) follow the area rules when the player chose so
            if (!updated.Contains("Nuzlocke static encounters", StringComparison.Ordinal))
            {
                const string statement = "if (FlagGet(FLAG_PS_NUZ_STATIC)) PokeStudio_BeforeWildBattle(); // Poke Studio: Nuzlocke static encounters";
                updated = InsertAtBodyStart(updated, "void BattleSetup_StartScriptedWildBattle(void)", statement);
                updated = InsertAtBodyStart(updated, "void BattleSetup_StartLegendaryBattle(void)", statement);
            }

            put(BattleSetup, setup, updated);
        }

        // item_use.c: the catch rule is checked when a ball is about to be thrown
        if (read(ItemUse) is { } itemUse && !itemUse.Contains("PokeStudio_UpdateCatchFlag", StringComparison.Ordinal))
        {
            var updated = InsertAtBodyStart(itemUse, "static u32 GetBallThrowableState(void)", "PokeStudio_UpdateCatchFlag(); // Poke Studio: Nuzlocke catch rules");
            if (updated != itemUse)
            {
                put(ItemUse, itemUse, AddInclude(updated));
            }
            else
            {
                notes.Add("The ball-throw check wasn't found, so the first-encounter rule applies to the first wild Pokémon only.");
            }
        }

        // projects patched by an older version: their fainting checks start right away, so the Oak's Lab battle (lost,
        // then healed by the story) left the starter fainted; they now wait for the Pokédex like the new ones
        foreach (var file in new[] { Healing, Pokemon, BattleScripts })
        {
            if (read(file) is { } old && old.Contains($"FlagGet(FLAG_PS_NUZ_FAINT)", StringComparison.Ordinal))
            {
                put(file, old, Regex.Replace(old, $@"FlagGet\(FLAG_PS_NUZ_FAINT\)(?=[^\n]*{Mark}|[^\n]*\n[^\n]*HealBoxPokemon)", "PS_NUZ_FAINT_ON"));
            }
        }

        // script_pokemon_util.c: a fainted Pokémon stays fainted in a Nuzlocke. Pokémon Centers, healing NPCs and blacking
        // out all heal through HealPlayerParty (and the PC boxes through HealPlayerBoxes), so nothing brings it back.
        if (read(Healing) is { } healing && !healing.Contains("stay fainted in a Nuzlocke", StringComparison.Ordinal))
        {
            var fn = healing.IndexOf("void HealPlayerParty(void)", StringComparison.Ordinal);
            var party = fn < 0 ? Match.Empty : Regex.Match(healing[fn..], @"^([ \t]+)HealPokemon\((&[^;]+)\);[^\n]*\n", RegexOptions.Multiline);
            var boxFn = healing.IndexOf("static void HealPlayerBoxes(void)", StringComparison.Ordinal);
            var box = boxFn < 0 ? Match.Empty : Regex.Match(healing[boxFn..], @"^([ \t]+)HealBoxPokemon\((\w+)\);[^\n]*\n", RegexOptions.Multiline);
            if (party.Success && box.Success)
            {
                var nl = CScanner.LineEnding(healing);
                var (pi, pm) = (party.Groups[1].Value, party.Groups[2].Value);
                var (bi, bm) = (box.Groups[1].Value, box.Groups[2].Value);
                var boxText = string.Join(nl,
                    $"{bi}{{ // {Mark}: fainted Pokémon stay fainted in a Nuzlocke",
                    $"{bi}    struct Pokemon psMon;",
                    $"{bi}    BoxMonToMon({bm}, &psMon);",
                    $"{bi}    if (!PS_NUZ_FAINT_ON || GetMonData(&psMon, MON_DATA_HP) != 0)",
                    $"{bi}        HealBoxPokemon({bm});",
                    $"{bi}}}") + nl;
                var edited = healing.Remove(boxFn + box.Index, box.Length).Insert(boxFn + box.Index, boxText);
                var partyText = $"{pi}if (!PS_NUZ_FAINT_ON || GetMonData({pm}, MON_DATA_HP) != 0) // {Mark}: fainted Pokémon stay fainted in a Nuzlocke{nl}{pi}    HealPokemon({pm});{nl}";
                edited = edited.Remove(fn + party.Index, party.Length).Insert(fn + party.Index, partyText);
                put(Healing, healing, edited);
            }
            else
            {
                notes.Add("The party healing code wasn't found, so a Pokémon Center can still bring back a fainted Pokémon in a Nuzlocke.");
            }
        }

        // wild_encounter.c and trainer_see.c: no battle starts while a Nuzlocke party has nobody left standing
        // (after a wipe the fainted team stays fainted; the player has to take a healthy one from the PC first)
        foreach (var (file, functions) in new[] { (WildEncounters, new[] { "StandardWildEncounter", "SweetScentWildEncounter" }), (TrainerSee, new[] { "CheckForTrainersWantingBattle" }) })
        {
            if (read(file) is not { } c || c.Contains("nobody left to battle", StringComparison.Ordinal))
            {
                continue;
            }

            var updated = c;
            foreach (var name in functions)
            {
                var head = Regex.Match(updated, $@"^[\w ]+\b{name}\([^)\n]*\)\r?\n\{{[^\n]*\n", RegexOptions.Multiline);
                if (head.Success)
                {
                    updated = updated.Insert(head.Index + head.Length, $"    if (!PokeStudio_NuzCanBattle()) // {Mark}: Nuzlocke, nobody left to battle{CScanner.LineEnding(c)}        return FALSE;{CScanner.LineEnding(c)}");
                }
            }

            if (updated != c)
            {
                put(file, c, AddInclude(updated));
            }
        }

        // projects patched by an older version: their Hard sight went through the game's distance helpers, which stop at
        // 3 tiles while every object slot is taken (a following Pokémon takes one), so trainers didn't see far after all
        if (read(TrainerSee) is { } oldSee)
        {
            var block = Regex.Match(oldSee, $@"^    sPsSeenDirection = 0;[^\n]*\n(?:[^\n]*{Mark}[^\n]*\n)*?    \}} // {Mark}\r?\n", RegexOptions.Multiline);
            var now = HardSight(CScanner.LineEnding(oldSee));
            if (block.Success && block.Value != now)
            {
                put(TrainerSee, oldSee, oldSee.Remove(block.Index, block.Length).Insert(block.Index, now));
            }
        }

        // trainer_see.c: trainers by difficulty (Easy: only bosses stop the player; Hard: every trainer sees far and in
        // every direction, turning to the player before walking over)
        if (read(TrainerSee) is { } see && !see.Contains("trainers by difficulty", StringComparison.Ordinal))
        {
            var nl = CScanner.LineEnding(see);
            var updated = see;
            var checkDef = Regex.Match(updated, @"^static u8 CheckTrainer\(u8 (\w+)\)\r?\n\{", RegexOptions.Multiline);
            var approach = Regex.Match(updated, @"^static u8 GetTrainerApproachDistance\(struct ObjectEvent \*trainerObj\)\r?\n\{[\s\S]*?PlayerGetDestCoords\(&x, &y\);[^\n]*\n", RegexOptions.Multiline);
            var queue = Regex.Match(updated, @"^([ \t]*)gApproachingTrainers\[gNoOfApproachingTrainers\]\.objectEventId = (\w+);", RegexOptions.Multiline);
            if (checkDef.Success && approach.Success && queue.Success && checkDef.Index < queue.Index && queue.Index < approach.Index)
            {
                // from the end of the file backwards, so earlier insertions don't move the later places
                var i = queue.Groups[1].Value;
                var id = queue.Groups[2].Value;
                updated = updated.Insert(approach.Index + approach.Length, HardSight(nl));
                updated = updated.Insert(queue.Index, string.Join(nl,
                    $"{i}if (trainerBattlePtr && CurrentBattlePyramidLocation() == PYRAMID_LOCATION_NONE // {Mark}: trainers by difficulty (Easy)",
                    $"{i}    && PokeStudio_TrainerIgnoresPlayer(((TrainerBattleParameter *)(trainerBattlePtr + 1))->params.opponentA)) // {Mark}",
                    $"{i}    return 0; // {Mark}",
                    $"{i}if (sPsSeenDirection != 0) // {Mark}: turn to the player first",
                    $"{i}    ObjectEventTurn(&gObjectEvents[{id}], sPsSeenDirection); // {Mark}",
                    ""));
                updated = updated.Insert(checkDef.Index, $"static u8 sPsSeenDirection; // {Mark}: trainers by difficulty{nl}");
                put(TrainerSee, see, AddInclude(updated));
            }
            else
            {
                notes.Add("The trainer sight code wasn't found, so trainers behave the same on every difficulty.");
            }
        }

        // pokemon.c: no revive items in a Nuzlocke (Revive, Max Revive, Revival Herb, Sacred Ash all go through here)
        if (read(Pokemon) is { } pokemonC && !pokemonC.Contains("no revives in a Nuzlocke", StringComparison.Ordinal))
        {
            var at = pokemonC.IndexOf("// Check use validity.", StringComparison.Ordinal);
            if (at >= 0 && pokemonC.IndexOf("ITEM4_REVIVE >> 2", at, StringComparison.Ordinal) is var r and > 0 && r - at < 200)
            {
                var nl = CScanner.LineEnding(pokemonC);
                var lineStart = pokemonC.LastIndexOf('\n', at) + 1;
                var indent = CScanner.IndentAt(pokemonC, at);
                var block = string.Join(nl,
                    $"{indent}if ((effectFlags & (ITEM4_REVIVE >> 2)) && PS_NUZ_FAINT_ON) // {Mark}: no revives in a Nuzlocke",
                    $"{indent}{{",
                    $"{indent}    itemEffectParam++;",
                    $"{indent}    break;",
                    $"{indent}}}") + nl;
                put(Pokemon, pokemonC, pokemonC.Insert(lineStart, block));
            }
            else
            {
                notes.Add("The revive item check wasn't found, so revive items still work in a Nuzlocke.");
            }
        }

        // battle_script_commands.c: Revival Blessing fails for the player in a Nuzlocke
        if (read(BattleScripts) is { } bsc && !bsc.Contains("no Revival Blessing in a Nuzlocke", StringComparison.Ordinal))
        {
            var fn = bsc.IndexOf("void BS_TryRevivalBlessing(void)", StringComparison.Ordinal);
            var first = fn < 0 ? -1 : bsc.IndexOf("u8 index = GetFirstFaintedPartyIndex(gBattlerAttacker);", fn, StringComparison.Ordinal);
            if (first >= 0)
            {
                var nl = CScanner.LineEnding(bsc);
                var lineEnd = bsc.IndexOf('\n', first) + 1;
                var indent = CScanner.IndentAt(bsc, first);
                put(BattleScripts, bsc, bsc.Insert(lineEnd,
                    $"{indent}if (PS_NUZ_FAINT_ON && IsOnPlayerSide(gBattlerAttacker)) // {Mark}: no Revival Blessing in a Nuzlocke{nl}{indent}    index = PARTY_SIZE;{nl}"));
            }
        }

        // battle_script_commands.c: nicknames required → straight to the naming screen
        if (read(BattleScripts) is { } scripts && !scripts.Contains("FLAG_PS_NUZ_NICK", StringComparison.Ordinal))
        {
            var fn = scripts.IndexOf("static void Cmd_trygivecaughtmonnick(void)\n", StringComparison.Ordinal) is var a and >= 0
                ? a : scripts.IndexOf("static void Cmd_trygivecaughtmonnick(void)\r\n", StringComparison.Ordinal);
            var case0 = fn < 0 ? -1 : scripts.IndexOf("case 0:", fn, StringComparison.Ordinal);
            if (case0 >= 0)
            {
                var nl = CScanner.LineEnding(scripts);
                var lineEnd = scripts.IndexOf('\n', case0) + 1;
                var indent = CScanner.IndentAt(scripts, case0) + "    ";
                var block = string.Join(nl,
                    $"{indent}if (FlagGet(FLAG_PS_NUZ_NICK)) // {Mark}: Nuzlocke nicknames - no yes/no, straight to naming",
                    $"{indent}{{",
                    $"{indent}    gBattleCommunication[MULTIUSE_STATE] = 2;",
                    $"{indent}    BeginFastPaletteFade(3);",
                    $"{indent}    break;",
                    $"{indent}}}") + nl;
                put(BattleScripts, scripts, scripts.Insert(lineEnd, block));
            }
            else
            {
                notes.Add("The caught-Pokémon nickname step wasn't found, so \"nicknames required\" only works as a rule the player follows.");
            }
        }

        // ... and the species' own name (or an empty one) doesn't count: the naming screen opens again
        if (read(BattleScripts) is { } named && !named.Contains("the species name doesn't count", StringComparison.Ordinal))
        {
            var fn = named.IndexOf("static void Cmd_trygivecaughtmonnick(void)", StringComparison.Ordinal);
            var set = fn < 0 ? -1 : named.IndexOf("SetMonData(GetBattlerMon(gBattlerTarget), MON_DATA_NICKNAME, gBattleStruct->caughtMonNick);", fn, StringComparison.Ordinal);
            if (set >= 0)
            {
                var nl = CScanner.LineEnding(named);
                var lineStart = named.LastIndexOf('\n', set) + 1;
                var indent = CScanner.IndentAt(named, set);
                var block = string.Join(nl,
                    $"{indent}if (FlagGet(FLAG_PS_NUZ_NICK) && (gBattleStruct->caughtMonNick[0] == EOS",
                    $"{indent} || StringCompare(gBattleStruct->caughtMonNick, GetSpeciesName(GetMonData(GetBattlerMon(gBattlerTarget), MON_DATA_SPECIES))) == 0))",
                    $"{indent}{{",
                    $"{indent}    // {Mark}: Nuzlocke nicknames - the species name doesn't count, so the naming screen opens again",
                    $"{indent}    gBattleCommunication[MULTIUSE_STATE] = 2;",
                    $"{indent}    BeginFastPaletteFade(3);",
                    $"{indent}    break;",
                    $"{indent}}}") + nl;
                put(BattleScripts, named, named.Insert(lineStart, block));
            }
        }

        // trainer pools: our pick function for the adaptive boss slot
        if (read(PoolsHeader) is { } poolsH && !poolsH.Contains("POOL_PICK_POKE_STUDIO_ADAPTIVE", StringComparison.Ordinal))
        {
            var m = Regex.Match(poolsH, @"^([ \t]*)POOL_PICK_LOWEST,[^\n]*\n", RegexOptions.Multiline);
            if (m.Success)
            {
                put(PoolsHeader, poolsH, poolsH.Insert(m.Index + m.Length, $"{m.Groups[1].Value}POOL_PICK_POKE_STUDIO_ADAPTIVE, // {Mark}: adaptive boss slot{CScanner.LineEnding(poolsH)}"));
            }
        }

        if (read(PoolsSource) is { } poolsC && !poolsC.Contains("POOL_PICK_POKE_STUDIO_ADAPTIVE", StringComparison.Ordinal))
        {
            var fn = poolsC.IndexOf("GetPickFunctions(", StringComparison.Ordinal);
            var def = fn < 0 ? -1 : poolsC.IndexOf("default:", fn, StringComparison.Ordinal);
            if (def >= 0)
            {
                var nl = CScanner.LineEnding(poolsC);
                var lineStart = poolsC.LastIndexOf('\n', def) + 1;
                var indent = CScanner.IndentAt(poolsC, def);
                var block = string.Join(nl,
                    $"{indent}case POOL_PICK_POKE_STUDIO_ADAPTIVE: // {Mark}: team in order, one slot chosen against the player's party",
                    $"{indent}    pickFunctions.LeadFunction = &PokeStudio_PickNone;",
                    $"{indent}    pickFunctions.AceFunction = &PokeStudio_PickNone;",
                    $"{indent}    pickFunctions.OtherFunction = &PokeStudio_PickAdaptive;",
                    $"{indent}    break;") + nl;
                put(PoolsSource, poolsC, AddInclude(poolsC.Insert(lineStart, block)));
            }
        }

        // the trainer list (compiled in data.c) names our pick function; older expansion versions (1.15) don't include
        // the pools header there
        if (read(DataSource) is { } dataC && read(PoolsHeader) is not null && !dataC.Contains("#include \"trainer_pools.h\"", StringComparison.Ordinal)
            && Regex.Match(dataC, @"^#include ""data\.h""[^\n]*\n", RegexOptions.Multiline) is { Success: true } dataH)
        {
            put(DataSource, dataC, dataC.Insert(dataH.Index + dataH.Length, $"#include \"trainer_pools.h\" // {Mark}: adaptive boss slot{CScanner.LineEnding(dataC)}"));
        }

        // the Pokémon Center helper stands in every Center
        if (o.CenterHelper.AnyService)
        {
            var gfx = read("include/constants/event_objects.h")?.Contains("OBJ_EVENT_GFX_SCIENTIST_1,", StringComparison.Ordinal) == true
                ? "OBJ_EVENT_GFX_SCIENTIST_1" : "OBJ_EVENT_GFX_SCIENTIST";
            var skipped = new List<string>();
            foreach (var map in x.CenterMaps)
            {
                if (read(map) is not { } json || json.Contains(CenterPlacement.HelperScript, StringComparison.Ordinal))
                {
                    continue;
                }

                var layoutId = Regex.Match(json, "\"layout\"\\s*:\\s*\"(\\w+)\"").Groups[1].Value;
                var grid = CenterPlacement.LoadGrid(read, readBytes, layoutId);
                var spot = grid is null ? null : CenterPlacement.Find(json, grid);
                if (spot is null)
                {
                    skipped.Add(map.Split('/')[2]);
                    continue;
                }

                put(map, json, CenterPlacement.AddHelper(json, spot, gfx));
            }

            if (skipped.Count > 0)
            {
                notes.Add($"No free spot for the Pokémon Center helper in: {string.Join(", ", skipped)}.");
            }
        }
    }

    /// <summary>
    /// GetTrainerApproachDistance's Hard branch: the player straight in line anywhere on screen (7 tiles left or right, 5 up
    /// or down, or the trainer's own longer range), in any direction. The distance is worked out here: the game's own helpers stop at 3 tiles while every
    /// object slot is taken, and a following Pokémon takes one.
    /// </summary>
    private static string HardSight(string nl) => string.Join(nl,
        $"    sPsSeenDirection = 0; // {Mark}: trainers by difficulty (Hard: far, every direction)",
        $"    if (PokeStudio_TrainersSeeFar() && trainerObj->trainerType != TRAINER_TYPE_BURIED) // {Mark}",
        $"    {{ // {Mark}",
        $"        s16 psDx = x - trainerObj->currentCoords.x, psDy = y - trainerObj->currentCoords.y; // {Mark}",
        $"        u8 psDir = psDx == 0 ? (psDy > 0 ? DIR_SOUTH : DIR_NORTH) : psDy == 0 ? (psDx > 0 ? DIR_EAST : DIR_WEST) : DIR_NONE; // {Mark}",
        $"        approachDistance = psDx == 0 ? abs(psDy) : abs(psDx); // {Mark}",
        $"        if (psDir != DIR_NONE && approachDistance != 0 && approachDistance <= max(trainerObj->trainerRange_berryTreeId, psDx == 0 ? 5 : 7) // {Mark}: as far as the screen shows",
        $"            && CheckPathBetweenTrainerAndPlayer(trainerObj, approachDistance, psDir)) // {Mark}",
        $"        {{ // {Mark}",
        $"            sPsSeenDirection = psDir; // {Mark}",
        $"            return psDir == trainerObj->facingDirection ? approachDistance : 1; // {Mark}: walk over only along their own line (from the side they'd end up in a path)",
        $"        }} // {Mark}",
        $"        return 0; // {Mark}",
        $"    }} // {Mark}",
        "");

    private static string AddInclude(string c)
    {
        if (c.Contains("#include \"poke_studio.h\"", StringComparison.Ordinal))
        {
            return c;
        }

        var nl = CScanner.LineEnding(c);
        // after the includes at the top (some files include data tables further down, after code that needs our header)
        var firstBody = c.IndexOf("\n{", StringComparison.Ordinal) is var b and >= 0 ? b : c.Length;
        var last = Regex.Matches(c, @"^#include [""<][^\n]*\n", RegexOptions.Multiline).LastOrDefault(m => m.Index < firstBody);
        return last is null ? c : c.Insert(last.Index + last.Length, $"#include \"poke_studio.h\" // {Mark}{nl}");
    }

    private static string InsertAtBodyStart(string c, string signature, string statement)
    {
        var at = c.IndexOf(signature + "\n", StringComparison.Ordinal) is var a and >= 0 ? a : c.IndexOf(signature + "\r\n", StringComparison.Ordinal);
        if (at < 0)
        {
            return c;
        }

        var open = c.IndexOf('{', at);
        var lineEnd = c.IndexOf('\n', open) + 1;
        return c.Insert(lineEnd, "    " + statement + CScanner.LineEnding(c));
    }

    /// <summary>After the follower-partner block (which restores the party), or at the start of the function.</summary>
    private static string InsertAfterFollowerBlock(string c, string signature)
    {
        const string statement = "PokeStudio_AfterBattle(); // Poke Studio: Nuzlocke rules after a battle";
        var at = c.IndexOf(signature + "\n", StringComparison.Ordinal) is var a and >= 0 ? a : c.IndexOf(signature + "\r\n", StringComparison.Ordinal);
        if (at < 0)
        {
            return c;
        }

        var open = c.IndexOf('{', at);
        var close = CScanner.FindMatching(c, open);
        var restore = c.IndexOf("RestorePartyAfterFollowerNPCBattle();", open, StringComparison.Ordinal);
        if (restore > 0 && restore < close)
        {
            var blockOpen = c.LastIndexOf('{', restore);
            var blockClose = CScanner.FindMatching(c, blockOpen);
            var lineEnd = c.IndexOf('\n', blockClose) + 1;
            var nl = CScanner.LineEnding(c);
            return c.Insert(lineEnd, nl + "    " + statement + nl);
        }

        return InsertAtBodyStart(c, signature, statement);
    }

    private static string BuildCodeHeader() => """
        // Generated by Poke Studio: functions of the new-game options code (src/poke_studio.c).
        #ifndef GUARD_POKE_STUDIO_H
        #define GUARD_POKE_STUDIO_H

        struct Trainer;
        struct PoolRules;

        void PokeStudio_BeforeWildBattle(void);
        void PokeStudio_UpdateCatchFlag(void);
        void PokeStudio_AfterBattle(void);
        bool32 PokeStudio_TrainerIgnoresPlayer(u32 trainerId);
        bool32 PokeStudio_TrainersSeeFar(void);
        void PokeStudio_HardTrainers(void);
        void PokeStudio_GymTrainersLeft(void);
        void PokeStudio_ToolsBlocked(void);
        void PokeStudio_ToolsFly(void);
        bool32 PokeStudio_NuzCanBattle(void);
        void PokeStudio_NuzAfterWipe(void);
        u32 PokeStudio_PickNone(const struct Trainer *trainer, u8 *poolIndexArray, u32 partyIndex, u32 monsCount, u32 battleTypeFlags, struct PoolRules *rules);
        u32 PokeStudio_PickAdaptive(const struct Trainer *trainer, u8 *poolIndexArray, u32 partyIndex, u32 monsCount, u32 battleTypeFlags, struct PoolRules *rules);
        void PokeStudio_MaxIvs(void);
        void PokeStudio_SetEvSpread(void);
        void PokeStudio_ListAbilities(void);
        void PokeStudio_AbilityChoiceHidden(void);
        void PokeStudio_SetAbility(void);

        #endif // GUARD_POKE_STUDIO_H

        """.ReplaceLineEndings("\n");

    internal static string BuildCode(GameOptionsSettings o, Ids ids, Extras x)
    {
        var sb = new StringBuilder();
        void L(string line = "") => sb.Append(line).Append('\n');
        L("// Generated by Poke Studio: Nuzlocke rules, the adaptive boss slot and the Pokémon Center helper.");
        L("// Edit them in the app (Game options); changes made here are replaced on the next save.");
        L("#include \"global.h\"");
        L("#include \"battle.h\"");
        L("#include \"battle_script_commands.h\"");
        L("#include \"battle_util.h\"");
        L("#include \"data.h\"");
        L("#include \"event_data.h\"");
        L("#include \"fieldmap.h\"");
        L("#include \"item.h\"");
        L("#include \"pokedex.h\"");
        L("#include \"pokemon.h\"");
        L("#include \"pokemon_storage_system.h\"");
        L("#include \"string_util.h\"");
        L("#include \"trainer_pools.h\"");
        L("#include \"constants/region_map_sections.h\"");
        L("#include \"battle_setup.h\"");
        L("#include \"event_object_movement.h\"");
        L("#include \"main.h\"");
        L("#include \"region_map.h\"");
        L("#include \"constants/trainer_types.h\"");
        L("#include \"difficulty.h\"");
        L("#include \"constants/difficulty.h\"");
        L("#include \"constants/opponents.h\"");
        L("#include \"poke_studio.h\"");
        L();
        L("#if defined(WE_FLAG_NO_CATCHING) && WE_FLAG_NO_CATCHING != 0");
        L("#define PS_NO_CATCH WE_FLAG_NO_CATCHING");
        L("#elif defined(B_FLAG_NO_CATCHING) && B_FLAG_NO_CATCHING != 0");
        L("#define PS_NO_CATCH B_FLAG_NO_CATCHING");
        L("#else");
        L("#define PS_NO_CATCH FLAG_PS_NO_CATCH");
        L("#endif");
        L();
        L(x.HasParties ? "#define PS_PLAYER_PARTY gParties[B_TRAINER_PLAYER]" : "#define PS_PLAYER_PARTY gPlayerParty");
        L(x.HasParties ? "#define PS_ENEMY_PARTY gParties[B_TRAINER_OPPONENT_A]" : "#define PS_ENEMY_PARTY gEnemyParty");
        L();

        var areas = o.NuzRuleUsed(o.NuzFirstEncounter) ? x.EncounterAreas.Where(a => ids.Flags.ContainsKey(AreaFlag(a))).ToList() : [];
        L("// Region map section → flag set once the first wild Pokémon there was met.");
        L("static const u16 sAreaFlags[][2] =");
        L("{");
        foreach (var a in areas)
        {
            L($"    {{{a}, {AreaFlag(a)}}},");
        }

        L("    {MAPSEC_NONE, 0},");
        L("};");
        L();
        L("// Evolution family of each species (the first stage); species not listed are their own family.");
        L("static const u16 sFamilyRoot[NUM_SPECIES] =");
        L("{");
        foreach (var (species, root) in x.FamilyRoots.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            L($"    [{species}] = {root},");
        }

        L("};");
        L();
        L("""
            static u32 FamilyRoot(u32 species)
            {
                return (species < NUM_SPECIES && sFamilyRoot[species] != SPECIES_NONE) ? sFamilyRoot[species] : species;
            }

            static bool32 FamilyCaught(u32 species)
            {
                u32 root = FamilyRoot(species);
                for (u32 i = 1; i < NUM_SPECIES; i++)
                {
                    if (FamilyRoot(i) == root && GetSetPokedexFlag(SpeciesToNationalPokedexNum(i), FLAG_GET_CAUGHT))
                        return TRUE;
                }
                return FALSE;
            }

            static u32 AreaFlag(u32 mapsec)
            {
                for (u32 i = 0; sAreaFlags[i][1] != 0; i++)
                {
                    if (sAreaFlags[i][0] == mapsec)
                        return sAreaFlags[i][1];
                }
                return 0;
            }

            // Which wild Pokémon of this battle may be caught (bit per enemy party slot); valid while sCatchRulesActive.
            EWRAM_DATA static u8 sCatchable = 0;
            EWRAM_DATA static bool8 sCatchRulesActive = FALSE;

            static bool32 IsDuplicate(u32 species)
            {
                return FlagGet(FLAG_PS_NUZ_DUPES) && FamilyCaught(species)
                    && !(FlagGet(FLAG_PS_NUZ_EEVEE) && FamilyRoot(species) == SPECIES_EEVEE);
            }

            // Before a wild battle: only the first Pokémon met in an area can be caught (dupes and shiny clauses aside).
            // In a wild double battle either of the two may be the catch (a ball can only be thrown once one is left).
            void PokeStudio_BeforeWildBattle(void)
            {
                u32 flag, fresh = 0;

                FlagClear(PS_NO_CATCH);
                sCatchable = 0;
                sCatchRulesActive = FlagGet(FLAG_PS_NUZ_FIRST);
                if (!sCatchRulesActive)
                    return;
                // an area's first encounter only counts once the player has a Poké Ball (the usual Nuzlocke rule):
                // battles before that, like the one on Route 101 before Birch gives you balls, don't use the area up
                if (!IsBagPocketNonEmpty(POCKET_POKE_BALLS))
                {
                    sCatchRulesActive = FALSE;
                    return;
                }
                for (u32 i = 0; i < 2; i++)
                {
                    struct Pokemon *wild = &PS_ENEMY_PARTY[i];
                    u32 species = GetMonData(wild, MON_DATA_SPECIES);
                    if (species == SPECIES_NONE)
                        continue;
                    if (FlagGet(FLAG_PS_NUZ_SHINY) && IsMonShiny(wild))
                        sCatchable |= 1u << i; // shiny clause: always catchable, doesn't use up the area
                    else if (!IsDuplicate(species))
                        fresh |= 1u << i;      // a duplicate doesn't use up the area
                }
                if (fresh == 0)
                    return;
                flag = AreaFlag(gMapHeader.regionMapSectionId);
                if (flag == 0)
                {
                    sCatchable |= fresh; // an area the rules don't track
                }
                else if (!FlagGet(flag))
                {
                    FlagSet(flag);
                    sCatchable |= fresh;
                }
            }

            // When a ball is about to be thrown: allowed only at a Pokémon the rules let the player catch.
            void PokeStudio_UpdateCatchFlag(void)
            {
                u32 slot;

                if (!sCatchRulesActive)
                    return;
                slot = gBattlerPartyIndexes[GetCatchingBattler()];
                if (slot < 8 && (sCatchable & (1u << slot)))
                    FlagClear(PS_NO_CATCH);
                else
                    FlagSet(PS_NO_CATCH);
            }

            static bool32 IsUsableMon(struct Pokemon *mon)
            {
                return GetMonData(mon, MON_DATA_SPECIES) != SPECIES_NONE && !GetMonData(mon, MON_DATA_IS_EGG);
            }

            // After any battle: Pokémon that fainted are gone, unless the whole team fainted (the run is over; the player decides).
            void PokeStudio_AfterBattle(void)
            {
                u32 alive = 0, released = 0;

                FlagClear(PS_NO_CATCH);
                sCatchRulesActive = FALSE;
                if (!PS_NUZ_FAINT_ON)
                    return;
                for (u32 i = 0; i < PARTY_SIZE; i++)
                {
                    if (IsUsableMon(&PS_PLAYER_PARTY[i]) && GetMonData(&PS_PLAYER_PARTY[i], MON_DATA_HP) != 0)
                        alive++;
                }
                if (alive == 0)
                    return;
                for (u32 i = 0; i < PARTY_SIZE; i++)
                {
                    if (IsUsableMon(&PS_PLAYER_PARTY[i]) && GetMonData(&PS_PLAYER_PARTY[i], MON_DATA_HP) == 0)
                    {
                        ZeroMonData(&PS_PLAYER_PARTY[i]);
                        released++;
                    }
                }
                if (released != 0)
                {
                    CompactPartySlots();
                    CalculatePlayerPartyCount();
                }
            }

            static bool32 IsStandingMon(struct Pokemon *mon)
            {
                return IsUsableMon(mon) && GetMonData(mon, MON_DATA_HP) != 0;
            }

            // Whether a battle may start: in a Nuzlocke, not while nobody in the party can fight, and never once the run is over.
            bool32 PokeStudio_NuzCanBattle(void)
            {
                if (!PS_NUZ_FAINT_ON)
                    return TRUE;
                if (FlagGet(FLAG_PS_NUZ_OVER))
                    return FALSE;
                for (u32 i = 0; i < PARTY_SIZE; i++)
                {
                    if (IsStandingMon(&PS_PLAYER_PARTY[i]))
                        return TRUE;
                }
                return FALSE;
            }

            // After the whole team fainted: VAR_RESULT is TRUE when the run goes on (the player chose so and a healthy
            // Pokémon is left in the PC); otherwise the run is over.
            void PokeStudio_NuzAfterWipe(void)
            {
                gSpecialVar_Result = FALSE;
                if (!FlagGet(FLAG_PS_NUZ_WIPE_ENDS) && !FlagGet(FLAG_PS_NUZ_OVER))
                {
                    for (u32 box = 0; box < TOTAL_BOXES_COUNT && !gSpecialVar_Result; box++)
                    {
                        for (u32 pos = 0; pos < IN_BOX_COUNT; pos++)
                        {
                            struct Pokemon mon;
                            if (GetBoxMonDataAt(box, pos, MON_DATA_SPECIES) == SPECIES_NONE)
                                continue;
                            BoxMonToMon(GetBoxedMonPtr(box, pos), &mon);
                            if (IsStandingMon(&mon))
                            {
                                gSpecialVar_Result = TRUE;
                                break;
                            }
                        }
                    }
                }
                if (!gSpecialVar_Result)
                    FlagSet(FLAG_PS_NUZ_OVER);
            }

            // Adaptive boss slot: the team stays in order; the flexible member (tag 7) and its candidates compete for one slot.
            u32 PokeStudio_PickNone(const struct Trainer *trainer, u8 *poolIndexArray, u32 partyIndex, u32 monsCount, u32 battleTypeFlags, struct PoolRules *rules)
            {
                return POOL_SLOT_DISABLED;
            }

            static uq4_12_t Effectiveness(enum Type attack, u32 species)
            {
                enum Type t1 = gSpeciesInfo[species].types[0], t2 = gSpeciesInfo[species].types[1];
                uq4_12_t mod = GetTypeModifier(attack, t1);
                if (t2 != t1)
                    mod = uq4_12_multiply(mod, GetTypeModifier(attack, t2));
                return mod;
            }

            // How well a species does against the player's party: its best STAB hit minus the worst hit it takes.
            static s32 MatchupScore(u32 species)
            {
                s32 score = 0;
                for (u32 i = 0; i < PARTY_SIZE; i++)
                {
                    struct Pokemon *mon = &PS_PLAYER_PARTY[i];
                    u32 foe = GetMonData(mon, MON_DATA_SPECIES);
                    s32 attack = 0, defend = 0;
                    if (!IsUsableMon(mon) || GetMonData(mon, MON_DATA_HP) == 0)
                        continue;
                    for (u32 j = 0; j < 2; j++)
                    {
                        s32 hit = (s32)Effectiveness(gSpeciesInfo[species].types[j], foe);
                        s32 taken = (s32)Effectiveness(gSpeciesInfo[foe].types[j], species);
                        if (hit > attack)
                            attack = hit;
                        if (taken > defend)
                            defend = taken;
                    }
                    score += attack - defend;
                }
                return score;
            }

            // Marks a member as used; the team's own members are always returned, even if a pool rule pruned them.
            static u32 TakeFromPool(u8 *poolIndexArray, u32 poolSize, u32 index)
            {
                for (u32 a = 0; a < poolSize; a++)
                {
                    if (poolIndexArray[a] == index)
                        poolIndexArray[a] = POOL_SLOT_DISABLED;
                }
                return index;
            }

            static bool32 SameAsTeamMember(const struct Trainer *trainer, u32 monsCount, u32 flexIndex, u32 species)
            {
                for (u32 i = 0; i < monsCount; i++)
                {
                    if (i != flexIndex && gSpeciesInfo[trainer->party[i].species].natDexNum == gSpeciesInfo[species].natDexNum)
                        return TRUE;
                }
                return FALSE;
            }

            u32 PokeStudio_PickAdaptive(const struct Trainer *trainer, u8 *poolIndexArray, u32 partyIndex, u32 monsCount, u32 battleTypeFlags, struct PoolRules *rules)
            {
                u32 flexIndex = POOL_SLOT_DISABLED, best = POOL_SLOT_DISABLED;
                s32 bestScore = 0;

                for (u32 i = 0; i < monsCount && i < trainer->poolSize; i++)
                {
                    if (trainer->party[i].tags & MON_POOL_TAG_TAG7)
                    {
                        flexIndex = i;
                        break;
                    }
                }
                // the team itself, in its own order (candidates come after it in the pool)
                if (partyIndex != flexIndex)
                    return TakeFromPool(poolIndexArray, trainer->poolSize, partyIndex);
                if (!FlagGet(FLAG_PS_ADAPTIVE))
                    return TakeFromPool(poolIndexArray, trainer->poolSize, flexIndex);
                for (u32 i = 0; i < trainer->poolSize; i++)
                {
                    if (i != flexIndex && i < monsCount)
                        continue;
                    if (!(trainer->party[i].tags & MON_POOL_TAG_TAG7))
                        continue;
                    bool32 available = FALSE;
                    for (u32 a = 0; a < trainer->poolSize; a++)
                    {
                        if (poolIndexArray[a] == i)
                            available = TRUE;
                    }
                    if (!available || SameAsTeamMember(trainer, monsCount, flexIndex, trainer->party[i].species))
                        continue;
                    s32 score = MatchupScore(trainer->party[i].species);
                    if (best == POOL_SLOT_DISABLED || score > bestScore)
                    {
                        best = i;
                        bestScore = score;
                    }
                }
                return TakeFromPool(poolIndexArray, trainer->poolSize, best != POOL_SLOT_DISABLED ? best : flexIndex);
            }

            // Pokémon Center helper (callnative): perfect IVs for the chosen party member (gSpecialVar_0x8004).
            void PokeStudio_MaxIvs(void)
            {
                struct Pokemon *mon = &PS_PLAYER_PARTY[gSpecialVar_0x8004];
                u32 iv = MAX_PER_STAT_IVS;
                for (u32 i = 0; i < NUM_STATS; i++)
                    SetMonData(mon, MON_DATA_HP_IV + i, &iv);
                CalculateMonStats(mon);
            }

            // EV spreads offered by the helper, chosen with gSpecialVar_Result.
            static const u8 sEvSpreads[][NUM_STATS] =
            {
            EV_SPREADS
            };

            void PokeStudio_SetEvSpread(void)
            {
                struct Pokemon *mon = &PS_PLAYER_PARTY[gSpecialVar_0x8004];
                u32 spread = gSpecialVar_Result < ARRAY_COUNT(sEvSpreads) ? gSpecialVar_Result : ARRAY_COUNT(sEvSpreads) - 1; // else: reset
                for (u32 i = 0; i < NUM_STATS; i++)
                {
                    u32 ev = sEvSpreads[spread][i];
                    SetMonData(mon, MON_DATA_HP_EV + i, &ev);
                }
                CalculateMonStats(mon);
            }
            """.ReplaceLineEndings("\n").Replace("EV_SPREADS", string.Join("\n", GameOptionsWriter.EvSpreads.Select(e => $"                {e.Stats}, // {e.Label}"))));

        // Pokémon Center helper: the ability changer (its list comes from the game's own species data, so changed
        // abilities are offered as they are now)
        L();
        L($"#define PS_HIDDEN_ABILITY_BADGE {o.CenterHelper.HiddenAbilityBadge.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        L("""
            // Abilities the chosen party member (gSpecialVar_0x8004) could switch to, in gStringVar1-3; gSpecialVar_0x8005 =
            // how many. Its own ability, empty slots and repeats are left out; the hidden one only once its badge is won.
            EWRAM_DATA static u8 sPsAbilitySlots[NUM_ABILITY_SLOTS] = {0};

            void PokeStudio_ListAbilities(void)
            {
                struct Pokemon *mon = &PS_PLAYER_PARTY[gSpecialVar_0x8004];
                enum Species species = GetMonData(mon, MON_DATA_SPECIES);
                enum Ability current = GetMonAbility(mon);
                u8 *names[] = {gStringVar1, gStringVar2, gStringVar3};
                enum Ability listed[NUM_ABILITY_SLOTS] = {0};
                u32 count = 0;
                bool32 hiddenOpen = PS_HIDDEN_ABILITY_BADGE == 0 || FlagGet(FLAG_BADGE01_GET + PS_HIDDEN_ABILITY_BADGE - 1);

                for (u32 slot = 0; slot < NUM_ABILITY_SLOTS && count < ARRAY_COUNT(names); slot++)
                {
                    enum Ability ability = GetSpeciesAbility(species, slot);
                    bool32 repeat = FALSE;
                    if (ability == ABILITY_NONE || ability == current || (slot >= NUM_NORMAL_ABILITY_SLOTS && !hiddenOpen))
                        continue;
                    for (u32 i = 0; i < count; i++)
                    {
                        if (listed[i] == ability)
                            repeat = TRUE;
                    }
                    if (repeat)
                        continue;
                    StringCopy(names[count], gAbilitiesInfo[ability].name);
                    listed[count] = ability;
                    sPsAbilitySlots[count] = slot;
                    count++;
                }
                gSpecialVar_0x8005 = count;
            }

            // gSpecialVar_0x8006 = TRUE when the picked ability (gSpecialVar_Result) is the hidden one (it costs more).
            void PokeStudio_AbilityChoiceHidden(void)
            {
                gSpecialVar_0x8006 = gSpecialVar_Result < NUM_ABILITY_SLOTS && sPsAbilitySlots[gSpecialVar_Result] >= NUM_NORMAL_ABILITY_SLOTS;
            }

            void PokeStudio_SetAbility(void)
            {
                struct Pokemon *mon = &PS_PLAYER_PARTY[gSpecialVar_0x8004];
                u32 slot = gSpecialVar_Result < NUM_ABILITY_SLOTS ? sPsAbilitySlots[gSpecialVar_Result] : 0;
                SetMonData(mon, MON_DATA_ABILITY_NUM, &slot);
            }
            """.ReplaceLineEndings("\n"));

        // trainers by difficulty: who always stops the player on Easy, and each gym's trainers for Hard's gym lock
        L();
        L($"#define PS_TRAINER_RULES {(TrainerRules(o) ? "TRUE" : "FALSE")}");
        L("static const u16 sMajorTrainers[] =");
        L("{");
        foreach (var t in x.MajorTrainers)
        {
            L($"    {t},");
        }

        L("    TRAINER_NONE,");
        L("};");
        for (var g = 0; g < x.Gyms.Count; g++)
        {
            L($"static const u16 sGymTrainers{g}[] = {{{string.Join(", ", x.Gyms[g].Trainers)}, TRAINER_NONE}}; // {x.Gyms[g].Leader}");
        }

        L($"static const u16 *const sGymTrainers[] = {{{(x.Gyms.Count == 0 ? "NULL" : string.Join(", ", Enumerable.Range(0, x.Gyms.Count).Select(g => $"sGymTrainers{g}")))}}};");
        L($"#define PS_GYM_COUNT {x.Gyms.Count}");
        sb.Append("""

            // Easy: only bosses and story battles stop the player; everyone else battles when talked to.
            bool32 PokeStudio_TrainerIgnoresPlayer(u32 trainerId)
            {
                if (!PS_TRAINER_RULES || GetCurrentDifficultyLevel() != DIFFICULTY_EASY)
                    return FALSE;
                for (u32 i = 0; sMajorTrainers[i] != TRAINER_NONE; i++)
                {
                    if (sMajorTrainers[i] == trainerId)
                        return FALSE;
                }
                return TRUE;
            }

            // Hard and Extra Hard: every trainer sees far, in every direction.
            bool32 PokeStudio_TrainersSeeFar(void)
            {
                return PS_TRAINER_RULES && GetCurrentDifficultyLevel() >= DIFFICULTY_HARD;
            }

            // For the routes' scripts: VAR_RESULT = TRUE on Hard and Extra Hard (route trainers stand on chokepoints).
            void PokeStudio_HardTrainers(void)
            {
                gSpecialVar_Result = PokeStudio_TrainersSeeFar();
            }

            // Hard and Extra Hard: VAR_RESULT = how many trainers of gym VAR_0x8004 are still unbeaten (0 otherwise).
            void PokeStudio_GymTrainersLeft(void)
            {
                gSpecialVar_Result = 0;
                if (!PS_TRAINER_RULES || GetCurrentDifficultyLevel() < DIFFICULTY_HARD || gSpecialVar_0x8004 >= PS_GYM_COUNT)
                    return;
                for (const u16 *t = sGymTrainers[gSpecialVar_0x8004]; *t != TRAINER_NONE; t++)
                {
                    if (!HasTrainerBeenFought(*t))
                        gSpecialVar_Result++;
                }
            }

            // The Tools menu on Hard and up: VAR_RESULT = TRUE while a trainer on this map is still unbeaten (no healing or
            // PC in the middle of a gym or a locked area).
            void PokeStudio_ToolsBlocked(void)
            {
                gSpecialVar_Result = FALSE;
                if (!PokeStudio_TrainersSeeFar())
                    return;
                for (u32 i = 0; i < OBJECT_EVENTS_COUNT; i++)
                {
                    const struct ObjectEvent *o = &gObjectEvents[i];
                    if (o->active && !o->isPlayer && o->trainerType != TRAINER_TYPE_NONE)
                    {
                        const u8 *script = GetObjectEventScriptPointerByObjectEventId(o->localId);
                        if (script != NULL && !GetTrainerFlagFromScriptPointer(script))
                        {
                            gSpecialVar_Result = TRUE;
                            return;
                        }
                    }
                }
            }

            // The Tools menu: the Fly map, to any town already visited.
            void PokeStudio_ToolsFly(void)
            {
                SetMainCallback2(CB2_OpenFlyMap);
            }
            """.ReplaceLineEndings("\n"));
        return sb.ToString();
    }
}
